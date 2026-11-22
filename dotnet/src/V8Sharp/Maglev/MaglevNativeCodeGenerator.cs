// The native Maglev backend: the graph becomes x64 machine code, as in V8
// (src/maglev/maglev-code-generator.cc, the x64 GenerateCode methods of
// src/maglev/x64/maglev-ir-x64.cc and maglev-assembler-x64.cc, with the role
// of src/maglev/maglev-regalloc.cc), instead of IL for RyuJIT
// (MaglevCodeGenerator). The design is Ryu's native code generator
// (architecture.md section 9.3):
//
//   - native code never calls managed code: anything it cannot do inline
//     is an exit to MaglevNativeCode.Run (deopts, interrupts, GC polls);
//   - it writes no object references (no GC write barrier), only numbers;
//   - every value has a home in the context (a pinned long[]); Int32 and
//     Float64 values are allocated to registers by a linear scan over the
//     block order (V8's StraightForwardRegisterAllocator allocates node by
//     node with spill slots; one location per value is enough here and
//     makes every exit a set of stores);
//   - tagged values stay in their homes (address, payload, kind) and are
//     rebuilt by the managed side at exits (MaglevNativeCode.Value).
//
// Graphs with nodes outside the supported subset (calls, stores of
// references, allocation, inlined frames, catch blocks ...) are generated
// as IL; --no-maglev-native generates every graph as IL.
//
// Registers: rbx holds the context; rax, rcx, rdx, r11, xmm0 and xmm1 are
// scratch; the others are allocated.
using V8Sharp.Codegen;
using V8Sharp.Codegen.X64;
using V8Sharp.Deoptimizer;
using static V8Sharp.Codegen.X64.Register;
using static V8Sharp.Codegen.X64.XMMRegister;
using IRegister = V8Sharp.Interpreter.Register;
using Reg = V8Sharp.Codegen.X64.Register;
using XReg = V8Sharp.Codegen.X64.XMMRegister;
using XLabel = V8Sharp.Codegen.X64.Label;

namespace V8Sharp.Maglev;

/// <summary>The graph uses something the native backend does not generate.</summary>
internal sealed class NativeUnsupportedException(string reason) : Exception(reason);

internal sealed class MaglevNativeCodeGenerator
{
    // ---- The context ---------------------------------------------------------------------------------------
    // 0..3: written by NativeCode.Call (frame, constants, spill array, interrupt flags).
    internal const int kEntryIdSlot = 4;
    internal const int kPollBudgetSlot = 5;
    /// <summary>Three words for the parallel moves' cycle breaking.</summary>
    const int kMoveTempSlot = 8;
    const int kFirstHome = 16;
    /// <summary>The ToBoolean result of a branch's managed path.</summary>
    internal const int kBoolResultSlot = 12;

    // The constants table's fixed entries (JSValues): the number tag, then the oddballs.
    const int kConstNumberTag = 0, kConstTrue = 1, kConstFalse = 2, kConstNull = 3, kConstTheHole = 4, kFirstObjectConstant = 5;

    static readonly Reg[] s_allocatableGp = [rsi, rdi, r8, r9, r10, r12, r13, r14, rbp];

    /// <summary>The poll budget (kPollBudgetSlot) while the code runs: a memory counter would chain every iteration through a store.</summary>
    const Reg kPollRegister = r15;
    static readonly XReg[] s_allocatableXmm = [xmm2, xmm3, xmm4, xmm5, xmm6, xmm7, xmm8, xmm9, xmm10, xmm11, xmm12, xmm13, xmm14, xmm15];
    static readonly Reg[] s_calleeSaved = [rbx, rbp, r12, r13, r14, r15];

    static int s_generated;

    // V8SHARP_MAGLEV_NATIVE_STATS=1 prints the native compilations and why the others were declined at exit.
    static readonly bool s_stats;

    static MaglevNativeCodeGenerator() => s_stats = InitStats();

    static bool InitStats()
    {
        if (Environment.GetEnvironmentVariable("V8SHARP_MAGLEV_NATIVE_STATS") != "1") return false;
        AppDomain.CurrentDomain.ProcessExit += static (_, _) =>
        {
            var list = new List<KeyValuePair<string, int>>(DeclineReasons);
            list.Sort(static (a, b) => b.Value.CompareTo(a.Value));
            int declined = 0;
            foreach (KeyValuePair<string, int> e in list) declined += e.Value;
            Console.Error.WriteLine($"[maglev native: {Generated} compiled natively, {declined} declined]");
            foreach (KeyValuePair<string, int> e in list) Console.Error.WriteLine($"  {e.Value,6} {e.Key}");
        };
        return true;
    }

    /// <summary>Why graphs were generated as IL, with counts (diagnostics and tests).</summary>
    internal static readonly System.Collections.Concurrent.ConcurrentDictionary<string, int> DeclineReasons = new(StringComparer.Ordinal);

    /// <summary>The number of graphs compiled to native code in this process (tests).</summary>
    public static int Generated => Volatile.Read(ref s_generated);

    /// <summary>Native code is generated where it can run and the flag is on.</summary>
    public static bool IsEnabled(Isolate isolate) => isolate.Flags.maglev_native && ExecutableMemory.Supported &&
                                                      System.Runtime.Intrinsics.X86.Sse41.IsSupported;

    // ---- Per-value state ----------------------------------------------------------------------------------

    sealed class Val(ValueNode node)
    {
        public readonly ValueNode Node = node;
        public int Home = -1;
        public Reg Gp = no_reg;
        public int Xmm = -1;
        public int Start = int.MaxValue, End = -1;
        public bool NeedsKind;
        /// <summary>The index in MaglevNativeCode.Values, once it has one.</summary>
        public int Info = -1;
        public bool InGp => Gp != no_reg;
        public bool InXmm => Xmm >= 0;
        public XReg XmmReg => (XReg)Xmm;
        public ValueRepresentation Repr => Node.Representation;
    }

    readonly MaglevCompilationInfo _info;
    readonly Graph _graph;
    readonly MaglevCode _code;
    readonly Assembler _masm = new();
    readonly Val?[] _vals;
    readonly List<BasicBlock> _blocks = [];
    readonly Dictionary<BasicBlock, (int Start, int End, int Index)> _blockPos = new(ReferenceEqualityComparer.Instance);
    readonly Dictionary<BasicBlock, XLabel> _labels = new(ReferenceEqualityComparer.Instance);
    readonly Dictionary<NodeBase, int> _nodePos = new(ReferenceEqualityComparer.Instance);
    readonly Dictionary<Node, List<Val>> _liveAfter = new(ReferenceEqualityComparer.Instance);
    readonly Dictionary<ControlNode, List<Val>> _liveAfterControl = new(ReferenceEqualityComparer.Instance);
    int _homeWords = kFirstHome;

    // Constants (the code's constants table).
    readonly List<JSValue> _constants = [JSValue.Zero, JSValue.True, JSValue.False, JSValue.Null, JSValue.TheHole];
    readonly Dictionary<HeapObject, int> _constantIndex = new(ReferenceEqualityComparer.Instance);

    // Exits.
    readonly List<NativeExit> _exits = [];
    readonly List<(XLabel Label, int Exit, List<Val> Stores)> _exitStubs = [];
    readonly List<(XLabel Label, int ResumeId, List<Val> Live, XLabel Continue)> _resumes = [];
    readonly Dictionary<(EagerDeoptInfo, int, NativeExitKind), XLabel> _deoptLabels = new();
    readonly List<DeoptPoint> _deoptPoints = [];
    readonly List<(int, int)[]> _deoptValues = [];
    readonly List<(FeedbackVector Vector, int Slot)> _speculationFeedback = [];
    readonly List<NativeValueInfo> _valueInfos = [];
    readonly Dictionary<ValueNode, int> _constantInfos = new(ReferenceEqualityComparer.Instance);
    readonly List<(XLabel Label, BasicBlock From, BasicBlock To)> _edgeStubs = [];
    XLabel _epilogue;
    XLabel _abort;
    int _abortExit = -1;
    BasicBlock? _nextBlock;
    int _maxScratch;

    MaglevNativeCodeGenerator(MaglevCompilationInfo info, MaglevCode code)
    {
        _info = info;
        _graph = info.Graph;
        _code = code;
        _vals = new Val?[_graph.NodeCount + 1];
    }

    /// <summary>
    /// Generates native code for the graph, or returns null with the reason
    /// when it uses something the backend does not support.
    /// </summary>
    public static MaglevNativeCode? TryGenerate(MaglevCompilationInfo info, MaglevCode code, out string? reason)
    {
        reason = null;
        try
        {
            var generator = new MaglevNativeCodeGenerator(info, code);
            return generator.Generate();
        }
        catch (NativeUnsupportedException e)
        {
            reason = e.Message;
            DeclineReasons.AddOrUpdate(reason, 1, static (_, n) => n + 1);
            return null;
        }
    }

    static NativeUnsupportedException Unsupported(string what) => new(what);

    // ---- Driver --------------------------------------------------------------------------------------------

    MaglevNativeCode? Generate()
    {
        foreach (BasicBlock block in _graph.Blocks)
        {
            if (block.IsDead) continue;
            if (block.IsExceptionHandler) throw Unsupported("catch block");
            _blocks.Add(block);
        }
        if (_graph.HasResumableGenerator) throw Unsupported("generator");
        CheckSupported();
        foreach (BasicBlock block in _blocks)
        {
            foreach (Node node in block.Nodes)
            {
                if (node.Opcode == Opcode.HandleNoHeapWritesInterrupt && node.Int0 != 1) _snapshots = true;
            }
        }
        _snapshots |= _managedNodes.Count > 0;
        foreach (NodeBase n in _managedNodes)
        {
            _inlineStores |= n.Opcode is Opcode.StoreTaggedField or Opcode.StoreFixedArrayElement or Opcode.StoreContextSlot;
        }
        _fastCallArity = _managedNodes.Count > 0 ? -1 : FramelessArity();
        _scratchBase = _fastCallArity + 1;
        Number();
        ComputeLiveness();
        PlanKinds();
        AllocateRegisters();

        // Prologue: save the callee-saved registers, the context in rbx, then the entry.
        _epilogue = _masm.NewLabel();
        _abort = _masm.NewLabel();
        foreach (Reg r in s_calleeSaved) _masm.Pushq(r);
        _masm.Movq(rbx, rdi);
        _masm.Movq(kPollRegister, new Operand(rbx, kPollBudgetSlot * 8));
        XLabel resumeDispatch = _masm.NewLabel();
        _masm.Cmpq(new Operand(rbx, kEntryIdSlot * 8), 0);
        _masm.J(Condition.not_equal, resumeDispatch);
        for (int i = 0; i < _blocks.Count; i++)
        {
            _nextBlock = i + 1 < _blocks.Count ? _blocks[i + 1] : null;
            EmitBlock(_blocks[i]);
        }
        EmitEdgeStubs();
        foreach (Action slowPath in _slowPaths) slowPath();
        EmitExitStubs();
        // Resumable exits continue here: the live values from their homes (the
        // tagged ones from the spill array, as the managed side rebuilt them).
        _masm.Bind(resumeDispatch);
        _masm.Movq(rax, new Operand(rbx, kEntryIdSlot * 8));
        foreach ((XLabel label, int id, _, _) in _resumes)
        {
            _masm.Cmpq(rax, id);
            _masm.J(Condition.equal, label);
        }
        _masm.Ud2();
        foreach ((XLabel label, int _, List<Val> live, XLabel cont) in _resumes)
        {
            _masm.Bind(label);
            int j = 0;
            _masm.Movq(rcx, new Operand(rbx, NativeCode.kSpillSlot * 8));
            foreach (Val v in live)
            {
                if (v.Repr == ValueRepresentation.kTagged)
                {
                    _masm.Movq(rax, new Operand(rcx, j * 16));
                    _masm.Movq(HomeOp(v), rax);
                    if (v.Node.Opcode == Opcode.LoadElements)
                    {
                        // The cached elements' array of the (possibly moved) elements.
                        _masm.Movq(rdx, rcx);
                        EmitCacheElementsData(v);
                        _masm.Movq(rcx, rdx);
                    }
                    else
                    {
                        _masm.Movq(rax, new Operand(rcx, j * 16 + 8));
                        _masm.Movq(HomeOp(v, 1), rax);
                    }
                    j++;
                }
                else if (v.InGp)
                {
                    _masm.Movl(v.Gp, HomeOp(v));
                }
                else if (v.InXmm)
                {
                    _masm.Movsd(v.XmmReg, HomeOp(v));
                }
            }
            _masm.Jmp(cont);
        }
        if (_abortExit >= 0)
        {
            _masm.Bind(_abort);
            _masm.Movl(rax, _abortExit);
            _masm.Jmp(_epilogue);
        }
        _masm.Bind(_epilogue);
        // The poll budget left: the managed side counts the back edges (MaglevNativeCode.Profile).
        _masm.Movq(new Operand(rbx, kPollBudgetSlot * 8), kPollRegister);
        for (int i = s_calleeSaved.Length - 1; i >= 0; i--) _masm.Popq(s_calleeSaved[i]);
        _masm.Ret();
        if (_masm.PendingFixups != 0) throw new InvalidOperationException("native code: a jump to an unbound label");

        nint entry = ExecutableMemory.Add(_masm.Code);
        if (entry == 0) throw Unsupported("no executable memory left");
        int firstHome = kFirstHome;
        var native = new MaglevNativeCode
        {
            Code = _code,
            Entry = entry,
            CodeSize = _masm.PcOffset,
            ContextWords = _homeWords,
            Constants = _constants.ToArray(),
            Values = _valueInfos.ToArray(),
            Exits = _exits.ToArray(),
            DeoptValues = _deoptValues.ToArray(),
            ManagedSlots = _managedSlots,
            HasManagedNodes = _managedNodes.Count > 0,
            FirstHome = firstHome,
            HomeEnd = _homeWords,
        };
        _code.DeoptPoints = _deoptPoints.ToArray();
        _code.SpeculationFeedback = _speculationFeedback.ToArray();
        _code.MaxScratchSize = _maxScratch;
        _code.Native = native;
        _code.Entry = native.Run;
        if (_fastCallArity >= 0)
        {
            native.FastCallArity = _fastCallArity;
            _code.FastCall = native.CreateFastCall();
            _code.FastCallArity = _fastCallArity;
            _code.DirectEntryKind = MaglevDirectEntryKind.Frameless;
        }
        if (_info.Isolate.Flags.print_maglev_code) Print(native);
        Interlocked.Increment(ref s_generated);
        return native;
    }

    void Print(MaglevNativeCode native)
    {
        Console.WriteLine($"--- native maglev code of {MaglevCompiler.DebugName(_info.Function.Shared)}: {native.CodeSize} bytes at 0x{native.Entry:x}, " +
                          $"{_exits.Count} exits, {_homeWords - kFirstHome} home words ---");
        var sb = new System.Text.StringBuilder();
        ReadOnlySpan<byte> code = _masm.Code;
        for (int i = 0; i < code.Length; i++)
        {
            if (i % 32 == 0) sb.Append(i.ToString("x6", System.Globalization.CultureInfo.InvariantCulture)).Append(' ');
            sb.Append(code[i].ToString("x2", System.Globalization.CultureInfo.InvariantCulture));
            if (i % 32 == 31 || i == code.Length - 1) sb.Append('\n');
        }
        Console.Write(sb.ToString());
    }

    // ---- Selection -----------------------------------------------------------------------------------------

    int _fastCallArity = -1;
    readonly HashSet<BasicBlock> _loopBlocks = new(ReferenceEqualityComparer.Instance);
    readonly HashSet<NodeBase> _managedNodes = new(ReferenceEqualityComparer.Instance);
    /// <summary>
    /// Some exit may run code that changes the heap (a managed node, an
    /// interrupt of a loop with calls): reload-rebuilt values get snapshots.
    /// </summary>
    bool _snapshots;
    /// <summary>
    /// Stores write numbers inline (a number over a number): a slot load's
    /// value may be overwritten later, so slot loads keep their kind word and
    /// numbers and oddballs are rebuilt from it (an object in a slot is never
    /// overwritten by native code; managed stores snapshot it first).
    /// </summary>
    bool _inlineStores;
    int _managedSlots;

    bool InLoop(BasicBlock block) => _loopBlocks.Contains(block);

    /// <summary>
    /// Stores of a number over a number are done inline (no write barrier:
    /// only the payload changes, as the IL backend's EmitStoreTagged); the
    /// managed side does the others, so such stores may be in loops.
    /// </summary>
    static bool HasInlineFastPath(Node node) => node.Opcode is Opcode.StoreTaggedField or Opcode.StoreFixedArrayElement or Opcode.StoreContextSlot or
        Opcode.ToBoolean or Opcode.ToBooleanLogicalNot or Opcode.LoadTypedArrayLength;

    /// <summary>
    /// Nodes the managed side runs (MaglevNativeCode.Execute); <paramref name="why"/>
    /// is set when the node is one of them but its form is not supported.
    /// </summary>
    static bool IsManagedNode(Node node, out string? why)
    {
        why = null;
        switch (node.Opcode)
        {
            case Opcode.StoreTaggedField:
                if (node.Int0 < JSObject.kPropertyArrayStorageBase && !InObjectLayout.IsContiguous) why = "StoreTaggedField layout";
                return true;
            case Opcode.StoreFixedArrayElement:
            case Opcode.StoreContextSlot:
            case Opcode.StorePropertyCellValue:
            case Opcode.ToBoolean:
            case Opcode.ToBooleanLogicalNot:
            case Opcode.TestUndetectable:
            case Opcode.TestTypeOf:
            case Opcode.LoadTypedArrayLength:
            case Opcode.StringLength:
            case Opcode.CheckInstanceType:
                return true;
            case Opcode.CallKnownJSFunction:
            {
                var info = (KnownCallInfo)node.Obj0!;
                if (info.Argc > MaglevFastCalls.kMaxArity) why = "CallKnownJSFunction with more than six arguments";
                return true;
            }
            case Opcode.CallBuiltin:
            {
                var info = (CallBuiltinInfo)node.Obj0!;
                if (info.RegisterStores.Length > 0 || info.RegisterOutputs.Length > 0 || info.ForwardsArguments || info.Elided ||
                    info.ArgumentsKind != ArgumentsObjectKind.None)
                {
                    why = "CallBuiltin " + info.Name + " reads or writes the frame";
                    return true;
                }
                foreach (BuiltinArg arg in info.Args)
                {
                    if (arg.Kind is BuiltinArgKind.State or BuiltinArgKind.RegisterIndex or BuiltinArgKind.RegisterRef or BuiltinArgKind.FeedbackByteRef)
                    {
                        why = "CallBuiltin " + info.Name + " takes " + arg.Kind;
                        return true;
                    }
                }
                foreach (System.Reflection.ParameterInfo p in info.Method.GetParameters())
                {
                    if (p.ParameterType.IsByRef) { why = "CallBuiltin " + info.Name + " by reference"; return true; }
                }
                return true;
            }
            default:
                return false;
        }
    }
    /// <summary>The first deopt scratch slot of the translations' values (after the receiver and arguments of a frameless entry).</summary>
    int _scratchBase;

    /// <summary>
    /// The arity of the frameless direct entry (MaglevNativeCode.FastCall*), or
    /// -1 without one: a function (not OSR code, not a class constructor) of
    /// at most kMaxArity parameters whose code reads only its closure,
    /// context, receiver and parameters from the frame, and handles no
    /// interrupts (which need the frame; loops without calls only deopt for
    /// them).
    /// </summary>
    int FramelessArity()
    {
        if (_info.IsOsr) return -1;
        SharedFunctionInfo shared = _info.Toplevel.SharedFunctionInfo;
        if (shared.IsClassConstructor || Globals.IsResumableFunction(shared.Kind)) return -1;
        int formal = _info.Toplevel.Bytecode.ParameterCount - 1;
        if (formal > MaglevFastCalls.kMaxArity) return -1;
        foreach (BasicBlock block in _blocks)
        {
            foreach (Node node in block.Nodes)
            {
                if (node.Opcode == Opcode.HandleNoHeapWritesInterrupt && node.Int0 != 1) return -1;
                if (node.Opcode != Opcode.InitialValue) continue;
                int r = node.Int0;
                if (r == Interpreter.InterpreterRuntime.kClosureOffset || r == Interpreter.InterpreterRuntime.kContextOffset) continue;
                bool parameter = false;
                for (int i = 0; i <= formal; i++) parameter |= IRegister.FromParameterIndex(i).Index == r;
                if (!parameter) return -1;
            }
        }
        return formal;
    }

    /// <summary>Functions whose native code lost to the IL backend at run time (MaglevNativeCode.Profile).</summary>
    static readonly System.Runtime.CompilerServices.ConditionalWeakTable<SharedFunctionInfo, object> s_declinedAtRunTime = new();

    internal static void DeclineAtRunTime(SharedFunctionInfo shared) => s_declinedAtRunTime.AddOrUpdate(shared, shared);

    void CheckSupported()
    {
        if (s_declinedAtRunTime.TryGetValue(_info.Toplevel.SharedFunctionInfo, out _)) throw Unsupported("managed exits outweighed its loops at run time");
        // The blocks of loops (from a header to the block of its JumpLoop, in block order).
        for (int i = 0; i < _blocks.Count; i++)
        {
            if (_blocks[i].Control is { Opcode: Opcode.JumpLoop, Target: { } header })
            {
                int h = _blocks.IndexOf(header);
                for (int k = Math.Max(h, 0); k <= i; k++) _loopBlocks.Add(_blocks[k]);
            }
        }
        if (_info.Isolate.Flags.maglev_native_loops_only)
        {
            bool loop = false;
            foreach (BasicBlock block in _blocks) loop |= block.Control?.Opcode == Opcode.JumpLoop;
            if (!loop) throw Unsupported("no loop");
        }
        foreach (BasicBlock block in _blocks)
        {
            foreach (Phi phi in block.Phis)
            {
                if (phi.IsExceptionPhi) throw Unsupported("exception phi");
                CheckRepresentation(phi);
            }
            bool interrupt = false;
            foreach (Node node in block.Nodes)
            {
                if (node.Unit is { IsInline: true } && node.Opcode == Opcode.HandleNoHeapWritesInterrupt && node.Int0 != 1)
                {
                    // Its runtime call would need the inlined frames pushed (EmitEnsureInlinedFrames).
                    throw Unsupported("interrupt check in an inlined function");
                }
                if (node.ExceptionHandler is not null) throw Unsupported("throwing node");
                if (IsManagedNode(node, out string? why))
                {
                    // Run by the managed side (an exit and a resume): not in a loop,
                    // where the round trip would cost more than RyuJIT's code.
                    if (why is not null) throw Unsupported(why);
                    // A call or a throw in an inlined function needs its frame pushed (EmitEnsureInlinedFrames).
                    if (node.Unit is { IsInline: true } && (node.IsCall || node.CanThrow || node.LazyDeoptInfo is not null))
                    {
                        throw Unsupported("managed call in an inlined function: " + node.Opcode);
                    }
                    if (InLoop(block) && !HasInlineFastPath(node)) throw Unsupported("managed node in a loop: " + node.Opcode);
                    _managedNodes.Add(node);
                }
                else
                {
                    if (node.LazyDeoptInfo is not null || node.IsCall) throw Unsupported("call " + node.Opcode);
                    if (!IsSupportedNode(node)) throw Unsupported(node.Opcode.ToString());
                }
                if (node.LazyDeoptInfo is { } lazy) CheckDeoptFrame(lazy);
                if (node is ValueNode v) CheckRepresentation(v);
                if (node.EagerDeoptInfo is { } eager) CheckDeoptFrame(eager);
                if (node.Opcode == Opcode.HandleNoHeapWritesInterrupt) interrupt = true;
            }
            ControlNode? c = block.Control;
            if (c is null) throw Unsupported("block without control");
            if (!IsSupportedControl(c)) throw Unsupported(c.Opcode.ToString());
            if (c.EagerDeoptInfo is { } ce) CheckDeoptFrame(ce);
            // Every back edge must be able to exit (interrupts, GC polls).
            if (c.Opcode == Opcode.JumpLoop && !interrupt) throw Unsupported("loop without interrupt check");
        }
    }

    static void CheckRepresentation(ValueNode v)
    {
        if (v.Representation == ValueRepresentation.kBit) throw Unsupported("bit value");
    }

    static void CheckDeoptFrame(DeoptInfo info)
    {
        for (DeoptFrame? f = info.TopFrame; f is not null; f = f.Parent)
        {
            if (f is not InterpretedDeoptFrame frame) throw Unsupported("deopt frame kind " + f.GetType().Name);
            foreach ((IRegister _, ValueNode value) in frame.Values)
            {
                // Elided allocations and arguments objects (the Deoptimizer materializes them): not generated.
                if (value is InlinedAllocation || value.Opcode == Opcode.CallBuiltin && ((CallBuiltinInfo)value.Obj0!).Elided)
                {
                    throw Unsupported("captured object in a deopt frame");
                }
            }
        }
    }

    static bool IsSupportedNode(Node node) => node.Opcode switch
    {
        Opcode.InitialValue or Opcode.Identity => true,
        Opcode.Int32AddWithOverflow or Opcode.Int32SubtractWithOverflow or Opcode.Int32MultiplyWithOverflow or
            Opcode.Int32Add or Opcode.Int32Subtract or Opcode.Int32Multiply or Opcode.Int32DivideWithOverflow or
            Opcode.Int32ModulusWithOverflow or Opcode.Int32IncrementWithOverflow or Opcode.Int32DecrementWithOverflow or
            Opcode.Int32NegateWithOverflow or Opcode.Int32AbsWithOverflow or Opcode.Int32BitwiseAnd or Opcode.Int32BitwiseOr or
            Opcode.Int32BitwiseXor or Opcode.Int32ShiftLeft or Opcode.Int32ShiftRight or Opcode.Int32ShiftRightLogical or
            Opcode.Int32BitwiseNot or Opcode.Int32Compare or Opcode.Int32ToBoolean => true,
        Opcode.Float64Add or Opcode.Float64Subtract or Opcode.Float64Multiply or Opcode.Float64Divide or
            Opcode.Float64Modulus or Opcode.Float64Negate or Opcode.Float64Abs or Opcode.Float64Sqrt or Opcode.Float64Round or Opcode.Float64Min or
            Opcode.Float64Max or Opcode.Float64Compare or Opcode.Float64ToBoolean => true,
        Opcode.CheckedSmiUntag or Opcode.CheckedNumberToInt32 or Opcode.UnsafeSmiUntag or Opcode.CheckedNumberOrOddballToFloat64 or
            Opcode.UnsafeNumberToFloat64 or Opcode.Int32ToNumber or Opcode.Uint32ToNumber or Opcode.Float64ToTagged or
            Opcode.HoleyFloat64ToTagged or Opcode.ChangeInt32ToFloat64 or Opcode.ChangeUint32ToFloat64 or
            Opcode.CheckedFloat64ToInt32 or Opcode.TruncateFloat64ToInt32 or Opcode.TruncateCheckedNumberOrOddballToInt32 or
            Opcode.CheckedUint32ToInt32 or Opcode.CheckedHoleyFloat64ToFloat64 => true,
        Opcode.CheckSmi or Opcode.CheckNumber or Opcode.CheckHeapObject or Opcode.CheckInt32IsSmi or
            Opcode.CheckInt32Condition or Opcode.CheckNotHole or Opcode.CheckValue => true,
        Opcode.CheckMaps => CheckMapsSupported(node),
        Opcode.CheckString => true,
        // Inlined frames pushed lazily need no code: native code makes no
        // calls, and a deopt pushes the frames its translation has
        // (Deoptimizer.Deoptimize, "a lazily pushed inlined frame").
        Opcode.EnterInlinedFrame or Opcode.LeaveInlinedFrame => !((MaglevCompilationUnit)node.Obj0!).EagerFrame,
        Opcode.LoadTaggedField or Opcode.LoadDoubleField => node.Int0 >= JSObject.kPropertyArrayStorageBase || InObjectLayout.IsContiguous,
        Opcode.LoadContextSlot or Opcode.LoadMap or Opcode.LoadPropertyCellValue or Opcode.ConvertHoleToUndefined => true,
        Opcode.CheckTypedArrayValid => true,
        Opcode.LoadTypedArrayElement => TypedElementSize((ElementsKind)node.Int0) > 0,
        // (Stores that ignore out-of-bounds indices, and clamped stores of doubles, stay IL.)
        Opcode.StoreTypedArrayElement => node.Int1 == 0 && TypedElementSize((ElementsKind)node.Int0) > 0 &&
                                         !((ElementsKind)node.Int0 == ElementsKind.UINT8_CLAMPED_ELEMENTS && !IsInt32Stored(node.Inputs[2])),
        Opcode.LoadElements or Opcode.LoadFixedArrayElement or Opcode.LoadFixedDoubleArrayElement or
            Opcode.LoadHoleyFixedDoubleArrayElement or Opcode.StoreFixedDoubleArrayElement or Opcode.LoadJSArrayLength => true,
        Opcode.TaggedEqual or Opcode.TaggedNotEqual or Opcode.LogicalNot => true,
        Opcode.HandleNoHeapWritesInterrupt => true,
        _ => false,
    };

    static bool CheckMapsSupported(Node node)
    {
        // CheckMapsWithMigration (a migration target among the maps) continues
        // after migrating: that would need a resumable exit. Not generated.
        foreach (Map map in (Map[])node.Obj0!)
        {
            if (map.IsMigrationTarget) return false;
        }
        return true;
    }

    static bool IsSupportedControl(ControlNode c) => c.Opcode switch
    {
        Opcode.Jump or Opcode.JumpLoop or Opcode.Return or Opcode.Deopt => true,
        Opcode.BranchIfInt32Compare => !c.Unsigned || c.Operation == CompareOperation.kLessThan,
        Opcode.BranchIfToBooleanTrue or Opcode.BranchIfFloat64Compare or Opcode.BranchIfReferenceEqual or Opcode.BranchIfRootConstant or
            Opcode.BranchIfInt32ToBooleanTrue or Opcode.BranchIfFloat64ToBooleanTrue => true,
        _ => false,
    };

    // ---- Numbering and liveness ------------------------------------------------------------------------------

    // A value outside the blocks (an elided allocation, a virtual object) has no location: the graph goes to IL.
    Val V(ValueNode node) => _vals[node.Id] ?? throw Unsupported($"value without a location ({node.Opcode})");

    Val? TryV(ValueNode node) => node.IsConstant ? null : _vals[node.Id];

    void Number()
    {
        int pos = 0;
        for (int b = 0; b < _blocks.Count; b++)
        {
            BasicBlock block = _blocks[b];
            int start = pos;
            foreach (Phi phi in block.Phis) _vals[phi.Id] = new Val(phi);
            pos += 2;
            foreach (Node node in block.Nodes)
            {
                _nodePos[node] = pos;
                if (node is ValueNode v)
                {
                    // An Identity of the same representation is its input: one location, no code.
                    _vals[v.Id] = v.Opcode == Opcode.Identity && !v.Inputs[0].IsConstant && _vals[v.Inputs[0].Id] is { } source &&
                                  source.Repr == v.Representation
                        ? source
                        : new Val(v);
                }
                pos += 2;
            }
            _nodePos[block.Control!] = pos;
            pos += 2;
            _blockPos[block] = (start, pos - 1, b);
        }
        foreach (Val? v in _vals)
        {
            if (v is null || v.Home >= 0) continue;
            v.Home = _homeWords;
            // Tagged: address, payload, kind, snapshot flag (MaglevNativeCode.Value).
            _homeWords += v.Repr == ValueRepresentation.kTagged ? 4 : 1;
        }
    }

    /// <summary>The values a deopt exit needs saved: the frame state's, and what rebuilding them reads.</summary>
    void DeoptUses(DeoptInfo info, HashSet<ValueNode> into)
    {
        for (DeoptFrame? f = info.TopFrame; f is not null; f = f.Parent)
        {
            var frame = (InterpretedDeoptFrame)f;
            foreach ((IRegister _, ValueNode value) in frame.Values) AddClosure(value, into);
            if (frame.Unit.IsInline && frame.Unit.Function is null) AddClosure(frame.Closure, into);
        }
    }

    /// <summary>A value and, for values rebuilt by repeating their load, the values the load reads.</summary>
    static void AddClosure(ValueNode value, HashSet<ValueNode> into)
    {
        if (value.IsConstant || !into.Add(value)) return;
        switch (value.Opcode)
        {
            case Opcode.LoadElements:
            case Opcode.LoadFixedArrayElement:
            case Opcode.LoadTaggedField:
            case Opcode.LoadContextSlot:
            case Opcode.LoadMap:
            case Opcode.LoadPropertyCellValue:
            case Opcode.ConvertHoleToUndefined:
            case Opcode.Identity when value.IsTagged:
                foreach (ValueNode input in value.Inputs) AddClosure(input, into);
                break;
        }
    }

    readonly Dictionary<BasicBlock, HashSet<ValueNode>> _liveIn = new(ReferenceEqualityComparer.Instance);
    readonly Dictionary<BasicBlock, HashSet<ValueNode>> _liveOut = new(ReferenceEqualityComparer.Instance);

    void ComputeLiveness()
    {
        foreach (BasicBlock b in _blocks)
        {
            _liveIn[b] = new(ReferenceEqualityComparer.Instance);
            _liveOut[b] = new(ReferenceEqualityComparer.Instance);
        }
        bool changed = true;
        while (changed)
        {
            changed = false;
            for (int i = _blocks.Count - 1; i >= 0; i--)
            {
                BasicBlock b = _blocks[i];
                var live = new HashSet<ValueNode>(ReferenceEqualityComparer.Instance);
                foreach (BasicBlock s in b.Successors())
                {
                    if (s.IsDead) continue;
                    foreach (ValueNode v in _liveIn[s]) live.Add(v);
                    int index = s.PredecessorIndexOf(b);
                    foreach (Phi phi in s.Phis) AddUse(phi.Inputs[index], live);
                }
                if (!live.SetEquals(_liveOut[b]))
                {
                    _liveOut[b] = new HashSet<ValueNode>(live, ReferenceEqualityComparer.Instance);
                    changed = true;
                }
                ControlNode c = b.Control!;
                foreach (ValueNode input in c.Inputs) AddClosure(input, live);
                if (c.EagerDeoptInfo is { } ce) DeoptUses(ce, live);
                for (int k = b.Nodes.Count - 1; k >= 0; k--)
                {
                    Node node = b.Nodes[k];
                    if (node.LazyDeoptInfo is { } lz) DeoptUses(lz, live);
                    // (After the lazy frame's uses: the frame may hold the node's own value.)
                    if (node is ValueNode v) live.Remove(v);
                    if (IsResumable(node))
                    {
                        // A resumable exit: the live values are rebuilt; so are what rebuilding them reads.
                        var closure = new HashSet<ValueNode>(ReferenceEqualityComparer.Instance);
                        foreach (ValueNode l in live) AddClosure(l, closure);
                        foreach (ValueNode l in closure) live.Add(l);
                        _liveAfter[node] = null!;
                    }
                    foreach (ValueNode input in node.Inputs) AddClosure(input, live);
                    if (node.EagerDeoptInfo is { } e) DeoptUses(e, live);
                }
                foreach (Phi phi in b.Phis) live.Remove(phi);
                if (!live.SetEquals(_liveIn[b]))
                {
                    _liveIn[b] = live;
                    changed = true;
                }
            }
        }

        // Intervals: the hull of the positions where a value is defined, used or live.
        for (int i = 0; i < _blocks.Count; i++)
        {
            BasicBlock b = _blocks[i];
            (int start, int end, _) = _blockPos[b];
            foreach (ValueNode v in _liveIn[b]) Extend(V(v), start);
            foreach (ValueNode v in _liveOut[b]) Extend(V(v), end);
            foreach (Phi phi in b.Phis)
            {
                Val p = V(phi);
                Extend(p, start);
                // The phi's moves write it at the end of each predecessor.
                foreach (BasicBlock pred in b.Predecessors)
                {
                    if (_blockPos.TryGetValue(pred, out var pp)) Extend(p, pp.End);
                }
            }
            var after = new HashSet<ValueNode>(_liveOut[b], ReferenceEqualityComparer.Instance);
            ControlNode c = b.Control!;
            int cpos = _nodePos[c];
            foreach (ValueNode input in c.Inputs)
            {
                var closure = new HashSet<ValueNode>(ReferenceEqualityComparer.Instance);
                AddClosure(input, closure);
                foreach (ValueNode u in closure) Extend(V(u), cpos);
            }
            if (c.EagerDeoptInfo is { } ce)
            {
                var uses = new HashSet<ValueNode>(ReferenceEqualityComparer.Instance);
                DeoptUses(ce, uses);
                foreach (ValueNode u in uses) Extend(V(u), cpos);
            }
            if (c.Opcode == Opcode.BranchIfToBooleanTrue)
            {
                // Its managed path resumes before the branch: what the successors need.
                var closure = new HashSet<ValueNode>(ReferenceEqualityComparer.Instance);
                foreach (ValueNode l in _liveOut[b]) AddClosure(l, closure);
                var list = new List<Val>();
                foreach (ValueNode l in closure)
                {
                    Val lv = V(l);
                    Extend(lv, cpos);
                    if (!list.Contains(lv)) list.Add(lv);
                }
                list.Sort(static (x, y) => x.Node.Id.CompareTo(y.Node.Id));
                _liveAfterControl[c] = list;
            }
            foreach (ValueNode input in c.Inputs) AddClosure(input, after);
            if (c.EagerDeoptInfo is { } ce2) DeoptUses(ce2, after);
            for (int k = b.Nodes.Count - 1; k >= 0; k--)
            {
                Node node = b.Nodes[k];
                int pos = _nodePos[node];
                bool resultLive = false;
                if (node is ValueNode v)
                {
                    Extend(V(v), pos);
                    resultLive = after.Remove(v);
                }
                if (node.LazyDeoptInfo is { } lz)
                {
                    var uses = new HashSet<ValueNode>(ReferenceEqualityComparer.Instance);
                    DeoptUses(lz, uses);
                    foreach (ValueNode u in uses)
                    {
                        if (ReferenceEquals(u, node)) continue;
                        Extend(V(u), pos);
                        after.Add(u);
                    }
                }
                if (IsResumable(node))
                {
                    var closure = new HashSet<ValueNode>(ReferenceEqualityComparer.Instance);
                    foreach (ValueNode l in after) AddClosure(l, closure);
                    // A managed node's own value, when used later: the resume reloads it.
                    if (resultLive) closure.Add((ValueNode)node);
                    var list = new List<Val>();
                    foreach (ValueNode l in closure)
                    {
                        Val lv = V(l);
                        Extend(lv, pos);
                        list.Add(lv);
                        // (Not the node's own value: it is not live before the node.)
                        if (!ReferenceEquals(l, node)) after.Add(l);
                    }
                    list.Sort(static (x, y) => x.Node.Id.CompareTo(y.Node.Id));
                    _liveAfter[node] = list;
                }
                foreach (ValueNode input in node.Inputs)
                {
                    // With what rebuilding it reads: an exit of this node (a
                    // Return, a migrating CheckMaps) may rebuild the input.
                    var closure = new HashSet<ValueNode>(ReferenceEqualityComparer.Instance);
                    AddClosure(input, closure);
                    foreach (ValueNode u in closure)
                    {
                        Extend(V(u), pos);
                        after.Add(u);
                    }
                }
                if (node.EagerDeoptInfo is { } e)
                {
                    var uses = new HashSet<ValueNode>(ReferenceEqualityComparer.Instance);
                    DeoptUses(e, uses);
                    foreach (ValueNode u in uses)
                    {
                        Extend(V(u), pos);
                        after.Add(u);
                    }
                }
            }
        }
    }

    bool IsResumable(Node node) => node.Opcode == Opcode.HandleNoHeapWritesInterrupt || _managedNodes.Contains(node);

    static void AddUse(ValueNode v, HashSet<ValueNode> live)
    {
        if (!v.IsConstant) live.Add(v);
    }

    static void Extend(Val v, int pos)
    {
        if (pos < v.Start) v.Start = pos;
        if (pos > v.End) v.End = pos;
    }

    /// <summary>
    /// Which tagged values keep a kind word: the inputs of tagged phis (and of
    /// what feeds them), whose value at an exit is known only at run time.
    /// </summary>
    void PlanKinds()
    {
        var work = new Stack<ValueNode>();
        foreach (BasicBlock b in _blocks)
        {
            foreach (Phi phi in b.Phis)
            {
                if (phi.IsTagged) work.Push(phi);
            }
        }
        while (work.Count > 0)
        {
            ValueNode n = work.Pop();
            Val v = V(n);
            if (v.NeedsKind) continue;
            v.NeedsKind = true;
            if (n is Phi phi)
            {
                foreach (ValueNode input in phi.Inputs)
                {
                    if (!input.IsConstant && input.IsTagged) work.Push(input);
                }
            }
            else if (n.Opcode == Opcode.Identity && !n.Inputs[0].IsConstant && n.Inputs[0].IsTagged)
            {
                work.Push(n.Inputs[0]);
            }
        }
    }

    // ---- Register allocation (linear scan) ------------------------------------------------------------------

    void AllocateRegisters()
    {
        var intervals = new List<Val>();
        for (int id = 0; id < _vals.Length; id++)
        {
            Val? v = _vals[id];
            // (An aliased Identity shares its input's Val: once.)
            if (v is null || v.Node.Id != id || v.End < 0 || v.Repr == ValueRepresentation.kTagged) continue;
            intervals.Add(v);
        }
        intervals.Sort(static (a, b) => a.Start != b.Start ? a.Start.CompareTo(b.Start) : a.Node.Id.CompareTo(b.Node.Id));
        var activeGp = new List<Val>();
        var activeXmm = new List<Val>();
        var freeGp = new Stack<Reg>();
        var freeXmm = new Stack<int>();
        for (int i = s_allocatableGp.Length - 1; i >= 0; i--) freeGp.Push(s_allocatableGp[i]);
        for (int i = s_allocatableXmm.Length - 1; i >= 0; i--) freeXmm.Push((int)s_allocatableXmm[i]);
        foreach (Val v in intervals)
        {
            bool xmm = v.Repr is ValueRepresentation.kFloat64 or ValueRepresentation.kHoleyFloat64;
            List<Val> active = xmm ? activeXmm : activeGp;
            // Expire the intervals that ended before this one starts.
            for (int k = active.Count - 1; k >= 0; k--)
            {
                Val a = active[k];
                if (a.End < v.Start)
                {
                    active.RemoveAt(k);
                    if (xmm) freeXmm.Push(a.Xmm);
                    else freeGp.Push(a.Gp);
                }
            }
            if ((xmm ? freeXmm.Count : freeGp.Count) > 0)
            {
                if (xmm) v.Xmm = freeXmm.Pop();
                else v.Gp = freeGp.Pop();
                active.Add(v);
                continue;
            }
            // Spill the interval that ends last (it stays in its home).
            Val victim = v;
            foreach (Val a in active)
            {
                if (a.End > victim.End) victim = a;
            }
            if (victim == v) continue;
            if (xmm)
            {
                v.Xmm = victim.Xmm;
                victim.Xmm = -1;
            }
            else
            {
                v.Gp = victim.Gp;
                victim.Gp = no_reg;
            }
            active.Remove(victim);
            active.Add(v);
        }
    }

    // ---- Operands ----------------------------------------------------------------------------------------------

    static Operand HomeOp(Val v, int word = 0) => new(rbx, (v.Home + word) * 8);

    XLabel BlockLabel(BasicBlock b)
    {
        if (!_labels.TryGetValue(b, out XLabel l)) _labels[b] = l = _masm.NewLabel();
        return l;
    }

    int ConstantIndex(HeapObject o)
    {
        if (ReferenceEquals(o, NumberTag.Instance)) return kConstNumberTag;
        if (ReferenceEquals(o, Oddball.True)) return kConstTrue;
        if (ReferenceEquals(o, Oddball.False)) return kConstFalse;
        if (ReferenceEquals(o, Oddball.Null)) return kConstNull;
        if (ReferenceEquals(o, Oddball.TheHole)) return kConstTheHole;
        if (!_constantIndex.TryGetValue(o, out int i))
        {
            i = _constants.Count;
            _constants.Add(JSValue.FromObject(o));
            _constantIndex[o] = i;
        }
        return i;
    }

    /// <summary>The address of a constant object into <paramref name="dst"/> (from the constants table).</summary>
    void LoadConstantAddress(Reg dst, int index)
    {
        _masm.Movq(dst, new Operand(rbx, NativeCode.kConstantsSlot * 8));
        _masm.Movq(dst, new Operand(dst, index * 16));
    }

    /// <summary>Loads an Int32/Uint32 value (or an integral constant) into <paramref name="dst"/>.</summary>
    void LoadInt32(ValueNode n, Reg dst)
    {
        if (n.IsConstant)
        {
            if (!n.TryGetInt32Constant(out int c)) throw Unsupported("not an int32 constant");
            _masm.Movl(dst, c);
            return;
        }
        Val v = V(n);
        if (v.Repr is not (ValueRepresentation.kInt32 or ValueRepresentation.kUint32)) throw Unsupported($"{n} as int32");
        if (v.InGp)
        {
            if (v.Gp != dst) _masm.Movl(dst, v.Gp);
        }
        else
        {
            _masm.Movl(dst, HomeOp(v));
        }
    }

    /// <summary>The register holding an Int32 value: its own, or <paramref name="scratch"/> loaded with it.</summary>
    Reg Int32Reg(ValueNode n, Reg scratch)
    {
        if (!n.IsConstant && V(n) is { InGp: true } v && v.Repr is ValueRepresentation.kInt32 or ValueRepresentation.kUint32) return v.Gp;
        LoadInt32(n, scratch);
        return scratch;
    }

    void LoadFloat64(ValueNode n, XReg dst)
    {
        if (n.IsConstant)
        {
            if (!n.TryGetFloat64Constant(out double d)) throw Unsupported("not a number constant");
            _masm.Movq(r11, BitConverter.DoubleToInt64Bits(d));
            _masm.Movq(dst, r11);
            return;
        }
        Val v = V(n);
        switch (v.Repr)
        {
            case ValueRepresentation.kFloat64:
            case ValueRepresentation.kHoleyFloat64:
                if (v.InXmm)
                {
                    if (v.XmmReg != dst) _masm.Movapd(dst, v.XmmReg);
                }
                else
                {
                    _masm.Movsd(dst, HomeOp(v));
                }
                return;
            case ValueRepresentation.kInt32:
                _masm.Xorpd(dst, dst);
                _masm.Cvtlsi2sd(dst, Int32Reg(n, r11));
                return;
            case ValueRepresentation.kUint32:
                LoadInt32(n, r11);
                _masm.Xorpd(dst, dst);
                _masm.Cvtqsi2sd(dst, r11);
                return;
            default:
                throw Unsupported($"{n} as float64");
        }
    }

    XReg Float64Reg(ValueNode n, XReg scratch)
    {
        if (!n.IsConstant && V(n) is { InXmm: true } v) return v.XmmReg;
        LoadFloat64(n, scratch);
        return scratch;
    }

    /// <summary>Loads a value as a tagged JSValue: its object address and its payload.</summary>
    void LoadTagged(ValueNode n, Reg ptr, Reg bits)
    {
        if (n.IsConstant)
        {
            JSValue c = n.ConstantValue();
            if (c.IsNumber)
            {
                LoadConstantAddress(ptr, kConstNumberTag);
                _masm.Movq(bits, c._bits);
            }
            else if (c.IsUndefined)
            {
                _masm.Movl(ptr, 0);
                _masm.Movl(bits, 0);
            }
            else
            {
                LoadConstantAddress(ptr, ConstantIndex(c.Object));
                _masm.Movq(bits, c._bits);
            }
            return;
        }
        Val v = V(n);
        switch (v.Repr)
        {
            case ValueRepresentation.kTagged:
                _masm.Movq(ptr, HomeOp(v));
                _masm.Movq(bits, HomeOp(v, 1));
                return;
            case ValueRepresentation.kHoleyFloat64:
                throw Unsupported("holey float64 as tagged");
            default:
                LoadFloat64(n, xmm1);
                _masm.Movq(bits, xmm1);
                LoadConstantAddress(ptr, kConstNumberTag);
                return;
        }
    }

    /// <summary>The object address of a tagged value into <paramref name="ptr"/>.</summary>
    void LoadTaggedPtr(ValueNode n, Reg ptr)
    {
        if (n.IsConstant)
        {
            JSValue c = n.ConstantValue();
            if (c.IsNumber) LoadConstantAddress(ptr, kConstNumberTag);
            else if (c.IsUndefined) _masm.Movl(ptr, 0);
            else LoadConstantAddress(ptr, ConstantIndex(c.Object));
            return;
        }
        Val v = V(n);
        if (v.Repr != ValueRepresentation.kTagged)
        {
            LoadConstantAddress(ptr, kConstNumberTag);
            return;
        }
        _masm.Movq(ptr, HomeOp(v));
    }

    /// <summary>The number payload of a tagged value (as bits) into <paramref name="bits"/>.</summary>
    void LoadTaggedBits(ValueNode n, Reg bits)
    {
        if (n.IsConstant)
        {
            _masm.Movq(bits, n.ConstantValue()._bits);
            return;
        }
        Val v = V(n);
        if (v.Repr != ValueRepresentation.kTagged)
        {
            LoadFloat64(n, xmm1);
            _masm.Movq(bits, xmm1);
            return;
        }
        _masm.Movq(bits, HomeOp(v, 1));
    }

    void StoreInt32Result(Val v, Reg src)
    {
        if (v.InGp)
        {
            if (v.Gp != src) _masm.Movl(v.Gp, src);
        }
        else
        {
            _masm.Movl(HomeOp(v), src);
        }
    }

    void StoreFloat64Result(Val v, XReg src)
    {
        if (v.InXmm)
        {
            if (v.XmmReg != src) _masm.Movapd(v.XmmReg, src);
        }
        else
        {
            _masm.Movsd(HomeOp(v), src);
        }
    }

    /// <summary>A tagged number result: the number tag's address and the double's bits.</summary>
    void StoreTaggedNumber(Val v, XReg value)
    {
        LoadConstantAddress(rax, kConstNumberTag);
        _masm.Movq(HomeOp(v), rax);
        _masm.Movsd(HomeOp(v, 1), value);
        if (v.NeedsKind) _masm.Movq(HomeOp(v, 2), (int)NativeTaggedKind.kNumber);
    }

    /// <summary>A tagged boolean result from the 0/1 in eax: True or False, and the kind word (always kept).</summary>
    void StoreTaggedBoolean(Val v)
    {
        _masm.Movq(rcx, new Operand(rbx, NativeCode.kConstantsSlot * 8));
        _masm.Movq(rdx, new Operand(rcx, kConstTrue * 16));
        _masm.Movq(rcx, new Operand(rcx, kConstFalse * 16));
        _masm.Testl(rax, rax);
        _masm.Cmovq(Condition.not_equal, rcx, rdx);
        _masm.Movq(HomeOp(v), rcx);
        _masm.Movq(HomeOp(v, 1), 0);
        _masm.Movl(rcx, (int)NativeTaggedKind.kFalse);
        _masm.Subl(rcx, rax);
        _masm.Movq(HomeOp(v, 2), rcx);
    }

    /// <summary>The kind word of the tagged value whose address is in rax (clobbers rcx, rdx).</summary>
    void EmitClassify(Val v)
    {
        XLabel done = _masm.NewLabel();
        _masm.Movl(rcx, (int)NativeTaggedKind.kUndefined);
        _masm.Testq(rax, rax);
        _masm.J(Condition.equal, done);
        _masm.Movq(rdx, new Operand(rbx, NativeCode.kConstantsSlot * 8));
        (int Index, NativeTaggedKind Kind)[] table =
        [
            (kConstNumberTag, NativeTaggedKind.kNumber), (kConstTrue, NativeTaggedKind.kTrue), (kConstFalse, NativeTaggedKind.kFalse),
            (kConstNull, NativeTaggedKind.kNull), (kConstTheHole, NativeTaggedKind.kTheHole),
        ];
        foreach ((int index, NativeTaggedKind kind) in table)
        {
            _masm.Movl(rcx, (int)kind);
            _masm.Cmpq(rax, new Operand(rdx, index * 16));
            _masm.J(Condition.equal, done);
        }
        _masm.Movl(rcx, (int)NativeTaggedKind.kOther);
        _masm.Bind(done);
        _masm.Movq(HomeOp(v, 2), rcx);
    }

    // ---- Exits ------------------------------------------------------------------------------------------------

    /// <summary>The index of the run-time description of a value (MaglevNativeCode.Value).</summary>
    int Info(ValueNode n)
    {
        if (n.IsConstant)
        {
            if (!_constantInfos.TryGetValue(n, out int ci))
            {
                ci = _valueInfos.Count;
                _valueInfos.Add(new NativeValueInfo { Kind = NativeValueKind.kConstant, Constant = n.ConstantValue(), NodeId = n.Id });
                _constantInfos[n] = ci;
            }
            return ci;
        }
        Val v = V(n);
        if (v.Info >= 0) return v.Info;
        var info = new NativeValueInfo { Home = v.Home, NodeId = n.Id };
        v.Info = _valueInfos.Count;
        _valueInfos.Add(info);
        if (_managedNodes.Contains(v.Node))
        {
            // A managed node's value: untagged in its home, tagged in its managed
            // slot. (ToBoolean has an inline path: both paths write its kind word.)
            if (v.Repr == ValueRepresentation.kTagged && v.Node.Opcode is not (Opcode.ToBoolean or Opcode.ToBooleanLogicalNot))
            {
                info.SnapSlot = _managedSlots++;
                info.Kind = NativeValueKind.kManaged;
                return v.Info;
            }
        }
        switch (v.Repr)
        {
            case ValueRepresentation.kInt32: info.Kind = NativeValueKind.kInt32; return v.Info;
            case ValueRepresentation.kUint32: info.Kind = NativeValueKind.kUint32; return v.Info;
            case ValueRepresentation.kFloat64: info.Kind = NativeValueKind.kFloat64; return v.Info;
            case ValueRepresentation.kHoleyFloat64: info.Kind = NativeValueKind.kHoleyFloat64; return v.Info;
        }
        switch (n.Opcode)
        {
            case Opcode.InitialValue:
                info.Kind = NativeValueKind.kFrameSlot;
                info.FrameIndex = n.Int0;
                break;
            case Opcode.Int32ToNumber:
            case Opcode.Uint32ToNumber:
            case Opcode.Float64ToTagged:
            case Opcode.LoadJSArrayLength:
                info.Kind = NativeValueKind.kTaggedNumber;
                break;
            case Opcode.HoleyFloat64ToTagged:
                info.Kind = NativeValueKind.kTaggedHoleyNumber;
                break;
            case Opcode.Int32Compare:
            case Opcode.Int32ToBoolean:
            case Opcode.Float64Compare:
            case Opcode.Float64ToBoolean:
            case Opcode.TaggedEqual:
            case Opcode.TaggedNotEqual:
            case Opcode.LogicalNot:
            case Opcode.ToBoolean:
            case Opcode.ToBooleanLogicalNot:
                info.Kind = NativeValueKind.kTaggedBoolean;
                break;
            case Opcode.LoadElements:
                info.Kind = NativeValueKind.kElements;
                info.Input0 = Info(n.Inputs[0]);
                break;
            case Opcode.LoadFixedArrayElement:
                info.Kind = NativeValueKind.kFixedArrayElement;
                info.Input0 = Info(n.Inputs[0]);
                info.Input1 = Info(n.Inputs[1]);
                break;
            case Opcode.Identity:
                info.Kind = NativeValueKind.kIdentity;
                info.Input0 = Info(n.Inputs[0]);
                break;
            case Opcode.LoadTaggedField:
                info.Kind = NativeValueKind.kTaggedField;
                info.Input0 = Info(n.Inputs[0]);
                info.FrameIndex = n.Int0;
                break;
            case Opcode.LoadContextSlot:
                info.Kind = NativeValueKind.kContextSlot;
                info.Input0 = Info(n.Inputs[0]);
                info.FrameIndex = n.Int0;
                info.Input1 = n.Int1;
                break;
            case Opcode.LoadMap:
                info.Kind = NativeValueKind.kMap;
                info.Input0 = Info(n.Inputs[0]);
                break;
            case Opcode.ConvertHoleToUndefined:
                info.Kind = NativeValueKind.kHoleToUndefined;
                info.Input0 = Info(n.Inputs[0]);
                break;
            case Opcode.LoadPropertyCellValue:
                info.Kind = NativeValueKind.kPropertyCell;
                info.Constant = JSValue.FromObject((PropertyCell)n.Obj0!);
                break;
            case Opcode.Phi:
                info.Kind = NativeValueKind.kTaggedDynamic;
                ValueNode? other = OtherSource((Phi)n);
                if (other is not null) info.Other = Info(other);
                break;
            default:
                throw Unsupported("rebuilding " + n.Opcode);
        }
        if (_snapshots && IsReloadKind(info.Kind)) info.SnapSlot = _managedSlots++;
        if (_inlineStores && info.Kind is NativeValueKind.kTaggedField or NativeValueKind.kFixedArrayElement or NativeValueKind.kContextSlot)
        {
            info.KindAtLoad = true;
        }
        return v.Info;
    }

    static bool IsReloadKind(NativeValueKind kind) => kind is NativeValueKind.kElements or NativeValueKind.kFixedArrayElement or
        NativeValueKind.kTaggedField or NativeValueKind.kContextSlot or NativeValueKind.kMap or NativeValueKind.kPropertyCell;

    /// <summary>
    /// The one value a tagged phi holds when its kind is kOther, which must be
    /// the same at every exit (a frame slot, a constant, the elements of one);
    /// null if the phi only holds numbers and oddballs.
    /// </summary>
    ValueNode? OtherSource(Phi phi)
    {
        var sources = new HashSet<ValueNode>(ReferenceEqualityComparer.Instance);
        var seen = new HashSet<ValueNode>(ReferenceEqualityComparer.Instance);
        var work = new Stack<ValueNode>();
        work.Push(phi);
        while (work.Count > 0)
        {
            ValueNode n = work.Pop();
            if (!seen.Add(n)) continue;
            if (n.IsConstant)
            {
                JSValue c = n.ConstantValue();
                if (!c.IsNumber && !c.IsUndefined && !c.IsNull && !c.IsTrue && !c.IsFalse && !c.IsTheHole) sources.Add(n);
                continue;
            }
            switch (n.Opcode)
            {
                case Opcode.Phi:
                    foreach (ValueNode input in n.Inputs) work.Push(input);
                    break;
                case Opcode.Identity:
                    work.Push(n.Inputs[0]);
                    break;
                default:
                    if (n.Representation != ValueRepresentation.kTagged) break;
                    if (Info(n) is int i && _valueInfos[i].Kind is NativeValueKind.kTaggedNumber or NativeValueKind.kTaggedHoleyNumber or
                            NativeValueKind.kTaggedBoolean)
                    {
                        break;
                    }
                    if (!IsTimeInvariant(n)) throw Unsupported($"tagged phi n{phi.Id} of {n}");
                    // (Code that may change the heap: only sources no store can change.)
                    if (_snapshots && !(n.IsConstant || n.Opcode == Opcode.InitialValue)) throw Unsupported($"tagged phi n{phi.Id} of a load");
                    sources.Add(n);
                    break;
            }
        }
        if (sources.Count > 1) throw Unsupported($"tagged phi n{phi.Id} of several objects");
        foreach (ValueNode s in sources) return s;
        return null;
    }

    /// <summary>
    /// A value that is the same wherever the code is (native code writes no
    /// references, so a load of a time-invariant object gives the same value
    /// at every exit).
    /// </summary>
    static bool IsTimeInvariant(ValueNode n)
    {
        if (n.IsConstant || n.Opcode == Opcode.InitialValue) return true;
        switch (n.Opcode)
        {
            case Opcode.LoadElements:
            case Opcode.LoadTaggedField:
            case Opcode.LoadContextSlot:
            case Opcode.LoadMap:
            case Opcode.LoadFixedArrayElement:
            case Opcode.Identity:
                foreach (ValueNode input in n.Inputs)
                {
                    if (!IsTimeInvariant(input)) return false;
                }
                return true;
            default:
                return false;
        }
    }

    /// <summary>The values an exit stores (those in registers) for the given needed set.</summary>
    List<Val> RegisterStores(IEnumerable<ValueNode> needed)
    {
        var stores = new List<Val>();
        foreach (ValueNode n in needed)
        {
            if (TryV(n) is { } v && (v.InGp || v.InXmm)) stores.Add(v);
        }
        return stores;
    }

    int NewExit(NativeExit exit)
    {
        _exits.Add(exit);
        return _exits.Count - 1;
    }

    /// <summary>The label of the eager deopt exit for <paramref name="info"/> (EagerExit of the IL backend).</summary>
    XLabel DeoptLabel(EagerDeoptInfo info, NativeExitKind kind = NativeExitKind.kDeopt, ValueNode? migrate = null)
    {
        int reason = ReasonCode(info);
        if (kind == NativeExitKind.kDeopt && _deoptLabels.TryGetValue((info, reason, kind), out XLabel existing)) return existing;
        var needed = new HashSet<ValueNode>(ReferenceEqualityComparer.Instance);
        DeoptUses(info, needed);
        if (migrate is not null) AddClosure(migrate, needed);
        var exit = new NativeExit { Kind = kind, DeoptIndex = DeoptPointFor(info), Reason = reason };
        if (migrate is not null) exit.Value = Info(migrate);
        XLabel label = _masm.NewLabel();
        _exitStubs.Add((label, NewExit(exit), RegisterStores(needed)));
        if (kind == NativeExitKind.kDeopt) _deoptLabels[(info, reason, kind)] = label;
        return label;
    }

    /// <summary>The reason a deopt passes (with the speculation feedback to update in its high bits), as the IL's EagerExit.</summary>
    int ReasonCode(EagerDeoptInfo info)
    {
        int feedback = 0;
        if (info.HoistedUntagging)
        {
            feedback = Deoptimizer.Deoptimizer.kHoistedUntaggingFeedback;
        }
        else if (info.FeedbackToUpdate is { } vector)
        {
            int i = _speculationFeedback.IndexOf((vector, info.FeedbackSlotToUpdate));
            if (i < 0)
            {
                i = _speculationFeedback.Count;
                _speculationFeedback.Add((vector, info.FeedbackSlotToUpdate));
            }
            feedback = i + 1;
        }
        return (int)info.Reason | feedback << 16;
    }

    readonly Dictionary<DeoptInfo, int> _deoptPointIndex = new(ReferenceEqualityComparer.Instance);
    readonly Dictionary<ValueNode, int> _scratchSlots = new(ReferenceEqualityComparer.Instance);

    /// <summary>The DeoptPoint of a frame state (built as the IL backend's EmitDeoptExits builds it).</summary>
    int DeoptPointFor(DeoptInfo info, ValueNode? lazyResult = null)
    {
        if (_deoptPointIndex.TryGetValue(info, out int index)) return index;
        // Frames outermost first.
        var frames = new List<InterpretedDeoptFrame>();
        for (DeoptFrame? f = info.TopFrame; f is not null; f = f.Parent) frames.Add((InterpretedDeoptFrame)f);
        frames.Reverse();
        var values = new List<(int, int)>();
        int Slot(ValueNode value)
        {
            if (!_scratchSlots.TryGetValue(value, out int slot))
            {
                _scratchSlots[value] = slot = _scratchBase + _scratchSlots.Count;
            }
            values.Add((slot, Info(value)));
            return slot;
        }
        var data = new DeoptFrameData[frames.Count];
        for (int i = 0; i < frames.Count; i++)
        {
            InterpretedDeoptFrame f = frames[i];
            MaglevCompilationUnit unit = f.Unit;
            var registers = new IRegister[f.Values.Length];
            var slots = new int[f.Values.Length];
            JSValue[]? constants = null;
            bool[]? isConstant = null;
            for (int k = 0; k < f.Values.Length; k++)
            {
                registers[k] = f.Values[k].Register;
                slots[k] = -1;
                ValueNode value = f.Values[k].Value;
                if (value.IsConstant)
                {
                    (constants ??= new JSValue[f.Values.Length])[k] = value.ConstantValue();
                    (isConstant ??= new bool[f.Values.Length])[k] = true;
                    continue;
                }
                slots[k] = Slot(value);
            }
            data[i] = new DeoptFrameData
            {
                InliningDepth = unit.InliningDepth,
                Argc = unit.Argc,
                IsConstruct = unit.IsConstruct,
                Function = unit.IsInline ? unit.Function : unit.Function ?? _info.Function,
                ClosureScratchSlot = unit.IsInline && unit.Function is null ? Slot(f.Closure) : -1,
                Shared = unit.SharedFunctionInfo,
                Bytecode = unit.Bytecode,
                FeedbackVector = unit.Feedback,
                BytecodeOffset = f.BytecodeOffset,
                NextOffset = f.NextOffset,
                Registers = registers,
                Constants = constants,
                IsConstant = isConstant,
                ScratchSlots = slots,
            };
        }
        var point = new DeoptPoint
        {
            Kind = info is LazyDeoptInfo ? DeoptimizeKind.kLazy : DeoptimizeKind.kEager,
            Reason = info is EagerDeoptInfo eager ? eager.Reason : DeoptimizeReason.kUnknown,
            Frames = data,
        };
        if (info is LazyDeoptInfo lazy)
        {
            point.ResultLocation = lazy.ResultLocation;
            if (lazyResult is not null && lazy.ResultSize == 1)
            {
                // (The managed side writes the call's result there itself.)
                int resultSlot = _scratchBase + _scratchSlots.Count;
                _scratchSlots[new ValueNode(Opcode.Constant, ValueRepresentation.kTagged)] = resultSlot;
                point.ResultScratchIndex = resultSlot;
            }
        }
        index = _deoptPoints.Count;
        _deoptPoints.Add(point);
        _deoptValues.Add(values.ToArray());
        info.DeoptIndex = index;
        _deoptPointIndex[info] = index;
        _maxScratch = Math.Max(_maxScratch, _scratchBase + _scratchSlots.Count);
        return index;
    }

    void EmitExitStubs()
    {
        foreach ((XLabel label, int exit, List<Val> stores) in _exitStubs)
        {
            _masm.Bind(label);
            foreach (Val v in stores)
            {
                if (v.InGp) _masm.Movl(HomeOp(v), v.Gp);
                else _masm.Movsd(HomeOp(v), v.XmmReg);
            }
            _masm.Movl(rax, exit);
            _masm.Jmp(_epilogue);
        }
    }

    XLabel AbortLabel()
    {
        if (_abortExit < 0)
        {
            _abortExit = NewExit(new NativeExit { Kind = NativeExitKind.kAbort, Message = "element access out of bounds in native Maglev code" });
        }
        return _abort;
    }

    // ---- Blocks and control ---------------------------------------------------------------------------------

    void EmitBlock(BasicBlock block)
    {
        _masm.Bind(BlockLabel(block));
        foreach (Node node in block.Nodes) EmitNode(node);
        EmitControl(block, block.Control!);
    }

    bool HasPhiMoves(BasicBlock to) => to.Phis.Count > 0;

    XLabel EdgeLabel(BasicBlock from, BasicBlock to)
    {
        if (!HasPhiMoves(to)) return BlockLabel(to);
        XLabel stub = _masm.NewLabel();
        _edgeStubs.Add((stub, from, to));
        return stub;
    }

    void EmitEdgeStubs()
    {
        foreach ((XLabel label, BasicBlock from, BasicBlock to) in _edgeStubs)
        {
            _masm.Bind(label);
            EmitPhiMoves(from, to);
            _masm.Jmp(BlockLabel(to));
        }
    }

    void EmitControl(BasicBlock block, ControlNode c)
    {
        switch (c.Opcode)
        {
            case Opcode.Jump:
            case Opcode.JumpLoop:
                EmitPhiMoves(block, c.Target!);
                if (!ReferenceEquals(c.Target, _nextBlock) || c.Opcode == Opcode.JumpLoop) _masm.Jmp(BlockLabel(c.Target!));
                return;
            case Opcode.Return:
            {
                var needed = new HashSet<ValueNode>(ReferenceEqualityComparer.Instance);
                AddClosure(c.Inputs[0], needed);
                int exit = NewExit(new NativeExit { Kind = NativeExitKind.kReturn, Value = Info(c.Inputs[0]) });
                foreach (Val v in RegisterStores(needed))
                {
                    if (v.InGp) _masm.Movl(HomeOp(v), v.Gp);
                    else _masm.Movsd(HomeOp(v), v.XmmReg);
                }
                _masm.Movl(rax, exit);
                _masm.Jmp(_epilogue);
                return;
            }
            case Opcode.Deopt:
                if (c.EagerDeoptInfo is null)
                {
                    _masm.Movl(rax, NewExit(new NativeExit { Kind = NativeExitKind.kUnreachable }));
                    _masm.Jmp(_epilogue);
                    return;
                }
                _masm.Jmp(DeoptLabel(c.EagerDeoptInfo));
                return;
        }
        BasicBlock t = c.Target!, f = c.FalseTarget!;
        XLabel trueLabel = EdgeLabel(block, t), falseLabel = EdgeLabel(block, f);
        bool falseFallsThrough = ReferenceEquals(f, _nextBlock) && !HasPhiMoves(f);
        EmitBranch(c, trueLabel, falseLabel);
        if (!falseFallsThrough) _masm.Jmp(falseLabel);
    }

    /// <summary>Jumps to <paramref name="ifTrue"/> when the branch's condition holds; falls through otherwise (or jumps to <paramref name="ifFalse"/>).</summary>
    void EmitBranch(ControlNode c, XLabel ifTrue, XLabel ifFalse)
    {
        switch (c.Opcode)
        {
            case Opcode.BranchIfToBooleanTrue:
            {
                // Inline for oddballs, numbers and receivers; the managed side for
                // the others (strings, BigInts ...), resuming with the 0/1 in a context word.
                XLabel slow = _masm.NewLabel(), join = _masm.NewLabel(), reload = _masm.NewLabel();
                if (!EmitToBooleanFast(c.Inputs[0], slow)) throw Unsupported("ToBoolean of an untagged value");
                _masm.Bind(join);
                _masm.Testl(rax, rax);
                _masm.J(Condition.not_equal, ifTrue);
                List<Val> live = _liveAfterControl[c];
                var needed = new HashSet<ValueNode>(ReferenceEqualityComparer.Instance);
                foreach (Val v in live) needed.Add(v.Node);
                AddClosure(c.Inputs[0], needed);
                var tagged = new List<int>();
                foreach (Val v in live)
                {
                    if (v.Repr == ValueRepresentation.kTagged) tagged.Add(Info(v.Node));
                }
                int resumeId = _resumes.Count + 1;
                var exit = new NativeExit
                {
                    Kind = NativeExitKind.kManagedNode,
                    Managed = new NativeManagedNode { Opcode = Opcode.BranchIfToBooleanTrue, Inputs = [Info(c.Inputs[0])], BytecodeOffset = c.BytecodeOffset },
                    ResumeId = resumeId,
                    TaggedLive = tagged.ToArray(),
                    Snapshot = SnapshotList(needed),
                };
                int exitId = NewExit(exit);
                List<Val> stores = RegisterStores(needed);
                _slowPaths.Add(() =>
                {
                    _masm.Bind(slow);
                    EmitExitStores(stores);
                    _masm.Movl(rax, exitId);
                    _masm.Jmp(_epilogue);
                    _masm.Bind(reload);
                    _masm.Movl(rax, new Operand(rbx, kBoolResultSlot * 8));
                    _masm.Jmp(join);
                });
                _resumes.Add((_masm.NewLabel(), resumeId, live, reload));
                return;
            }
            case Opcode.BranchIfInt32Compare:
            {
                Reg a = Int32Reg(c.Inputs[0], rax);
                CompareInt32(a, c.Inputs[1]);
                _masm.J(c.Unsigned ? Condition.below : Int32Condition(c.Operation), ifTrue);
                return;
            }
            case Opcode.BranchIfFloat64Compare:
                EmitFloat64CompareBranch(c.Operation, c.Inputs[0], c.Inputs[1], ifTrue, ifFalse);
                return;
            case Opcode.BranchIfReferenceEqual:
                EmitIdentical(c.Inputs[0], c.Inputs[1], ifTrue, ifFalse);
                return;
            case Opcode.BranchIfRootConstant:
                LoadTaggedPtr(c.Inputs[0], rax);
                if ((RootIndex)c.Int1 == RootIndex.kUndefinedValue)
                {
                    _masm.Testq(rax, rax);
                }
                else
                {
                    LoadConstantAddress(rcx, RootConstantIndex((RootIndex)c.Int1));
                    _masm.Cmpq(rax, rcx);
                }
                _masm.J(Condition.equal, ifTrue);
                return;
            case Opcode.BranchIfInt32ToBooleanTrue:
            {
                Reg a = Int32Reg(c.Inputs[0], rax);
                _masm.Testl(a, a);
                _masm.J(Condition.not_equal, ifTrue);
                return;
            }
            case Opcode.BranchIfFloat64ToBooleanTrue:
            {
                XReg x = Float64Reg(c.Inputs[0], xmm0);
                _masm.Xorpd(xmm1, xmm1);
                _masm.Ucomisd(x, xmm1);
                // Unordered sets ZF: NaN is false, as zero.
                _masm.J(Condition.not_equal, ifTrue);
                return;
            }
            default:
                throw Unsupported(c.Opcode.ToString());
        }
    }

    static int RootConstantIndex(RootIndex root) => root switch
    {
        RootIndex.kNullValue => kConstNull,
        RootIndex.kTrueValue => kConstTrue,
        RootIndex.kFalseValue => kConstFalse,
        RootIndex.kTheHoleValue => kConstTheHole,
        _ => throw Unsupported("root " + root),
    };

    static Condition Int32Condition(CompareOperation op) => op switch
    {
        CompareOperation.kEqual or CompareOperation.kStrictEqual => Condition.equal,
        CompareOperation.kLessThan => Condition.less,
        CompareOperation.kLessThanOrEqual => Condition.less_equal,
        CompareOperation.kGreaterThan => Condition.greater,
        _ => Condition.greater_equal,
    };

    void CompareInt32(Reg a, ValueNode b)
    {
        if (b.IsConstant && b.TryGetInt32Constant(out int c))
        {
            _masm.Cmpl(a, c);
            return;
        }
        Val v = V(b);
        if (v.InGp) _masm.Cmpl(a, v.Gp);
        else _masm.Cmpl(a, HomeOp(v));
    }

    /// <summary>A float64 comparison branch with IEEE semantics (a comparison with NaN is false).</summary>
    void EmitFloat64CompareBranch(CompareOperation op, ValueNode left, ValueNode right, XLabel ifTrue, XLabel ifFalse)
    {
        XReg a = Float64Reg(left, xmm0);
        XReg b = Float64Reg(right, xmm1);
        switch (op)
        {
            case CompareOperation.kEqual:
            case CompareOperation.kStrictEqual:
                _masm.Ucomisd(a, b);
                _masm.J(Condition.parity_even, ifFalse);
                _masm.J(Condition.equal, ifTrue);
                return;
            case CompareOperation.kLessThan:
                _masm.Ucomisd(b, a);
                _masm.J(Condition.above, ifTrue);
                return;
            case CompareOperation.kLessThanOrEqual:
                _masm.Ucomisd(b, a);
                _masm.J(Condition.above_equal, ifTrue);
                return;
            case CompareOperation.kGreaterThan:
                _masm.Ucomisd(a, b);
                _masm.J(Condition.above, ifTrue);
                return;
            default:
                _masm.Ucomisd(a, b);
                _masm.J(Condition.above_equal, ifTrue);
                return;
        }
    }

    /// <summary>JSValue.IsIdenticalTo: the same object, and for numbers the same bits.</summary>
    void EmitIdentical(ValueNode left, ValueNode right, XLabel ifTrue, XLabel ifFalse)
    {
        LoadTagged(left, rax, rdx);
        LoadTagged(right, rcx, r11);
        _masm.Cmpq(rax, rcx);
        _masm.J(Condition.not_equal, ifFalse);
        LoadConstantAddress(rcx, kConstNumberTag);
        _masm.Cmpq(rax, rcx);
        _masm.J(Condition.not_equal, ifTrue);
        _masm.Cmpq(rdx, r11);
        _masm.J(Condition.equal, ifTrue);
        _masm.Jmp(ifFalse);
    }

    // ---- Phi moves (parallel moves at the end of a predecessor) ----------------------------------------------

    sealed class Move(ValueNode? src, Val? srcTemp, Val dst)
    {
        public ValueNode? Src = src;
        /// <summary>The cycle-breaking temporary standing for the source (a value's copy in kMoveTempSlot).</summary>
        public Val? SrcTemp = srcTemp;
        public readonly Val Dst = dst;
    }

    /// <summary>A location identity for conflict detection (register or home word).</summary>
    static long Loc(Val v) => v.InGp ? 1000 + (int)v.Gp : v.InXmm ? 2000 + v.Xmm : 10000 + v.Home;

    long? SrcLoc(Move m)
    {
        if (m.SrcTemp is not null) return -1;
        if (m.Src!.IsConstant) return null;
        return Loc(V(m.Src));
    }

    void EmitPhiMoves(BasicBlock from, BasicBlock to)
    {
        int index = to.PredecessorIndexOf(from);
        var pending = new List<Move>();
        foreach (Phi phi in to.Phis)
        {
            Val dst = V(phi);
            ValueNode input = phi.Inputs[index];
            if (!input.IsConstant && ReferenceEquals(input, phi)) continue;
            if (!input.IsConstant && V(input) is { } src && Loc(src) == Loc(dst) && src.Repr == dst.Repr) continue;
            pending.Add(new Move(input, null, dst));
        }
        while (pending.Count > 0)
        {
            int pick = -1;
            for (int i = 0; i < pending.Count && pick < 0; i++)
            {
                long dl = Loc(pending[i].Dst);
                bool blocked = false;
                for (int k = 0; k < pending.Count; k++)
                {
                    if (k != i && SrcLoc(pending[k]) == dl) { blocked = true; break; }
                }
                if (!blocked) pick = i;
            }
            if (pick >= 0)
            {
                EmitMove(pending[pick]);
                pending.RemoveAt(pick);
                continue;
            }
            // A cycle: the first move's source goes to the temporary.
            Move m = pending[0];
            Val src = V(m.Src!);
            var temp = new Val(src.Node) { Home = kMoveTempSlot, NeedsKind = src.NeedsKind };
            CopyToTemp(src, temp);
            m.SrcTemp = temp;
            m.Src = null;
        }
    }

    void CopyToTemp(Val src, Val temp)
    {
        if (src.Repr == ValueRepresentation.kTagged)
        {
            for (int w = 0; w < 3; w++)
            {
                _masm.Movq(rax, HomeOp(src, w));
                _masm.Movq(HomeOp(temp, w), rax);
            }
        }
        else if (src.InGp)
        {
            _masm.Movl(HomeOp(temp), src.Gp);
        }
        else if (src.InXmm)
        {
            _masm.Movsd(HomeOp(temp), src.XmmReg);
        }
        else
        {
            _masm.Movq(rax, HomeOp(src));
            _masm.Movq(HomeOp(temp), rax);
        }
    }

    void EmitMove(Move m)
    {
        Val dst = m.Dst;
        if (m.SrcTemp is { } temp)
        {
            // The temporary has the source's representation (cycles are of same-representation phis).
            if (dst.Repr == ValueRepresentation.kTagged)
            {
                for (int w = 0; w < 3; w++)
                {
                    _masm.Movq(rax, HomeOp(temp, w));
                    _masm.Movq(HomeOp(dst, w), rax);
                }
            }
            else if (dst.InGp) _masm.Movl(dst.Gp, HomeOp(temp));
            else if (dst.InXmm) _masm.Movsd(dst.XmmReg, HomeOp(temp));
            else
            {
                _masm.Movq(rax, HomeOp(temp));
                _masm.Movq(HomeOp(dst), rax);
            }
            return;
        }
        ValueNode src = m.Src!;
        switch (dst.Repr)
        {
            case ValueRepresentation.kInt32:
            case ValueRepresentation.kUint32:
                if (dst.InGp)
                {
                    LoadInt32(src, dst.Gp);
                }
                else
                {
                    LoadInt32(src, rax);
                    _masm.Movl(HomeOp(dst), rax);
                }
                return;
            case ValueRepresentation.kFloat64:
            case ValueRepresentation.kHoleyFloat64:
                if (dst.InXmm)
                {
                    LoadFloat64(src, dst.XmmReg);
                }
                else
                {
                    LoadFloat64(src, xmm0);
                    _masm.Movsd(HomeOp(dst), xmm0);
                }
                return;
            default:
                StoreTaggedFrom(src, dst);
                return;
        }
    }

    /// <summary>Writes value <paramref name="src"/>, tagged, into the tagged home of <paramref name="dst"/> (with its kind word).</summary>
    void StoreTaggedFrom(ValueNode src, Val dst)
    {
        if (src.IsConstant)
        {
            JSValue c = src.ConstantValue();
            LoadTaggedPtr(src, rax);
            _masm.Movq(HomeOp(dst), rax);
            _masm.Movq(rax, c._bits);
            _masm.Movq(HomeOp(dst, 1), rax);
            if (dst.NeedsKind) _masm.Movq(HomeOp(dst, 2), (int)KindOfConstant(c));
            return;
        }
        Val s = V(src);
        if (s.Repr == ValueRepresentation.kTagged)
        {
            for (int w = 0; w < (dst.NeedsKind ? 3 : 2); w++)
            {
                _masm.Movq(rax, HomeOp(s, w));
                _masm.Movq(HomeOp(dst, w), rax);
            }
            return;
        }
        if (s.Repr == ValueRepresentation.kHoleyFloat64) throw Unsupported("holey float64 phi input");
        LoadFloat64(src, xmm0);
        StoreTaggedNumber(dst, xmm0);
    }

    static NativeTaggedKind KindOfConstant(JSValue c)
    {
        if (c.IsNumber) return NativeTaggedKind.kNumber;
        if (c.IsUndefined) return NativeTaggedKind.kUndefined;
        if (c.IsTrue) return NativeTaggedKind.kTrue;
        if (c.IsFalse) return NativeTaggedKind.kFalse;
        if (c.IsNull) return NativeTaggedKind.kNull;
        if (c.IsTheHole) return NativeTaggedKind.kTheHole;
        return NativeTaggedKind.kOther;
    }

    // ---- Nodes ---------------------------------------------------------------------------------------------

    void EmitNode(Node node)
    {
        if (_managedNodes.Contains(node))
        {
            EmitManagedNode(node);
            return;
        }
        var v = node as ValueNode;
        Val? r = v is null ? null : V(v);
        switch (node.Opcode)
        {
            case Opcode.InitialValue:
            {
                // The frame slot (fp + Int0) into the home.
                _masm.Movq(rcx, new Operand(rbx, NativeCode.kFrameSlot * 8));
                _masm.Movq(rax, new Operand(rcx, node.Int0 * 16 + 8));
                _masm.Movq(HomeOp(r!, 1), rax);
                _masm.Movq(rax, new Operand(rcx, node.Int0 * 16));
                _masm.Movq(HomeOp(r!), rax);
                if (r!.NeedsKind) EmitClassify(r);
                return;
            }
            case Opcode.Identity:
                if (!node.Inputs[0].IsConstant && ReferenceEquals(r, V(node.Inputs[0]))) return;
                switch (r!.Repr)
                {
                    case ValueRepresentation.kInt32:
                    case ValueRepresentation.kUint32:
                        if (r.InGp) LoadInt32(node.Inputs[0], r.Gp);
                        else { LoadInt32(node.Inputs[0], rax); _masm.Movl(HomeOp(r), rax); }
                        return;
                    case ValueRepresentation.kFloat64:
                    case ValueRepresentation.kHoleyFloat64:
                        StoreFloat64Result(r, Float64Reg(node.Inputs[0], xmm0));
                        return;
                    default:
                        StoreTaggedFrom(node.Inputs[0], r);
                        return;
                }

            // ---- Int32 ---------------------------------------------------------------------------------------
            case Opcode.Int32AddWithOverflow:
            case Opcode.Int32SubtractWithOverflow:
            case Opcode.Int32MultiplyWithOverflow:
            case Opcode.Int32Add:
            case Opcode.Int32Subtract:
            case Opcode.Int32Multiply:
            case Opcode.Int32BitwiseAnd:
            case Opcode.Int32BitwiseOr:
            case Opcode.Int32BitwiseXor:
                EmitInt32Binary(node, r!);
                return;
            case Opcode.Int32DivideWithOverflow:
            case Opcode.Int32ModulusWithOverflow:
                EmitInt32DivMod(node, r!);
                return;
            case Opcode.Int32IncrementWithOverflow:
            case Opcode.Int32DecrementWithOverflow:
                LoadInt32(node.Inputs[0], rax);
                if (node.Opcode == Opcode.Int32IncrementWithOverflow) _masm.Addl(rax, 1);
                else _masm.Subl(rax, 1);
                _masm.J(Condition.overflow, DeoptLabel(node.EagerDeoptInfo!));
                StoreInt32Result(r!, rax);
                return;
            case Opcode.Int32NegateWithOverflow:
                // 0 negates to -0, kMinInt overflows.
                LoadInt32(node.Inputs[0], rax);
                _masm.Testl(rax, rax);
                _masm.J(Condition.equal, DeoptLabel(node.EagerDeoptInfo!));
                _masm.Negl(rax);
                _masm.J(Condition.overflow, DeoptLabel(node.EagerDeoptInfo!));
                StoreInt32Result(r!, rax);
                return;
            case Opcode.Int32AbsWithOverflow:
                LoadInt32(node.Inputs[0], rcx);
                _masm.Movl(rax, rcx);
                _masm.Negl(rax);
                _masm.J(Condition.overflow, DeoptLabel(node.EagerDeoptInfo!));
                _masm.Cmovl(Condition.negative, rax, rcx);
                StoreInt32Result(r!, rax);
                return;
            case Opcode.Int32ShiftLeft:
            case Opcode.Int32ShiftRight:
            case Opcode.Int32ShiftRightLogical:
                if (node.Inputs[1].IsConstant && node.Inputs[1].TryGetInt32Constant(out int amount))
                {
                    // By a constant: the count masked to 5 bits, as the CPU does.
                    Reg dst = r!.InGp ? r.Gp : rax;
                    LoadInt32(node.Inputs[0], dst);
                    amount &= 31;
                    if (amount != 0)
                    {
                        if (node.Opcode == Opcode.Int32ShiftLeft) _masm.Shll(dst, amount);
                        else if (node.Opcode == Opcode.Int32ShiftRight) _masm.Sarl(dst, amount);
                        else _masm.Shrl(dst, amount);
                    }
                    StoreInt32Result(r, dst);
                    return;
                }
                LoadInt32(node.Inputs[1], rcx);
                LoadInt32(node.Inputs[0], rax);
                if (node.Opcode == Opcode.Int32ShiftLeft) _masm.Shll_cl(rax);
                else if (node.Opcode == Opcode.Int32ShiftRight) _masm.Sarl_cl(rax);
                else _masm.Shrl_cl(rax);
                StoreInt32Result(r!, rax);
                return;
            case Opcode.Int32BitwiseNot:
                LoadInt32(node.Inputs[0], rax);
                _masm.Notl(rax);
                StoreInt32Result(r!, rax);
                return;
            case Opcode.Int32Compare:
            {
                Reg a = Int32Reg(node.Inputs[0], rax);
                CompareInt32(a, node.Inputs[1]);
                _masm.Setcc(Int32Condition((CompareOperation)node.Int0), rax);
                _masm.Movzxbl(rax, rax);
                StoreTaggedBoolean(r!);
                return;
            }
            case Opcode.Int32ToBoolean:
            {
                Reg a = Int32Reg(node.Inputs[0], rcx);
                _masm.Testl(a, a);
                _masm.Setcc(node.Int0 != 0 ? Condition.equal : Condition.not_equal, rax);
                _masm.Movzxbl(rax, rax);
                StoreTaggedBoolean(r!);
                return;
            }

            // ---- Float64 -------------------------------------------------------------------------------------
            case Opcode.Float64Add:
            case Opcode.Float64Subtract:
            case Opcode.Float64Multiply:
            case Opcode.Float64Divide:
            {
                XReg b = Float64Reg(node.Inputs[1], xmm1);
                XReg dst = r!.InXmm && r.XmmReg != b ? r.XmmReg : xmm0;
                LoadFloat64(node.Inputs[0], dst);
                switch (node.Opcode)
                {
                    case Opcode.Float64Add: _masm.Addsd(dst, b); break;
                    case Opcode.Float64Subtract: _masm.Subsd(dst, b); break;
                    case Opcode.Float64Multiply: _masm.Mulsd(dst, b); break;
                    default: _masm.Divsd(dst, b); break;
                }
                StoreFloat64Result(r, dst);
                return;
            }
            case Opcode.Float64Modulus:
                EmitFloat64Modulus(node, r!);
                return;
            case Opcode.Float64Negate:
            case Opcode.Float64Abs:
                // The sign bit flipped or cleared (NaN's too, as the IL's neg and Math.Abs).
                LoadFloat64(node.Inputs[0], xmm0);
                _masm.Movq(rax, xmm0);
                if (node.Opcode == Opcode.Float64Negate)
                {
                    _masm.Movq(rcx, long.MinValue);
                    _masm.Xorq(rax, rcx);
                }
                else
                {
                    _masm.Movq(rcx, long.MaxValue);
                    _masm.Andq(rax, rcx);
                }
                _masm.Movq(xmm0, rax);
                StoreFloat64Result(r!, xmm0);
                return;
            case Opcode.Float64Sqrt:
                _masm.Sqrtsd(xmm0, Float64Reg(node.Inputs[0], xmm0));
                StoreFloat64Result(r!, xmm0);
                return;
            case Opcode.Float64Round:
                EmitFloat64Round(node, r!);
                return;
            case Opcode.Float64Min:
            case Opcode.Float64Max:
                EmitFloat64MinMax(node, r!);
                return;
            case Opcode.Float64Compare:
            {
                XLabel t = _masm.NewLabel(), f = _masm.NewLabel(), done = _masm.NewLabel();
                EmitFloat64CompareBranch((CompareOperation)node.Int0, node.Inputs[0], node.Inputs[1], t, f);
                _masm.Bind(f);
                _masm.Movl(rax, 0);
                _masm.Jmp(done);
                _masm.Bind(t);
                _masm.Movl(rax, 1);
                _masm.Bind(done);
                StoreTaggedBoolean(r!);
                return;
            }
            case Opcode.Float64ToBoolean:
            {
                XReg x = Float64Reg(node.Inputs[0], xmm0);
                _masm.Xorpd(xmm1, xmm1);
                _masm.Ucomisd(x, xmm1);
                _masm.Setcc(node.Int0 != 0 ? Condition.equal : Condition.not_equal, rax);
                _masm.Movzxbl(rax, rax);
                StoreTaggedBoolean(r!);
                return;
            }

            // ---- Conversions ---------------------------------------------------------------------------------
            case Opcode.CheckedSmiUntag:
            case Opcode.CheckedNumberToInt32:
            {
                // A number that is an int32 and not -0.
                XLabel exit = DeoptLabel(node.EagerDeoptInfo!);
                EmitCheckNumber(node.Inputs[0], exit);
                LoadTaggedBits(node.Inputs[0], rdx);
                EmitCheckedBitsToInt32(exit);
                StoreInt32Result(r!, rax);
                return;
            }
            case Opcode.UnsafeSmiUntag:
                LoadTaggedBits(node.Inputs[0], rdx);
                _masm.Movq(xmm0, rdx);
                _masm.Cvttsd2si(rax, xmm0);
                StoreInt32Result(r!, rax);
                return;
            case Opcode.CheckedNumberOrOddballToFloat64:
            case Opcode.TruncateCheckedNumberOrOddballToInt32:
                EmitNumberOrOddballToFloat64(node);
                if (node.Opcode == Opcode.CheckedNumberOrOddballToFloat64)
                {
                    StoreFloat64Result(r!, xmm0);
                    return;
                }
                EmitTruncateFloat64ToInt32();
                StoreInt32Result(r!, rax);
                return;
            case Opcode.UnsafeNumberToFloat64:
                LoadTaggedBits(node.Inputs[0], rax);
                _masm.Movq(xmm0, rax);
                StoreFloat64Result(r!, xmm0);
                return;
            case Opcode.Int32ToNumber:
            case Opcode.Uint32ToNumber:
            case Opcode.Float64ToTagged:
                if (node.Inputs[0].IsConstant)
                {
                    double d = node.Opcode == Opcode.Uint32ToNumber && node.Inputs[0].TryGetInt32Constant(out int ui)
                        ? (uint)ui
                        : node.Inputs[0].TryGetFloat64Constant(out double cd) ? cd : throw Unsupported("constant");
                    _masm.Movq(rax, BitConverter.DoubleToInt64Bits(d));
                    _masm.Movq(xmm0, rax);
                }
                else
                {
                    LoadFloat64(node.Inputs[0], xmm0);
                }
                StoreTaggedNumber(r!, xmm0);
                return;
            case Opcode.HoleyFloat64ToTagged:
            {
                // The hole is undefined.
                XLabel number = _masm.NewLabel(), done = _masm.NewLabel();
                LoadFloat64(node.Inputs[0], xmm0);
                _masm.Movq(rax, xmm0);
                _masm.Movq(rcx, EngineGlobals.kHoleNanInt64);
                _masm.Cmpq(rax, rcx);
                _masm.J(Condition.not_equal, number);
                // Undefined; the kind word (always kept: a resume rewrites the
                // payload) tells the rebuild which it is.
                _masm.Movq(HomeOp(r!), 0);
                _masm.Movq(HomeOp(r!, 1), 0);
                _masm.Movq(HomeOp(r!, 2), (int)NativeTaggedKind.kUndefined);
                _masm.Jmp(done);
                _masm.Bind(number);
                StoreTaggedNumber(r!, xmm0);
                _masm.Movq(HomeOp(r!, 2), (int)NativeTaggedKind.kNumber);
                _masm.Bind(done);
                return;
            }
            case Opcode.ChangeInt32ToFloat64:
            case Opcode.ChangeUint32ToFloat64:
                LoadFloat64(node.Inputs[0], xmm0);
                StoreFloat64Result(r!, xmm0);
                return;
            case Opcode.CheckedFloat64ToInt32:
                LoadFloat64(node.Inputs[0], xmm0);
                _masm.Movq(rdx, xmm0);
                EmitCheckedBitsToInt32(DeoptLabel(node.EagerDeoptInfo!));
                StoreInt32Result(r!, rax);
                return;
            case Opcode.TruncateFloat64ToInt32:
                LoadFloat64(node.Inputs[0], xmm0);
                EmitTruncateFloat64ToInt32();
                StoreInt32Result(r!, rax);
                return;
            case Opcode.CheckedUint32ToInt32:
            {
                Reg a = Int32Reg(node.Inputs[0], rax);
                _masm.Testl(a, a);
                _masm.J(Condition.negative, DeoptLabel(node.EagerDeoptInfo!));
                StoreInt32Result(r!, a);
                return;
            }
            case Opcode.CheckedHoleyFloat64ToFloat64:
                LoadFloat64(node.Inputs[0], xmm0);
                _masm.Movq(rax, xmm0);
                _masm.Movq(rcx, EngineGlobals.kHoleNanInt64);
                _masm.Cmpq(rax, rcx);
                _masm.J(Condition.equal, DeoptLabel(node.EagerDeoptInfo!));
                StoreFloat64Result(r!, xmm0);
                return;

            // ---- Checks --------------------------------------------------------------------------------------
            case Opcode.CheckSmi:
            {
                XLabel exit = DeoptLabel(node.EagerDeoptInfo!);
                EmitCheckNumber(node.Inputs[0], exit);
                LoadTaggedBits(node.Inputs[0], rdx);
                EmitCheckedBitsToInt32(exit);
                // (uint)(i - SmiMinValue) <= (uint)(SmiMaxValue - SmiMinValue) (JSValue.IsSmiDouble).
                _masm.Subl(rax, JSValue.SmiMinValue);
                _masm.Cmpl(rax, JSValue.SmiMaxValue - JSValue.SmiMinValue);
                _masm.J(Condition.above, exit);
                return;
            }
            case Opcode.CheckNumber:
                EmitCheckNumber(node.Inputs[0], DeoptLabel(node.EagerDeoptInfo!));
                return;
            case Opcode.CheckHeapObject:
                // Not a number (undefined included).
                LoadTaggedPtr(node.Inputs[0], rax);
                LoadConstantAddress(rcx, kConstNumberTag);
                _masm.Cmpq(rax, rcx);
                _masm.J(Condition.equal, DeoptLabel(node.EagerDeoptInfo!));
                return;
            case Opcode.CheckNotHole:
                LoadTaggedPtr(node.Inputs[0], rax);
                LoadConstantAddress(rcx, kConstTheHole);
                _masm.Cmpq(rax, rcx);
                _masm.J(Condition.equal, DeoptLabel(node.EagerDeoptInfo!));
                return;
            case Opcode.CheckValue:
            {
                if (node.Obj0 is not HeapObject expected) throw Unsupported("CheckValue of " + node.Obj0?.GetType().Name);
                LoadTaggedPtr(node.Inputs[0], rax);
                LoadConstantAddress(rcx, ConstantIndex(expected));
                _masm.Cmpq(rax, rcx);
                _masm.J(Condition.not_equal, DeoptLabel(node.EagerDeoptInfo!));
                return;
            }
            case Opcode.CheckInt32IsSmi:
            {
                Reg a = Int32Reg(node.Inputs[0], rcx);
                _masm.Movl(rax, a);
                _masm.Subl(rax, JSValue.SmiMinValue);
                _masm.Cmpl(rax, JSValue.SmiMaxValue - JSValue.SmiMinValue);
                _masm.J(Condition.above, DeoptLabel(node.EagerDeoptInfo!));
                return;
            }
            case Opcode.CheckInt32Condition:
            {
                // Deopt unless lhs < rhs (unsigned when Int1 is set).
                Reg a = Int32Reg(node.Inputs[0], rax);
                CompareInt32(a, node.Inputs[1]);
                _masm.J(node.Int1 != 0 ? Condition.above_equal : Condition.greater_equal, DeoptLabel(node.EagerDeoptInfo!));
                return;
            }
            case Opcode.CheckMaps:
                EmitCheckMaps(node);
                return;

            // ---- Loads and stores ----------------------------------------------------------------------------
            case Opcode.LoadElements:
                LoadTaggedPtr(node.Inputs[0], rax);
                _masm.Movq(rax, new Operand(rax, NativeLayout.JSObjectElements));
                _masm.Movq(HomeOp(r!), rax);
                EmitCacheElementsData(r!);
                ClearSnapshot(r!);
                if (r!.NeedsKind) _masm.Movq(HomeOp(r, 2), (int)NativeTaggedKind.kOther);
                return;
            case Opcode.LoadFixedArrayElement:
                EmitElementAddress(node, NativeLayout.FixedArrayData, 16);
                _masm.Movq(rax, new Operand(rcx, NativeLayout.ArrayData));
                _masm.Movq(rdx, new Operand(rcx, NativeLayout.ArrayData + 8));
                _masm.Movq(HomeOp(r!), rax);
                _masm.Movq(HomeOp(r!, 1), rdx);
                ClearSnapshot(r!);
                if (r!.NeedsKind || _inlineStores) EmitClassify(r);
                return;
            case Opcode.LoadFixedDoubleArrayElement:
            case Opcode.LoadHoleyFixedDoubleArrayElement:
                EmitElementAddress(node, NativeLayout.FixedDoubleArrayData, 8);
                _masm.Movsd(xmm0, new Operand(rcx, NativeLayout.ArrayData));
                StoreFloat64Result(r!, xmm0);
                return;
            case Opcode.StoreFixedDoubleArrayElement:
            {
                // Every NaN is stored as double.NaN (MaglevBuiltins.StoreDoubleData).
                LoadFloat64(node.Inputs[2], xmm0);
                EmitElementAddress(node, NativeLayout.FixedDoubleArrayData, 8);
                XLabel store = _masm.NewLabel();
                _masm.Ucomisd(xmm0, xmm0);
                _masm.J(Condition.parity_odd, store);
                _masm.Movq(rax, BitConverter.DoubleToInt64Bits(double.NaN));
                _masm.Movq(xmm0, rax);
                _masm.Bind(store);
                _masm.Movsd(new Operand(rcx, NativeLayout.ArrayData), xmm0);
                return;
            }
            case Opcode.LoadTaggedField:
            case Opcode.LoadDoubleField:
            {
                // JSObject.FieldAt: the PropertyArray (bounds-checked) or the in-object slots (from the map check).
                int storage = node.Int0;
                LoadTaggedPtr(node.Inputs[0], rcx);
                int offset;
                if (storage >= JSObject.kPropertyArrayStorageBase)
                {
                    int k = storage - JSObject.kPropertyArrayStorageBase;
                    _masm.Movq(rcx, new Operand(rcx, NativeLayout.JSObjectFields));
                    _masm.Cmpl(new Operand(rcx, NativeLayout.ArrayLength), k);
                    _masm.J(Condition.below_equal, AbortLabel());
                    offset = NativeLayout.ArrayData + k * 16;
                }
                else
                {
                    offset = NativeLayout.InObjectSlots + storage * 16;
                }
                if (node.Opcode == Opcode.LoadDoubleField)
                {
                    // A Double field holds a number (or the hole NaN): its payload.
                    _masm.Movsd(xmm0, new Operand(rcx, offset + 8));
                    StoreFloat64Result(r!, xmm0);
                    return;
                }
                EmitLoadTaggedSlot(r!, new Operand(rcx, offset));
                return;
            }
            case Opcode.LoadContextSlot:
            {
                // The context depth levels up, then its slot (EmitContextSlotAddress).
                LoadTaggedPtr(node.Inputs[0], rax);
                for (int d = 0; d < node.Int1; d++)
                {
                    _masm.Movq(rcx, new Operand(rax, NativeLayout.ContextSlots));
                    _masm.Movq(rax, new Operand(rcx, NativeLayout.ArrayData + (int)Context.Field.PREVIOUS_INDEX * 16));
                }
                _masm.Movq(rcx, new Operand(rax, NativeLayout.ContextSlots));
                _masm.Cmpl(new Operand(rcx, NativeLayout.ArrayLength), node.Int0);
                _masm.J(Condition.below_equal, AbortLabel());
                EmitLoadTaggedSlot(r!, new Operand(rcx, NativeLayout.ArrayData + node.Int0 * 16));
                return;
            }
            case Opcode.LoadMap:
            {
                // The map of a JSReceiver, undefined for anything else.
                XLabel notReceiver = _masm.NewLabel(), done = _masm.NewLabel();
                ValueNode value = node.Inputs[0];
                LoadTaggedPtr(value, rax);
                if (!(node.CheckType == CheckType.kOmitHeapObjectCheck || NodeTypes.Is(value.Type, NodeType.kJSReceiver)))
                {
                    EmitBranchIfNotReceiver(notReceiver);
                }
                _masm.Movq(rax, new Operand(rax, NativeLayout.JSReceiverMap));
                _masm.Jmp(done);
                _masm.Bind(notReceiver);
                _masm.Movl(rax, 0);
                _masm.Bind(done);
                _masm.Movq(HomeOp(r!), rax);
                _masm.Movq(HomeOp(r!, 1), 0);
                ClearSnapshot(r!);
                if (r!.NeedsKind)
                {
                    // Undefined (0) or another object (kOther).
                    _masm.Movl(rcx, (int)NativeTaggedKind.kOther);
                    _masm.Movl(rdx, (int)NativeTaggedKind.kUndefined);
                    _masm.Testq(rax, rax);
                    _masm.Cmovq(Condition.equal, rcx, rdx);
                    _masm.Movq(HomeOp(r, 2), rcx);
                }
                return;
            }
            case Opcode.CheckString:
            {
                // IsString: an object whose instance type is a string's.
                XLabel exit = DeoptLabel(node.EagerDeoptInfo!);
                LoadTaggedPtr(node.Inputs[0], rax);
                _masm.Testq(rax, rax);
                _masm.J(Condition.equal, exit);
                _masm.Movzxwl(rcx, new Operand(rax, NativeLayout.HeapObjectInstanceType));
                _masm.Cmpl(rcx, (int)InstanceTypeChecks.LastString);
                _masm.J(Condition.above, exit);
                return;
            }
            case Opcode.EnterInlinedFrame:
            case Opcode.LeaveInlinedFrame:
                return;
            case Opcode.CheckTypedArrayValid:
            {
                // Not detached, and for a write not immutable (MaglevBuiltins.IsTypedArrayValid).
                XLabel exit = DeoptLabel(node.EagerDeoptInfo!);
                LoadTaggedPtr(node.Inputs[0], rax);
                _masm.Movq(rax, new Operand(rax, NativeLayout.ViewBuffer));
                _masm.Movzxbl(rcx, new Operand(rax, NativeLayout.BufferWasDetached));
                _masm.Testl(rcx, rcx);
                _masm.J(Condition.not_equal, exit);
                if (node.Int0 == 1)
                {
                    _masm.Movzxbl(rcx, new Operand(rax, NativeLayout.BufferIsImmutable));
                    _masm.Testl(rcx, rcx);
                    _masm.J(Condition.not_equal, exit);
                }
                return;
            }
            case Opcode.LoadTypedArrayElement:
                EmitLoadTypedElement(node, r!);
                return;
            case Opcode.StoreTypedArrayElement:
                EmitStoreTypedElement(node);
                return;
            case Opcode.ConvertHoleToUndefined:
            {
                // The hole is undefined (address 0, payload 0); anything else itself.
                XLabel notHole = _masm.NewLabel();
                LoadTagged(node.Inputs[0], rax, rdx);
                LoadConstantAddress(rcx, kConstTheHole);
                _masm.Cmpq(rax, rcx);
                _masm.J(Condition.not_equal, notHole);
                _masm.Movl(rax, 0);
                _masm.Movl(rdx, 0);
                _masm.Bind(notHole);
                _masm.Movq(HomeOp(r!), rax);
                _masm.Movq(HomeOp(r!, 1), rdx);
                if (r!.NeedsKind) EmitClassify(r);
                return;
            }
            case Opcode.LoadPropertyCellValue:
                LoadConstantAddress(rcx, ConstantIndex((PropertyCell)node.Obj0!));
                EmitLoadTaggedSlot(r!, new Operand(rcx, NativeLayout.PropertyCellValue));
                return;
            case Opcode.LoadJSArrayLength:
                LoadTaggedPtr(node.Inputs[0], rax);
                _masm.Movsd(xmm0, new Operand(rax, NativeLayout.JSArrayLength));
                StoreTaggedNumber(r!, xmm0);
                return;

            // ---- Operations ----------------------------------------------------------------------------------
            case Opcode.TaggedEqual:
            case Opcode.TaggedNotEqual:
            {
                XLabel t = _masm.NewLabel(), f = _masm.NewLabel(), done = _masm.NewLabel();
                EmitIdentical(node.Inputs[0], node.Inputs[1], t, f);
                bool equal = node.Opcode == Opcode.TaggedEqual;
                _masm.Bind(f);
                _masm.Movl(rax, equal ? 0 : 1);
                _masm.Jmp(done);
                _masm.Bind(t);
                _masm.Movl(rax, equal ? 1 : 0);
                _masm.Bind(done);
                StoreTaggedBoolean(r!);
                return;
            }
            case Opcode.LogicalNot:
                // !IsTrue(input).
                LoadTaggedPtr(node.Inputs[0], rcx);
                LoadConstantAddress(rdx, kConstTrue);
                _masm.Cmpq(rcx, rdx);
                _masm.Setcc(Condition.not_equal, rax);
                _masm.Movzxbl(rax, rax);
                StoreTaggedBoolean(r!);
                return;

            case Opcode.HandleNoHeapWritesInterrupt:
                EmitInterruptCheck(node);
                return;
            default:
                throw Unsupported(node.Opcode.ToString());
        }
    }

    void EmitInt32Binary(Node node, Val r)
    {
        ValueNode b = node.Inputs[1];
        Reg rb = rcx;
        int c = 0;
        bool imm = b.IsConstant && b.TryGetInt32Constant(out c);
        if (!imm) rb = Int32Reg(b, rcx);
        // Into the result's register when the right operand is not in it (its
        // interval starts here: nothing else lives in it, a deopt reads no result).
        Reg rax = r.InGp && r.Gp != rb ? r.Gp : Reg.rax;
        LoadInt32(node.Inputs[0], rax);
        bool overflowCheck = node.Opcode is Opcode.Int32AddWithOverflow or Opcode.Int32SubtractWithOverflow or Opcode.Int32MultiplyWithOverflow;
        switch (node.Opcode)
        {
            case Opcode.Int32AddWithOverflow:
            case Opcode.Int32Add:
                if (imm) _masm.Addl(rax, c); else _masm.Addl(rax, rb);
                break;
            case Opcode.Int32SubtractWithOverflow:
            case Opcode.Int32Subtract:
                if (imm) _masm.Subl(rax, c); else _masm.Subl(rax, rb);
                break;
            case Opcode.Int32MultiplyWithOverflow:
            case Opcode.Int32Multiply:
                // The operands for the -0 check (imull overwrites the left one).
                if (node.Opcode == Opcode.Int32MultiplyWithOverflow)
                {
                    _masm.Movl(rdx, rax);
                    if (imm) _masm.Orl(rdx, c); else _masm.Orl(rdx, rb);
                }
                if (imm) _masm.Imull(rax, rax, c);
                else _masm.Imull(rax, rb);
                break;
            case Opcode.Int32BitwiseAnd:
                if (imm) _masm.Andl(rax, c); else _masm.Andl(rax, rb);
                break;
            case Opcode.Int32BitwiseOr:
                // x | 0 (ToInt32 of an int32) is x.
                if (imm) { if (c != 0) _masm.Orl(rax, c); }
                else _masm.Orl(rax, rb);
                break;
            default:
                if (imm) _masm.Xorl(rax, c); else _masm.Xorl(rax, rb);
                break;
        }
        if (overflowCheck)
        {
            XLabel exit = DeoptLabel(node.EagerDeoptInfo!);
            _masm.J(Condition.overflow, exit);
            if (node.Opcode == Opcode.Int32MultiplyWithOverflow)
            {
                // A zero product with a negative operand is -0.
                XLabel ok = _masm.NewLabel();
                _masm.Testl(rax, rax);
                _masm.J(Condition.not_equal, ok);
                _masm.Testl(rdx, rdx);
                _masm.J(Condition.negative, exit);
                _masm.Bind(ok);
            }
        }
        StoreInt32Result(r, rax);
    }

    /// <summary>MaglevBuiltins.Int32Divide / Int32Modulus: deopt where the result is not an int32 (or -0).</summary>
    void EmitInt32DivMod(Node node, Val r)
    {
        XLabel exit = DeoptLabel(node.EagerDeoptInfo!);
        LoadInt32(node.Inputs[1], rcx);
        LoadInt32(node.Inputs[0], rax);
        _masm.Testl(rcx, rcx);
        _masm.J(Condition.equal, exit);
        if (node.Opcode == Opcode.Int32DivideWithOverflow)
        {
            // a == 0 && b < 0: -0.
            XLabel nonzero = _masm.NewLabel();
            _masm.Testl(rax, rax);
            _masm.J(Condition.not_equal, nonzero);
            _masm.Testl(rcx, rcx);
            _masm.J(Condition.negative, exit);
            _masm.Bind(nonzero);
            // kMinInt / -1 overflows.
            XLabel divide = _masm.NewLabel();
            _masm.Cmpl(rcx, -1);
            _masm.J(Condition.not_equal, divide);
            _masm.Cmpl(rax, int.MinValue);
            _masm.J(Condition.equal, exit);
            _masm.Bind(divide);
            _masm.Cdq();
            _masm.Idivl(rcx);
            // A remainder: not an int32.
            _masm.Testl(rdx, rdx);
            _masm.J(Condition.not_equal, exit);
            StoreInt32Result(r, rax);
            return;
        }
        // b == -1, or b == kMinInt == a: the result is 0, or -0 for a negative dividend.
        XLabel general = _masm.NewLabel(), zero = _masm.NewLabel(), done = _masm.NewLabel();
        _masm.Cmpl(rcx, -1);
        _masm.J(Condition.equal, zero);
        _masm.Cmpl(rcx, int.MinValue);
        _masm.J(Condition.not_equal, general);
        _masm.Cmpl(rax, int.MinValue);
        _masm.J(Condition.not_equal, general);
        _masm.Bind(zero);
        _masm.Testl(rax, rax);
        _masm.J(Condition.negative, exit);
        _masm.Movl(rdx, 0);
        _masm.Jmp(done);
        _masm.Bind(general);
        _masm.Movl(r11, rax);
        _masm.Cdq();
        _masm.Idivl(rcx);
        // r == 0 && a < 0: -0.
        _masm.Testl(rdx, rdx);
        _masm.J(Condition.not_equal, done);
        _masm.Testl(r11, r11);
        _masm.J(Condition.negative, exit);
        _masm.Bind(done);
        StoreInt32Result(r, rdx);
    }

    /// <summary>
    /// The int32 of the double whose bits are in rdx, into rax, jumping to
    /// <paramref name="exit"/> unless the double is that int32 exactly (the
    /// round trip has the same bits only for an integral double in range that
    /// is not -0), as the IL's EmitCheckedBitsToInt32.
    /// </summary>
    void EmitCheckedBitsToInt32(XLabel exit)
    {
        _masm.Movq(xmm0, rdx);
        _masm.Cvttsd2si(rax, xmm0);
        _masm.Xorpd(xmm1, xmm1);
        _masm.Cvtlsi2sd(xmm1, rax);
        _masm.Movq(rcx, xmm1);
        _masm.Cmpq(rcx, rdx);
        _masm.J(Condition.not_equal, exit);
    }

    void EmitCheckNumber(ValueNode value, XLabel exit)
    {
        if (!value.IsConstant && V(value).Repr != ValueRepresentation.kTagged) return;
        if (value.IsConstant)
        {
            if (!value.ConstantValue().IsNumber) _masm.Jmp(exit);
            return;
        }
        LoadTaggedPtr(value, rax);
        LoadConstantAddress(rcx, kConstNumberTag);
        _masm.Cmpq(rax, rcx);
        _masm.J(Condition.not_equal, exit);
    }

    /// <summary>
    /// JS ToInt32 of the double in xmm0, into eax (DoubleToInt32): the
    /// truncation when it is in int64 range (its low 32 bits are the result
    /// modulo 2^32), else from the exponent and mantissa (0 for NaN and the
    /// infinities).
    /// </summary>
    void EmitTruncateFloat64ToInt32()
    {
        XLabel done = _masm.NewLabel(), slow = _masm.NewLabel();
        _masm.Cvttsd2siq(rax, xmm0);
        _masm.Movq(rcx, long.MinValue);
        _masm.Cmpq(rax, rcx);
        _masm.J(Condition.equal, slow);
        _masm.Movl(rax, rax);
        _masm.Jmp(done);
        _masm.Bind(slow);
        // |x| >= 2^63, NaN or infinite: x = mantissa * 2^shift with shift >= 11.
        _masm.Movq(rdx, xmm0);
        _masm.Movq(rcx, rdx);
        _masm.Shrq(rcx, 52);
        _masm.Andl(rcx, 0x7FF);
        _masm.Movl(rax, 0);
        _masm.Cmpl(rcx, 0x7FF);
        _masm.J(Condition.equal, done);
        _masm.Subl(rcx, 1075);
        _masm.Cmpl(rcx, 31);
        _masm.J(Condition.greater, done);
        _masm.Movq(rax, (1L << 52) - 1);
        _masm.Andq(rax, rdx);
        _masm.Movq(r11, 1L << 52);
        _masm.Orq(rax, r11);
        _masm.Shlq_cl(rax);
        _masm.Testq(rdx, rdx);
        _masm.J(Condition.positive, done);
        _masm.Negl(rax);
        _masm.Bind(done);
    }

    /// <summary>CheckedNumberOrOddballToFloat64 / its truncation: the number (or the oddball's ToNumber) into xmm0.</summary>
    void EmitNumberOrOddballToFloat64(Node node)
    {
        XLabel exit = DeoptLabel(node.EagerDeoptInfo!);
        ValueNode input = node.Inputs[0];
        if (!input.IsConstant && V(input).Repr != ValueRepresentation.kTagged)
        {
            LoadFloat64(input, xmm0);
            return;
        }
        bool allowOddball = NodeTypes.CanBe((NodeType)node.Int0, NodeType.kOddball);
        bool booleanOnly = (NodeType)node.Int0 == NodeType.kNumberOrBoolean;
        XLabel done = _masm.NewLabel();
        LoadTaggedPtr(input, rax);
        LoadTaggedBits(input, rdx);
        _masm.Movq(xmm0, rdx);
        LoadConstantAddress(rcx, kConstNumberTag);
        _masm.Cmpq(rax, rcx);
        _masm.J(Condition.equal, done);
        if (!allowOddball)
        {
            _masm.Jmp(exit);
            _masm.Bind(done);
            return;
        }
        (int Index, double Value)[] oddballs = booleanOnly
            ? [(kConstTrue, 1), (kConstFalse, 0)]
            : [(kConstTrue, 1), (kConstFalse, 0), (kConstNull, 0)];
        foreach ((int index, double value) in oddballs)
        {
            XLabel next = _masm.NewLabel();
            LoadConstantAddress(rcx, index);
            _masm.Cmpq(rax, rcx);
            _masm.J(Condition.not_equal, next);
            _masm.Movq(rdx, BitConverter.DoubleToInt64Bits(value));
            _masm.Movq(xmm0, rdx);
            _masm.Jmp(done);
            _masm.Bind(next);
        }
        if (!booleanOnly)
        {
            // Undefined is NaN.
            _masm.Testq(rax, rax);
            _masm.J(Condition.not_equal, exit);
            _masm.Movq(rdx, BitConverter.DoubleToInt64Bits(double.NaN));
            _masm.Movq(xmm0, rdx);
            _masm.Jmp(done);
        }
        else
        {
            _masm.Jmp(exit);
        }
        _masm.Bind(done);
    }

    void EmitFloat64Round(Node node, Val r)
    {
        XReg x = Float64Reg(node.Inputs[0], xmm0);
        switch (node.Int0)
        {
            case 0:
                _masm.Roundsd(xmm0, x, Assembler.RoundingMode.kRoundDown);
                break;
            case 1:
                _masm.Roundsd(xmm0, x, Assembler.RoundingMode.kRoundUp);
                break;
            case 2:
            {
                // BuiltinsMath.Float64Round: ceil(x), minus 1 unless ceil(x) - 0.5 <= x.
                var constant = new Operand(rbx, kMoveTempSlot * 8);
                var ceil = new Operand(rbx, (kMoveTempSlot + 1) * 8);
                XLabel done = _masm.NewLabel();
                if (x != xmm1) _masm.Movapd(xmm1, x);
                _masm.Roundsd(xmm0, xmm1, Assembler.RoundingMode.kRoundUp);
                _masm.Movsd(ceil, xmm0);
                _masm.Movq(rax, BitConverter.DoubleToInt64Bits(-0.5));
                _masm.Movq(constant, rax);
                _masm.Addsd(xmm0, constant);
                // t <= x: x above or equal t (unordered: false).
                _masm.Ucomisd(xmm1, xmm0);
                _masm.Movsd(xmm0, ceil);
                _masm.J(Condition.above_equal, done);
                _masm.Movq(rax, BitConverter.DoubleToInt64Bits(-1.0));
                _masm.Movq(constant, rax);
                _masm.Addsd(xmm0, constant);
                _masm.Bind(done);
                break;
            }
            default:
                _masm.Roundsd(xmm0, x, Assembler.RoundingMode.kRoundToZero);
                break;
        }
        StoreFloat64Result(r, xmm0);
    }

    /// <summary>
    /// The IL's rem of doubles, which is C's fmod: x87 fprem until its partial
    /// remainder is complete (exact, as fmod). A NaN dividend is the result
    /// quieted, as glibc's fmod returns it (fprem would pick the NaN with the
    /// bigger payload when both are NaN).
    /// </summary>
    void EmitFloat64Modulus(Node node, Val r)
    {
        var dividend = new Operand(rbx, kMoveTempSlot * 8);
        var divisor = new Operand(rbx, (kMoveTempSlot + 1) * 8);
        XLabel loop = _masm.NewLabel(), done = _masm.NewLabel(), nan = _masm.NewLabel();
        LoadFloat64(node.Inputs[0], xmm0);
        LoadFloat64(node.Inputs[1], xmm1);
        _masm.Ucomisd(xmm0, xmm0);
        _masm.J(Condition.parity_even, nan);
        _masm.Movsd(dividend, xmm0);
        _masm.Movsd(divisor, xmm1);
        _masm.Fld_d(divisor);
        _masm.Fld_d(dividend);
        _masm.Bind(loop);
        _masm.Fprem();
        _masm.Fnstsw_ax();
        // C2 (bit 10): the reduction is incomplete.
        _masm.Testl(rax, 0x400);
        _masm.J(Condition.not_equal, loop);
        _masm.Fstp_d(dividend);
        _masm.Fstp_st0();
        _masm.Movsd(xmm0, dividend);
        _masm.Jmp(done);
        _masm.Bind(nan);
        _masm.Movq(rax, xmm0);
        _masm.Movq(rcx, 1L << 51);
        _masm.Orq(rax, rcx);
        _masm.Movq(xmm0, rax);
        _masm.Bind(done);
        StoreFloat64Result(r, xmm0);
    }

    /// <summary>MaglevBuiltins.Float64Min/Max: double.NaN if either is NaN, -0 below +0.</summary>
    void EmitFloat64MinMax(Node node, Val r)
    {
        bool min = node.Opcode == Opcode.Float64Min;
        LoadFloat64(node.Inputs[0], xmm0);
        LoadFloat64(node.Inputs[1], xmm1);
        XLabel takeB = _masm.NewLabel(), nan = _masm.NewLabel(), done = _masm.NewLabel();
        _masm.Ucomisd(xmm0, xmm1);
        _masm.J(Condition.parity_even, nan);
        // a < b: min takes a, max takes b; a > b: the other way round.
        _masm.J(Condition.below, min ? done : takeB);
        _masm.J(Condition.above, min ? takeB : done);
        // Equal: min takes a if a has the sign bit, max takes b then.
        _masm.Movq(rax, xmm0);
        _masm.Testq(rax, rax);
        if (min)
        {
            _masm.J(Condition.negative, done);
        }
        else
        {
            _masm.J(Condition.positive, done);
        }
        _masm.Bind(takeB);
        _masm.Movapd(xmm0, xmm1);
        _masm.Jmp(done);
        _masm.Bind(nan);
        _masm.Movq(rax, BitConverter.DoubleToInt64Bits(double.NaN));
        _masm.Movq(xmm0, rax);
        _masm.Bind(done);
        StoreFloat64Result(r, xmm0);
    }

    // The elements' array (FixedArray._data or FixedDoubleArray._data, at the
    // same offset) is cached in the payload word of a LoadElements value's
    // home, which an object does not use (nothing reads an object's payload,
    // and its rebuild repeats the load): an element access then loads it
    // once instead of following the elements each time, as the IL backend's
    // elements data locals. It is an address, valid while the code runs; a
    // resume computes it again.

    /// <summary>The elements' array address into the payload word (the elements' address is in rax).</summary>
    void EmitCacheElementsData(Val r)
    {
        if (NativeLayout.FixedArrayData == NativeLayout.FixedDoubleArrayData)
        {
            _masm.Movq(rcx, new Operand(rax, NativeLayout.FixedArrayData));
            _masm.Movq(HomeOp(r, 1), rcx);
        }
        else
        {
            _masm.Movq(HomeOp(r, 1), 0);
        }
    }

    bool HasCachedElementsData(ValueNode elements) =>
        NativeLayout.FixedArrayData == NativeLayout.FixedDoubleArrayData && !elements.IsConstant &&
        elements.Opcode == Opcode.LoadElements;

    /// <summary>A reload-rebuilt value is defined again: its snapshot (if any) is no longer its value.</summary>
    void ClearSnapshot(Val r)
    {
        if (_snapshots) _masm.Movq(HomeOp(r, 3), 0);
    }

    /// <summary>The run-time values to keep before an exit that may change the heap: the reload-rebuilt ones among <paramref name="needed"/>.</summary>
    int[] SnapshotList(IEnumerable<ValueNode> needed)
    {
        if (!_snapshots) return [];
        var closure = new HashSet<ValueNode>(ReferenceEqualityComparer.Instance);
        foreach (ValueNode n in needed) AddClosure(n, closure);
        var list = new List<int>();
        foreach (ValueNode n in closure)
        {
            if (n.IsConstant || !n.IsTagged || _managedNodes.Contains(n)) continue;
            int info = Info(n);
            if (IsReloadKind(_valueInfos[info].Kind) && !list.Contains(info)) list.Add(info);
        }
        return list.ToArray();
    }

    /// <summary>
    /// A node the managed side runs (MaglevNativeCode.Execute), as Ryu's
    /// native code exits to interpret one instruction: the values it needs
    /// and the live values are stored, the code returns, and the resume
    /// entry continues after the node with its value in its home.
    /// </summary>
    void EmitManagedNode(Node node)
    {
        List<Val> live = _liveAfter[node];
        var needed = new HashSet<ValueNode>(ReferenceEqualityComparer.Instance);
        foreach (Val v in live) needed.Add(v.Node);
        foreach (ValueNode input in node.Inputs) AddClosure(input, needed);
        if (node.LazyDeoptInfo is { } lazy) DeoptUses(lazy, needed);
        if (node.EagerDeoptInfo is { } eager) DeoptUses(eager, needed);
        needed.Remove(node as ValueNode ?? new ValueNode(Opcode.Constant, ValueRepresentation.kTagged));
        var m = new NativeManagedNode
        {
            Opcode = node.Opcode,
            Int0 = node.Int0,
            Int1 = node.Int1,
            Obj0 = node.Opcode == Opcode.StorePropertyCellValue ? node.Obj0 : node.Obj0,
            BytecodeOffset = node.BytecodeOffset,
            Inlined = node.Unit is { IsInline: true },
        };
        m.Inputs = new int[node.Inputs.Length];
        for (int i = 0; i < m.Inputs.Length; i++) m.Inputs[i] = Info(node.Inputs[i]);
        if (node is ValueNode value)
        {
            Val r = V(value);
            m.Result = Info(value);
            m.ResultHome = r.Home;
            m.ResultRepresentation = value.Representation;
            m.ResultNeedsKind = r.NeedsKind;
        }
        if (node.LazyDeoptInfo is { } lazyInfo) m.LazyDeoptIndex = DeoptPointFor(lazyInfo, node as ValueNode);
        if (node.EagerDeoptInfo is { } eagerInfo)
        {
            m.EagerDeoptIndex = DeoptPointFor(eagerInfo);
            m.EagerReason = ReasonCode(eagerInfo);
        }
        if (node.Opcode == Opcode.CallBuiltin)
        {
            var info = (CallBuiltinInfo)node.Obj0!;
            m.Invoker = System.Reflection.MethodInvoker.Create(info.Method);
            System.Reflection.ParameterInfo[] parameters = info.Method.GetParameters();
            m.ParameterTypes = new Type[parameters.Length];
            for (int i = 0; i < parameters.Length; i++) m.ParameterTypes[i] = parameters[i].ParameterType;
            m.ReturnType = info.Method.ReturnType;
        }
        var tagged = new List<int>();
        foreach (Val v in live)
        {
            if (v.Repr == ValueRepresentation.kTagged) tagged.Add(Info(v.Node));
        }
        int resumeId = _resumes.Count + 1;
        var exit = new NativeExit
        {
            Kind = NativeExitKind.kManagedNode,
            Managed = m,
            ResumeId = resumeId,
            TaggedLive = tagged.ToArray(),
            Snapshot = SnapshotList(needed),
        };
        XLabel cont = _masm.NewLabel();
        XLabel slow = _masm.NewLabel();
        bool fast = HasInlineFastPath(node) && node.Opcode switch
        {
            Opcode.ToBoolean or Opcode.ToBooleanLogicalNot => EmitToBooleanNodeFastPath(node, slow, cont),
            Opcode.LoadTypedArrayLength => EmitTypedArrayLengthFastPath(node, slow, cont),
            _ => EmitStoreNumberFastPath(node, slow, cont),
        };
        // The slow path out of line when there is a fast path.
        int exitId = NewExit(exit);
        List<Val> stores = RegisterStores(needed);
        if (fast)
        {
            _slowPaths.Add(() =>
            {
                _masm.Bind(slow);
                EmitExitStores(stores);
                _masm.Movl(rax, exitId);
                _masm.Jmp(_epilogue);
            });
        }
        else
        {
            EmitExitStores(stores);
            _masm.Movl(rax, exitId);
            _masm.Jmp(_epilogue);
        }
        _resumes.Add((_masm.NewLabel(), resumeId, live, cont));
        _masm.Bind(cont);
    }

    readonly List<Action> _slowPaths = [];

    static int TypedElementSize(ElementsKind kind) => kind switch
    {
        ElementsKind.INT8_ELEMENTS or ElementsKind.UINT8_ELEMENTS or ElementsKind.UINT8_CLAMPED_ELEMENTS => 1,
        ElementsKind.INT16_ELEMENTS or ElementsKind.UINT16_ELEMENTS => 2,
        ElementsKind.INT32_ELEMENTS or ElementsKind.UINT32_ELEMENTS or ElementsKind.FLOAT32_ELEMENTS => 4,
        ElementsKind.FLOAT64_ELEMENTS => 8,
        _ => 0,
    };

    /// <summary>The value of a typed store goes as an int32 (the IL's isFloat test, negated).</summary>
    static bool IsInt32Stored(ValueNode stored) =>
        !(stored.Representation is ValueRepresentation.kFloat64 or ValueRepresentation.kHoleyFloat64 ||
          stored.IsConstant && !stored.TryGetInt32Constant(out _));

    /// <summary>
    /// MaglevBuiltins.TypedArrayLength inline for a fixed-length view (0 when
    /// detached, the length clamped to int.MaxValue); variable-length views go
    /// to the managed side.
    /// </summary>
    bool EmitTypedArrayLengthFastPath(Node node, XLabel slow, XLabel done)
    {
        Val r = V((ValueNode)node);
        XLabel have = _masm.NewLabel();
        LoadTaggedPtr(node.Inputs[0], rax);
        _masm.Movzxbl(rcx, new Operand(rax, NativeLayout.ViewIsLengthTracking));
        _masm.Movzxbl(rdx, new Operand(rax, NativeLayout.ViewIsBackedByRab));
        _masm.Orl(rcx, rdx);
        _masm.J(Condition.not_equal, slow);
        _masm.Movq(rcx, new Operand(rax, NativeLayout.ViewBuffer));
        _masm.Movq(rdx, new Operand(rax, NativeLayout.TypedArrayRawLength));
        _masm.Movzxbl(rcx, new Operand(rcx, NativeLayout.BufferWasDetached));
        _masm.Testl(rcx, rcx);
        _masm.J(Condition.equal, have);
        _masm.Movl(rdx, 0);
        _masm.Bind(have);
        if (node.Int0 == 1)
        {
            // The whole length as a double (lengths are below 2^53).
            _masm.Xorpd(xmm0, xmm0);
            _masm.Cvtqsi2sd(xmm0, rdx);
            StoreFloat64Result(r, xmm0);
        }
        else
        {
            _masm.Movq(rcx, int.MaxValue);
            _masm.Cmpq(rdx, rcx);
            _masm.Cmovq(Condition.above, rdx, rcx);
            StoreInt32Result(r, rdx);
        }
        _masm.Jmp(done);
        return true;
    }

    /// <summary>
    /// The address of a typed element into rcx (MaglevBuiltins.TypedElement:
    /// the buffer's bytes at ByteOffset + index * size), bounds-checked
    /// against the byte array (an index the graph's checks let through
    /// aborts, as the IL's array access throws).
    /// </summary>
    void EmitTypedElementAddress(Node node, int size)
    {
        XLabel abort = AbortLabel();
        LoadTaggedPtr(node.Inputs[0], rax);
        _masm.Movq(rcx, new Operand(rax, NativeLayout.ViewBuffer));
        _masm.Movq(rcx, new Operand(rcx, NativeLayout.BufferBackingStore));
        _masm.Testq(rcx, rcx);
        _masm.J(Condition.equal, abort);
        _masm.Movq(rcx, new Operand(rcx, NativeLayout.BackingStoreBuffer));
        // byteIndex = ByteOffset + index * size, in 64 bits; then byteIndex + size <= length.
        Reg index = Int32Reg(node.Inputs[1], rdx);
        _masm.Movsxlq(rdx, index);
        if (size > 1) _masm.Imulq(rdx, rdx, size);
        _masm.Addq(rdx, new Operand(rax, NativeLayout.ViewByteOffset));
        _masm.Movl(r11, new Operand(rcx, NativeLayout.ArrayLength));
        _masm.Subq(r11, size);
        _masm.Cmpq(rdx, r11);
        _masm.J(Condition.above, abort);
        _masm.Leaq(rcx, new Operand(rcx, NativeLayout.ArrayData, rdx));
    }

    void EmitLoadTypedElement(Node node, Val r)
    {
        var kind = (ElementsKind)node.Int0;
        int size = TypedElementSize(kind);
        EmitTypedElementAddress(node, size);
        var slot = new Operand(rcx, 0);
        switch (kind)
        {
            case ElementsKind.INT8_ELEMENTS: _masm.Movsxbl(rax, slot); break;
            case ElementsKind.UINT8_ELEMENTS or ElementsKind.UINT8_CLAMPED_ELEMENTS: _masm.Movzxbl(rax, slot); break;
            case ElementsKind.INT16_ELEMENTS: _masm.Movsxwl(rax, slot); break;
            case ElementsKind.UINT16_ELEMENTS: _masm.Movzxwl(rax, slot); break;
            case ElementsKind.INT32_ELEMENTS or ElementsKind.UINT32_ELEMENTS: _masm.Movl(rax, slot); break;
            case ElementsKind.FLOAT32_ELEMENTS:
                _masm.Movss(xmm0, slot);
                _masm.Cvtss2sd(xmm0, xmm0);
                StoreFloat64Result(r, xmm0);
                return;
            default:
                _masm.Movsd(xmm0, slot);
                StoreFloat64Result(r, xmm0);
                return;
        }
        StoreInt32Result(r, rax);
    }

    void EmitStoreTypedElement(Node node)
    {
        var kind = (ElementsKind)node.Int0;
        int size = TypedElementSize(kind);
        ValueNode stored = node.Inputs[2];
        // The value first (it may use r11), into rsi-free scratch: eax or xmm0.
        bool floatKind = kind is ElementsKind.FLOAT32_ELEMENTS or ElementsKind.FLOAT64_ELEMENTS;
        if (floatKind)
        {
            LoadFloat64(stored, xmm0);
            if (kind == ElementsKind.FLOAT32_ELEMENTS) _masm.Cvtsd2ss(xmm0, xmm0);
        }
        else
        {
            if (!IsInt32Stored(stored)) throw Unsupported("typed store of a double into an integer kind");
            LoadInt32(stored, rax);
            if (kind == ElementsKind.UINT8_CLAMPED_ELEMENTS)
            {
                // value < 0 ? 0 : value > 255 ? 255 : value.
                _masm.Movl(rdx, 0);
                _masm.Cmpl(rax, 0);
                _masm.Cmovl(Condition.less, rax, rdx);
                _masm.Movl(rdx, 255);
                _masm.Cmpl(rax, 255);
                _masm.Cmovl(Condition.greater, rax, rdx);
            }
            // (Kept out of the address computation's registers.)
            _masm.Movq(new Operand(rbx, kMoveTempSlot * 8), rax);
        }
        EmitTypedElementAddress(node, size);
        var slot = new Operand(rcx, 0);
        if (floatKind)
        {
            if (kind == ElementsKind.FLOAT32_ELEMENTS) _masm.Movss(slot, xmm0);
            else _masm.Movsd(slot, xmm0);
            return;
        }
        _masm.Movq(rax, new Operand(rbx, kMoveTempSlot * 8));
        switch (size)
        {
            case 1: _masm.Movb(slot, rax); break;
            case 2: _masm.Movw(slot, rax); break;
            default: _masm.Movl(slot, rax); break;
        }
    }

    /// <summary>The 0/1 of ToBoolean in eax for oddballs, numbers and receivers; others jump to <paramref name="slow"/>.</summary>
    bool EmitToBooleanFast(ValueNode input, XLabel slow)
    {
        if (!input.IsConstant && V(input).Repr != ValueRepresentation.kTagged) return false;
        XLabel done = _masm.NewLabel(), notNumber = _masm.NewLabel();
        LoadTaggedPtr(input, rcx);
        _masm.Movl(rax, 0);
        _masm.Testq(rcx, rcx);
        _masm.J(Condition.equal, done);
        foreach ((int index, int result) in new[] { (kConstTrue, 1), (kConstFalse, 0), (kConstNull, 0) })
        {
            LoadConstantAddress(rdx, index);
            _masm.Movl(rax, result);
            _masm.Cmpq(rcx, rdx);
            _masm.J(Condition.equal, done);
        }
        LoadConstantAddress(rdx, kConstNumberTag);
        _masm.Cmpq(rcx, rdx);
        _masm.J(Condition.not_equal, notNumber);
        // A number: neither 0 nor NaN (unordered sets ZF).
        LoadTaggedBits(input, rdx);
        _masm.Movq(xmm0, rdx);
        _masm.Xorpd(xmm1, xmm1);
        _masm.Ucomisd(xmm0, xmm1);
        _masm.Setcc(Condition.not_equal, rax);
        _masm.Movzxbl(rax, rax);
        _masm.Jmp(done);
        _masm.Bind(notNumber);
        // A receiver: unless its map is undetectable (MaglevBuiltins.ToBoolean).
        _masm.Movzxwl(rdx, new Operand(rcx, NativeLayout.HeapObjectInstanceType));
        _masm.Cmpl(rdx, (int)InstanceTypeChecks.FirstJSReceiver);
        _masm.J(Condition.below, slow);
        _masm.Movq(rdx, new Operand(rcx, NativeLayout.JSReceiverMap));
        _masm.Movzxbl(rdx, new Operand(rdx, NativeLayout.MapBitField));
        _masm.Testl(rdx, Map.Bits1.IsUndetectableBit);
        _masm.Setcc(Condition.equal, rax);
        _masm.Movzxbl(rax, rax);
        _masm.Bind(done);
        return true;
    }

    bool EmitToBooleanNodeFastPath(Node node, XLabel slow, XLabel done)
    {
        if (!EmitToBooleanFast(node.Inputs[0], slow)) return false;
        if (node.Opcode == Opcode.ToBooleanLogicalNot) _masm.Xorl(rax, 1);
        StoreTaggedBoolean(V((ValueNode)node));
        _masm.Jmp(done);
        return true;
    }

    void EmitExitStores(List<Val> stores)
    {
        foreach (Val v in stores)
        {
            if (v.InGp) _masm.Movl(HomeOp(v), v.Gp);
            else _masm.Movsd(HomeOp(v), v.XmmReg);
        }
    }

    /// <summary>
    /// The inline path of a store: a number into a slot holding a number
    /// writes only the payload (NaN canonical for a Double field, as
    /// MaglevBuiltins.StoreDoubleField); anything else jumps to
    /// <paramref name="slow"/>. False when the store has no such path.
    /// </summary>
    bool EmitStoreNumberFastPath(Node node, XLabel slow, XLabel done)
    {
        // (Decided before any code: a jump to slow must have the slow path.)
        ValueNode value = node.Opcode switch
        {
            Opcode.StoreTaggedField => node.Inputs[1],
            Opcode.StoreFixedArrayElement => node.Inputs[2],
            _ => node.Inputs[1],
        };
        if (value.IsConstant ? !value.ConstantValue().IsNumber : V(value).Repr == ValueRepresentation.kHoleyFloat64) return false;
        if (node.Opcode == Opcode.StoreTaggedField && node.Int0 < JSObject.kPropertyArrayStorageBase && !InObjectLayout.IsContiguous) return false;
        switch (node.Opcode)
        {
            case Opcode.StoreTaggedField:
            {
                int storage = node.Int0;
                LoadTaggedPtr(node.Inputs[0], rcx);
                int offset;
                if (storage >= JSObject.kPropertyArrayStorageBase)
                {
                    int k = storage - JSObject.kPropertyArrayStorageBase;
                    _masm.Movq(rcx, new Operand(rcx, NativeLayout.JSObjectFields));
                    _masm.Cmpl(new Operand(rcx, NativeLayout.ArrayLength), k);
                    _masm.J(Condition.below_equal, slow);
                    offset = NativeLayout.ArrayData + k * 16;
                }
                else
                {
                    offset = NativeLayout.InObjectSlots + storage * 16;
                }
                _masm.Leaq(rcx, new Operand(rcx, offset));
                break;
            }
            case Opcode.StoreFixedArrayElement:
                EmitElementAddress(node, NativeLayout.FixedArrayData, 16);
                _masm.Leaq(rcx, new Operand(rcx, NativeLayout.ArrayData));
                break;
            case Opcode.StoreContextSlot:
                LoadTaggedPtr(node.Inputs[0], rax);
                for (int d = 0; d < node.Int1; d++)
                {
                    _masm.Movq(rcx, new Operand(rax, NativeLayout.ContextSlots));
                    _masm.Movq(rax, new Operand(rcx, NativeLayout.ArrayData + (int)Context.Field.PREVIOUS_INDEX * 16));
                }
                _masm.Movq(rcx, new Operand(rax, NativeLayout.ContextSlots));
                _masm.Cmpl(new Operand(rcx, NativeLayout.ArrayLength), node.Int0);
                _masm.J(Condition.below_equal, slow);
                _masm.Leaq(rcx, new Operand(rcx, NativeLayout.ArrayData + node.Int0 * 16));
                break;
            default:
                return false;
        }
        // rcx: the slot. The value a number (its bits in rdx), the slot a number.
        LoadTaggedPtr(value, rax);
        LoadConstantAddress(r11, kConstNumberTag);
        _masm.Cmpq(rax, r11);
        _masm.J(Condition.not_equal, slow);
        _masm.Cmpq(new Operand(rcx, 0), r11);
        _masm.J(Condition.not_equal, slow);
        LoadTaggedBits(value, rdx);
        if (node.Opcode == Opcode.StoreTaggedField && node.Int1 != 0)
        {
            XLabel store = _masm.NewLabel();
            _masm.Movq(xmm0, rdx);
            _masm.Ucomisd(xmm0, xmm0);
            _masm.J(Condition.parity_odd, store);
            _masm.Movq(rdx, BitConverter.DoubleToInt64Bits(JSValue.QuietNaN));
            _masm.Bind(store);
        }
        _masm.Movq(new Operand(rcx, 8), rdx);
        _masm.Jmp(done);
        return true;
    }

    /// <summary>A tagged JSValue at <paramref name="slot"/> into the home of <paramref name="r"/> (and its kind).</summary>
    void EmitLoadTaggedSlot(Val r, Operand slot)
    {
        _masm.Movq(rdx, slot.WithOffset(8));
        _masm.Movq(rax, slot);
        _masm.Movq(HomeOp(r, 1), rdx);
        _masm.Movq(HomeOp(r), rax);
        ClearSnapshot(r);
        if (r.NeedsKind || _inlineStores) EmitClassify(r);
    }

    /// <summary>Jumps to <paramref name="notReceiver"/> unless the address in rax is a JSReceiver's (clobbers rcx).</summary>
    void EmitBranchIfNotReceiver(XLabel notReceiver)
    {
        _masm.Testq(rax, rax);
        _masm.J(Condition.equal, notReceiver);
        _masm.Movzxwl(rcx, new Operand(rax, NativeLayout.HeapObjectInstanceType));
        _masm.Cmpl(rcx, (int)InstanceTypeChecks.FirstJSReceiver);
        _masm.J(Condition.below, notReceiver);
    }

    void EmitCheckMaps(Node node)
    {
        var maps = (Map[])node.Obj0!;
        XLabel exit = node.Int1 == 1
            ? DeoptLabel(node.EagerDeoptInfo!, NativeExitKind.kDeoptAfterMigration, node.Inputs[0])
            : DeoptLabel(node.EagerDeoptInfo!);
        XLabel notReceiver = node.Int1 == 1 ? DeoptLabel(node.EagerDeoptInfo!) : exit;
        ValueNode value = node.Inputs[0];
        LoadTaggedPtr(value, rax);
        if (!(node.CheckType == CheckType.kOmitHeapObjectCheck || NodeTypes.Is(value.Type, NodeType.kJSReceiver)))
        {
            EmitBranchIfNotReceiver(notReceiver);
        }
        _masm.Movq(rax, new Operand(rax, NativeLayout.JSReceiverMap));
        XLabel ok = _masm.NewLabel();
        _masm.Movq(rdx, new Operand(rbx, NativeCode.kConstantsSlot * 8));
        for (int i = 0; i < maps.Length; i++)
        {
            _masm.Cmpq(rax, new Operand(rdx, ConstantIndex(maps[i]) * 16));
            if (i == maps.Length - 1) _masm.J(Condition.not_equal, exit);
            else _masm.J(Condition.equal, ok);
        }
        _masm.Bind(ok);
    }

    /// <summary>
    /// The address of an element into rcx: the elements' array (field at
    /// <paramref name="dataField"/>), bounds-checked (an index the graph's
    /// checks let through out of bounds aborts, as the IL's array access throws).
    /// </summary>
    void EmitElementAddress(Node node, int dataField, int elementSize)
    {
        if (HasCachedElementsData(node.Inputs[0]))
        {
            _masm.Movq(rcx, HomeOp(V(node.Inputs[0]), 1));
        }
        else
        {
            LoadTaggedPtr(node.Inputs[0], rcx);
            _masm.Movq(rcx, new Operand(rcx, dataField));
        }
        Reg index = Int32Reg(node.Inputs[1], rdx);
        _masm.Cmpl(index, new Operand(rcx, NativeLayout.ArrayLength));
        _masm.J(Condition.above_equal, AbortLabel());
        _masm.Movl(rdx, index);
        if (elementSize == 16)
        {
            _masm.Shlq(rdx, 4);
            _masm.Addq(rcx, rdx);
        }
        else
        {
            _masm.Leaq(rcx, new Operand(rcx, 0, rdx, ScaleFactor.times_8));
        }
    }

    void EmitInterruptCheck(Node node)
    {
        // Interrupts pending (for a loop without calls, only those it cannot
        // defer), or the poll budget spent: a resumable exit.
        List<Val> live = _liveAfter[node];
        var tagged = new List<int>();
        foreach (Val v in live)
        {
            if (v.Repr == ValueRepresentation.kTagged) tagged.Add(Info(v.Node));
        }
        int resumeInterrupt = _resumes.Count + 1;
        var interrupt = new NativeExit
        {
            Kind = NativeExitKind.kInterrupt,
            InterruptKind = node.Int0,
            ResumeId = resumeInterrupt,
            TaggedLive = tagged.ToArray(),
            BytecodeOffset = node.BytecodeOffset,
            DeoptIndex = node.EagerDeoptInfo is { } e ? DeoptPointFor(e) : -1,
            Reason = node.EagerDeoptInfo is { } e2 ? (int)e2.Reason : 0,
        };
        if (node.Int0 == 1 && interrupt.DeoptIndex < 0) throw Unsupported("interrupt check without deopt info");
        if (node.Int0 != 1)
        {
            var keep = new HashSet<ValueNode>(ReferenceEqualityComparer.Instance);
            foreach (Val v in live) keep.Add(v.Node);
            if (node.EagerDeoptInfo is { } fs) DeoptUses(fs, keep);
            interrupt.Snapshot = SnapshotList(keep);
        }
        var poll = new NativeExit { Kind = NativeExitKind.kPoll, ResumeId = resumeInterrupt, TaggedLive = interrupt.TaggedLive };
        // The stub stores the live values and, for the deopt an interrupt can
        // take, the frame state's (which may be dead after the check).
        var needed = new HashSet<ValueNode>(ReferenceEqualityComparer.Instance);
        foreach (Val v in live) needed.Add(v.Node);
        if (node.EagerDeoptInfo is { } frameState) DeoptUses(frameState, needed);
        List<Val> stores = RegisterStores(needed);
        XLabel interruptStub = _masm.NewLabel(), pollStub = _masm.NewLabel(), cont = _masm.NewLabel();
        _exitStubs.Add((interruptStub, NewExit(interrupt), stores));
        _exitStubs.Add((pollStub, NewExit(poll), stores));
        _resumes.Add((_masm.NewLabel(), resumeInterrupt, live, cont));
        _masm.Movq(rax, new Operand(rbx, NativeCode.kInterruptFlagsSlot * 8));
        _masm.Movl(rax, new Operand(rax, 0));
        if (node.Int0 == 1)
        {
            _masm.Andl(rax, ~(int)(StackGuard.InterruptFlag.INSTALL_CODE | StackGuard.InterruptFlag.INSTALL_BASELINE_CODE |
                                   StackGuard.InterruptFlag.INSTALL_MAGLEV_CODE));
        }
        else
        {
            _masm.Testl(rax, rax);
        }
        _masm.J(Condition.not_equal, interruptStub);
        _masm.Decq(kPollRegister);
        _masm.J(Condition.equal, pollStub);
        _masm.Bind(cont);
    }
}

/// <summary>
/// The field offsets native code reads (V8: the object layouts of
/// objects/*-inl.h and the field offsets of the Torque classes): measured
/// from the CLR's layout of V8Sharp's classes once per process.
/// </summary>
internal static class NativeLayout
{
    /// <summary>The first element of an array (after the method table pointer and the length).</summary>
    public const int ArrayData = 16;
    /// <summary>The length (int32) of an array.</summary>
    public const int ArrayLength = 8;

    public static readonly int HeapObjectInstanceType;
    public static readonly int JSReceiverMap;
    public static readonly int JSObjectElements;
    public static readonly int JSArrayLength;
    public static readonly int FixedArrayData;
    public static readonly int FixedDoubleArrayData;
    public static readonly int JSObjectFields;
    /// <summary>The first in-object slot (JSObjectInObject1._slots0; the others follow, InObjectLayout.IsContiguous).</summary>
    public static readonly int InObjectSlots;
    public static readonly int ContextSlots;
    public static readonly int PropertyCellValue;
    public static readonly int MapBitField;
    public static readonly int ViewBuffer, ViewByteOffset, ViewIsLengthTracking, ViewIsBackedByRab, TypedArrayRawLength;
    public static readonly int BufferWasDetached, BufferIsImmutable, BufferBackingStore, BackingStoreBuffer;

    static NativeLayout()
    {
        var array = (JSArray)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(JSArray));
        HeapObjectInstanceType = FieldOffsets.Of(array, ref System.Runtime.CompilerServices.Unsafe.AsRef(in array.InstanceType));
        JSReceiverMap = FieldOffsets.Of(array, ref array.Map);
        JSObjectElements = FieldOffsets.Of(array, ref array.Elements);
        JSArrayLength = FieldOffsets.Of(array, ref array._length);
        JSObjectFields = FieldOffsets.Of(array, ref array._fields);
        var inObject = (JSObjectInObject1)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(JSObjectInObject1));
        InObjectSlots = FieldOffsets.Of(inObject, ref InObjectLayout.First(inObject));
        var context = (Context)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(Context));
        ContextSlots = FieldOffsets.Of(context, ref System.Runtime.CompilerServices.Unsafe.AsRef(in context.Slots));
        var cell = (PropertyCell)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(PropertyCell));
        PropertyCellValue = FieldOffsets.Of(cell, ref cell.Value);
        var map = (Map)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(Map));
        MapBitField = FieldOffsets.Of(map, ref map.BitField);
        var typed = (JSTypedArray)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(JSTypedArray));
        ViewBuffer = FieldOffsets.Of(typed, ref typed.Buffer);
        ViewByteOffset = FieldOffsets.Of(typed, ref typed.ByteOffset);
        ViewIsLengthTracking = FieldOffsets.Of(typed, ref typed.IsLengthTracking);
        ViewIsBackedByRab = FieldOffsets.Of(typed, ref typed.IsBackedByRab);
        TypedArrayRawLength = FieldOffsets.Of(typed, ref typed.RawLength);
        var buffer = (JSArrayBuffer)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(JSArrayBuffer));
        BufferWasDetached = FieldOffsets.Of(buffer, ref buffer.WasDetached);
        BufferIsImmutable = FieldOffsets.Of(buffer, ref buffer.IsImmutable);
        BufferBackingStore = FieldOffsets.Of(buffer, ref buffer._backingStore);
        var store = (BackingStore)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(BackingStore));
        BackingStoreBuffer = FieldOffsets.Of(store, ref store._buffer);
        var fixedArray = (FixedArray)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(FixedArray));
        FixedArrayData = FieldOffsets.Of(fixedArray, ref fixedArray._data);
        var doubleArray = (FixedDoubleArray)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(FixedDoubleArray));
        FixedDoubleArrayData = FieldOffsets.Of(doubleArray, ref doubleArray._data);
        JSValue probe = default;
        if (FieldOffsets.Distance(ref probe, ref System.Runtime.CompilerServices.Unsafe.AsRef(in probe._obj)) != 0 ||
            FieldOffsets.Distance(ref probe, ref System.Runtime.CompilerServices.Unsafe.AsRef(in probe._bits)) != 8)
        {
            throw new InvalidOperationException("native Maglev code expects JSValue as { object, long }");
        }
    }
}
