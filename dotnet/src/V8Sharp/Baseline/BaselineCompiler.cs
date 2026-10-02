// Port of src/baseline/baseline-compiler.{h,cc}: Sparkplug, the non-optimizing
// compiler that turns bytecode into code in one linear pass, one handler per
// bytecode, calling the same builtins and ICs as the interpreter and keeping
// the interpreter's frame layout. V8 emits machine code; V8Sharp emits IL into
// a DynamicMethod that RyuJIT compiles (architecture.md section 9).
//
// Structure as in V8:
//   GenerateCode: PreVisit (mark jump targets and entry points), Prologue,
//                 then VisitSingleBytecode for every bytecode;
//   Build:        finalize the code object.
// Each Visit* mirrors BaselineCompiler::Visit* (and the Ignition handler of
// the same name): operands are decoded at compile time and passed as
// constants to the builtin (BaselineBuiltins), register operands become frame
// slot accesses, jumps become IL branches.
//
// Differences from V8's code (the frame is the same; see BaselineAssembler):
// - The bytecode offset is stored in the frame record before each bytecode
//   that can throw or call (V8 recovers it from the return address through
//   the bytecode offset table).
// - Exceptions: V8 unwinds into baseline code at the handler's pc. The IL
//   entry takes the bytecode offset to start at (InterpreterState.Pc) and
//   dispatches to it, so BaselineExecution.Run re-enters the method at the
//   handler after the frame's handler lookup. Throw/ReThrow to a handler of
//   the same frame branch there without a .NET exception.
// - OSR from Ignition enters at a loop header through the same dispatch.
using System.Reflection;
using System.Reflection.Emit;
using V8Sharp.Codegen;
using V8Sharp.Interpreter;

namespace V8Sharp.Baseline;

public sealed partial class BaselineCompiler
{
    /// <summary>kAverageBytecodeToInstructionRatio (x64).</summary>
    public const int kAverageBytecodeToInstructionRatio = 7;

    readonly Isolate _isolate;
    readonly SharedFunctionInfo _shared;
    readonly BytecodeArray _bytecode;
    readonly DynamicMethod? _method;
    readonly TypeBuilder? _type;
    readonly MethodBuilder? _methodBuilder;

    /// <summary>
    /// V8SHARP_BASELINE_DYNAMICMETHOD=1 emits DynamicMethods (compiled once, fully
    /// optimized) instead of methods in the tiered code space (BaselineCodeSpace).
    /// </summary>
    static readonly bool s_useDynamicMethod = Environment.GetEnvironmentVariable("V8SHARP_BASELINE_DYNAMICMETHOD") == "1";
    readonly BaselineAssembler _masm;
    readonly BaselineILEmitter _il;

    /// <summary>
    /// Compact code: the bytecodes call the out-of-line builtins instead of
    /// emitting their fast paths, for a function whose full code would exceed
    /// RyuJIT's optimization limits (BaselineILEmitter).
    /// </summary>
    readonly bool _compact;
    readonly BytecodeArrayIterator _iterator;

    // Labels at bytecode offsets (V8: labels_ / label_tags_).
    readonly Label[] _labels;
    readonly bool[] _isJumpTarget;
    // Offsets the code can be entered at: 0, exception handlers, loop headers.
    readonly SortedSet<int> _entryOffsets = [];
    Label _reenter;
    LocalBuilder? _valueTemp;

    static readonly Dictionary<string, MethodInfo> s_builtins = LoadBuiltins();
    static readonly Dictionary<string, MethodInfo> s_calls = typeof(BaselineCalls).GetMethods(BindingFlags.Public | BindingFlags.Static)
        .ToDictionary(m => m.Name, StringComparer.Ordinal);

    /// <summary>The name of a function's baseline method (computed on the main thread: it reads the heap).</summary>
    public static string MethodName(SharedFunctionInfo shared)
    {
        string name = shared.Name().ToString();
        return "baseline:" + (name.Length == 0 ? "(anonymous)" : name);
    }

    public BaselineCompiler(Isolate isolate, SharedFunctionInfo sharedFunctionInfo, BytecodeArray bytecode, bool compact = false,
        string? methodName = null, bool optimizeFully = false)
    {
        _isolate = isolate;
        _compact = compact;
        _shared = sharedFunctionInfo;
        _bytecode = bytecode;
        string name = methodName ?? MethodName(sharedFunctionInfo);
        if (s_useDynamicMethod)
        {
            _method = new DynamicMethod(name, typeof(JSValue), [typeof(BaselineCode), typeof(Isolate), typeof(InterpreterState).MakeByRefType()],
                typeof(BaselineCompiler).Module, skipVisibility: true);
            _il = new BaselineILEmitter(_method.GetILGenerator(Math.Max(64, bytecode.Length * 16)));
        }
        else
        {
            (_type, _methodBuilder) = BaselineCodeSpace.For(isolate).DefineMethod(name);
            // Code compiled on the concurrent compiler's thread is jitted there
            // with full optimization right away (AggressiveOptimization skips
            // RyuJIT's tier 0): the main thread never runs it unoptimized.
            if (optimizeFully)
            {
                _methodBuilder.SetImplementationFlags(MethodImplAttributes.IL | MethodImplAttributes.Managed |
                                                      MethodImplAttributes.AggressiveOptimization);
            }
            _il = new BaselineILEmitter(_methodBuilder.GetILGenerator(Math.Max(64, bytecode.Length * 16)));
        }
        _masm = new BaselineAssembler(_il);
        _iterator = new BytecodeArrayIterator(bytecode);
        _labels = new Label[bytecode.Length + 1];
        _isJumpTarget = new bool[bytecode.Length + 1];
    }

    static Dictionary<string, MethodInfo> LoadBuiltins()
    {
        var result = new Dictionary<string, MethodInfo>(StringComparer.Ordinal);
        foreach (MethodInfo m in typeof(BaselineBuiltins).GetMethods(BindingFlags.Public | BindingFlags.Static)) result[m.Name] = m;
        foreach (MethodInfo m in typeof(BaselineExecution).GetMethods(BindingFlags.Public | BindingFlags.Static)) result[m.Name] = m;
        return result;
    }

    // RyuJIT's MinOpts limits (compSetOptimizationLevel), with a margin.
    const int kMaxOptimizedILBytes = 50000;
    const int kMaxOptimizedInstructions = 16000;
    const int kMaxOptimizedBlocks = 1600;
    const int kMaxOptimizedLocalReferences = 6500;

    /// <summary>
    /// Whether RyuJIT would compile the generated method with minimal
    /// optimization (BaselineILEmitter): the code is then generated again in
    /// the compact form.
    /// </summary>
    public bool ExceedsOptimizationLimits =>
        _il.ILOffset > kMaxOptimizedILBytes || _il.Instructions > kMaxOptimizedInstructions ||
        _il.BlockBoundaries > kMaxOptimizedBlocks || _il.LocalReferences > kMaxOptimizedLocalReferences;

    /// <summary>The emitter's counts (for tracing and tests).</summary>
    internal string Statistics =>
        $"il={_il.ILOffset} instructions={_il.Instructions} blocks<={_il.BlockBoundaries} localrefs={_il.LocalReferences}" +
        (_compact ? " compact" : "");

    /// <summary>BaselineCompiler::EstimateInstructionSize.</summary>
    public static long EstimateInstructionSize(BytecodeArray bytecode) => (long)bytecode.Length * kAverageBytecodeToInstructionRatio;

    // ---- GenerateCode / Build ------------------------------------------------------------------

    /// <summary>BaselineCompiler::GenerateCode.</summary>
    public void GenerateCode()
    {
        // PreVisit: the exception handlers are entry points (V8: indirect jump
        // targets), and so is every loop header (OSR from the interpreter).
        _entryOffsets.Add(0);
        if (_bytecode.HandlerTable.Length != 0)
        {
            var table = new HandlerTable(_bytecode.HandlerTable);
            for (uint i = 0; i < table.NumberOfRangeEntries(); ++i)
            {
                int handler = table.GetRangeHandler(i);
                _entryOffsets.Add(handler);
                EnsureLabel(handler);
            }
        }
        for (; !_iterator.Done(); _iterator.Advance()) PreVisitSingleBytecode();
        _iterator.Reset();

        SetUpRegisterCache();
        Prologue();
        for (; !_iterator.Done(); _iterator.Advance()) VisitSingleBytecode();

        // Falling off the end is impossible (the bytecode ends in Return, Throw
        // or a jump), but IL requires a terminated method.
        _masm.LoadInt(_bytecode.Length);
        CallBuiltin("Illegal");
        _masm.Return();
    }

    /// <summary>
    /// BaselineCompiler::Build: the finished method and its IL size. The entry
    /// delegate is closed over the code object (the method's first parameter),
    /// which makes invoking it a direct call (an open static delegate goes
    /// through a shuffle thunk).
    /// </summary>
    public (BaselineCodeEntry Entry, int ILSize) Build(BaselineCode code)
    {
        BaselineCodeEntry entry;
        if (_method is not null)
        {
            entry = (BaselineCodeEntry)_method.CreateDelegate(typeof(BaselineCodeEntry), code);
        }
        else
        {
            Type type = BaselineCodeSpace.CreateType(_type!);
            CompiledMethod = type.GetMethod(_methodBuilder!.Name)!;
            entry = (BaselineCodeEntry)CompiledMethod.CreateDelegate(typeof(BaselineCodeEntry), code);
        }
        return (entry, _il.ILOffset);
    }

    /// <summary>The finished method (after Build; null for a DynamicMethod).</summary>
    public MethodInfo? CompiledMethod { get; private set; }

    Label EnsureLabel(int offset)
    {
        if (!_isJumpTarget[offset])
        {
            _isJumpTarget[offset] = true;
            _labels[offset] = _il.DefineLabel();
        }
        return _labels[offset];
    }

    void PreVisitSingleBytecode()
    {
        Bytecode bytecode = _iterator.CurrentBytecode();
        if (bytecode == Bytecode.JumpLoop)
        {
            int target = JumpTargetOffset();
            EnsureLabel(target);
            _entryOffsets.Add(target);
        }
        else if (Bytecodes.IsJump(bytecode))
        {
            EnsureLabel(JumpTargetOffset());
        }
        else if (Bytecodes.IsSwitch(bytecode))
        {
            foreach ((int _, int target) in JumpTableTargets()) EnsureLabel(target);
        }
    }

    // ---- Prologue ---------------------------------------------------------------------------------

    static readonly FieldInfo s_registerStack = typeof(V8Sharp.Isolate).GetField(nameof(V8Sharp.Isolate.RegisterStack))!;
    static readonly MethodInfo s_interpreterFrames = typeof(V8Sharp.Isolate).GetProperty(nameof(V8Sharp.Isolate.InterpreterFrames))!.GetMethod!;
    static readonly FieldInfo s_stFp = typeof(InterpreterState).GetField(nameof(InterpreterState.Fp))!;
    static readonly FieldInfo s_stFrameIndex = typeof(InterpreterState).GetField(nameof(InterpreterState.FrameIndex))!;
    static readonly FieldInfo s_stFunction = typeof(InterpreterState).GetField(nameof(InterpreterState.Function))!;
    static readonly FieldInfo s_constantPoolValues = typeof(BytecodeArray).GetField(nameof(BytecodeArray.ConstantPoolValues))!;
    static readonly FieldInfo s_stBytecode = typeof(InterpreterState).GetField(nameof(InterpreterState.Bytecode))!;
    static readonly FieldInfo s_stAccumulator = typeof(InterpreterState).GetField(nameof(InterpreterState.Accumulator))!;
    static readonly FieldInfo s_stContext = typeof(InterpreterState).GetField(nameof(InterpreterState.Context))!;
    static readonly FieldInfo s_stFeedbackVector = typeof(InterpreterState).GetField(nameof(InterpreterState.FeedbackVector))!;
    static readonly FieldInfo s_stPc = typeof(InterpreterState).GetField(nameof(InterpreterState.Pc))!;
    static readonly MethodInfo s_bytecodes = typeof(BytecodeArray).GetProperty(nameof(BytecodeArray.Bytecodes))!.GetMethod!;

    /// <summary>
    /// BaselineCompiler::Prologue. The frame itself was built by the entry
    /// (InterpreterExecution.EnterFrame, V8's BaselineOutOfLinePrologue: stack
    /// check, invocation count, register file fill); the prologue loads the
    /// frame into locals and dispatches to the entry offset.
    /// </summary>
    void Prologue()
    {
        BaselineILEmitter il = _il;
        // fpRef = ref isolate.RegisterStack[st.Fp]; fp = st.Fp
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Ldfld, s_registerStack);
        il.Emit(OpCodes.Ldarg_2);
        il.Emit(OpCodes.Ldfld, s_stFp);
        il.Emit(OpCodes.Ldelema, typeof(JSValue));
        il.Emit(OpCodes.Stloc, _masm.FpRef);
        il.Emit(OpCodes.Ldarg_2);
        il.Emit(OpCodes.Ldfld, s_stFp);
        il.Emit(OpCodes.Stloc, _masm.Fp);
        // frame = ref isolate.InterpreterFrames[st.FrameIndex]
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Call, s_interpreterFrames);
        il.Emit(OpCodes.Ldarg_2);
        il.Emit(OpCodes.Ldfld, s_stFrameIndex);
        il.Emit(OpCodes.Ldelema, typeof(InterpreterFrameRecord));
        il.Emit(OpCodes.Stloc, _masm.Frame);
        il.Emit(OpCodes.Ldarg_2);
        il.Emit(OpCodes.Ldfld, s_stFunction);
        il.Emit(OpCodes.Stloc, _masm.Function);
        // (The entry materialized the constant pool.)
        il.Emit(OpCodes.Ldarg_2);
        il.Emit(OpCodes.Ldfld, s_stBytecode);
        il.Emit(OpCodes.Ldfld, s_constantPoolValues);
        il.Emit(OpCodes.Stloc, _masm.Constants);
        il.Emit(OpCodes.Ldarg_2);
        il.Emit(OpCodes.Ldfld, s_stBytecode);
        il.Emit(OpCodes.Callvirt, s_bytecodes);
        il.Emit(OpCodes.Stloc, _masm.Code);

        // Re-entry (after a Throw dispatched to a handler of this frame): the
        // accumulator, context and target offset come from the state.
        _reenter = il.DefineLabel();
        il.MarkLabel(_reenter);
        il.Emit(OpCodes.Ldarg_2);
        il.Emit(OpCodes.Ldfld, s_stAccumulator);
        il.Emit(OpCodes.Stloc, _masm.Acc);
        il.Emit(OpCodes.Ldarg_2);
        il.Emit(OpCodes.Ldfld, s_stContext);
        il.Emit(OpCodes.Stloc, _masm.Context);
        il.Emit(OpCodes.Ldarg_2);
        il.Emit(OpCodes.Ldfld, s_stFeedbackVector);
        il.Emit(OpCodes.Stloc, _masm.Fv);
        // Baseline frames always have a feedback vector (Runtime_InstallBaselineCode).
        il.Emit(OpCodes.Ldloc, _masm.Fv);
        il.Emit(OpCodes.Ldfld, s_feedbackSlots);
        il.Emit(OpCodes.Stloc, _masm.FeedbackSlots);
        il.Emit(OpCodes.Ldarg_2);
        il.Emit(OpCodes.Ldfld, s_stPc);
        il.Emit(OpCodes.Stloc, _masm.Scratch);

        var entryStubs = new List<(int Offset, Label Stub)>();
        foreach (int offset in _entryOffsets)
        {
            Label target = EnsureLabel(offset);
            if (_registerLocals is not null)
            {
                // Cached registers are loaded from the frame on entry.
                target = il.DefineLabel();
                entryStubs.Add((offset, target));
            }
            il.Emit(OpCodes.Ldloc, _masm.Scratch);
            if (offset == 0)
            {
                il.Emit(OpCodes.Brfalse, target);
            }
            else
            {
                il.Emit(OpCodes.Ldc_I4, offset);
                il.Emit(OpCodes.Beq, target);
            }
        }
        // Not an entry offset.
        il.Emit(OpCodes.Ldloc, _masm.Scratch);
        CallBuiltin("Illegal");
        _masm.Return();

        foreach ((int offset, Label stub) in entryStubs)
        {
            il.MarkLabel(stub);
            if (offset == 0)
            {
                // A new frame: the register file is undefined (as the locals
                // start out), except new.target, which the caller stored.
                Register incoming = _bytecode.IncomingNewTargetOrGeneratorRegister;
                if (incoming.IsValid && IsCached(incoming)) ReloadRegisters(incoming.Index, 1);
            }
            else
            {
                // OSR from the interpreter at a loop header.
                ReloadRegisters(0, _registerLocals!.Length);
            }
            il.Emit(OpCodes.Br, _labels[offset]);
        }
    }

    // ---- Operand helpers (BaselineCompiler::RegisterOperand, Constant, Uint ...) --------------------------

    Register RegisterOperand(int i) => _iterator.GetRegisterOperand(i);
    int ConstantPoolIndex(int i) => (int)_iterator.GetConstantPoolIndexOperand(i);
    int FeedbackSlot(int i) => _iterator.GetSlotOperand(i);
    int Uint(int i) => (int)_iterator.GetUnsignedImmediateOperand(i);
    int Int(int i) => _iterator.GetImmediateOperand(i);
    int Flag8(int i) => (int)_iterator.GetFlag8Operand(i);
    int Flag16(int i) => (int)_iterator.GetFlag16Operand(i);
    int ContextSlot(int i) => (int)_iterator.GetContextSlotOperand(i);
    int RegisterCount(int i) => (int)_iterator.GetRegisterCountOperand(i);

    /// <summary>The offset of the current bytecode after its prefix: what the interpreter keeps as its pc.</summary>
    int Cursor => _iterator.CurrentOffset() + _iterator.CurrentBytecodeSize() - _iterator.CurrentBytecodeSizeWithoutPrefix();

    /// <summary>The raw byte of a one-byte operand (IntrinsicId).</summary>
    int RawByteOperand(int i) => _bytecode.Bytecodes[Cursor + _iterator.CurrentOperandOffset(i)];

    /// <summary>The bytecode array offset of an embedded feedback operand.</summary>
    int EmbeddedFeedbackOffset(int i) => Cursor + _iterator.CurrentOperandOffset(i);

    void CallBuiltin(string name) => _masm.Call(s_builtins[name]);

    void CallCalls(string name) => _masm.Call(s_calls[name]);

    JSValue[] ConstantPoolValues => _bytecode.ConstantPoolValues ?? InterpreterRuntime.MaterializeConstantPool(_isolate, _bytecode);

    /// <summary>
    /// The absolute target of the current jump (BytecodeArrayIterator::GetJumpTargetOffset,
    /// reading a constant pool operand as a number: the engine's constant pools hold
    /// materialized values).
    /// </summary>
    int JumpTargetOffset()
    {
        Bytecode bytecode = _iterator.CurrentBytecode();
        int relative;
        if (Bytecodes.IsJumpImmediate(bytecode))
        {
            relative = Uint(0);
            if (bytecode == Bytecode.JumpLoop) relative = -relative;
        }
        else
        {
            relative = (int)ConstantPoolValues[ConstantPoolIndex(0)].Number;
        }
        return _iterator.GetAbsoluteOffset(relative);
    }

    /// <summary>
    /// The (case value, absolute target) pairs of the current switch's jump
    /// table, skipping the holes (JumpTableTargetOffsets::UpdateAndAdvanceToValid).
    /// </summary>
    List<(int CaseValue, int Target)> JumpTableTargets()
    {
        int tableStart, tableSize, caseValueBase;
        if (_iterator.CurrentBytecode() == Bytecode.SwitchOnGeneratorState)
        {
            tableStart = ConstantPoolIndex(1);
            tableSize = Uint(2);
            caseValueBase = 0;
        }
        else
        {
            tableStart = ConstantPoolIndex(0);
            tableSize = Uint(1);
            caseValueBase = Int(2);
        }
        JSValue[] constants = ConstantPoolValues;
        var result = new List<(int, int)>(tableSize);
        for (int i = 0; i < tableSize; i++)
        {
            JSValue entry = constants[tableStart + i];
            if (!entry.IsNumber) continue;
            result.Add((caseValueBase + i, _iterator.GetAbsoluteOffset((int)entry.Number)));
        }
        return result;
    }

    // Push helpers for builtin arguments.
    void Isolate() => _masm.LoadIsolate();
    void State() => _masm.LoadState();
    void Acc() => _masm.LoadAccumulator();
    void Ctx() => _masm.LoadContext();
    void Fv() => _masm.LoadFeedbackVector();
    void Fn() => _masm.LoadFunction();
    void I(int value) => _masm.LoadInt(value);
    /// <summary>Pushes the value of register <paramref name="r"/> (its IL local when the register is cached).</summary>
    void Reg(Register r)
    {
        if (IsCached(r)) Emit(OpCodes.Ldloc, _registerLocals![r.Index]);
        else _masm.LoadRegister(r);
    }

    /// <summary>
    /// Pushes the address of register <paramref name="r"/>'s frame slot. For a
    /// cached register this is the frame copy: a bytecode whose builtin writes
    /// a register through it has the register reloaded afterwards
    /// (VisitSingleBytecode).
    /// </summary>
    void RegRef(Register r) => _masm.LoadRegisterAddress(r);
    void RegIndex(Register r) => _masm.LoadRegisterStackIndex(r);
    void Const(int index) => _masm.LoadConstant(index);
    void Feedback(int operandIndex) => _masm.LoadEmbeddedFeedbackAddress(EmbeddedFeedbackOffset(operandIndex));
    void SetAcc() => _masm.StoreAccumulator();

    LocalBuilder ValueTemp => _valueTemp ??= _il.DeclareLocal(typeof(JSValue));

    // ---- VisitSingleBytecode ----------------------------------------------------------------------------------

    /// <summary>
    /// The bytecodes that can neither throw nor call out: they need no bytecode
    /// offset in the frame.
    /// </summary>
    static bool NeedsBytecodeOffset(Bytecode bytecode)
    {
        if (Bytecodes.IsAnyStar(bytecode)) return false;
        switch (bytecode)
        {
            case Bytecode.Ldar:
            case Bytecode.LdaZero:
            case Bytecode.LdaSmi:
            case Bytecode.LdaUndefined:
            case Bytecode.LdaNull:
            case Bytecode.LdaTheHole:
            case Bytecode.LdaTdzHole:
            case Bytecode.LdaTrue:
            case Bytecode.LdaFalse:
            case Bytecode.LdaConstant:
            case Bytecode.LdaContextSlotNoCell:
            case Bytecode.LdaContextSlot:
            case Bytecode.LdaImmutableContextSlot:
            case Bytecode.LdaCurrentContextSlotNoCell:
            case Bytecode.LdaCurrentContextSlot:
            case Bytecode.LdaImmutableCurrentContextSlot:
            case Bytecode.StaContextSlotNoCell:
            case Bytecode.StaContextSlot:
            case Bytecode.StaCurrentContextSlotNoCell:
            case Bytecode.StaCurrentContextSlot:
            case Bytecode.Mov:
            case Bytecode.PushContext:
            case Bytecode.PopContext:
            case Bytecode.TestReferenceEqual:
            case Bytecode.TestUndetectable:
            case Bytecode.TestNull:
            case Bytecode.TestUndefined:
            case Bytecode.TestTypeOf:
            case Bytecode.ToBooleanLogicalNot:
            case Bytecode.LogicalNot:
            case Bytecode.ToBoolean:
            case Bytecode.Jump:
            case Bytecode.JumpConstant:
            case Bytecode.JumpIfNullConstant:
            case Bytecode.JumpIfNotNullConstant:
            case Bytecode.JumpIfUndefinedConstant:
            case Bytecode.JumpIfNotUndefinedConstant:
            case Bytecode.JumpIfUndefinedOrNullConstant:
            case Bytecode.JumpIfTrueConstant:
            case Bytecode.JumpIfFalseConstant:
            case Bytecode.JumpIfJSReceiverConstant:
            case Bytecode.JumpIfForInDoneConstant:
            case Bytecode.JumpIfToBooleanTrueConstant:
            case Bytecode.JumpIfToBooleanFalseConstant:
            case Bytecode.JumpIfToBooleanTrue:
            case Bytecode.JumpIfToBooleanFalse:
            case Bytecode.JumpIfTrue:
            case Bytecode.JumpIfFalse:
            case Bytecode.JumpIfNull:
            case Bytecode.JumpIfNotNull:
            case Bytecode.JumpIfUndefined:
            case Bytecode.JumpIfNotUndefined:
            case Bytecode.JumpIfUndefinedOrNull:
            case Bytecode.JumpIfJSReceiver:
            case Bytecode.JumpIfForInDone:
            case Bytecode.SwitchOnSmiNoFeedback:
            case Bytecode.ForInStep:
            case Bytecode.SetPendingMessage:
            case Bytecode.Debugger:
                return false;
            default:
                return true;
        }
    }

    /// <summary>BaselineCompiler::VisitSingleBytecode.</summary>
    void VisitSingleBytecode()
    {
        int offset = _iterator.CurrentOffset();
        if (_isJumpTarget[offset]) _masm.Bind(_labels[offset]);

        Bytecode bytecode = _iterator.CurrentBytecode();
        if (NeedsBytecodeOffset(bytecode)) _masm.StoreBytecodeOffset(Cursor);
        if (_registerLocals is not null) SpillRegisterListOperands(bytecode);

        switch (bytecode)
        {
            // ---- Loading the accumulator ------------------------------------------------------------------
            case Bytecode.Ldar:
                Reg(RegisterOperand(0));
                SetAcc();
                break;
            case Bytecode.LdaZero:
                SetAccNumber(0.0);
                break;
            case Bytecode.LdaSmi:
                SetAccNumber(Int(0));
                break;
            case Bytecode.LdaUndefined:
                SetAccUndefined();
                break;
            case Bytecode.LdaNull:
                SetAccRoot(s_null);
                break;
            case Bytecode.LdaTheHole:
            case Bytecode.LdaTdzHole:
                SetAccRoot(s_theHole);
                break;
            case Bytecode.LdaTrue:
                SetAccRoot(s_true);
                break;
            case Bytecode.LdaFalse:
                SetAccRoot(s_false);
                break;
            case Bytecode.LdaConstant:
                Const(ConstantPoolIndex(0));
                SetAcc();
                break;

            // Deviation (as the interpreter): no ContextCells, so the cell and
            // no-cell variants are the same load and store.
            case Bytecode.LdaContextSlotNoCell:
            case Bytecode.LdaContextSlot:
            case Bytecode.LdaImmutableContextSlot:
                VisitLdaContextSlot(RegisterOperand(0), ContextSlot(1), Uint(2));
                break;
            case Bytecode.LdaCurrentContextSlotNoCell:
            case Bytecode.LdaCurrentContextSlot:
            case Bytecode.LdaImmutableCurrentContextSlot:
                VisitLdaCurrentContextSlot(ContextSlot(0));
                break;

            // ---- Register transfers -------------------------------------------------------------------------
            case Bytecode.Star:
                if (_compact) _masm.StoreAccumulatorToRegister(RegisterOperand(0));
                else EmitStar(RegisterOperand(0));
                break;
            case Bytecode.Star0:
            case Bytecode.Star1:
            case Bytecode.Star2:
            case Bytecode.Star3:
            case Bytecode.Star4:
            case Bytecode.Star5:
            case Bytecode.Star6:
            case Bytecode.Star7:
            case Bytecode.Star8:
            case Bytecode.Star9:
            case Bytecode.Star10:
            case Bytecode.Star11:
            case Bytecode.Star12:
            case Bytecode.Star13:
            case Bytecode.Star14:
            case Bytecode.Star15:
                if (_compact) _masm.StoreAccumulatorToRegister(_iterator.GetStarTargetRegister());
                else EmitStar(_iterator.GetStarTargetRegister());
                break;
            case Bytecode.Mov:
                if (_compact) _masm.MoveRegister(RegisterOperand(0), RegisterOperand(1));
                else EmitMov(RegisterOperand(0), RegisterOperand(1));
                break;

            case Bytecode.PushContext:
            {
                // Saves the current context in <context>, and pushes the accumulator
                // as the new current context.
                Register saved = RegisterOperand(0);
                RegRef(saved);
                Ctx();
                CallBuiltin("Box");
                _il.Emit(OpCodes.Stobj, typeof(JSValue));
                Isolate();
                Acc();
                _masm.LoadFrameSlotAddress(InterpreterRuntime.kContextOffset);
                CallBuiltin("PushContext");
                _masm.StoreContext();
                break;
            }
            case Bytecode.PopContext:
                Isolate();
                Reg(RegisterOperand(0));
                _masm.LoadFrameSlotAddress(InterpreterRuntime.kContextOffset);
                CallBuiltin("PopContext");
                _masm.StoreContext();
                break;

            // ---- Test operations -------------------------------------------------------------------------------
            case Bytecode.TestReferenceEqual:
                Reg(RegisterOperand(0));
                Acc();
                CallBuiltin("TestReferenceEqual");
                SetAcc();
                break;
            case Bytecode.TestUndetectable:
            case Bytecode.TestNull:
            case Bytecode.TestUndefined:
                if (_compact)
                {
                    Acc();
                    CallBuiltin(bytecode.ToString());
                    SetAcc();
                }
                else
                {
                    VisitTestOddball(bytecode);
                }
                break;
            case Bytecode.TestTypeOf:
                Acc();
                I(Flag8(0));
                CallBuiltin("TestTypeOf");
                SetAcc();
                break;

            // ---- Globals ------------------------------------------------------------------------------------------
            case Bytecode.LdaGlobal:
            case Bytecode.LdaGlobalInsideTypeof:
                if (_compact)
                {
                    Isolate();
                    Fv();
                    I(FeedbackSlot(1));
                    Ctx();
                    Const(ConstantPoolIndex(0));
                    CallBuiltin(bytecode == Bytecode.LdaGlobal ? "LdaGlobalSlow" : "LdaGlobalInsideTypeofSlow");
                    SetAcc();
                }
                else
                {
                    VisitLdaGlobal(bytecode == Bytecode.LdaGlobalInsideTypeof);
                }
                break;
            case Bytecode.StaGlobal:
                Isolate();
                Fv();
                I(FeedbackSlot(1));
                Ctx();
                Const(ConstantPoolIndex(0));
                Acc();
                CallBuiltin("StaGlobal");
                break;

            // ---- Context stores ----------------------------------------------------------------------------------------
            case Bytecode.StaContextSlotNoCell:
            case Bytecode.StaContextSlot:
                VisitStaContextSlot(RegisterOperand(0), ContextSlot(1), Uint(2));
                break;
            case Bytecode.StaCurrentContextSlotNoCell:
            case Bytecode.StaCurrentContextSlot:
                VisitStaCurrentContextSlot(ContextSlot(0));
                break;

            // ---- Lookup slots ----------------------------------------------------------------------------------------------
            case Bytecode.LdaLookupSlot:
            case Bytecode.LdaLookupSlotInsideTypeof:
                Isolate();
                Ctx();
                Const(ConstantPoolIndex(0));
                _masm.LoadBool(bytecode == Bytecode.LdaLookupSlotInsideTypeof);
                CallBuiltin("LdaLookupSlot");
                SetAcc();
                break;
            case Bytecode.LdaLookupContextSlotNoCell:
            case Bytecode.LdaLookupContextSlot:
            case Bytecode.LdaLookupContextSlotNoCellInsideTypeof:
            case Bytecode.LdaLookupContextSlotInsideTypeof:
                Isolate();
                Ctx();
                Const(ConstantPoolIndex(0));
                I(ContextSlot(1));
                I(Uint(2));
                _masm.LoadBool(bytecode is Bytecode.LdaLookupContextSlotNoCellInsideTypeof or Bytecode.LdaLookupContextSlotInsideTypeof);
                CallBuiltin("LdaLookupContextSlot");
                SetAcc();
                break;
            case Bytecode.LdaLookupGlobalSlot:
            case Bytecode.LdaLookupGlobalSlotInsideTypeof:
                Isolate();
                Fv();
                Ctx();
                Const(ConstantPoolIndex(0));
                I(FeedbackSlot(1));
                I(Uint(2));
                _masm.LoadBool(bytecode == Bytecode.LdaLookupGlobalSlotInsideTypeof);
                CallBuiltin("LdaLookupGlobalSlot");
                SetAcc();
                break;
            case Bytecode.StaLookupSlot:
                Isolate();
                Ctx();
                Const(ConstantPoolIndex(0));
                I(Flag8(1));
                Acc();
                CallBuiltin("StaLookupSlot");
                SetAcc();
                break;

            // ---- Property loads ----------------------------------------------------------------------------------------------
            case Bytecode.GetNamedProperty:
                if (_compact)
                {
                    Isolate();
                    Fv();
                    I(FeedbackSlot(2));
                    Reg(RegisterOperand(0));
                    Const(ConstantPoolIndex(1));
                    CallBuiltin("GetNamedPropertySlow");
                    SetAcc();
                }
                else
                {
                    VisitGetNamedProperty();
                }
                break;
            case Bytecode.GetNamedPropertyFromSuper:
                Isolate();
                Fv();
                I(FeedbackSlot(2));
                Reg(RegisterOperand(0));
                Acc();
                Const(ConstantPoolIndex(1));
                CallBuiltin("GetNamedPropertyFromSuper");
                SetAcc();
                break;
            case Bytecode.GetKeyedProperty:
                if (_compact)
                {
                    Isolate();
                    Fv();
                    I(FeedbackSlot(1));
                    Reg(RegisterOperand(0));
                    Acc();
                    CallBuiltin("GetKeyedPropertySlow");
                    SetAcc();
                }
                else
                {
                    VisitGetKeyedProperty();
                }
                break;
            case Bytecode.GetEnumeratedKeyedProperty:
                Isolate();
                Fv();
                I(FeedbackSlot(3));
                Reg(RegisterOperand(0));
                Acc();
                Reg(RegisterOperand(1));
                Reg(RegisterOperand(2));
                CallBuiltin("GetEnumeratedKeyedProperty");
                SetAcc();
                break;
            case Bytecode.GetPrivateField:
                Isolate();
                Fv();
                I(FeedbackSlot(4));
                Reg(RegisterOperand(0));
                I(ContextSlot(1));
                I(Uint(2));
                Reg(RegisterOperand(3));
                CallBuiltin("GetPrivateField");
                SetAcc();
                break;

            // ---- Module variables -----------------------------------------------------------------------------------------------
            case Bytecode.LdaModuleVariable:
                Isolate();
                Ctx();
                I(Int(0));
                I(Uint(1));
                CallBuiltin("LdaModuleVariable");
                SetAcc();
                break;
            case Bytecode.StaModuleVariable:
                Isolate();
                Ctx();
                I(Int(0));
                I(Uint(1));
                Acc();
                CallBuiltin("StaModuleVariable");
                break;

            // ---- Property stores ----------------------------------------------------------------------------------------------------
            case Bytecode.SetNamedProperty:
                if (_compact)
                {
                    Isolate();
                    Fv();
                    I(FeedbackSlot(2));
                    Reg(RegisterOperand(0));
                    Const(ConstantPoolIndex(1));
                    Acc();
                    CallBuiltin("SetNamedPropertySlow");
                }
                else
                {
                    VisitSetNamedProperty();
                }
                break;
            case Bytecode.DefineNamedOwnProperty:
                Isolate();
                Fv();
                I(FeedbackSlot(2));
                Reg(RegisterOperand(0));
                Const(ConstantPoolIndex(1));
                Acc();
                CallBuiltin("DefineNamedOwnProperty");
                break;
            case Bytecode.SetKeyedProperty:
                if (_compact)
                {
                    Isolate();
                    Fv();
                    I(FeedbackSlot(2));
                    Reg(RegisterOperand(0));
                    Reg(RegisterOperand(1));
                    Acc();
                    CallBuiltin("SetKeyedPropertySlow");
                }
                else
                {
                    VisitSetKeyedProperty();
                }
                break;
            case Bytecode.StaInArrayLiteral:
                Isolate();
                Fv();
                I(FeedbackSlot(2));
                Reg(RegisterOperand(0));
                Reg(RegisterOperand(1));
                Acc();
                CallBuiltin("StaInArrayLiteral");
                break;
            case Bytecode.DefineKeyedOwnProperty:
            case Bytecode.DefineKeyedOwnPropertyInLiteral:
                Isolate();
                Fv();
                I(FeedbackSlot(3));
                Reg(RegisterOperand(0));
                Reg(RegisterOperand(1));
                I(Flag8(2));
                Acc();
                CallBuiltin(bytecode == Bytecode.DefineKeyedOwnProperty ? "DefineKeyedOwnProperty" : "DefineKeyedOwnPropertyInLiteral");
                break;
            case Bytecode.SetPrototypeProperties:
                Isolate();
                Ctx();
                Fn();
                Const(ConstantPoolIndex(0));
                I(FeedbackSlot(1));
                Acc();
                CallBuiltin("SetPrototypeProperties");
                SetAcc();
                break;
            case Bytecode.SetPrivateField:
                Isolate();
                Fv();
                I(FeedbackSlot(4));
                Reg(RegisterOperand(0));
                I(ContextSlot(1));
                I(Uint(2));
                Reg(RegisterOperand(3));
                Acc();
                CallBuiltin("SetPrivateField");
                break;

            // ---- Binary operators ---------------------------------------------------------------------------------------------------
            case Bytecode.Add: if (_compact) VisitBinaryOp("Add"); else VisitArithmetic(Operation.Add, false); break;
            case Bytecode.Sub: if (_compact) VisitBinaryOp("Subtract"); else VisitArithmetic(Operation.Subtract, false); break;
            case Bytecode.Mul: if (_compact) VisitBinaryOp("Multiply"); else VisitArithmetic(Operation.Multiply, false); break;
            case Bytecode.Div: VisitBinaryOp("Divide"); break;
            case Bytecode.Mod: VisitBinaryOp("Modulus"); break;
            case Bytecode.Exp: VisitBinaryOp("Exponentiate"); break;
            case Bytecode.BitwiseOr: if (_compact) VisitBinaryOp("BitwiseOr"); else VisitBitwise(Operation.BitwiseOr, false); break;
            case Bytecode.BitwiseXor: if (_compact) VisitBinaryOp("BitwiseXor"); else VisitBitwise(Operation.BitwiseXor, false); break;
            case Bytecode.BitwiseAnd: if (_compact) VisitBinaryOp("BitwiseAnd"); else VisitBitwise(Operation.BitwiseAnd, false); break;
            case Bytecode.ShiftLeft: if (_compact) VisitBinaryOp("ShiftLeft"); else VisitBitwise(Operation.ShiftLeft, false); break;
            case Bytecode.ShiftRight: if (_compact) VisitBinaryOp("ShiftRight"); else VisitBitwise(Operation.ShiftRight, false); break;
            case Bytecode.ShiftRightLogical: if (_compact) VisitBinaryOp("ShiftRightLogical"); else VisitBitwise(Operation.ShiftRightLogical, false); break;
            case Bytecode.AddSmi: if (_compact) VisitBinaryOpWithSmi("Add"); else VisitArithmetic(Operation.Add, true); break;
            case Bytecode.SubSmi: if (_compact) VisitBinaryOpWithSmi("Subtract"); else VisitArithmetic(Operation.Subtract, true); break;
            case Bytecode.MulSmi: if (_compact) VisitBinaryOpWithSmi("Multiply"); else VisitArithmetic(Operation.Multiply, true); break;
            case Bytecode.DivSmi: VisitBinaryOpWithSmi("Divide"); break;
            case Bytecode.ModSmi: VisitBinaryOpWithSmi("Modulus"); break;
            case Bytecode.ExpSmi: VisitBinaryOpWithSmi("Exponentiate"); break;
            case Bytecode.BitwiseOrSmi: if (_compact) VisitBinaryOpWithSmi("BitwiseOr"); else VisitBitwise(Operation.BitwiseOr, true); break;
            case Bytecode.BitwiseXorSmi: if (_compact) VisitBinaryOpWithSmi("BitwiseXor"); else VisitBitwise(Operation.BitwiseXor, true); break;
            case Bytecode.BitwiseAndSmi: if (_compact) VisitBinaryOpWithSmi("BitwiseAnd"); else VisitBitwise(Operation.BitwiseAnd, true); break;
            case Bytecode.ShiftLeftSmi: if (_compact) VisitBinaryOpWithSmi("ShiftLeft"); else VisitBitwise(Operation.ShiftLeft, true); break;
            case Bytecode.ShiftRightSmi: if (_compact) VisitBinaryOpWithSmi("ShiftRight"); else VisitBitwise(Operation.ShiftRight, true); break;
            case Bytecode.ShiftRightLogicalSmi: if (_compact) VisitBinaryOpWithSmi("ShiftRightLogical"); else VisitBitwise(Operation.ShiftRightLogical, true); break;
            case Bytecode.Add_StringConstant_Internalize:
                Isolate();
                Fv();
                I(FeedbackSlot(1));
                I(Flag8(2));
                Reg(RegisterOperand(0));
                Acc();
                CallBuiltin("AddStringConstantInternalize");
                SetAcc();
                break;

            // ---- Unary operators ------------------------------------------------------------------------------------------------------
            case Bytecode.Inc: if (_compact) VisitUnaryOp("Increment"); else VisitIncDec(true); break;
            case Bytecode.Dec: if (_compact) VisitUnaryOp("Decrement"); else VisitIncDec(false); break;
            case Bytecode.Negate: VisitUnaryOp("Negate"); break;
            case Bytecode.BitwiseNot: VisitUnaryOp("BitwiseNot"); break;
            case Bytecode.ToBooleanLogicalNot:
                VisitToBoolean(negate: true);
                break;
            case Bytecode.LogicalNot:
                Acc();
                CallBuiltin("LogicalNot");
                SetAcc();
                break;
            case Bytecode.TypeOf:
                Isolate();
                Fv();
                I(FeedbackSlot(0));
                Acc();
                CallBuiltin("TypeOf");
                SetAcc();
                break;
            case Bytecode.DeletePropertyStrict:
            case Bytecode.DeletePropertySloppy:
                Isolate();
                Reg(RegisterOperand(0));
                Acc();
                CallBuiltin(bytecode == Bytecode.DeletePropertyStrict ? "DeletePropertyStrict" : "DeletePropertySloppy");
                SetAcc();
                break;
            case Bytecode.GetSuperConstructor:
            {
                RegRef(RegisterOperand(0));
                Isolate();
                Acc();
                CallBuiltin("GetSuperConstructor");
                _il.Emit(OpCodes.Stobj, typeof(JSValue));
                break;
            }
            case Bytecode.FindNonDefaultConstructorOrConstruct:
            {
                Register output = RegisterOperand(2);
                Isolate();
                Reg(RegisterOperand(0));
                Reg(RegisterOperand(1));
                RegRef(output);
                RegRef(new Register(output.Index + 1));
                CallBuiltin("FindNonDefaultConstructorOrConstruct");
                break;
            }

            // ---- Calls ------------------------------------------------------------------------------------------------------------------
            case Bytecode.CallAnyReceiver:
            case Bytecode.CallProperty:
            case Bytecode.CallUndefinedReceiver:
                Isolate();
                Fv();
                I(FeedbackSlot(3));
                Reg(RegisterOperand(0));
                RegIndex(RegisterOperand(1));
                I(RegisterCount(2));
                CallCalls(bytecode switch
                {
                    Bytecode.CallAnyReceiver => "CallAnyReceiver",
                    Bytecode.CallProperty => "CallProperty",
                    _ => "CallUndefinedReceiver",
                });
                SetAcc();
                break;
            case Bytecode.CallWithSpread:
                Isolate();
                Fv();
                I(FeedbackSlot(3));
                Reg(RegisterOperand(0));
                RegIndex(RegisterOperand(1));
                I(RegisterCount(2));
                CallBuiltin("CallWithSpread");
                SetAcc();
                break;
            case Bytecode.CallProperty0:
                Isolate();
                Fv();
                I(FeedbackSlot(2));
                Reg(RegisterOperand(0));
                Reg(RegisterOperand(1));
                CallCalls("CallProperty0");
                SetAcc();
                break;
            case Bytecode.CallProperty1:
                Isolate();
                Fv();
                I(FeedbackSlot(3));
                Reg(RegisterOperand(0));
                Reg(RegisterOperand(1));
                Reg(RegisterOperand(2));
                CallCalls("CallProperty1");
                SetAcc();
                break;
            case Bytecode.CallProperty2:
                Isolate();
                Fv();
                I(FeedbackSlot(4));
                Reg(RegisterOperand(0));
                Reg(RegisterOperand(1));
                Reg(RegisterOperand(2));
                Reg(RegisterOperand(3));
                CallCalls("CallProperty2");
                SetAcc();
                break;
            case Bytecode.CallUndefinedReceiver0:
                Isolate();
                Fv();
                I(FeedbackSlot(1));
                Reg(RegisterOperand(0));
                CallCalls("CallUndefinedReceiver0");
                SetAcc();
                break;
            case Bytecode.CallUndefinedReceiver1:
                Isolate();
                Fv();
                I(FeedbackSlot(2));
                Reg(RegisterOperand(0));
                Reg(RegisterOperand(1));
                CallCalls("CallUndefinedReceiver1");
                SetAcc();
                break;
            case Bytecode.CallUndefinedReceiver2:
                Isolate();
                Fv();
                I(FeedbackSlot(3));
                Reg(RegisterOperand(0));
                Reg(RegisterOperand(1));
                Reg(RegisterOperand(2));
                CallCalls("CallUndefinedReceiver2");
                SetAcc();
                break;
            case Bytecode.CallRuntime:
                Isolate();
                I((int)_iterator.GetRuntimeIdOperand(0));
                RegIndex(RegisterOperand(1));
                I(RegisterCount(2));
                CallBuiltin("CallRuntime");
                SetAcc();
                break;
            case Bytecode.CallRuntimeForPair:
            {
                Register output = RegisterOperand(3);
                Isolate();
                I((int)_iterator.GetRuntimeIdOperand(0));
                RegIndex(RegisterOperand(1));
                I(RegisterCount(2));
                RegRef(output);
                RegRef(new Register(output.Index + 1));
                CallBuiltin("CallRuntimeForPair");
                SetAcc();
                break;
            }
            case Bytecode.CallJSRuntime:
                Isolate();
                Ctx();
                I((int)_iterator.GetNativeContextIndexOperand(0));
                RegIndex(RegisterOperand(1));
                I(RegisterCount(2));
                CallBuiltin("CallJSRuntime");
                SetAcc();
                break;
            case Bytecode.InvokeIntrinsic:
                Isolate();
                I(RawByteOperand(0));
                RegIndex(RegisterOperand(1));
                I(RegisterCount(2));
                CallBuiltin("InvokeIntrinsic");
                SetAcc();
                break;

            // ---- Construct ------------------------------------------------------------------------------------------------------------------
            case Bytecode.Construct:
            case Bytecode.ConstructWithSpread:
                Isolate();
                Fv();
                I(FeedbackSlot(3));
                Reg(RegisterOperand(0));
                RegIndex(RegisterOperand(1));
                I(RegisterCount(2));
                Acc();
                CallBuiltin(bytecode == Bytecode.Construct ? "Construct" : "ConstructWithSpread");
                SetAcc();
                break;
            case Bytecode.ConstructForwardAllArgs:
                Isolate();
                State();
                Fv();
                I(FeedbackSlot(1));
                Reg(RegisterOperand(0));
                Acc();
                CallBuiltin("ConstructForwardAllArgs");
                SetAcc();
                break;

            // ---- Compare operations ---------------------------------------------------------------------------------------------------------
            case Bytecode.TestEqual: if (_compact) VisitBinaryOp("TestEqual"); else VisitCompare(Operation.Equal); break;
            case Bytecode.TestEqualStrict:
                if (_compact)
                {
                    Reg(RegisterOperand(0));
                    Acc();
                    Feedback(1);
                    CallBuiltin("TestEqualStrict");
                    SetAcc();
                }
                else
                {
                    VisitCompare(Operation.StrictEqual);
                }
                break;
            case Bytecode.TestLessThan: if (_compact) VisitBinaryOp("TestLessThan"); else VisitCompare(Operation.LessThan); break;
            case Bytecode.TestGreaterThan: if (_compact) VisitBinaryOp("TestGreaterThan"); else VisitCompare(Operation.GreaterThan); break;
            case Bytecode.TestLessThanOrEqual: if (_compact) VisitBinaryOp("TestLessThanOrEqual"); else VisitCompare(Operation.LessThanOrEqual); break;
            case Bytecode.TestGreaterThanOrEqual: if (_compact) VisitBinaryOp("TestGreaterThanOrEqual"); else VisitCompare(Operation.GreaterThanOrEqual); break;
            case Bytecode.TestInstanceOf:
                Isolate();
                Fv();
                I(FeedbackSlot(1));
                Reg(RegisterOperand(0));
                Acc();
                CallBuiltin("TestInstanceOf");
                SetAcc();
                break;
            case Bytecode.TestIn:
                Isolate();
                Fv();
                I(FeedbackSlot(1));
                Reg(RegisterOperand(0));
                Acc();
                CallBuiltin("TestIn");
                SetAcc();
                break;

            // ---- Cast operators ---------------------------------------------------------------------------------------------------------------
            case Bytecode.ToName:
                Isolate();
                Acc();
                CallBuiltin("ToName");
                SetAcc();
                break;
            case Bytecode.ToNumber:
            case Bytecode.ToNumeric:
                Isolate();
                Fv();
                I(FeedbackSlot(0));
                Acc();
                CallBuiltin(bytecode == Bytecode.ToNumber ? "ToNumber" : "ToNumeric");
                SetAcc();
                break;
            case Bytecode.ToObject:
                RegRef(RegisterOperand(0));
                Isolate();
                Acc();
                CallBuiltin("ToObject");
                _il.Emit(OpCodes.Stobj, typeof(JSValue));
                break;
            case Bytecode.ToString:
                Isolate();
                Acc();
                CallBuiltin("ToString");
                SetAcc();
                break;
            case Bytecode.ToBoolean:
                VisitToBoolean(negate: false);
                break;

            // ---- Literals --------------------------------------------------------------------------------------------------------------------------
            case Bytecode.CreateRegExpLiteral:
                Isolate();
                Fv();
                I(FeedbackSlot(1));
                Const(ConstantPoolIndex(0));
                I(Flag16(2));
                CallBuiltin("CreateRegExpLiteral");
                SetAcc();
                break;
            case Bytecode.CreateArrayLiteral:
            case Bytecode.CreateObjectLiteral:
                Isolate();
                Fv();
                I(FeedbackSlot(1));
                Const(ConstantPoolIndex(0));
                I(Flag8(2));
                CallBuiltin(bytecode == Bytecode.CreateArrayLiteral ? "CreateArrayLiteral" : "CreateObjectLiteral");
                SetAcc();
                break;
            case Bytecode.CreateArrayFromIterable:
                Isolate();
                Acc();
                CallBuiltin("CreateArrayFromIterable");
                SetAcc();
                break;
            case Bytecode.CreateEmptyArrayLiteral:
                Isolate();
                Fv();
                I(FeedbackSlot(0));
                CallBuiltin("CreateEmptyArrayLiteral");
                SetAcc();
                break;
            case Bytecode.CreateEmptyObjectLiteral:
                Isolate();
                Ctx();
                CallBuiltin("CreateEmptyObjectLiteral");
                SetAcc();
                break;
            case Bytecode.CloneObject:
                Isolate();
                Fv();
                I(FeedbackSlot(2));
                Reg(RegisterOperand(0));
                I(Flag8(1));
                CallBuiltin("CloneObject");
                SetAcc();
                break;
            case Bytecode.GetTemplateObject:
                Isolate();
                Fv();
                I(FeedbackSlot(1));
                Fn();
                Const(ConstantPoolIndex(0));
                CallBuiltin("GetTemplateObject");
                SetAcc();
                break;
            case Bytecode.CreateClosure:
                Isolate();
                Ctx();
                Fn();
                Const(ConstantPoolIndex(0));
                I(FeedbackSlot(1));
                CallBuiltin("CreateClosure");
                SetAcc();
                break;

            // ---- Context allocation --------------------------------------------------------------------------------------------------------------
            case Bytecode.CreateBlockContext:
                Isolate();
                Ctx();
                Const(ConstantPoolIndex(0));
                CallBuiltin("CreateBlockContext");
                SetAcc();
                break;
            case Bytecode.CreateCatchContext:
                Isolate();
                Ctx();
                Reg(RegisterOperand(0));
                Const(ConstantPoolIndex(1));
                CallBuiltin("CreateCatchContext");
                SetAcc();
                break;
            case Bytecode.CreateFunctionContext:
            case Bytecode.CreateFunctionContextWithCells:
            case Bytecode.CreateEvalContext:
                Isolate();
                Ctx();
                Const(ConstantPoolIndex(0));
                _masm.LoadBool(bytecode == Bytecode.CreateEvalContext);
                CallBuiltin("CreateFunctionContext");
                SetAcc();
                break;
            case Bytecode.CreateWithContext:
                Isolate();
                Ctx();
                Reg(RegisterOperand(0));
                Const(ConstantPoolIndex(1));
                CallBuiltin("CreateWithContext");
                SetAcc();
                break;

            // ---- Arguments allocation -------------------------------------------------------------------------------------------------------------
            case Bytecode.CreateMappedArguments:
                Isolate();
                State();
                Ctx();
                CallBuiltin("CreateMappedArguments");
                SetAcc();
                break;
            case Bytecode.CreateUnmappedArguments:
                Isolate();
                State();
                CallBuiltin("CreateUnmappedArguments");
                SetAcc();
                break;
            case Bytecode.CreateRestParameter:
                Isolate();
                State();
                CallBuiltin("CreateRestParameter");
                SetAcc();
                break;

            // ---- Control flow ------------------------------------------------------------------------------------------------------------------------
            case Bytecode.JumpLoop:
                VisitJumpLoop();
                break;
            case Bytecode.Jump:
            case Bytecode.JumpConstant:
                _masm.Jump(_labels[JumpTargetOffset()]);
                break;
            case Bytecode.JumpIfNullConstant:
            case Bytecode.JumpIfNull:
            case Bytecode.JumpIfNotNullConstant:
            case Bytecode.JumpIfNotNull:
            case Bytecode.JumpIfUndefinedConstant:
            case Bytecode.JumpIfUndefined:
            case Bytecode.JumpIfNotUndefinedConstant:
            case Bytecode.JumpIfNotUndefined:
            case Bytecode.JumpIfUndefinedOrNullConstant:
            case Bytecode.JumpIfUndefinedOrNull:
            case Bytecode.JumpIfJSReceiverConstant:
            case Bytecode.JumpIfJSReceiver:
                VisitConditionalJump(bytecode);
                break;
            case Bytecode.JumpIfTrueConstant:
            case Bytecode.JumpIfTrue:
            case Bytecode.JumpIfFalseConstant:
            case Bytecode.JumpIfFalse:
            case Bytecode.JumpIfToBooleanTrueConstant:
            case Bytecode.JumpIfToBooleanTrue:
            case Bytecode.JumpIfToBooleanFalseConstant:
            case Bytecode.JumpIfToBooleanFalse:
                if (_fusedCompare is not null) VisitFusedJump(bytecode);
                else VisitConditionalJump(bytecode);
                break;
            case Bytecode.JumpIfForInDoneConstant:
            case Bytecode.JumpIfForInDone:
                // index == cache length (both numbers: ForInPrepare / ForInStep).
                RegNum(RegisterOperand(1));
                RegNum(RegisterOperand(2));
                Emit(OpCodes.Beq, _labels[JumpTargetOffset()]);
                break;
            case Bytecode.SwitchOnSmiNoFeedback:
                VisitSwitchOnSmiNoFeedback();
                break;

            // ---- for-in / for-of --------------------------------------------------------------------------------------------------------------------
            case Bytecode.ForInEnumerate:
                Isolate();
                Reg(RegisterOperand(0));
                CallBuiltin("ForInEnumerate");
                SetAcc();
                break;
            case Bytecode.ForInPrepare:
            {
                Register output = RegisterOperand(0);
                Isolate();
                Fv();
                I(FeedbackSlot(1));
                Acc();
                RegRef(output);
                RegRef(new Register(output.Index + 1));
                RegRef(new Register(output.Index + 2));
                CallBuiltin("ForInPrepare");
                CallBuiltin("Zero");
                SetAcc();
                break;
            }
            case Bytecode.ForInNext:
            {
                Register pair = RegisterOperand(2);
                Isolate();
                Fv();
                I(FeedbackSlot(3));
                Reg(RegisterOperand(0));
                Reg(RegisterOperand(1));
                Reg(pair);
                Reg(new Register(pair.Index + 1));
                CallBuiltin("ForInNext");
                SetAcc();
                break;
            }
            case Bytecode.ForInStep:
                if (IsCached(RegisterOperand(0)))
                {
                    // The index is a number: only the payload changes.
                    Emit(OpCodes.Ldloca, _registerLocals![RegisterOperand(0).Index]);
                    RegNum(RegisterOperand(0));
                    Emit(OpCodes.Ldc_R8, 1.0);
                    Emit(OpCodes.Add);
                    Emit(OpCodes.Stfld, s_num);
                }
                else
                {
                    RegRef(RegisterOperand(0));
                    CallBuiltin("ForInStep");
                }
                break;
            case Bytecode.ForOfNext:
                Isolate();
                Fv();
                I(FeedbackSlot(2));
                Reg(RegisterOperand(0));
                Reg(RegisterOperand(1));
                CallBuiltin("ForOfNext");
                SetAcc();
                break;

            // ---- Non-local control flow ------------------------------------------------------------------------------------------------------------------
            case Bytecode.SetPendingMessage:
                Isolate();
                Acc();
                CallBuiltin("SetPendingMessage");
                SetAcc();
                break;
            case Bytecode.Throw:
            case Bytecode.ReThrow:
                // Runtime_Throw / Runtime_ReThrow: when this frame has a handler for
                // the offset, continue there (the state holds its offset, context and
                // the exception); otherwise the builtin throws out of the frame.
                Isolate();
                State();
                Acc();
                CallBuiltin(bytecode == Bytecode.Throw ? "Throw" : "ReThrow");
                _masm.JumpIfTrue(_reenter);
                _masm.Return();
                break;
            case Bytecode.Return:
                VisitReturn();
                break;
            case Bytecode.ThrowReferenceErrorIfTdzHole:
                Isolate();
                Acc();
                Const(ConstantPoolIndex(0));
                CallBuiltin("ThrowReferenceErrorIfHole");
                break;
            case Bytecode.ThrowSuperNotCalledIfTdzHole:
                Isolate();
                Acc();
                CallBuiltin("ThrowSuperNotCalledIfHole");
                break;
            case Bytecode.ThrowSuperAlreadyCalledIfNotTdzHole:
                Isolate();
                Acc();
                CallBuiltin("ThrowSuperAlreadyCalledIfNotHole");
                break;
            case Bytecode.ThrowIfNotSuperConstructor:
                Isolate();
                Fn();
                Reg(RegisterOperand(0));
                CallBuiltin("ThrowIfNotSuperConstructor");
                break;

            // ---- Generators -------------------------------------------------------------------------------------------------------------------------------
            case Bytecode.SwitchOnGeneratorState:
                VisitSwitchOnGeneratorState();
                break;
            case Bytecode.SuspendGenerator:
                Isolate();
                State();
                Ctx();
                Reg(RegisterOperand(0));
                I(RegisterOperand(1).Index);
                I(RegisterCount(2));
                I(Uint(3));
                I(Cursor);
                CallBuiltin("SuspendGenerator");
                _masm.Return();
                break;
            case Bytecode.ResumeGenerator:
                Isolate();
                State();
                Reg(RegisterOperand(0));
                I(RegisterOperand(1).Index);
                I(RegisterCount(2));
                CallBuiltin("ResumeGenerator");
                SetAcc();
                break;

            // ---- Iterator protocol ---------------------------------------------------------------------------------------------------------------------------
            case Bytecode.GetIterator:
                Isolate();
                Fv();
                I(FeedbackSlot(1));
                I(FeedbackSlot(2));
                Reg(RegisterOperand(0));
                CallBuiltin("GetIterator");
                SetAcc();
                break;
            case Bytecode.ArrayDestructure:
                Isolate();
                Acc();
                RegIndex(RegisterOperand(0));
                I(RegisterCount(1));
                CallBuiltin("ArrayDestructure");
                _masm.LoadAccumulatorAddress();
                _il.Emit(OpCodes.Initobj, typeof(JSValue));
                break;

            // ---- Debugger, coverage, abort -------------------------------------------------------------------------------------------------------------------------
            case Bytecode.Debugger:
                // Runtime_HandleDebuggerStatement: no debugger is attached.
                _masm.LoadAccumulatorAddress();
                _il.Emit(OpCodes.Initobj, typeof(JSValue));
                break;
            case Bytecode.IncBlockCounter:
                Isolate();
                Fn();
                I((int)_iterator.GetCoverageSlotOperand(0));
                CallBuiltin("IncBlockCounter");
                break;
            case Bytecode.Abort:
                Isolate();
                I(RawByteOperand(0));
                CallBuiltin("Abort");
                break;

            default:
                // DebugBreak*, Illegal: never in baseline-compiled bytecode.
                I(offset);
                CallBuiltin("Illegal");
                break;
        }
        if (_registerLocals is not null) ReloadRegisterOutputOperands(bytecode);
    }

    /// <summary>A binary operation with the register operand as lhs and embedded feedback (operand 1).</summary>
    void VisitBinaryOp(string builtin)
    {
        Isolate();
        Reg(RegisterOperand(0));
        Acc();
        Feedback(1);
        CallBuiltin(builtin);
        SetAcc();
    }

    /// <summary>A binary operation of the accumulator and a Smi immediate (operand 0), embedded feedback (operand 1).</summary>
    void VisitBinaryOpWithSmi(string builtin)
    {
        Isolate();
        Acc();
        I(Int(0));
        CallBuiltin("Smi");
        Feedback(1);
        CallBuiltin(builtin);
        SetAcc();
    }

    /// <summary>A unary operation on the accumulator with embedded feedback (operand 0).</summary>
    void VisitUnaryOp(string builtin)
    {
        Isolate();
        Acc();
        Feedback(0);
        CallBuiltin(builtin);
        SetAcc();
    }

    /// <summary>
    /// BaselineCompiler::VisitJumpLoop: the interrupt budget (with the stack
    /// check) and the back edge. There is no optimized OSR code to check for.
    /// </summary>
    void VisitJumpLoop()
    {
        int target = JumpTargetOffset();
        int weight = Uint(0) + _iterator.CurrentBytecodeSizeWithoutPrefix();
        // JumpLoop clobbers the accumulator.
        SetAccUndefined();
        EmitUpdateInterruptBudget(weight, backEdge: true, _labels[target]);
    }

    /// <summary>BaselineCompiler::VisitReturn: BaselineLeaveFrame with the profiling weight.</summary>
    void VisitReturn()
    {
        int profilingWeight = _iterator.CurrentOffset() + _iterator.CurrentBytecodeSizeWithoutPrefix();
        Label leave = _il.DefineLabel();
        EmitUpdateInterruptBudget(profilingWeight, backEdge: false, leave);
        _il.MarkLabel(leave);
        _masm.Return();
    }

    /// <summary>BaselineCompiler::VisitSwitchOnSmiNoFeedback: an IL jump table.</summary>
    void VisitSwitchOnSmiNoFeedback()
    {
        int caseValueBase = Int(2);
        int tableLength = Uint(1);
        var table = new Label[tableLength];
        Label fallThrough = _masm.NewLabel();
        for (int i = 0; i < tableLength; i++) table[i] = fallThrough;
        foreach ((int caseValue, int target) in JumpTableTargets())
        {
            table[caseValue - caseValueBase] = _labels[target];
        }
        Acc();
        I(caseValueBase);
        CallBuiltin("SwitchCase");
        _masm.Switch(table);
        _masm.Bind(fallThrough);
    }

    /// <summary>
    /// BaselineCompiler::VisitSwitchOnGeneratorState: when the generator
    /// register holds a generator, resume it (its context becomes current) and
    /// jump to the suspend point.
    /// </summary>
    void VisitSwitchOnGeneratorState()
    {
        Register generator = RegisterOperand(0);
        int tableLength = Uint(2);
        Label fallThrough = _masm.NewLabel();

        RegRef(generator);
        CallBuiltin("IsUndefined");
        _masm.JumpIfTrue(fallThrough);

        var table = new Label[tableLength];
        Label invalid = _masm.NewLabel();
        for (int i = 0; i < tableLength; i++) table[i] = invalid;
        foreach ((int caseValue, int target) in JumpTableTargets())
        {
            table[caseValue] = _labels[target];
        }
        Isolate();
        Reg(generator);
        _masm.LoadFrameSlotAddress(InterpreterRuntime.kContextOffset);
        _il.Emit(OpCodes.Ldloca, _masm.Context);
        I(tableLength);
        CallBuiltin("ResumeGeneratorState");
        _masm.Switch(table);
        // A state outside the table was rejected by ResumeGeneratorState.
        _masm.Bind(invalid);
        I(_iterator.CurrentOffset());
        CallBuiltin("Illegal");
        _masm.Bind(fallThrough);
    }
}
