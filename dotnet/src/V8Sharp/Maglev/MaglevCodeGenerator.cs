// Port of src/maglev/maglev-code-generator.{h,cc} (and the GenerateCode
// methods of maglev-ir.cc) for IL: the graph, in block order, becomes one
// static method of a type in the baseline tier's dynamic code space
// (BaselineCodeSpace), so RyuJIT's tiering applies to it as to baseline code.
//
//   V8 (machine code)                       V8Sharp (IL)
//   register allocation, spill slots         one IL local per value node, of the
//                                            representation's CLR type (JSValue,
//                                            int, double); RyuJIT allocates
//   gap moves / phi moves                    stores to the phi locals at the end of
//                                            the predecessor, on split edges for
//                                            conditional branches
//   deferred code: eager deopt exits         out-of-line blocks after the body: the
//                                            translation's values go to the isolate's
//                                            deopt scratch buffer, then the
//                                            Deoptimizer, then ret
//   lazy deopt (patched return address)      after each call: a test of the code's
//                                            marked_for_deoptimization bit
//   safepoint table, stack slots             none: the GC sees the IL locals
//   bytecode offset for the stack walker     a store of the offset into the frame
//                                            record before nodes that call or throw
//   embedded heap constants                  static fields of the code's type
//
// The method: static JSValue Code(MaglevCode code, Isolate isolate,
// ref InterpreterState state), bound to its code object as a MaglevCodeEntry.
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using V8Sharp.Baseline;
using V8Sharp.Deoptimizer;
using V8Sharp.Interpreter;

namespace V8Sharp.Maglev;

internal sealed class MaglevCodeGenerator
{
    static readonly int kJSValueSize = Unsafe.SizeOf<JSValue>();

    readonly MaglevCompilationInfo _info;
    readonly Graph _graph;
    readonly MaglevCode _code;
    readonly TypeBuilder _type;
    readonly MethodBuilder _method;
    readonly ILGenerator _il;

    // Frame locals of the outermost frame.
    readonly LocalBuilder _fpRef;
    readonly LocalBuilder _fp;
    readonly LocalBuilder _frame;
    readonly LocalBuilder _baseFrameIndex;
    // Scratch locals.
    readonly LocalBuilder _tmpLong;
    readonly LocalBuilder _tmpDouble;
    readonly LocalBuilder _tmpInt;
    readonly LocalBuilder _tmpMap;
    readonly LocalBuilder _tmpValue;
    readonly LocalBuilder _tmpObject;

    readonly List<(FieldBuilder Field, object? Value)> _staticConstants = [];
    readonly Dictionary<(object, Type), FieldBuilder> _constantFields = new();
    readonly List<DeoptPoint> _deoptPoints = [];
    readonly Dictionary<(DeoptFrame, DeoptimizeReason, int), Label> _eagerExits = new();
    readonly List<(FeedbackVector Vector, int Slot)> _speculationFeedback = [];
    // One deopt exit per frame state: the checks of a checkpoint branch to a
    // stub that sets the reason and jumps to it.
    readonly Dictionary<DeoptFrame, Label> _frameExits = new(ReferenceEqualityComparer.Instance);
    readonly List<(Label Stub, Label Exit, int Reason)> _eagerStubs = [];
    LocalBuilder? _deoptReason;
    int _spilledValues;
    readonly List<(Label Label, DeoptInfo Info, DeoptimizeKind Kind, DeoptimizeReason Reason, ValueNode? Result)> _pendingExits = [];
    readonly List<(Label Label, BasicBlock From, BasicBlock To)> _edgeStubs = [];
    int _maxScratch;

    // Catch blocks (exception handlers): the body runs in a .NET try region
    // whose catch clause moves the throwing node's values into the catch
    // block's exception phis (V8's exception handler trampolines) and
    // re-enters the region at its start, which dispatches to the catch block.
    bool _hasCatchBlocks;
    LocalBuilder? _throwSite;
    LocalBuilder? _dispatch;
    LocalBuilder? _result;
    LocalBuilder? _exception;
    Label _reenter;
    Label _end;
    readonly List<BasicBlock> _catchBlocks = [];
    readonly List<Node> _throwSites = [];

    public MaglevCodeGenerator(MaglevCompilationInfo info, MaglevCode code)
    {
        _info = info;
        _graph = info.Graph;
        _code = code;
        string name = "maglev:" + MaglevCompiler.DebugName(info.Function.Shared) + (info.IsOsr ? "@osr" + info.OsrOffset : "");
        (_type, _method) = BaselineCodeSpace.For(info.Isolate).DefineMethod(name, typeof(JSValue),
            [typeof(MaglevCode), typeof(Isolate), typeof(InterpreterState).MakeByRefType()]);
        if (s_aggressiveOptimization) _method.SetImplementationFlags(MethodImplAttributes.AggressiveOptimization);
        _il = _method.GetILGenerator(4096);
        _fpRef = _il.DeclareLocal(typeof(JSValue).MakeByRefType());
        _fp = _il.DeclareLocal(typeof(int));
        _frame = _il.DeclareLocal(typeof(InterpreterFrameRecord).MakeByRefType());
        _baseFrameIndex = _il.DeclareLocal(typeof(int));
        _tmpLong = _il.DeclareLocal(typeof(long));
        _tmpDouble = _il.DeclareLocal(typeof(double));
        _tmpInt = _il.DeclareLocal(typeof(int));
        _tmpMap = _il.DeclareLocal(typeof(Map));
        _tmpValue = _il.DeclareLocal(typeof(JSValue));
        _tmpObject = _il.DeclareLocal(typeof(HeapObject));
    }

    /// <summary>
    /// V8SHARP_MAGLEV_AGGRESSIVE=1 compiles Maglev methods fully optimized at
    /// once (MethodImplAttributes.AggressiveOptimization) instead of through
    /// RyuJIT's tier 0.
    /// </summary>
    static readonly bool s_aggressiveOptimization = Environment.GetEnvironmentVariable("V8SHARP_MAGLEV_AGGRESSIVE") == "1";

    // ---- Reflection handles ------------------------------------------------------------------------------

    static readonly FieldInfo s_obj = typeof(JSValue).GetField("_obj", BindingFlags.NonPublic | BindingFlags.Instance)!;
    static readonly FieldInfo s_num = typeof(JSValue).GetField("_num", BindingFlags.NonPublic | BindingFlags.Instance)!;
    static readonly ConstructorInfo s_jsValueFromDouble = typeof(JSValue).GetConstructor([typeof(double)])!;
    static readonly FieldInfo s_numberTag = typeof(NumberTag).GetField(nameof(NumberTag.Instance))!;
    static readonly FieldInfo s_null = typeof(JSValue).GetField(nameof(JSValue.Null))!;
    static readonly FieldInfo s_true = typeof(JSValue).GetField(nameof(JSValue.True))!;
    static readonly FieldInfo s_false = typeof(JSValue).GetField(nameof(JSValue.False))!;
    static readonly FieldInfo s_theHole = typeof(JSValue).GetField(nameof(JSValue.TheHole))!;
    static readonly FieldInfo s_oddballTheHole = typeof(Oddball).GetField(nameof(Oddball.TheHole))!;
    static readonly FieldInfo s_registerStack = typeof(Isolate).GetField(nameof(Isolate.RegisterStack))!;
    static readonly FieldInfo s_jsObjectFields = typeof(JSObject).GetField("_fields", BindingFlags.NonPublic | BindingFlags.Instance)!;
    static readonly FieldInfo s_inObjectSlots0 =
        typeof(JSObjectInObject1).GetField("_slots0", BindingFlags.NonPublic | BindingFlags.Instance)!;
    static readonly FieldInfo s_receiverMap = typeof(JSReceiver).GetField(nameof(JSReceiver.Map))!;
    static readonly FieldInfo s_instanceType = typeof(HeapObject).GetField(nameof(HeapObject.InstanceType))!;
    static readonly FieldInfo s_stateFunction = typeof(InterpreterState).GetField(nameof(InterpreterState.Function))!;
    static readonly FieldInfo s_stateContext = typeof(InterpreterState).GetField(nameof(InterpreterState.Context))!;
    static readonly MethodInfo s_jsValueFromObject = typeof(JSValue).GetMethod(nameof(JSValue.FromObject), [typeof(HeapObject)])!;
    static readonly FieldInfo s_interpreterFrameDepth = typeof(Isolate).GetField(nameof(Isolate.InterpreterFrameDepth))!;
    static readonly MethodInfo s_interpreterFrames = typeof(Isolate).GetProperty(nameof(Isolate.InterpreterFrames))!.GetMethod!;
    static readonly FieldInfo s_stFp = typeof(InterpreterState).GetField(nameof(InterpreterState.Fp))!;
    static readonly FieldInfo s_stFrameIndex = typeof(InterpreterState).GetField(nameof(InterpreterState.FrameIndex))!;
    static readonly FieldInfo s_recordPc = typeof(InterpreterFrameRecord).GetField(nameof(InterpreterFrameRecord.Pc))!;
    static readonly FieldInfo s_recordFp = typeof(InterpreterFrameRecord).GetField(nameof(InterpreterFrameRecord.Fp))!;
    static readonly FieldInfo s_markedForDeoptimization = typeof(MaglevCode).GetField(nameof(MaglevCode.MarkedForDeoptimization))!;
    static readonly FieldInfo s_deoptScratch = typeof(Isolate).GetField(nameof(Isolate.MaglevDeoptScratch))!;
    static readonly FieldInfo s_propertyCellValue = typeof(PropertyCell).GetField(nameof(PropertyCell.Value))!;
    static readonly MethodInfo s_deoptimize = typeof(V8Sharp.Deoptimizer.Deoptimizer).GetMethod(nameof(V8Sharp.Deoptimizer.Deoptimizer.Deoptimize))!;
    static readonly MethodInfo s_doubleToInt64Bits = typeof(BitConverter).GetMethod(nameof(BitConverter.DoubleToInt64Bits), [typeof(double)])!;

    static MethodInfo B(string name) => typeof(MaglevBuiltins).GetMethod(name) ?? throw new InvalidOperationException("no MaglevBuiltins." + name);

    // ---- Driver ----------------------------------------------------------------------------------------------

    public (MaglevCodeEntry Entry, int ILSize) Generate()
    {
        AllocateLocals();
        EmitPrologue();
        foreach (BasicBlock block in _graph.Blocks)
        {
            if (!block.IsDead && block.IsExceptionHandler) _catchBlocks.Add(block);
        }
        _hasCatchBlocks = _catchBlocks.Count > 0;
        if (_hasCatchBlocks) EmitTryRegionStart();
        foreach (BasicBlock block in _graph.Blocks)
        {
            if (block.IsDead) continue;
            EmitBlock(block);
        }
        EmitEdgeStubs();
        int bodySize = _il.ILOffset;
        EmitDeoptExits();
        if (_hasCatchBlocks) EmitTryRegionEnd();
        if (_info.Isolate.Flags.trace_opt_verbose)
        {
            Console.WriteLine($"[maglev code: {bodySize} bytes IL body, {_il.ILOffset - bodySize} bytes in {_pendingExits.Count} deopt exits " +
                              $"({_frameExits.Count} eager, {_pendingExits.Count - _frameExits.Count} lazy; {_eagerStubs.Count} eager checks, {_spilledValues} values)]");
        }

        _code.DeoptPoints = _deoptPoints.ToArray();
        _code.SpeculationFeedback = _speculationFeedback.ToArray();
        _code.MaxScratchSize = _maxScratch;
        Type type = BaselineCodeSpace.CreateType(_type);
        foreach ((FieldBuilder field, object? value) in _staticConstants)
        {
            type.GetField(field.Name)!.SetValue(null, value);
        }
        MethodInfo method = type.GetMethod(_method.Name)!;
        var entry = (MaglevCodeEntry)method.CreateDelegate(typeof(MaglevCodeEntry), _code);
        return (entry, _il.ILOffset);
    }

    Label BlockLabel(BasicBlock block)
    {
        if (!block.LabelDefined)
        {
            block.Label = _il.DefineLabel();
            block.LabelDefined = true;
        }
        return block.Label;
    }

    /// <summary>An IL local for every value node that needs one (used, not a constant).</summary>
    /// <summary>Locals an IL method can have (ldloc's 16-bit index, with room for the code generator's own).</summary>
    const int kMaxLocals = 65000;

    void AllocateLocals()
    {
        int count = 0;
        foreach (BasicBlock block in _graph.Blocks)
        {
            if (block.IsDead) continue;
            foreach (Phi phi in block.Phis) if (phi.UseCount > 0) count++;
            foreach (Node node in block.Nodes) if (node is ValueNode { IsConstant: false, UseCount: > 0 }) count++;
        }
        // (V8's limit is the frame size, kMaxStackSlots.)
        if (count > kMaxLocals) throw new MaglevBailoutException($"too many values for IL locals ({count})");
        foreach (BasicBlock block in _graph.Blocks)
        {
            if (block.IsDead) continue;
            foreach (Phi phi in block.Phis)
            {
                if (phi.UseCount > 0) phi.Local = _il.DeclareLocal(ClrType(phi.Representation));
            }
            foreach (Node node in block.Nodes)
            {
                if (node is ValueNode v && !v.IsConstant && v.UseCount > 0) v.Local = _il.DeclareLocal(ClrType(v.Representation));
            }
        }
    }

    static Type ClrType(ValueRepresentation repr) => repr switch
    {
        ValueRepresentation.kTagged => typeof(JSValue),
        ValueRepresentation.kInt32 or ValueRepresentation.kUint32 => typeof(int),
        ValueRepresentation.kFloat64 or ValueRepresentation.kHoleyFloat64 => typeof(double),
        _ => typeof(bool),
    };

    /// <summary>The frame of the outermost function: fpRef, fp, the frame record and its index.</summary>
    void EmitPrologue()
    {
        _il.Emit(OpCodes.Ldarg_1);
        _il.Emit(OpCodes.Ldfld, s_registerStack);
        _il.Emit(OpCodes.Ldarg_2);
        _il.Emit(OpCodes.Ldfld, s_stFp);
        _il.Emit(OpCodes.Ldelema, typeof(JSValue));
        _il.Emit(OpCodes.Stloc, _fpRef);
        _il.Emit(OpCodes.Ldarg_2);
        _il.Emit(OpCodes.Ldfld, s_stFp);
        _il.Emit(OpCodes.Stloc, _fp);
        _il.Emit(OpCodes.Ldarg_2);
        _il.Emit(OpCodes.Ldfld, s_stFrameIndex);
        _il.Emit(OpCodes.Stloc, _baseFrameIndex);
        _il.Emit(OpCodes.Ldarg_1);
        _il.Emit(OpCodes.Call, s_interpreterFrames);
        _il.Emit(OpCodes.Ldloc, _baseFrameIndex);
        _il.Emit(OpCodes.Ldelema, typeof(InterpreterFrameRecord));
        _il.Emit(OpCodes.Stloc, _frame);
    }

    // ---- Constants ------------------------------------------------------------------------------------------

    /// <summary>Pushes a constant object as <paramref name="type"/> from a static field of the code's type.</summary>
    void LoadConstantObject(object? value, Type type)
    {
        if (value is null && type.IsClass)
        {
            _il.Emit(OpCodes.Ldnull);
            return;
        }
        object key = value ?? DBNull.Value;
        if (!_constantFields.TryGetValue((key, type), out FieldBuilder? field))
        {
            field = _type.DefineField("k" + _constantFields.Count.ToString(System.Globalization.CultureInfo.InvariantCulture), type,
                FieldAttributes.Public | FieldAttributes.Static);
            _constantFields[(key, type)] = field;
            object? boxed = type == typeof(JSValue) ? value is JSValue jv ? jv : JSValue.FromObject((HeapObject?)value) : value;
            _staticConstants.Add((field, boxed));
        }
        _il.Emit(OpCodes.Ldsfld, field);
    }

    void LoadUndefined()
    {
        _il.Emit(OpCodes.Ldloca, _tmpValue);
        _il.Emit(OpCodes.Initobj, typeof(JSValue));
        _il.Emit(OpCodes.Ldloc, _tmpValue);
    }

    /// <summary>Pushes a constant node's value in <paramref name="repr"/>.</summary>
    void LoadConstant(ValueNode node, ValueRepresentation repr)
    {
        switch (repr)
        {
            case ValueRepresentation.kInt32:
            case ValueRepresentation.kUint32:
                if (!node.TryGetInt32Constant(out int i)) throw new InvalidOperationException("not an int32 constant");
                _il.Emit(OpCodes.Ldc_I4, i);
                return;
            case ValueRepresentation.kFloat64:
            case ValueRepresentation.kHoleyFloat64:
                if (!node.TryGetFloat64Constant(out double d)) throw new InvalidOperationException("not a float64 constant");
                _il.Emit(OpCodes.Ldc_R8, d);
                return;
        }
        switch (node.Opcode)
        {
            case Opcode.SmiConstant:
            case Opcode.Int32Constant:
                _il.Emit(OpCodes.Ldc_R8, (double)node.Int0);
                _il.Emit(OpCodes.Newobj, s_jsValueFromDouble);
                return;
            case Opcode.Float64Constant:
                _il.Emit(OpCodes.Ldc_R8, node.Double0);
                _il.Emit(OpCodes.Newobj, s_jsValueFromDouble);
                return;
            case Opcode.RootConstant:
                switch ((RootIndex)node.Int0)
                {
                    case RootIndex.kUndefinedValue: LoadUndefined(); return;
                    case RootIndex.kNullValue: _il.Emit(OpCodes.Ldsfld, s_null); return;
                    case RootIndex.kTrueValue: _il.Emit(OpCodes.Ldsfld, s_true); return;
                    case RootIndex.kFalseValue: _il.Emit(OpCodes.Ldsfld, s_false); return;
                    default: _il.Emit(OpCodes.Ldsfld, s_theHole); return;
                }
            default:
                if (node.Value0.IsNumber)
                {
                    _il.Emit(OpCodes.Ldc_R8, node.Value0.Number);
                    _il.Emit(OpCodes.Newobj, s_jsValueFromDouble);
                    return;
                }
                LoadConstantObject(node.Value0, typeof(JSValue));
                return;
        }
    }

    // ---- Values -------------------------------------------------------------------------------------------

    /// <summary>Pushes the value of <paramref name="node"/> in its own representation.</summary>
    void Load(ValueNode node)
    {
        if (node.IsConstant)
        {
            LoadConstant(node, node.Representation);
            return;
        }
        _il.Emit(OpCodes.Ldloc, node.Local ?? throw new InvalidOperationException($"no local for {node}"));
    }

    /// <summary>Pushes the value of <paramref name="node"/> converted to <paramref name="repr"/> (constants, tagging).</summary>
    void Load(ValueNode node, ValueRepresentation repr)
    {
        if (node.IsConstant)
        {
            LoadConstant(node, repr);
            return;
        }
        Load(node);
        ValueRepresentation from = node.Representation;
        if (from == repr || from is ValueRepresentation.kFloat64 or ValueRepresentation.kHoleyFloat64 &&
            repr is ValueRepresentation.kFloat64 or ValueRepresentation.kHoleyFloat64)
        {
            return;
        }
        if (repr == ValueRepresentation.kTagged)
        {
            EmitTag(from);
            return;
        }
        if (repr is ValueRepresentation.kFloat64 or ValueRepresentation.kHoleyFloat64)
        {
            // ChangeInt32ToFloat64 / ChangeUint32ToFloat64 at the use (phi inputs).
            if (from == ValueRepresentation.kInt32)
            {
                _il.Emit(OpCodes.Conv_R8);
                return;
            }
            if (from == ValueRepresentation.kUint32)
            {
                _il.Emit(OpCodes.Conv_R_Un);
                _il.Emit(OpCodes.Conv_R8);
                return;
            }
        }
        if (repr == ValueRepresentation.kInt32 && from == ValueRepresentation.kUint32) return;
        throw new InvalidOperationException($"no conversion {from} -> {repr} for {node}");
    }

    /// <summary>Converts the untagged value on the stack to a JSValue.</summary>
    void EmitTag(ValueRepresentation from)
    {
        switch (from)
        {
            case ValueRepresentation.kInt32:
                _il.Emit(OpCodes.Conv_R8);
                _il.Emit(OpCodes.Newobj, s_jsValueFromDouble);
                break;
            case ValueRepresentation.kUint32:
                _il.Emit(OpCodes.Conv_R_Un);
                _il.Emit(OpCodes.Conv_R8);
                _il.Emit(OpCodes.Newobj, s_jsValueFromDouble);
                break;
            case ValueRepresentation.kFloat64:
                _il.Emit(OpCodes.Newobj, s_jsValueFromDouble);
                break;
            case ValueRepresentation.kHoleyFloat64:
                _il.Emit(OpCodes.Call, B(nameof(MaglevBuiltins.HoleyFloat64ToTagged)));
                break;
            case ValueRepresentation.kTagged:
                break;
            default:
                throw new InvalidOperationException("cannot tag " + from);
        }
    }

    void Store(ValueNode node)
    {
        if (node.Local is null)
        {
            _il.Emit(OpCodes.Pop);
            return;
        }
        _il.Emit(OpCodes.Stloc, node.Local);
    }

    /// <summary>Pushes a JSValue's heap object as <paramref name="type"/> (castclass for a typed parameter).</summary>
    void LoadAsObject(ValueNode node, Type type)
    {
        Load(node, ValueRepresentation.kTagged);
        _il.Emit(OpCodes.Ldfld, s_obj);
        if (type != typeof(HeapObject) && type != typeof(object)) _il.Emit(OpCodes.Castclass, type);
    }

    // ---- Frames --------------------------------------------------------------------------------------------

    /// <summary>Pushes the address of register <paramref name="index"/> of <paramref name="unit"/>'s frame.</summary>
    void LoadFrameSlotAddress(MaglevCompilationUnit? unit, int index)
    {
        if (unit is null || !unit.IsInline) _il.Emit(OpCodes.Ldloc, _fpRef);
        else _il.Emit(OpCodes.Ldloc, unit.FpRefLocal ?? throw new InvalidOperationException("inlined frame not entered"));
        if (index != 0)
        {
            _il.Emit(OpCodes.Ldc_I4, index * kJSValueSize);
            _il.Emit(OpCodes.Conv_I);
            _il.Emit(OpCodes.Add);
        }
    }

    void LoadFp(MaglevCompilationUnit? unit)
    {
        if (unit is null || !unit.IsInline) _il.Emit(OpCodes.Ldloc, _fp);
        else _il.Emit(OpCodes.Ldloc, unit.FpLocal!);
    }

    void LoadFrameRecord(MaglevCompilationUnit? unit)
    {
        if (unit is null || !unit.IsInline) _il.Emit(OpCodes.Ldloc, _frame);
        else _il.Emit(OpCodes.Ldloc, unit.FrameRecordLocal!);
    }

    /// <summary>Records the node's bytecode offset in its frame's record (for stack traces and messages).</summary>
    void StoreBytecodeOffset(NodeBase node)
    {
        if (node.BytecodeOffset < 0) return;
        LoadFrameRecord(node.Unit);
        _il.Emit(OpCodes.Ldc_I4, node.BytecodeOffset);
        _il.Emit(OpCodes.Stfld, s_recordPc);
    }

    // ---- Catch blocks --------------------------------------------------------------------------------------

    /// <summary>
    /// Starts the try region around the body: re-entered after an exception
    /// with the catch block to continue at in _dispatch (-1 on entry).
    /// </summary>
    void EmitTryRegionStart()
    {
        _throwSite = _il.DeclareLocal(typeof(int));
        _dispatch = _il.DeclareLocal(typeof(int));
        _result = _il.DeclareLocal(typeof(JSValue));
        _exception = _il.DeclareLocal(typeof(JSValue));
        _end = _il.DefineLabel();
        _reenter = _il.DefineLabel();
        _il.Emit(OpCodes.Ldc_I4_M1);
        _il.Emit(OpCodes.Stloc, _throwSite);
        _il.Emit(OpCodes.Ldc_I4_M1);
        _il.Emit(OpCodes.Stloc, _dispatch);
        _il.MarkLabel(_reenter);
        _il.BeginExceptionBlock();
        var labels = new Label[_catchBlocks.Count];
        for (int i = 0; i < labels.Length; i++) labels[i] = BlockLabel(_catchBlocks[i]);
        _il.Emit(OpCodes.Ldloc, _dispatch);
        _il.Emit(OpCodes.Switch, labels);
    }

    /// <summary>
    /// The catch clause: an exception of a throwing node with a catch block
    /// (_throwSite) runs the node's trampoline; others propagate (the filter).
    /// </summary>
    void EmitTryRegionEnd()
    {
        _il.BeginExceptFilterBlock();
        _il.Emit(OpCodes.Isinst, typeof(JavaScriptException));
        _il.Emit(OpCodes.Ldnull);
        _il.Emit(OpCodes.Cgt_Un);
        _il.Emit(OpCodes.Ldloc, _throwSite!);
        _il.Emit(OpCodes.Ldc_I4_0);
        _il.Emit(OpCodes.Clt);
        _il.Emit(OpCodes.Ldc_I4_0);
        _il.Emit(OpCodes.Ceq);
        _il.Emit(OpCodes.And);
        _il.BeginCatchBlock(null!);
        _il.Emit(OpCodes.Ldarg_1);
        _il.Emit(OpCodes.Ldloc, _baseFrameIndex);
        _il.Emit(OpCodes.Ldloc, _fp);
        _il.Emit(OpCodes.Ldc_I4, _info.Toplevel.Bytecode.RegisterCount);
        _il.Emit(OpCodes.Add);
        Call(nameof(MaglevBuiltins.EnterCatchBlock));
        _il.Emit(OpCodes.Stloc, _exception!);
        var trampolines = new Label[_throwSites.Count];
        for (int i = 0; i < trampolines.Length; i++) trampolines[i] = _il.DefineLabel();
        _il.Emit(OpCodes.Ldloc, _throwSite!);
        _il.Emit(OpCodes.Switch, trampolines);
        _il.Emit(OpCodes.Rethrow);
        for (int i = 0; i < trampolines.Length; i++)
        {
            _il.MarkLabel(trampolines[i]);
            ExceptionHandlerInfo handler = _throwSites[i].ExceptionHandler!;
            BasicBlock catchBlock = handler.CatchState.Block!;
            var stored = new List<Phi>();
            foreach (Phi phi in catchBlock.Phis)
            {
                if (phi.Local is null) continue;
                if (phi.Inputs.Length == 0) _il.Emit(OpCodes.Ldloc, _exception!);
                else Load(phi.Inputs[handler.ThrowIndex], phi.Representation);
                stored.Add(phi);
            }
            for (int k = stored.Count - 1; k >= 0; k--) _il.Emit(OpCodes.Stloc, stored[k].Local!);
            _il.Emit(OpCodes.Ldc_I4, _catchBlocks.IndexOf(catchBlock));
            _il.Emit(OpCodes.Stloc, _dispatch!);
            _il.Emit(OpCodes.Ldc_I4_M1);
            _il.Emit(OpCodes.Stloc, _throwSite!);
            _il.Emit(OpCodes.Leave, _reenter);
        }
        _il.EndExceptionBlock();
        _il.MarkLabel(_end);
        _il.Emit(OpCodes.Ldloc, _result!);
        _il.Emit(OpCodes.Ret);
    }

    /// <summary>Returns the value on the stack (from inside the try region: through _result).</summary>
    void EmitReturn()
    {
        if (!_hasCatchBlocks)
        {
            _il.Emit(OpCodes.Ret);
            return;
        }
        _il.Emit(OpCodes.Stloc, _result!);
        _il.Emit(OpCodes.Leave, _end);
    }

    // ---- Blocks ------------------------------------------------------------------------------------------

    void EmitBlock(BasicBlock block)
    {
        _il.MarkLabel(BlockLabel(block));
        foreach (Node node in block.Nodes)
        {
            if (IsDeadNode(node)) continue;
            if (IsElidedArguments(node)) continue;
            if (node.Unit is { IsInline: true } unit && NeedsFrame(node)) EmitEnsureInlinedFrames(unit);
            if (_hasCatchBlocks && node.ExceptionHandler is { CatchState.Block: { IsDead: false } })
            {
                // The node's exceptions continue at its catch block.
                _il.Emit(OpCodes.Ldc_I4, _throwSites.Count);
                _il.Emit(OpCodes.Stloc, _throwSite!);
                _throwSites.Add(node);
                EmitNode(node);
                _il.Emit(OpCodes.Ldc_I4_M1);
                _il.Emit(OpCodes.Stloc, _throwSite!);
                continue;
            }
            EmitNode(node);
        }
        EmitControl(block, block.Control!);
    }

    /// <summary>
    /// Whether a node of inlined code needs its interpreter frame: it calls
    /// out (the callee, a throw or a stack walk can observe the frame) or
    /// reads or writes the frame's slots.
    /// </summary>
    static bool NeedsFrame(Node node) =>
        node.Opcode is Opcode.CallBuiltin or Opcode.LoadRegister or Opcode.StoreRegister or Opcode.SetCurrentContext or
            Opcode.HandleNoHeapWritesInterrupt ||
        node.Opcode != Opcode.EnterInlinedFrame &&
        (node.Properties & (OpProperties.kCall | OpProperties.kCanThrow | OpProperties.kLazyDeopt)) != 0;

    static bool IsElidedArguments(NodeBase node) => node.Obj0 is CallBuiltinInfo { Elided: true };

    static bool IsDeadNode(Node node) =>
        node is ValueNode { UseCount: 0 } &&
        (node.Properties & (OpProperties.kEagerDeopt | OpProperties.kCanWrite | OpProperties.kCall | OpProperties.kNotIdempotent |
                            OpProperties.kCanThrow | OpProperties.kLazyDeopt)) == 0;

    /// <summary>Phi moves for the edge <paramref name="from"/> -> <paramref name="to"/> (parallel: all values first, then the stores).</summary>
    void EmitPhiMoves(BasicBlock from, BasicBlock to)
    {
        if (to.Phis.Count == 0) return;
        int index = to.PredecessorIndexOf(from);
        var stored = new List<Phi>();
        foreach (Phi phi in to.Phis)
        {
            if (phi.Local is null) continue;
            Load(phi.Inputs[index], phi.Representation);
            stored.Add(phi);
        }
        for (int i = stored.Count - 1; i >= 0; i--) _il.Emit(OpCodes.Stloc, stored[i].Local!);
    }

    /// <summary>The label of the edge <paramref name="from"/> -> <paramref name="to"/>: a stub with phi moves when the target has phis.</summary>
    Label EdgeLabel(BasicBlock from, BasicBlock to)
    {
        if (to.Phis.Exists(static p => p.Local is not null))
        {
            Label stub = _il.DefineLabel();
            _edgeStubs.Add((stub, from, to));
            return stub;
        }
        return BlockLabel(to);
    }

    void EmitEdgeStubs()
    {
        // (Stubs can be added while emitting stubs: none are, a stub only jumps.)
        foreach ((Label label, BasicBlock from, BasicBlock to) in _edgeStubs)
        {
            _il.MarkLabel(label);
            EmitPhiMoves(from, to);
            _il.Emit(OpCodes.Br, BlockLabel(to));
        }
    }

    void EmitControl(BasicBlock block, ControlNode c)
    {
        switch (c.Opcode)
        {
            case Opcode.Jump:
            case Opcode.JumpLoop:
                EmitPhiMoves(block, c.Target!);
                _il.Emit(OpCodes.Br, BlockLabel(c.Target!));
                return;
            case Opcode.Return:
                Load(c.Inputs[0], ValueRepresentation.kTagged);
                EmitReturn();
                return;
            case Opcode.Deopt:
                if (c.EagerDeoptInfo is null)
                {
                    // Unreachable (after a throw).
                    _il.Emit(OpCodes.Call, B(nameof(MaglevBuiltins.Unreachable)));
                    LoadUndefined();
                    EmitReturn();
                    return;
                }
                _il.Emit(OpCodes.Br, EagerExit(c.EagerDeoptInfo));
                return;
            case Opcode.Switch:
            {
                Load(c.Inputs[0], ValueRepresentation.kInt32);
                _il.Emit(OpCodes.Ldc_I4, c.Int0);
                _il.Emit(OpCodes.Sub);
                BasicBlock?[] targets = c.Targets!;
                var labels = new Label[targets.Length - 1];
                for (int i = 0; i < labels.Length; i++) labels[i] = EdgeLabel(block, targets[i]!);
                _il.Emit(OpCodes.Switch, labels);
                _il.Emit(OpCodes.Br, EdgeLabel(block, targets[^1]!));
                return;
            }
        }
        // Conditional branches: push the condition (true -> Target).
        EmitBranchCondition(c);
        Label trueLabel = EdgeLabel(block, c.Target!);
        Label falseLabel = EdgeLabel(block, c.FalseTarget!);
        _il.Emit(OpCodes.Brtrue, trueLabel);
        _il.Emit(OpCodes.Br, falseLabel);
    }

    /// <summary>Pushes the bool condition of a branch control node.</summary>
    void EmitBranchCondition(ControlNode c)
    {
        switch (c.Opcode)
        {
            case Opcode.BranchIfToBooleanTrue:
                Load(c.Inputs[0], ValueRepresentation.kTagged);
                _il.Emit(OpCodes.Call, B(nameof(MaglevBuiltins.ToBoolean)));
                return;
            case Opcode.BranchIfInt32Compare:
                Load(c.Inputs[0], ValueRepresentation.kInt32);
                Load(c.Inputs[1], ValueRepresentation.kInt32);
                if (c.Unsigned && c.Operation == CompareOperation.kLessThan)
                {
                    _il.Emit(OpCodes.Clt_Un);
                    return;
                }
                if (c.Unsigned) throw new InvalidOperationException("unsigned compare " + c.Operation);
                EmitCompare(c.Operation, isFloat: false);
                return;
            case Opcode.BranchIfFloat64Compare:
                Load(c.Inputs[0], ValueRepresentation.kFloat64);
                Load(c.Inputs[1], ValueRepresentation.kFloat64);
                EmitCompare(c.Operation, isFloat: true);
                return;
            case Opcode.BranchIfReferenceEqual:
                Load(c.Inputs[0], ValueRepresentation.kTagged);
                Load(c.Inputs[1], ValueRepresentation.kTagged);
                _il.Emit(OpCodes.Call, B(nameof(MaglevBuiltins.IsIdentical)));
                return;
            case Opcode.BranchIfRootConstant:
            {
                Load(c.Inputs[0], ValueRepresentation.kTagged);
                _il.Emit(OpCodes.Ldfld, s_obj);
                switch ((RootIndex)c.Int1)
                {
                    case RootIndex.kUndefinedValue:
                        _il.Emit(OpCodes.Ldnull);
                        break;
                    case RootIndex.kNullValue:
                        _il.Emit(OpCodes.Ldsfld, s_null);
                        _il.Emit(OpCodes.Ldfld, s_obj);
                        break;
                    case RootIndex.kTrueValue:
                        _il.Emit(OpCodes.Ldsfld, s_true);
                        _il.Emit(OpCodes.Ldfld, s_obj);
                        break;
                    case RootIndex.kFalseValue:
                        _il.Emit(OpCodes.Ldsfld, s_false);
                        _il.Emit(OpCodes.Ldfld, s_obj);
                        break;
                    default:
                        _il.Emit(OpCodes.Ldsfld, s_oddballTheHole);
                        break;
                }
                _il.Emit(OpCodes.Ceq);
                return;
            }
            case Opcode.BranchIfUndefinedOrNull:
                Load(c.Inputs[0], ValueRepresentation.kTagged);
                _il.Emit(OpCodes.Call, B(nameof(MaglevBuiltins.IsUndefinedOrNull)));
                return;
            case Opcode.BranchIfJSReceiver:
                Load(c.Inputs[0], ValueRepresentation.kTagged);
                _il.Emit(OpCodes.Call, B(nameof(MaglevBuiltins.IsJSReceiver)));
                return;
            case Opcode.BranchIfInt32ToBooleanTrue:
                Load(c.Inputs[0], ValueRepresentation.kInt32);
                _il.Emit(OpCodes.Ldc_I4_0);
                _il.Emit(OpCodes.Cgt_Un);
                return;
            case Opcode.BranchIfFloat64ToBooleanTrue:
                Load(c.Inputs[0], ValueRepresentation.kFloat64);
                _il.Emit(OpCodes.Call, B(nameof(MaglevBuiltins.Float64ToBoolean)));
                return;
            default:
                throw new InvalidOperationException("unknown control node " + c.Opcode);
        }
    }

    /// <summary>Pushes the bool of a comparison of the two values on the stack (IEEE semantics for floats).</summary>
    void EmitCompare(CompareOperation op, bool isFloat)
    {
        switch (op)
        {
            case CompareOperation.kEqual:
            case CompareOperation.kStrictEqual:
                _il.Emit(OpCodes.Ceq);
                break;
            case CompareOperation.kLessThan:
                _il.Emit(OpCodes.Clt);
                break;
            case CompareOperation.kGreaterThan:
                _il.Emit(OpCodes.Cgt);
                break;
            case CompareOperation.kLessThanOrEqual:
                // a <= b == !(a > b or unordered).
                _il.Emit(isFloat ? OpCodes.Cgt_Un : OpCodes.Cgt);
                _il.Emit(OpCodes.Ldc_I4_0);
                _il.Emit(OpCodes.Ceq);
                break;
            default:
                // a >= b == !(a < b or unordered).
                _il.Emit(isFloat ? OpCodes.Clt_Un : OpCodes.Clt);
                _il.Emit(OpCodes.Ldc_I4_0);
                _il.Emit(OpCodes.Ceq);
                break;
        }
    }

    // ---- Deopt exits ----------------------------------------------------------------------------------------

    /// <summary>The label of the eager deopt exit for <paramref name="info"/> (shared by identical frames and reasons).</summary>
    Label EagerExit(EagerDeoptInfo info)
    {
        // The stub passes the reason and, in its high bits, the speculation feedback to update.
        int feedback = 0;
        if (info.FeedbackToUpdate is { } vector)
        {
            int i = _speculationFeedback.IndexOf((vector, info.FeedbackSlotToUpdate));
            if (i < 0)
            {
                i = _speculationFeedback.Count;
                _speculationFeedback.Add((vector, info.FeedbackSlotToUpdate));
            }
            feedback = i + 1;
        }
        if (_eagerExits.TryGetValue((info.TopFrame, info.Reason, feedback), out Label stub)) return stub;
        if (!_frameExits.TryGetValue(info.TopFrame, out Label exit))
        {
            exit = _il.DefineLabel();
            _frameExits[info.TopFrame] = exit;
            _pendingExits.Add((exit, info, DeoptimizeKind.kEager, info.Reason, null));
        }
        stub = _il.DefineLabel();
        _eagerExits[(info.TopFrame, info.Reason, feedback)] = stub;
        _eagerStubs.Add((stub, exit, (int)info.Reason | feedback << 16));
        return stub;
    }

    /// <summary>After a call: deoptimize lazily if the code was invalidated meanwhile.</summary>
    void EmitLazyDeoptCheck(Node node, ValueNode? result)
    {
        LazyDeoptInfo info = node.LazyDeoptInfo!;
        Label exit = _il.DefineLabel();
        _pendingExits.Add((exit, info, DeoptimizeKind.kLazy, DeoptimizeReason.kUnknown, result));
        _il.Emit(OpCodes.Ldarg_0);
        _il.Emit(OpCodes.Ldfld, s_markedForDeoptimization);
        _il.Emit(OpCodes.Brtrue, exit);
    }

    void EmitDeoptExits()
    {
        _deoptReason ??= _il.DeclareLocal(typeof(int));
        foreach ((Label stub, Label exit, int reason) in _eagerStubs)
        {
            _il.MarkLabel(stub);
            _il.Emit(OpCodes.Ldc_I4, reason);
            _il.Emit(OpCodes.Stloc, _deoptReason);
            _il.Emit(OpCodes.Br, exit);
        }
        foreach ((Label label, DeoptInfo info, DeoptimizeKind kind, DeoptimizeReason reason, ValueNode? result) in _pendingExits)
        {
            _il.MarkLabel(label);
            var spill = new List<ValueNode?>();
            var point = new DeoptPoint { Kind = kind, Reason = reason };
            // Frames outermost first.
            var frames = new List<InterpretedDeoptFrame>();
            for (DeoptFrame? f = info.TopFrame; f is not null; f = f.Parent) frames.Add((InterpretedDeoptFrame)f);
            frames.Reverse();
            var data = new DeoptFrameData[frames.Count];
            int scratch = 0;
            for (int i = 0; i < frames.Count; i++)
            {
                InterpretedDeoptFrame f = frames[i];
                MaglevCompilationUnit unit = f.Unit;
                var registers = new Register[f.Values.Length];
                ArgumentsObjectKind[]? materialize = null;
                for (int k = 0; k < f.Values.Length; k++)
                {
                    registers[k] = f.Values[k].Register;
                    ValueNode value = f.Values[k].Value;
                    if (IsElidedArguments(value))
                    {
                        // The Deoptimizer creates the elided arguments object.
                        (materialize ??= new ArgumentsObjectKind[f.Values.Length])[k] = ((CallBuiltinInfo)value.Obj0!).ArgumentsKind;
                        spill.Add(null);
                        continue;
                    }
                    spill.Add(value);
                }
                data[i] = new DeoptFrameData
                {
                    InliningDepth = unit.InliningDepth,
                    Argc = unit.Argc,
                    IsConstruct = unit.IsConstruct,
                    Function = unit.Function ?? _info.Function,
                    Bytecode = unit.Bytecode,
                    FeedbackVector = unit.Feedback,
                    BytecodeOffset = f.BytecodeOffset,
                    NextOffset = f.NextOffset,
                    Registers = registers,
                    Materialize = materialize,
                    ScratchStart = scratch,
                };
                scratch += registers.Length;
            }
            if (kind == DeoptimizeKind.kLazy && info is LazyDeoptInfo lazy)
            {
                point.ResultLocation = lazy.ResultLocation;
                if (result is not null && lazy.ResultSize == 1)
                {
                    point.ResultScratchIndex = scratch;
                    spill.Add(result);
                    scratch++;
                }
            }
            _spilledValues += spill.Count;
            EmitSpill(spill);
            point.Frames = data;
            point.ScratchSize = scratch;
            _maxScratch = Math.Max(_maxScratch, scratch);
            int index = _deoptPoints.Count;
            _deoptPoints.Add(point);
            info.DeoptIndex = index;
            // Deoptimizer::Deoptimize(isolate, ref state, code, index, reason); return to MaglevExecution.Run.
            _il.Emit(OpCodes.Ldarg_1);
            _il.Emit(OpCodes.Ldarg_2);
            _il.Emit(OpCodes.Ldarg_0);
            _il.Emit(OpCodes.Ldc_I4, index);
            if (kind == DeoptimizeKind.kEager) _il.Emit(OpCodes.Ldloc, _deoptReason);
            else _il.Emit(OpCodes.Ldc_I4, (int)reason);
            _il.Emit(OpCodes.Call, s_deoptimize);
            LoadUndefined();
            EmitReturn();
        }
    }

    /// <summary>
    /// Stores <paramref name="values"/> to the deopt scratch buffer from index
    /// 0, in chunks through MaglevBuiltins.Spill* (an elided value, null, is
    /// left for the Deoptimizer): a few bytes of IL per value, so the exits
    /// do not dominate the method's IL size.
    /// </summary>
    void EmitSpill(List<ValueNode?> values)
    {
        int i = 0;
        while (i < values.Count)
        {
            int remaining = values.Count - i;
            int chunk = remaining >= 8 ? 8 : remaining >= 4 ? 4 : remaining >= 2 ? 2 : 1;
            _il.Emit(OpCodes.Ldarg_1);
            _il.Emit(OpCodes.Ldfld, s_deoptScratch);
            _il.Emit(OpCodes.Ldc_I4, i);
            for (int k = 0; k < chunk; k++)
            {
                if (values[i + k] is { } value) Load(value, ValueRepresentation.kTagged);
                else LoadUndefined();
            }
            Call("Spill" + chunk);
            i += chunk;
        }
    }

    void EmitStoreScratch(int index, ValueNode value)
    {
        _il.Emit(OpCodes.Ldarg_1);
        _il.Emit(OpCodes.Ldfld, s_deoptScratch);
        _il.Emit(OpCodes.Ldc_I4, index);
        _il.Emit(OpCodes.Ldelema, typeof(JSValue));
        Load(value, ValueRepresentation.kTagged);
        _il.Emit(OpCodes.Stobj, typeof(JSValue));
    }

    // ---- Nodes --------------------------------------------------------------------------------------------

    void Call(string helper) => _il.Emit(OpCodes.Call, B(helper));

    void DeoptIfFalse(Node node) => _il.Emit(OpCodes.Brfalse, EagerExit(node.EagerDeoptInfo!));

    void DeoptIfTrue(Node node) => _il.Emit(OpCodes.Brtrue, EagerExit(node.EagerDeoptInfo!));

    void EmitNode(Node node)
    {
        var v = node as ValueNode;
        switch (node.Opcode)
        {
            // ---- Values ---------------------------------------------------------------------------
            case Opcode.InitialValue:
                if (node.Int0 is InterpreterRuntime.kClosureOffset or InterpreterRuntime.kContextOffset)
                {
                    // The closure and the context come from the state (MaglevCalls
                    // does not write their frame slots).
                    _il.Emit(OpCodes.Ldarg_2);
                    _il.Emit(OpCodes.Ldfld, node.Int0 == InterpreterRuntime.kClosureOffset ? s_stateFunction : s_stateContext);
                    _il.Emit(OpCodes.Call, s_jsValueFromObject);
                    Store(v!);
                    return;
                }
                LoadFrameSlotAddress(null, node.Int0);
                _il.Emit(OpCodes.Ldobj, typeof(JSValue));
                Store(v!);
                return;
            case Opcode.StoreRegister:
                LoadFrameSlotAddress(node.Unit, node.Int0);
                Load(node.Inputs[0], ValueRepresentation.kTagged);
                _il.Emit(OpCodes.Stobj, typeof(JSValue));
                return;
            case Opcode.LoadRegister:
                LoadFrameSlotAddress(node.Unit, node.Int0);
                _il.Emit(OpCodes.Ldobj, typeof(JSValue));
                Store(v!);
                return;
            case Opcode.Identity:
                Load(node.Inputs[0], v!.Representation);
                Store(v);
                return;

            // ---- Int32 ----------------------------------------------------------------------------
            case Opcode.Int32AddWithOverflow:
            case Opcode.Int32SubtractWithOverflow:
            case Opcode.Int32MultiplyWithOverflow:
                EmitInt32Overflowing(node);
                return;
            case Opcode.Int32DivideWithOverflow:
            case Opcode.Int32ModulusWithOverflow:
                Load(node.Inputs[0], ValueRepresentation.kInt32);
                Load(node.Inputs[1], ValueRepresentation.kInt32);
                Call(node.Opcode == Opcode.Int32DivideWithOverflow ? nameof(MaglevBuiltins.Int32Divide) : nameof(MaglevBuiltins.Int32Modulus));
                _il.Emit(OpCodes.Stloc, _tmpLong);
                _il.Emit(OpCodes.Ldloc, _tmpLong);
                _il.Emit(OpCodes.Ldc_I8, long.MinValue);
                _il.Emit(OpCodes.Beq, EagerExit(node.EagerDeoptInfo!));
                _il.Emit(OpCodes.Ldloc, _tmpLong);
                _il.Emit(OpCodes.Conv_I4);
                Store(v!);
                return;
            case Opcode.Int32IncrementWithOverflow:
            case Opcode.Int32DecrementWithOverflow:
                Load(node.Inputs[0], ValueRepresentation.kInt32);
                _il.Emit(OpCodes.Ldc_I4, node.Opcode == Opcode.Int32IncrementWithOverflow ? int.MaxValue : int.MinValue);
                _il.Emit(OpCodes.Beq, EagerExit(node.EagerDeoptInfo!));
                Load(node.Inputs[0], ValueRepresentation.kInt32);
                _il.Emit(OpCodes.Ldc_I4_1);
                _il.Emit(node.Opcode == Opcode.Int32IncrementWithOverflow ? OpCodes.Add : OpCodes.Sub);
                Store(v!);
                return;
            case Opcode.Int32NegateWithOverflow:
                // 0 negates to -0, kMinInt overflows.
                Load(node.Inputs[0], ValueRepresentation.kInt32);
                _il.Emit(OpCodes.Brfalse, EagerExit(node.EagerDeoptInfo!));
                Load(node.Inputs[0], ValueRepresentation.kInt32);
                _il.Emit(OpCodes.Ldc_I4, int.MinValue);
                _il.Emit(OpCodes.Beq, EagerExit(node.EagerDeoptInfo!));
                Load(node.Inputs[0], ValueRepresentation.kInt32);
                _il.Emit(OpCodes.Neg);
                Store(v!);
                return;
            case Opcode.Int32AbsWithOverflow:
                Load(node.Inputs[0], ValueRepresentation.kInt32);
                _il.Emit(OpCodes.Ldc_I4, int.MinValue);
                _il.Emit(OpCodes.Beq, EagerExit(node.EagerDeoptInfo!));
                Load(node.Inputs[0], ValueRepresentation.kInt32);
                _il.Emit(OpCodes.Call, typeof(Math).GetMethod(nameof(Math.Abs), [typeof(int)])!);
                Store(v!);
                return;
            case Opcode.Int32BitwiseAnd:
            case Opcode.Int32BitwiseOr:
            case Opcode.Int32BitwiseXor:
                Load(node.Inputs[0], ValueRepresentation.kInt32);
                Load(node.Inputs[1], ValueRepresentation.kInt32);
                _il.Emit(node.Opcode == Opcode.Int32BitwiseAnd ? OpCodes.And : node.Opcode == Opcode.Int32BitwiseOr ? OpCodes.Or : OpCodes.Xor);
                Store(v!);
                return;
            case Opcode.Int32ShiftLeft:
            case Opcode.Int32ShiftRight:
            case Opcode.Int32ShiftRightLogical:
                Load(node.Inputs[0], ValueRepresentation.kInt32);
                Load(node.Inputs[1], ValueRepresentation.kInt32);
                _il.Emit(OpCodes.Ldc_I4, 31);
                _il.Emit(OpCodes.And);
                _il.Emit(node.Opcode == Opcode.Int32ShiftLeft ? OpCodes.Shl : node.Opcode == Opcode.Int32ShiftRight ? OpCodes.Shr : OpCodes.Shr_Un);
                Store(v!);
                return;
            case Opcode.Int32BitwiseNot:
                Load(node.Inputs[0], ValueRepresentation.kInt32);
                _il.Emit(OpCodes.Not);
                Store(v!);
                return;
            case Opcode.Int32Compare:
                Load(node.Inputs[0], ValueRepresentation.kInt32);
                Load(node.Inputs[1], ValueRepresentation.kInt32);
                EmitCompare((CompareOperation)node.Int0, isFloat: false);
                Call(nameof(MaglevBuiltins.Boolean));
                Store(v!);
                return;
            case Opcode.Int32ToBoolean:
                Load(node.Inputs[0], ValueRepresentation.kInt32);
                _il.Emit(OpCodes.Ldc_I4_0);
                _il.Emit(node.Int0 != 0 ? OpCodes.Ceq : OpCodes.Cgt_Un);
                Call(nameof(MaglevBuiltins.Boolean));
                Store(v!);
                return;

            // ---- Float64 ----------------------------------------------------------------------------
            case Opcode.Float64Add:
            case Opcode.Float64Subtract:
            case Opcode.Float64Multiply:
            case Opcode.Float64Divide:
            case Opcode.Float64Modulus:
                Load(node.Inputs[0], ValueRepresentation.kFloat64);
                Load(node.Inputs[1], ValueRepresentation.kFloat64);
                _il.Emit(node.Opcode switch
                {
                    Opcode.Float64Add => OpCodes.Add,
                    Opcode.Float64Subtract => OpCodes.Sub,
                    Opcode.Float64Multiply => OpCodes.Mul,
                    Opcode.Float64Divide => OpCodes.Div,
                    _ => OpCodes.Rem,
                });
                Store(v!);
                return;
            case Opcode.Float64Exponentiate:
                Load(node.Inputs[0], ValueRepresentation.kFloat64);
                Load(node.Inputs[1], ValueRepresentation.kFloat64);
                Call(nameof(MaglevBuiltins.Float64Exponentiate));
                Store(v!);
                return;
            case Opcode.Float64Negate:
                Load(node.Inputs[0], ValueRepresentation.kFloat64);
                _il.Emit(OpCodes.Neg);
                Store(v!);
                return;
            case Opcode.Float64Abs:
                Load(node.Inputs[0], ValueRepresentation.kFloat64);
                _il.Emit(OpCodes.Call, typeof(Math).GetMethod(nameof(Math.Abs), [typeof(double)])!);
                Store(v!);
                return;
            case Opcode.Float64Sqrt:
                Load(node.Inputs[0], ValueRepresentation.kFloat64);
                _il.Emit(OpCodes.Call, typeof(Math).GetMethod(nameof(Math.Sqrt), [typeof(double)])!);
                Store(v!);
                return;
            case Opcode.Float64Round:
                Load(node.Inputs[0], ValueRepresentation.kFloat64);
                _il.Emit(OpCodes.Ldc_I4, node.Int0);
                Call(nameof(MaglevBuiltins.Float64Round));
                Store(v!);
                return;
            case Opcode.Float64Min:
            case Opcode.Float64Max:
                Load(node.Inputs[0], ValueRepresentation.kFloat64);
                Load(node.Inputs[1], ValueRepresentation.kFloat64);
                Call(node.Opcode == Opcode.Float64Min ? nameof(MaglevBuiltins.Float64Min) : nameof(MaglevBuiltins.Float64Max));
                Store(v!);
                return;
            case Opcode.Float64Ieee754Unary:
                Load(node.Inputs[0], ValueRepresentation.kFloat64);
                _il.Emit(OpCodes.Ldc_I4, node.Int0);
                Call(nameof(MaglevBuiltins.Float64Ieee754Unary));
                Store(v!);
                return;
            case Opcode.Float64Ieee754Binary:
                Load(node.Inputs[0], ValueRepresentation.kFloat64);
                Load(node.Inputs[1], ValueRepresentation.kFloat64);
                Call(nameof(MaglevBuiltins.Float64Atan2));
                Store(v!);
                return;
            case Opcode.Float64Compare:
                Load(node.Inputs[0], ValueRepresentation.kFloat64);
                Load(node.Inputs[1], ValueRepresentation.kFloat64);
                EmitCompare((CompareOperation)node.Int0, isFloat: true);
                Call(nameof(MaglevBuiltins.Boolean));
                Store(v!);
                return;
            case Opcode.Float64ToBoolean:
                Load(node.Inputs[0], ValueRepresentation.kFloat64);
                Call(nameof(MaglevBuiltins.Float64ToBoolean));
                if (node.Int0 != 0)
                {
                    _il.Emit(OpCodes.Ldc_I4_0);
                    _il.Emit(OpCodes.Ceq);
                }
                Call(nameof(MaglevBuiltins.Boolean));
                Store(v!);
                return;

            // ---- Conversions ---------------------------------------------------------------------------
            case Opcode.CheckedSmiUntag:
            case Opcode.CheckedNumberToInt32:
                EmitCheckedTaggedToInt32(node);
                return;
            case Opcode.UnsafeSmiUntag:
                Load(node.Inputs[0], ValueRepresentation.kTagged);
                _il.Emit(OpCodes.Ldfld, s_num);
                _il.Emit(OpCodes.Conv_I4);
                Store(v!);
                return;
            case Opcode.CheckedNumberOrOddballToFloat64:
            {
                bool allowOddball = NodeTypes.CanBe((NodeType)node.Int0, NodeType.kOddball);
                Load(node.Inputs[0], ValueRepresentation.kTagged);
                if (allowOddball)
                {
                    if ((NodeType)node.Int0 == NodeType.kNumberOrBoolean) Call(nameof(MaglevBuiltins.IsNumberOrBoolean));
                    else Call(nameof(MaglevBuiltins.IsNumberOrOddball));
                    DeoptIfFalse(node);
                    Load(node.Inputs[0], ValueRepresentation.kTagged);
                    Call(nameof(MaglevBuiltins.NumberOrOddballToFloat64));
                }
                else
                {
                    _il.Emit(OpCodes.Ldfld, s_obj);
                    _il.Emit(OpCodes.Ldsfld, s_numberTag);
                    _il.Emit(OpCodes.Bne_Un, EagerExit(node.EagerDeoptInfo!));
                    Load(node.Inputs[0], ValueRepresentation.kTagged);
                    _il.Emit(OpCodes.Ldfld, s_num);
                }
                Store(v!);
                return;
            }
            case Opcode.UnsafeNumberToFloat64:
                Load(node.Inputs[0], ValueRepresentation.kTagged);
                _il.Emit(OpCodes.Ldfld, s_num);
                Store(v!);
                return;
            case Opcode.Int32ToNumber:
            case Opcode.Uint32ToNumber:
            case Opcode.Float64ToTagged:
            case Opcode.HoleyFloat64ToTagged:
                Load(node.Inputs[0]);
                EmitTag(node.Inputs[0].IsConstant ? ConstantRepresentationFor(node.Opcode) : node.Inputs[0].Representation);
                Store(v!);
                return;
            case Opcode.ChangeInt32ToFloat64:
                Load(node.Inputs[0], ValueRepresentation.kInt32);
                _il.Emit(OpCodes.Conv_R8);
                Store(v!);
                return;
            case Opcode.ChangeUint32ToFloat64:
                Load(node.Inputs[0], ValueRepresentation.kUint32);
                _il.Emit(OpCodes.Conv_R_Un);
                _il.Emit(OpCodes.Conv_R8);
                Store(v!);
                return;
            case Opcode.CheckedFloat64ToInt32:
                Load(node.Inputs[0], ValueRepresentation.kFloat64);
                _il.Emit(OpCodes.Stloc, _tmpDouble);
                EmitCheckedFloat64ToInt32(node);
                Store(v!);
                return;
            case Opcode.TruncateFloat64ToInt32:
                Load(node.Inputs[0], ValueRepresentation.kFloat64);
                Call(nameof(MaglevBuiltins.TruncateFloat64ToInt32));
                Store(v!);
                return;
            case Opcode.TruncateCheckedNumberOrOddballToInt32:
            {
                bool allowOddball = NodeTypes.CanBe((NodeType)node.Int0, NodeType.kOddball);
                Load(node.Inputs[0], ValueRepresentation.kTagged);
                Call(!allowOddball ? nameof(MaglevBuiltins.IsNumber)
                    : (NodeType)node.Int0 == NodeType.kNumberOrBoolean ? nameof(MaglevBuiltins.IsNumberOrBoolean)
                    : nameof(MaglevBuiltins.IsNumberOrOddball));
                DeoptIfFalse(node);
                Load(node.Inputs[0], ValueRepresentation.kTagged);
                Call(nameof(MaglevBuiltins.NumberOrOddballToFloat64));
                Call(nameof(MaglevBuiltins.TruncateFloat64ToInt32));
                Store(v!);
                return;
            }
            case Opcode.CheckedUint32ToInt32:
                Load(node.Inputs[0], ValueRepresentation.kUint32);
                _il.Emit(OpCodes.Ldc_I4_0);
                _il.Emit(OpCodes.Blt, EagerExit(node.EagerDeoptInfo!));
                Load(node.Inputs[0], ValueRepresentation.kUint32);
                Store(v!);
                return;
            case Opcode.CheckedHoleyFloat64ToFloat64:
                Load(node.Inputs[0], ValueRepresentation.kHoleyFloat64);
                Call(nameof(MaglevBuiltins.IsHoleNaN));
                DeoptIfTrue(node);
                Load(node.Inputs[0], ValueRepresentation.kHoleyFloat64);
                Store(v!);
                return;

            // ---- Checks ---------------------------------------------------------------------------------
            case Opcode.CheckSmi:
                Load(node.Inputs[0], ValueRepresentation.kTagged);
                Call(nameof(MaglevBuiltins.IsSmi));
                DeoptIfFalse(node);
                return;
            case Opcode.CheckNumber:
                Load(node.Inputs[0], ValueRepresentation.kTagged);
                _il.Emit(OpCodes.Ldfld, s_obj);
                _il.Emit(OpCodes.Ldsfld, s_numberTag);
                _il.Emit(OpCodes.Bne_Un, EagerExit(node.EagerDeoptInfo!));
                return;
            case Opcode.CheckHeapObject:
                Load(node.Inputs[0], ValueRepresentation.kTagged);
                Call(nameof(MaglevBuiltins.IsHeapObject));
                DeoptIfFalse(node);
                return;
            case Opcode.CheckString:
                Load(node.Inputs[0], ValueRepresentation.kTagged);
                Call(nameof(MaglevBuiltins.IsString));
                DeoptIfFalse(node);
                return;
            case Opcode.CheckSymbol:
                Load(node.Inputs[0], ValueRepresentation.kTagged);
                Call(nameof(MaglevBuiltins.IsSymbol));
                DeoptIfFalse(node);
                return;
            case Opcode.CheckInstanceType:
                Load(node.Inputs[0], ValueRepresentation.kTagged);
                Call(node.Int0 switch
                {
                    1 => nameof(MaglevBuiltins.IsInternalizedString),
                    2 => nameof(MaglevBuiltins.IsJSReceiver),
                    _ => nameof(MaglevBuiltins.IsWritableElements),
                });
                DeoptIfFalse(node);
                return;
            case Opcode.TransitionElementsKind:
                _il.Emit(OpCodes.Ldarg_1);
                Load(node.Inputs[0], ValueRepresentation.kTagged);
                LoadConstantObject(node.Obj0, typeof(Map));
                Call(nameof(MaglevBuiltins.TransitionElementsKind));
                DeoptIfFalse(node);
                return;
            case Opcode.CheckValueEqualsString:
                Load(node.Inputs[0], ValueRepresentation.kTagged);
                LoadConstantObject(node.Obj0, typeof(JSString));
                LoadConstantObject(node.Value0, typeof(JSValue));
                Call(nameof(MaglevBuiltins.ValueEqualsString));
                DeoptIfFalse(node);
                return;
            case Opcode.CheckValue:
                Load(node.Inputs[0], ValueRepresentation.kTagged);
                _il.Emit(OpCodes.Ldfld, s_obj);
                LoadConstantObject(node.Obj0, node.Obj0!.GetType());
                _il.Emit(OpCodes.Bne_Un, EagerExit(node.EagerDeoptInfo!));
                return;
            case Opcode.CheckMaps:
                EmitCheckMaps(node);
                return;
            case Opcode.CheckInt32IsSmi:
                Load(node.Inputs[0], ValueRepresentation.kInt32);
                _il.Emit(OpCodes.Ldc_I4, JSValue.SmiMinValue);
                _il.Emit(OpCodes.Blt, EagerExit(node.EagerDeoptInfo!));
                Load(node.Inputs[0], ValueRepresentation.kInt32);
                _il.Emit(OpCodes.Ldc_I4, JSValue.SmiMaxValue);
                _il.Emit(OpCodes.Bgt, EagerExit(node.EagerDeoptInfo!));
                return;
            case Opcode.CheckInt32Condition:
                // Deopt unless lhs < rhs (unsigned when Int1 is set).
                Load(node.Inputs[0], ValueRepresentation.kInt32);
                Load(node.Inputs[1], ValueRepresentation.kInt32);
                _il.Emit(node.Int1 != 0 ? OpCodes.Bge_Un : OpCodes.Bge, EagerExit(node.EagerDeoptInfo!));
                return;
            case Opcode.CheckNotHole:
                Load(node.Inputs[0], ValueRepresentation.kTagged);
                _il.Emit(OpCodes.Ldfld, s_obj);
                _il.Emit(OpCodes.Ldsfld, s_oddballTheHole);
                _il.Emit(OpCodes.Beq, EagerExit(node.EagerDeoptInfo!));
                return;
            case Opcode.CheckValidityCell:
                LoadConstantObject(node.Obj0, typeof(Cell));
                Call(nameof(MaglevBuiltins.IsValidCell));
                DeoptIfFalse(node);
                return;

            // ---- Loads and stores ----------------------------------------------------------------------
            case Opcode.LoadMap:
            {
                Label notReceiver = _il.DefineLabel(), done = _il.DefineLabel();
                EmitLoadMapOrBranch(node.Inputs[0], notReceiver);
                _il.Emit(OpCodes.Br, done);
                _il.MarkLabel(notReceiver);
                _il.Emit(OpCodes.Ldnull);
                _il.MarkLabel(done);
                _il.Emit(OpCodes.Call, s_jsValueFromObject);
                Store(v!);
                return;
            }
            case Opcode.LoadTaggedField:
                if (TryLoadFieldAddress(node.Inputs[0], node.Int0))
                {
                    _il.Emit(OpCodes.Ldobj, typeof(JSValue));
                }
                else
                {
                    Load(node.Inputs[0], ValueRepresentation.kTagged);
                    _il.Emit(OpCodes.Ldc_I4, node.Int0);
                    Call(nameof(MaglevBuiltins.LoadField));
                }
                Store(v!);
                return;
            case Opcode.StoreTaggedField:
                if (node.Int1 == 0 && TryLoadFieldAddress(node.Inputs[0], node.Int0))
                {
                    Load(node.Inputs[1], ValueRepresentation.kTagged);
                    _il.Emit(OpCodes.Stobj, typeof(JSValue));
                    return;
                }
                Load(node.Inputs[0], ValueRepresentation.kTagged);
                _il.Emit(OpCodes.Ldc_I4, node.Int0);
                Load(node.Inputs[1], ValueRepresentation.kTagged);
                Call(node.Int1 != 0 ? nameof(MaglevBuiltins.StoreDoubleField) : nameof(MaglevBuiltins.StoreField));
                return;
            case Opcode.StoreMapTransition:
                Load(node.Inputs[0], ValueRepresentation.kTagged);
                LoadConstantObject(node.Obj0, typeof(Map));
                _il.Emit(OpCodes.Ldc_I4, node.Int0);
                Load(node.Inputs[1], ValueRepresentation.kTagged);
                _il.Emit(node.Int1 != 0 ? OpCodes.Ldc_I4_1 : OpCodes.Ldc_I4_0);
                Call(nameof(MaglevBuiltins.StoreTransition));
                return;
            case Opcode.LoadElements:
                Load(node.Inputs[0], ValueRepresentation.kTagged);
                Call(nameof(MaglevBuiltins.LoadElements));
                Store(v!);
                return;
            case Opcode.LoadFixedArrayElement:
                Load(node.Inputs[0], ValueRepresentation.kTagged);
                Load(node.Inputs[1], ValueRepresentation.kInt32);
                Call(nameof(MaglevBuiltins.LoadFixedArrayElement));
                Store(v!);
                return;
            case Opcode.ConvertHoleToUndefined:
                Load(node.Inputs[0], ValueRepresentation.kTagged);
                Call(nameof(MaglevBuiltins.ConvertHoleToUndefined));
                Store(v!);
                return;
            case Opcode.LoadFixedDoubleArrayElement:
            case Opcode.LoadHoleyFixedDoubleArrayElement:
                Load(node.Inputs[0], ValueRepresentation.kTagged);
                Load(node.Inputs[1], ValueRepresentation.kInt32);
                Call(nameof(MaglevBuiltins.LoadFixedDoubleArrayElement));
                Store(v!);
                return;
            case Opcode.StoreFixedArrayElement:
                Load(node.Inputs[0], ValueRepresentation.kTagged);
                Load(node.Inputs[1], ValueRepresentation.kInt32);
                Load(node.Inputs[2], ValueRepresentation.kTagged);
                Call(nameof(MaglevBuiltins.StoreFixedArrayElement));
                return;
            case Opcode.StoreFixedDoubleArrayElement:
                Load(node.Inputs[0], ValueRepresentation.kTagged);
                Load(node.Inputs[1], ValueRepresentation.kInt32);
                Load(node.Inputs[2], ValueRepresentation.kFloat64);
                Call(nameof(MaglevBuiltins.StoreFixedDoubleArrayElement));
                return;
            case Opcode.LoadJSArrayLength:
                Load(node.Inputs[0], ValueRepresentation.kTagged);
                Call(nameof(MaglevBuiltins.LoadJSArrayLength));
                Store(v!);
                return;
            case Opcode.LoadFixedArrayLength:
                Load(node.Inputs[0], ValueRepresentation.kTagged);
                Call(nameof(MaglevBuiltins.LoadFixedArrayLength));
                Store(v!);
                return;
            case Opcode.LoadContextSlot:
                Load(node.Inputs[0], ValueRepresentation.kTagged);
                _il.Emit(OpCodes.Ldc_I4, node.Int1);
                _il.Emit(OpCodes.Ldc_I4, node.Int0);
                Call(nameof(MaglevBuiltins.LoadContextSlot));
                Store(v!);
                return;
            case Opcode.StoreContextSlot:
                Load(node.Inputs[0], ValueRepresentation.kTagged);
                _il.Emit(OpCodes.Ldc_I4, node.Int1);
                _il.Emit(OpCodes.Ldc_I4, node.Int0);
                Load(node.Inputs[1], ValueRepresentation.kTagged);
                Call(nameof(MaglevBuiltins.StoreContextSlot));
                return;
            case Opcode.StringLength:
                Load(node.Inputs[0], ValueRepresentation.kTagged);
                Call(nameof(MaglevBuiltins.StringLength));
                Store(v!);
                return;
            case Opcode.BuiltinStringPrototypeCharCodeAt:
                Load(node.Inputs[0], ValueRepresentation.kTagged);
                Load(node.Inputs[1], ValueRepresentation.kInt32);
                Call(nameof(MaglevBuiltins.StringCharCodeAt));
                Store(v!);
                return;
            case Opcode.BuiltinStringPrototypeCharCodeAtOrNaN:
                Load(node.Inputs[0], ValueRepresentation.kTagged);
                Load(node.Inputs[1], ValueRepresentation.kInt32);
                Call(nameof(MaglevBuiltins.StringCharCodeAtOrNaN));
                Store(v!);
                return;
            case Opcode.CheckedObjectToIndex:
                Load(node.Inputs[0], ValueRepresentation.kTagged);
                _il.Emit(OpCodes.Ldloca, _tmpInt);
                Call(nameof(MaglevBuiltins.TryObjectToIndex));
                DeoptIfFalse(node);
                _il.Emit(OpCodes.Ldloc, _tmpInt);
                Store(v!);
                return;
            case Opcode.LoadPropertyCellValue:
                LoadConstantObject(node.Obj0, typeof(PropertyCell));
                _il.Emit(OpCodes.Ldfld, s_propertyCellValue);
                Store(v!);
                return;
            case Opcode.StorePropertyCellValue:
                LoadConstantObject(node.Obj0, typeof(PropertyCell));
                Load(node.Inputs[0], ValueRepresentation.kTagged);
                _il.Emit(OpCodes.Stfld, s_propertyCellValue);
                return;
            case Opcode.MaybeGrowFastElements:
                _il.Emit(OpCodes.Ldarg_1);
                Load(node.Inputs[0], ValueRepresentation.kTagged);
                Load(node.Inputs[1], ValueRepresentation.kInt32);
                _il.Emit(OpCodes.Ldc_I4, node.Int0);
                _il.Emit(OpCodes.Ldc_I4, node.Int1);
                Call(nameof(MaglevBuiltins.MaybeGrowFastElements));
                _il.Emit(OpCodes.Stloc, _tmpValue);
                _il.Emit(OpCodes.Ldloc, _tmpValue);
                _il.Emit(OpCodes.Ldfld, s_obj);
                _il.Emit(OpCodes.Brfalse, EagerExit(node.EagerDeoptInfo!));
                _il.Emit(OpCodes.Ldloc, _tmpValue);
                Store(v!);
                return;

            // ---- Operations ------------------------------------------------------------------------------
            case Opcode.TaggedEqual:
                Load(node.Inputs[0], ValueRepresentation.kTagged);
                Load(node.Inputs[1], ValueRepresentation.kTagged);
                Call(nameof(MaglevBuiltins.TaggedEqual));
                Store(v!);
                return;
            case Opcode.ToBoolean:
            case Opcode.ToBooleanLogicalNot:
                Load(node.Inputs[0], ValueRepresentation.kTagged);
                Call(nameof(MaglevBuiltins.ToBoolean));
                if (node.Opcode == Opcode.ToBooleanLogicalNot)
                {
                    _il.Emit(OpCodes.Ldc_I4_0);
                    _il.Emit(OpCodes.Ceq);
                }
                Call(nameof(MaglevBuiltins.Boolean));
                Store(v!);
                return;
            case Opcode.LogicalNot:
                Load(node.Inputs[0], ValueRepresentation.kTagged);
                Call(nameof(MaglevBuiltins.IsTrue));
                _il.Emit(OpCodes.Ldc_I4_0);
                _il.Emit(OpCodes.Ceq);
                Call(nameof(MaglevBuiltins.Boolean));
                Store(v!);
                return;
            case Opcode.TestUndetectable:
                Load(node.Inputs[0], ValueRepresentation.kTagged);
                Call(nameof(MaglevBuiltins.TestUndetectable));
                Store(v!);
                return;
            case Opcode.TestTypeOf:
                Load(node.Inputs[0], ValueRepresentation.kTagged);
                _il.Emit(OpCodes.Ldc_I4, node.Int0);
                Call(nameof(MaglevBuiltins.TestTypeOf));
                Store(v!);
                return;
            case Opcode.FastNewObject:
                _il.Emit(OpCodes.Ldarg_1);
                LoadConstantObject(node.Obj0, typeof(Map));
                Call(nameof(MaglevBuiltins.FastNewObject));
                Store(v!);
                return;
            case Opcode.HandleNoHeapWritesInterrupt:
                StoreBytecodeOffset(node);
                _il.Emit(OpCodes.Ldarg_1);
                Call(nameof(MaglevBuiltins.HandleInterrupts));
                return;
            case Opcode.SetCurrentContext:
                _il.Emit(OpCodes.Ldarg_1);
                LoadFrameSlotAddress(node.Unit, InterpreterRuntime.kContextOffset);
                Load(node.Inputs[0], ValueRepresentation.kTagged);
                Call(nameof(MaglevBuiltins.SetCurrentContext));
                return;
            case Opcode.EnterInlinedFrame:
                EmitEnterInlinedFrame(node);
                return;
            case Opcode.LeaveInlinedFrame:
            {
                var unit = (MaglevCompilationUnit)node.Obj0!;
                Label notPushed = _il.DefineLabel();
                if (!unit.EagerFrame)
                {
                    // A lazy frame: pop it only if the inlined code pushed it.
                    _il.Emit(OpCodes.Ldarg_1);
                    _il.Emit(OpCodes.Ldfld, s_interpreterFrameDepth);
                    _il.Emit(OpCodes.Ldloc, _baseFrameIndex);
                    _il.Emit(OpCodes.Ldc_I4, unit.InliningDepth);
                    _il.Emit(OpCodes.Add);
                    _il.Emit(OpCodes.Ble, notPushed);
                }
                _il.Emit(OpCodes.Ldarg_1);
                _il.Emit(OpCodes.Ldloc, _baseFrameIndex);
                _il.Emit(OpCodes.Ldc_I4, unit.InliningDepth);
                _il.Emit(OpCodes.Add);
                Load(node.Inputs[0], ValueRepresentation.kTagged);
                Call(nameof(MaglevBuiltins.LeaveInlinedFrame));
                _il.MarkLabel(notPushed);
                return;
            }
            case Opcode.CallBuiltin:
                EmitCallBuiltin(node);
                return;
            default:
                throw new MaglevBailoutException("no code generation for " + node.Opcode);
        }
    }

    static ValueRepresentation ConstantRepresentationFor(Opcode tagging) => tagging switch
    {
        Opcode.Int32ToNumber => ValueRepresentation.kInt32,
        Opcode.Uint32ToNumber => ValueRepresentation.kUint32,
        _ => ValueRepresentation.kFloat64,
    };

    /// <summary>Int32{Add,Subtract,Multiply}WithOverflow: 64-bit result, deopt unless it is an int32 (and not -0).</summary>
    void EmitInt32Overflowing(Node node)
    {
        Load(node.Inputs[0], ValueRepresentation.kInt32);
        _il.Emit(OpCodes.Conv_I8);
        Load(node.Inputs[1], ValueRepresentation.kInt32);
        _il.Emit(OpCodes.Conv_I8);
        _il.Emit(node.Opcode switch
        {
            Opcode.Int32AddWithOverflow => OpCodes.Add,
            Opcode.Int32SubtractWithOverflow => OpCodes.Sub,
            _ => OpCodes.Mul,
        });
        _il.Emit(OpCodes.Stloc, _tmpLong);
        Label exit = EagerExit(node.EagerDeoptInfo!);
        _il.Emit(OpCodes.Ldloc, _tmpLong);
        _il.Emit(OpCodes.Conv_I4);
        _il.Emit(OpCodes.Conv_I8);
        _il.Emit(OpCodes.Ldloc, _tmpLong);
        _il.Emit(OpCodes.Bne_Un, exit);
        if (node.Opcode == Opcode.Int32MultiplyWithOverflow)
        {
            // A zero product with a negative operand is -0.
            Label ok = _il.DefineLabel();
            _il.Emit(OpCodes.Ldloc, _tmpLong);
            _il.Emit(OpCodes.Brtrue, ok);
            Load(node.Inputs[0], ValueRepresentation.kInt32);
            Load(node.Inputs[1], ValueRepresentation.kInt32);
            _il.Emit(OpCodes.Or);
            _il.Emit(OpCodes.Ldc_I4_0);
            _il.Emit(OpCodes.Blt, exit);
            _il.MarkLabel(ok);
        }
        _il.Emit(OpCodes.Ldloc, _tmpLong);
        _il.Emit(OpCodes.Conv_I4);
        Store((ValueNode)node);
    }

    /// <summary>CheckedSmiUntag: a number that is an int32 and not -0.</summary>
    void EmitCheckedTaggedToInt32(Node node)
    {
        Label exit = EagerExit(node.EagerDeoptInfo!);
        Load(node.Inputs[0], ValueRepresentation.kTagged);
        _il.Emit(OpCodes.Ldfld, s_obj);
        _il.Emit(OpCodes.Ldsfld, s_numberTag);
        _il.Emit(OpCodes.Bne_Un, exit);
        Load(node.Inputs[0], ValueRepresentation.kTagged);
        _il.Emit(OpCodes.Ldfld, s_num);
        _il.Emit(OpCodes.Stloc, _tmpDouble);
        EmitCheckedFloat64ToInt32(node);
        Store((ValueNode)node);
    }

    /// <summary>The int32 of _tmpDouble, deoptimizing for a fraction, NaN, out of range or -0. Leaves the int on the stack.</summary>
    void EmitCheckedFloat64ToInt32(Node node)
    {
        Label exit = EagerExit(node.EagerDeoptInfo!);
        _il.Emit(OpCodes.Ldloc, _tmpDouble);
        _il.Emit(OpCodes.Conv_I4);
        _il.Emit(OpCodes.Stloc, _tmpInt);
        _il.Emit(OpCodes.Ldloc, _tmpInt);
        _il.Emit(OpCodes.Conv_R8);
        _il.Emit(OpCodes.Ldloc, _tmpDouble);
        _il.Emit(OpCodes.Bne_Un, exit);
        Label ok = _il.DefineLabel();
        _il.Emit(OpCodes.Ldloc, _tmpInt);
        _il.Emit(OpCodes.Brtrue, ok);
        _il.Emit(OpCodes.Ldloc, _tmpDouble);
        _il.Emit(OpCodes.Call, s_doubleToInt64Bits);
        _il.Emit(OpCodes.Brtrue, exit);
        _il.MarkLabel(ok);
        _il.Emit(OpCodes.Ldloc, _tmpInt);
    }

    /// <summary>
    /// Pushes the address of the field at <paramref name="storageIndex"/> of the
    /// map-checked JSObject <paramref name="obj"/> (JSObject.FieldAt inlined:
    /// a big method exceeds RyuJIT's inlining budget, so the helper would be a
    /// call). False when the layout needs the helper.
    /// </summary>
    bool TryLoadFieldAddress(ValueNode obj, int storageIndex)
    {
        if (storageIndex >= JSObject.kPropertyArrayStorageBase)
        {
            Load(obj, ValueRepresentation.kTagged);
            _il.Emit(OpCodes.Ldfld, s_obj);
            _il.Emit(OpCodes.Ldfld, s_jsObjectFields);
            _il.Emit(OpCodes.Ldc_I4, storageIndex - JSObject.kPropertyArrayStorageBase);
            _il.Emit(OpCodes.Ldelema, typeof(JSValue));
            return true;
        }
        if (!InObjectLayout.IsContiguous) return false;
        // The in-object slots are one run from JSObjectInObject1._slots0.
        Load(obj, ValueRepresentation.kTagged);
        _il.Emit(OpCodes.Ldfld, s_obj);
        _il.Emit(OpCodes.Ldflda, s_inObjectSlots0);
        if (storageIndex != 0)
        {
            _il.Emit(OpCodes.Ldc_I4, storageIndex * Unsafe.SizeOf<JSValue>());
            _il.Emit(OpCodes.Add);
        }
        return true;
    }

    /// <summary>
    /// Pushes the map of the tagged value <paramref name="value"/>, branching to
    /// <paramref name="notReceiver"/> when it is not a JSReceiver
    /// (MaglevBuiltins.MapOf inlined).
    /// </summary>
    void EmitLoadMapOrBranch(ValueNode value, Label notReceiver)
    {
        Load(value, ValueRepresentation.kTagged);
        _il.Emit(OpCodes.Ldfld, s_obj);
        if (NodeTypes.Is(value.Type, NodeType.kJSReceiver))
        {
            _il.Emit(OpCodes.Ldfld, s_receiverMap);
            return;
        }
        _il.Emit(OpCodes.Stloc, _tmpObject);
        _il.Emit(OpCodes.Ldloc, _tmpObject);
        _il.Emit(OpCodes.Brfalse, notReceiver);
        _il.Emit(OpCodes.Ldloc, _tmpObject);
        _il.Emit(OpCodes.Ldfld, s_instanceType);
        _il.Emit(OpCodes.Ldc_I4, (int)InstanceTypeChecks.FirstJSReceiver);
        _il.Emit(OpCodes.Blt_Un, notReceiver);
        _il.Emit(OpCodes.Ldloc, _tmpObject);
        _il.Emit(OpCodes.Ldfld, s_receiverMap);
    }

    void EmitCheckMaps(Node node)
    {
        var maps = (Map[])node.Obj0!;
        Label exit = EagerExit(node.EagerDeoptInfo!);
        // CheckMapsWithMigration: when a map is a migration target, an object
        // with a deprecated map is migrated and checked again.
        bool migrate = false;
        foreach (Map map in maps) migrate |= map.IsMigrationTarget;
        Label fail = migrate ? _il.DefineLabel() : exit;
        Label ok = _il.DefineLabel();
        EmitLoadMapOrBranch(node.Inputs[0], exit);
        if (maps.Length == 1)
        {
            LoadConstantObject(maps[0], typeof(Map));
            _il.Emit(OpCodes.Beq, ok);
        }
        else
        {
            _il.Emit(OpCodes.Stloc, _tmpMap);
            foreach (Map map in maps)
            {
                _il.Emit(OpCodes.Ldloc, _tmpMap);
                LoadConstantObject(map, typeof(Map));
                _il.Emit(OpCodes.Beq, ok);
            }
        }
        _il.Emit(OpCodes.Br, fail);
        if (migrate)
        {
            _il.MarkLabel(fail);
            _il.Emit(OpCodes.Ldarg_1);
            Load(node.Inputs[0], ValueRepresentation.kTagged);
            LoadConstantObject(maps, typeof(Map[]));
            Call(nameof(MaglevBuiltins.MigrateAndCheckMaps));
            _il.Emit(OpCodes.Brfalse, exit);
        }
        _il.MarkLabel(ok);
    }

    /// <summary>EnterInlinedFrame: push the frame, then write the receiver and the arguments.</summary>
    void EmitEnterInlinedFrame(Node node)
    {
        var unit = (MaglevCompilationUnit)node.Obj0!;
        unit.FpLocal ??= _il.DeclareLocal(typeof(int));
        unit.FpRefLocal ??= _il.DeclareLocal(typeof(JSValue).MakeByRefType());
        unit.FrameRecordLocal ??= _il.DeclareLocal(typeof(InterpreterFrameRecord).MakeByRefType());
        // Lazy frames are pushed by EmitEnsureInlinedFrames before the first
        // node that needs them.
        if (unit.EagerFrame)
        {
            // Its callers' lazy frames come first (the push stores the call's
            // bytecode offset in the caller's frame).
            if (unit.Caller is { IsInline: true } caller) EmitEnsureInlinedFrames(caller);
            EmitPushInlinedFrame(unit);
        }
    }

    /// <summary>
    /// Pushes the frames of the inlined functions <paramref name="unit"/> is
    /// nested in (and its own) that are not pushed yet: a frame at inlining
    /// depth d exists when the frame depth exceeds the optimized frame's index + d.
    /// </summary>
    void EmitEnsureInlinedFrames(MaglevCompilationUnit unit)
    {
        Span<MaglevCompilationUnit?> chain = new MaglevCompilationUnit?[unit.InliningDepth + 1];
        for (MaglevCompilationUnit? u = unit; u is { IsInline: true }; u = u.Caller) chain[u.InliningDepth] = u;
        for (int d = 1; d <= unit.InliningDepth; d++)
        {
            MaglevCompilationUnit u = chain[d]!;
            if (u.EagerFrame) continue;
            Label pushed = _il.DefineLabel();
            _il.Emit(OpCodes.Ldarg_1);
            _il.Emit(OpCodes.Ldfld, s_interpreterFrameDepth);
            _il.Emit(OpCodes.Ldloc, _baseFrameIndex);
            _il.Emit(OpCodes.Ldc_I4, d);
            _il.Emit(OpCodes.Add);
            _il.Emit(OpCodes.Bgt, pushed);
            EmitPushInlinedFrame(u);
            _il.MarkLabel(pushed);
        }
    }

    /// <summary>EnterInlinedFrame: pushes the frame of the inlined <paramref name="unit"/> and stores its receiver and arguments.</summary>
    void EmitPushInlinedFrame(MaglevCompilationUnit unit)
    {
        Node node = unit.EntryNode!;
        int argc = node.Int0;
        StoreBytecodeOffset(node);
        // fp = EnterInlinedFrame(isolate, function, bytecode, argc, isConstruct)
        _il.Emit(OpCodes.Ldarg_1);
        LoadConstantObject(unit.Function, typeof(JSFunction));
        LoadConstantObject(unit.Bytecode, typeof(BytecodeArray));
        _il.Emit(OpCodes.Ldc_I4, argc);
        _il.Emit(node.Int1 != 0 ? OpCodes.Ldc_I4_1 : OpCodes.Ldc_I4_0);
        Call(nameof(MaglevBuiltins.EnterInlinedFrame));
        _il.Emit(OpCodes.Stloc, unit.FpLocal!);
        // fpRef = ref isolate.RegisterStack[fp]
        _il.Emit(OpCodes.Ldarg_1);
        _il.Emit(OpCodes.Ldfld, s_registerStack);
        _il.Emit(OpCodes.Ldloc, unit.FpLocal!);
        _il.Emit(OpCodes.Ldelema, typeof(JSValue));
        _il.Emit(OpCodes.Stloc, unit.FpRefLocal!);
        // frame = ref isolate.InterpreterFrames[base + depth]
        _il.Emit(OpCodes.Ldarg_1);
        _il.Emit(OpCodes.Call, s_interpreterFrames);
        _il.Emit(OpCodes.Ldloc, _baseFrameIndex);
        _il.Emit(OpCodes.Ldc_I4, unit.InliningDepth);
        _il.Emit(OpCodes.Add);
        _il.Emit(OpCodes.Ldelema, typeof(InterpreterFrameRecord));
        _il.Emit(OpCodes.Stloc, unit.FrameRecordLocal!);
        // The receiver and the arguments (the new.target register of a construct).
        LoadFrameSlotAddress(unit, InterpreterRuntime.kReceiverOffset);
        Load(node.Inputs[0], ValueRepresentation.kTagged);
        _il.Emit(OpCodes.Stobj, typeof(JSValue));
        for (int i = 0; i < argc; i++)
        {
            LoadFrameSlotAddress(unit, InterpreterRuntime.kFirstArgumentOffset - i);
            Load(node.Inputs[1 + i], ValueRepresentation.kTagged);
            _il.Emit(OpCodes.Stobj, typeof(JSValue));
        }
        if (node.Int2 != 0)
        {
            Register incoming = unit.Bytecode.IncomingNewTargetOrGeneratorRegister;
            if (incoming.IsValid)
            {
                LoadFrameSlotAddress(unit, incoming.Index);
                Load(node.Inputs[^1], ValueRepresentation.kTagged);
                _il.Emit(OpCodes.Stobj, typeof(JSValue));
            }
        }
    }

    /// <summary>A CallBuiltin node: register stores, the arguments, the call, the result, the lazy deopt check.</summary>
    void EmitCallBuiltin(Node node)
    {
        var info = (CallBuiltinInfo)node.Obj0!;
        MethodInfo method = info.Method;
        ParameterInfo[] parameters = method.GetParameters();
        int storeInputBase = node.Inputs.Length - info.RegisterStores.Length;
        for (int i = 0; i < info.RegisterStores.Length; i++)
        {
            LoadFrameSlotAddress(node.Unit, info.RegisterStores[i].Register.Index);
            Load(node.Inputs[storeInputBase + i], ValueRepresentation.kTagged);
            _il.Emit(OpCodes.Stobj, typeof(JSValue));
        }
        if (node.IsCall || node.CanThrow) StoreBytecodeOffset(node);
        for (int a = 0; a < info.Args.Length; a++)
        {
            BuiltinArg arg = info.Args[a];
            Type type = parameters[a].ParameterType;
            switch (arg.Kind)
            {
                case BuiltinArgKind.Isolate:
                    _il.Emit(OpCodes.Ldarg_1);
                    break;
                case BuiltinArgKind.State:
                    _il.Emit(OpCodes.Ldarg_2);
                    break;
                case BuiltinArgKind.Input:
                {
                    ValueNode input = node.Inputs[arg.Index];
                    if (type == typeof(JSValue)) Load(input, ValueRepresentation.kTagged);
                    else if (type == typeof(int) || type == typeof(uint)) Load(input, ValueRepresentation.kInt32);
                    else if (type == typeof(double)) Load(input, ValueRepresentation.kFloat64);
                    else LoadAsObject(input, type);
                    break;
                }
                case BuiltinArgKind.Int:
                    _il.Emit(OpCodes.Ldc_I4, arg.Index);
                    break;
                case BuiltinArgKind.Bool:
                    _il.Emit(arg.Index != 0 ? OpCodes.Ldc_I4_1 : OpCodes.Ldc_I4_0);
                    break;
                case BuiltinArgKind.Constant:
                    if (type == typeof(JSValue))
                    {
                        object? c = arg.Object;
                        if (c is JSValue jv && jv.IsNumber)
                        {
                            _il.Emit(OpCodes.Ldc_R8, jv.Number);
                            _il.Emit(OpCodes.Newobj, s_jsValueFromDouble);
                        }
                        else if (c is JSValue { IsUndefined: true } || c is null)
                        {
                            LoadUndefined();
                        }
                        else
                        {
                            LoadConstantObject(c is JSValue v2 ? v2.Object : c, typeof(JSValue));
                        }
                    }
                    else
                    {
                        object? c = arg.Object is JSValue jv ? jv.HeapObjectOrNull : arg.Object;
                        LoadConstantObject(c, type == typeof(HeapObject) || !type.IsInstanceOfType(c) && c is not null ? c!.GetType() : type);
                    }
                    break;
                case BuiltinArgKind.RegisterIndex:
                    LoadFp(node.Unit);
                    _il.Emit(OpCodes.Ldc_I4, arg.Index);
                    _il.Emit(OpCodes.Add);
                    break;
                case BuiltinArgKind.RegisterRef:
                    LoadFrameSlotAddress(node.Unit, arg.Index);
                    break;
                case BuiltinArgKind.FeedbackByteRef:
                    LoadConstantObject(node.Unit!.Bytecode.Bytecodes, typeof(byte[]));
                    _il.Emit(OpCodes.Ldc_I4, arg.Index);
                    _il.Emit(OpCodes.Ldelema, typeof(byte));
                    break;
                case BuiltinArgKind.Closure:
                    LoadConstantObject(node.Unit!.Function, type);
                    break;
            }
        }
        _il.Emit(OpCodes.Call, method);
        if (info.DeoptIfFalse)
        {
            DeoptIfFalse(node);
        }
        else if (method.ReturnType != typeof(void))
        {
            if (node is ValueNode v) Store(v);
            else _il.Emit(OpCodes.Pop);
        }
        if (node.LazyDeoptInfo is not null) EmitLazyDeoptCheck(node, node as ValueNode);
    }
}
