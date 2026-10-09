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

internal sealed partial class MaglevCodeGenerator
{
    static readonly int kJSValueSize = Unsafe.SizeOf<JSValue>();

    /// <summary>JSValue (on the stack) -> its number payload as a double.</summary>
    void EmitLoadNumber()
    {
        _il.Emit(OpCodes.Ldfld, s_bits);
        _il.Emit(OpCodes.Call, s_int64BitsToDouble);
    }

    readonly MaglevCompilationInfo _info;
    readonly Graph _graph;
    readonly MaglevCode _code;
    readonly TypeBuilder _type;
    readonly MethodBuilder _method;
    readonly MaglevILEmitter _il;

    // Frame locals of the outermost frame.
    readonly LocalBuilder _fpRef;
    readonly LocalBuilder _fp;
    readonly LocalBuilder _frame;
    readonly LocalBuilder _baseFrameIndex;
    // Scratch locals, declared on first use (a small method's local count
    // decides whether RyuJIT can inline it into its direct entry).
    LocalBuilder? _tmpLongLocal, _tmpDoubleLocal, _tmpIntLocal, _tmpMapLocal, _tmpValueLocal, _tmpObjectLocal;
    LocalBuilder _tmpLong => _tmpLongLocal ??= _il.DeclareLocal(typeof(long));
    LocalBuilder _tmpDouble => _tmpDoubleLocal ??= _il.DeclareLocal(typeof(double));
    LocalBuilder _tmpInt => _tmpIntLocal ??= _il.DeclareLocal(typeof(int));
    LocalBuilder _tmpMap => _tmpMapLocal ??= _il.DeclareLocal(typeof(Map));
    LocalBuilder _tmpValue => _tmpValueLocal ??= _il.DeclareLocal(typeof(JSValue));
    LocalBuilder _tmpObject => _tmpObjectLocal ??= _il.DeclareLocal(typeof(HeapObject));

    readonly List<(FieldBuilder Field, object? Value)> _staticConstants = [];
    // (Shared with the region generators: their methods are on the same type.)
    readonly Dictionary<(object, Type), FieldBuilder> _constantFields = new();
    List<DeoptPoint> _deoptPoints = [];
    readonly Dictionary<(DeoptFrame, DeoptimizeReason, int), Label> _eagerExits = new();
    List<(FeedbackVector Vector, int Slot)> _speculationFeedback = [];
    // One deopt exit per frame state: the checks of a checkpoint branch to a
    // stub that sets the reason and jumps to it.
    readonly Dictionary<DeoptFrame, Label> _frameExits = new(ReferenceEqualityComparer.Instance);
    readonly List<(Label Stub, Label Exit, int Reason)> _eagerStubs = [];
    LocalBuilder? _deoptReason;
    LocalBuilder? _deoptIndex;
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

    readonly bool _optimizeFully;

    /// <param name="optimizeFully">
    /// RyuJIT compiles the method fully optimized at once (AggressiveOptimization):
    /// concurrent compiles, whose JIT cost is off the main thread.
    /// </param>
    public MaglevCodeGenerator(MaglevCompilationInfo info, MaglevCode code, bool optimizeFully = false)
        : this(info, code, optimizeFully, null)
    {
    }

    /// <summary>
    /// <paramref name="primary"/> non-null: the frameless direct entry of the
    /// code <paramref name="primary"/> generates (TryGenerateFramelessEntry),
    /// sharing its deopt points.
    /// </summary>
    MaglevCodeGenerator(MaglevCompilationInfo info, MaglevCode code, bool optimizeFully, MaglevCodeGenerator? primary, bool lazyFrame = false)
    {
        _optimizeFully = optimizeFully;
        _info = info;
        _graph = info.Graph;
        _code = code;
        string name = "maglev:" + MaglevCompiler.DebugName(info.Function.Shared) + (info.IsOsr ? "@osr" + info.OsrOffset : "");
        if (primary is null)
        {
            (_type, _method) = BaselineCodeSpace.For(info.Isolate).DefineMethod(name, typeof(JSValue),
                [typeof(MaglevCode), typeof(Isolate), typeof(InterpreterState).MakeByRefType()]);
        }
        else
        {
            _frameless = true;
            _lazyFrame = lazyFrame;
            _fastCallArity = primary._fastCallArity;
            _deoptPoints = primary._deoptPoints;
            _speculationFeedback = primary._speculationFeedback;
            (_type, _method) = BaselineCodeSpace.For(info.Isolate).DefineMethod(name + (lazyFrame ? ":lazy" : ":frameless"), typeof(JSValue), FastCallParameterTypes(_fastCallArity));
        }
        _il = new MaglevILEmitter(_method.GetILGenerator(4096));
        _fpRef = _il.DeclareLocal(typeof(JSValue).MakeByRefType());
        _fp = _il.DeclareLocal(typeof(int));
        _frame = _il.DeclareLocal(typeof(InterpreterFrameRecord).MakeByRefType());
        _baseFrameIndex = _il.DeclareLocal(typeof(int));
    }

    /// <summary>
    /// V8SHARP_MAGLEV_AGGRESSIVE=1 compiles Maglev methods fully optimized at
    /// once (MethodImplAttributes.AggressiveOptimization) instead of through
    /// RyuJIT's tier 0.
    /// </summary>
    static readonly int s_aggressiveMaxIL = int.TryParse(Environment.GetEnvironmentVariable("V8SHARP_MAGLEV_AGGRESSIVE_MAX_IL"), out int n) ? n : 0;

    // ---- Reflection handles ------------------------------------------------------------------------------

    static readonly FieldInfo s_obj = typeof(JSValue).GetField("_obj", BindingFlags.NonPublic | BindingFlags.Instance)!;
    // A number's payload is JSValue._bits (the double's bits).
    static readonly FieldInfo s_bits = typeof(JSValue).GetField("_bits", BindingFlags.NonPublic | BindingFlags.Instance)!;
    static readonly MethodInfo s_int64BitsToDouble = typeof(BitConverter).GetMethod(nameof(BitConverter.Int64BitsToDouble), [typeof(long)])!;
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
    static readonly MethodInfo s_jsValueFromObject = typeof(JSValue).GetMethod(nameof(JSValue.FromObject), [typeof(HeapObject)])!;
    static readonly FieldInfo s_interpreterFrameDepth = typeof(Isolate).GetField(nameof(Isolate.InterpreterFrameDepth))!;
    static readonly MethodInfo s_interpreterFrames = typeof(Isolate).GetProperty(nameof(Isolate.InterpreterFrames))!.GetMethod!;
    static readonly FieldInfo s_stFp = typeof(InterpreterState).GetField(nameof(InterpreterState.Fp))!;
    static readonly FieldInfo s_stFrameIndex = typeof(InterpreterState).GetField(nameof(InterpreterState.FrameIndex))!;
    static readonly FieldInfo s_recordFp = typeof(InterpreterFrameRecord).GetField(nameof(InterpreterFrameRecord.Fp))!;
    static readonly FieldInfo s_markedForDeoptimization = typeof(MaglevCode).GetField(nameof(MaglevCode.MarkedForDeoptimization))!;
    static readonly FieldInfo s_deoptScratch = typeof(Isolate).GetField(nameof(Isolate.MaglevDeoptScratch))!;
    static readonly FieldInfo s_propertyCellValue = typeof(PropertyCell).GetField(nameof(PropertyCell.Value))!;
    static readonly MethodInfo s_storeSlot = typeof(Baseline.BaselineBuiltins).GetMethod(nameof(Baseline.BaselineBuiltins.StoreSlot))!;
    static readonly MethodInfo s_doubleToInt64Bits = typeof(BitConverter).GetMethod(nameof(BitConverter.DoubleToInt64Bits), [typeof(double)])!;

    /// <summary>Time spent creating the code's type and delegate (V8SHARP_JIT_STATS).</summary>
    internal static double CreateTypeMs;
    // Diagnostics: V8SHARP_IL_HISTOGRAM=1 prints the IL bytes and block boundaries per node kind at exit.
    static readonly Dictionary<string, (int Count, int Bytes, int Blocks)>? s_ilHistogram = InitHistogram();

    static readonly int s_histogramTop = int.TryParse(Environment.GetEnvironmentVariable("V8SHARP_IL_HISTOGRAM_TOP"), out int top) ? top : 30;

    static Dictionary<string, (int, int, int)>? InitHistogram()
    {
        if (Environment.GetEnvironmentVariable("V8SHARP_IL_HISTOGRAM") != "1") return null;
        var h = new Dictionary<string, (int, int, int)>();
        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            var list = new List<(string, int, int, int)>();
            foreach (var e in h) list.Add((e.Key, e.Value.Item1, e.Value.Item2, e.Value.Item3));
            list.Sort(static (a, b) => b.Item3.CompareTo(a.Item3));
            long count = 0, bytes = 0, blocks = 0;
            foreach ((string _, int c, int by, int bl) in list) { count += c; bytes += by; blocks += bl; }
            Console.Error.WriteLine($"IL {bytes,8} {count,7} {blocks,7} (total: bytes, nodes, blocks)");
            for (int i = 0; i < list.Count && i < s_histogramTop; i++)
            {
                Console.Error.WriteLine($"IL {list[i].Item3,8} {list[i].Item2,7} {list[i].Item4,7} {list[i].Item1}");
            }
        };
        return h;
    }

    static readonly System.Collections.Concurrent.ConcurrentDictionary<string, MethodInfo> s_builtins = new(StringComparer.Ordinal);

    static MethodInfo B(string name) => s_builtins.GetOrAdd(name, static n =>
        typeof(MaglevBuiltins).GetMethod(n) ?? throw new InvalidOperationException("no MaglevBuiltins." + n));

    // ---- Driver ----------------------------------------------------------------------------------------------

    public (MaglevCodeEntry Entry, int ILSize) Generate()
    {
        int bodySize;
        if (_plan is not null)
        {
            // The regions, and this method as their dispatcher (MaglevCodeGenerator.Regions.cs).
            EmitSplit();
            bodySize = _il.ILOffset;
        }
        else
        {
            AllocateLocals();
            EmitPrologue();
            foreach (BasicBlock block in _graph.Blocks)
            {
                if (!block.IsDead && block.IsExceptionHandler) _catchBlocks.Add(block);
            }
            _hasCatchBlocks = _catchBlocks.Count > 0;
            if (_hasCatchBlocks) EmitTryRegionStart();
            var blocks = new List<BasicBlock>(_graph.Blocks.Count);
            foreach (BasicBlock block in _graph.Blocks)
            {
                if (!block.IsDead) blocks.Add(block);
            }
            _blockCosts = new(blocks.Count, ReferenceEqualityComparer.Instance);
            for (int i = 0; i < blocks.Count; i++)
            {
                _nextBlock = i + 1 < blocks.Count ? blocks[i + 1] : null;
                EmitBlock(blocks[i]);
            }
            _nextBlock = null;
            EmitEdgeStubs();
            bodySize = _il.ILOffset;
            EmitDeoptExits();
            if (_hasCatchBlocks) EmitTryRegionEnd();
            // Over RyuJIT's optimization limits: the code again, in regions.
            if (ExceedsJitLimits() && TryGenerateSplit() is { } split) return split;
        }
        if (_il.ILOffset > kMaxOptimizedILBytes && !_info.Isolate.Flags.allow_natives_syntax)
        {
            // V8SHARP_MAGLEV_MAX_IL: a limit for experiments (none by default;
            // RyuJIT compiles a method over its limits with MinOpts).
            throw new MaglevBailoutException($"IL beyond RyuJIT's optimization limits ({_il.ILOffset} bytes)");
        }
        if (_info.Isolate.Flags.trace_opt_verbose)
        {
            Console.WriteLine($"[maglev code: {bodySize} bytes IL body, {_il.ILOffset - bodySize} bytes in {_pendingExits.Count} deopt exits " +
                              $"({_frameExits.Count} eager, {_pendingExits.Count - _frameExits.Count} lazy; {_eagerStubs.Count} eager checks, {_spilledValues} values); " +
                              $"{_il.Instructions} instructions, {_il.BlockBoundaries} block boundaries, {_il.LocalReferences} local references, {_il.Locals} locals]");
        }

        MethodBuilder? fastCall = DefineFastCallEntry();
        MaglevCodeGenerator? frameless = fastCall is not null && _plan is null ? TryGenerateFramelessEntry() : null;
        if (fastCall is not null && (_info.Isolate.Flags.trace_opt_verbose || s_traceEntries))
        {
            Console.WriteLine($"[maglev direct entry of {MaglevCompiler.DebugName(_info.Function.Shared)}: " +
                              $"{(frameless is null ? "frameful" : frameless._lazyFrame ? "lazy frame, " + frameless._lazyUnits.Count + " lazy inlined" : "frameless")}]");
        }
        MethodImplAttributes bodyFlags = MethodImplAttributes.IL;
        if (_optimizeFully || _il.ILOffset <= s_aggressiveMaxIL || _il.ILOffset > kAggressiveILBytes)
        {
            bodyFlags |= MethodImplAttributes.AggressiveOptimization;
            fastCall?.SetImplementationFlags(MethodImplAttributes.AggressiveOptimization);
        }
        // A small body without exception handlers is inlined into its direct
        // entry: a direct call is then one .NET call, as V8's call is one jump
        // to the callee's code.
        if (fastCall is not null && !_hasCatchBlocks && _il.ILOffset <= kInlineIntoFastCallILBytes) bodyFlags |= MethodImplAttributes.AggressiveInlining;
        if (bodyFlags != MethodImplAttributes.IL) _method.SetImplementationFlags(bodyFlags);
        _code.DeoptPoints = _deoptPoints.ToArray();
        _code.SpeculationFeedback = _speculationFeedback.ToArray();
        _code.MaxScratchSize = Math.Max(_maxScratch, frameless?._maxScratch ?? 0);
        _code.ILCounts = (_il.Instructions, _il.BlockBoundaries, _il.LocalReferences, _il.Locals);
        long createStart = System.Diagnostics.Stopwatch.GetTimestamp();
        Type type = BaselineCodeSpace.CreateType(_type);
        if (_plan is not null)
        {
            BaselineCodeSpace.CreateType(_plan.Transfer);
            var regionMethods = new MethodInfo[_plan.Generators.Length];
            for (int k = 0; k < regionMethods.Length; k++) regionMethods[k] = type.GetMethod(_plan.Generators[k]._method.Name)!;
            _code.RegionMethods = regionMethods;
            if (s_splitFilter is not null) foreach (MethodInfo m in regionMethods) DumpIL(m);
        }
        foreach ((FieldBuilder field, object? value) in _staticConstants)
        {
            type.GetField(field.Name)!.SetValue(null, value);
        }
        MethodInfo method = type.GetMethod(_method.Name)!;
        var entry = (MaglevCodeEntry)method.CreateDelegate(typeof(MaglevCodeEntry), _code);
        if (fastCall is not null)
        {
            _code.FastCall = frameless?.CreateFramelessDelegate() ??
                             type.GetMethod(fastCall.Name)!.CreateDelegate(MaglevFastCalls.DelegateTypes[_fastCallArity], _code);
            _code.FastCallArity = _fastCallArity;
            _code.DirectEntryKind = frameless is null ? MaglevDirectEntryKind.Frameful
                : frameless._lazyFrame ? MaglevDirectEntryKind.LazyFrame : MaglevDirectEntryKind.Frameless;
        }
        CreateTypeMs += System.Diagnostics.Stopwatch.GetElapsedTime(createStart).TotalMilliseconds;
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
        if (s_shareLocals && TryAllocateSharedLocals()) return;
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

    /// <summary>
    /// The most IL a tiering compile may produce (V8SHARP_MAGLEV_MAX_IL; no
    /// limit by default). RyuJIT compiles a method over 60000 IL bytes, 20000
    /// IL instructions, 2000 basic blocks or 8000 local references with
    /// MinOpts, AggressiveOptimization or not ("Tier-0 switched MinOpts" in
    /// DOTNET_JitDisasmSummary); methods over <see cref="kAggressiveILBytes"/>
    /// are compiled fully optimized at once, as the concurrent compiles are.
    /// </summary>
    static readonly int kMaxOptimizedILBytes = int.TryParse(Environment.GetEnvironmentVariable("V8SHARP_MAGLEV_MAX_IL"), out int maxIL) ? maxIL : int.MaxValue;

    /// <summary>The largest body inlined into its direct entry (RyuJIT's inlinee limits: no EH, few locals).</summary>
    static readonly int kInlineIntoFastCallILBytes =
        int.TryParse(Environment.GetEnvironmentVariable("V8SHARP_MAGLEV_INLINE_BODY_IL"), out int inlineBody) ? inlineBody : 1200;

    /// <summary>Methods with more IL are compiled with AggressiveOptimization (see kMaxOptimizedILBytes).</summary>
    const int kAggressiveILBytes = 20000;

    // V8SHARP_MAGLEV_SHARE_LOCALS=0 gives every value its own IL local (for comparison).
    static readonly bool s_shareLocals = Environment.GetEnvironmentVariable("V8SHARP_MAGLEV_SHARE_LOCALS") != "0";

    /// <summary>
    /// The register allocation of maglev-regalloc.cc for IL: values whose live
    /// ranges do not overlap share an IL local of their CLR type, so the
    /// method has about as many locals as values live at once. RyuJIT
    /// compiles a method with more than 512 locals without inlining and one
    /// with more than 2000 without optimization (MinOpts), which is what
    /// limited the size of the graphs the tier could compile.
    ///
    /// Live ranges (LiveRangeAndNextUseProcessor, maglev-pre-regalloc-codegen-processors.h):
    /// positions number the nodes in emission order; a value is live from its
    /// definition (a phi: from the end of its first predecessor) to its last
    /// use, counting node inputs, the values of the nodes' deopt frames (read
    /// by the out-of-line exits at the node), phi inputs at the end of the
    /// predecessor and the arguments of an inlined call whose frame is pushed
    /// lazily (read by any node of the inlined function); a value live into a
    /// loop header from outside is live to the loop's last back edge, and so
    /// is a loop header's phi. Code with catch blocks keeps one local per
    /// value: their trampolines read the values of the throwing node.
    /// </summary>
    bool TryAllocateSharedLocals() => TryAllocateSharedLocals(_graph.Blocks, null, null);

    /// <param name="blocks">The blocks of the method (a region's, or all).</param>
    /// <param name="preassigned">Values that have their locals already (a region's entry values).</param>
    /// <param name="usesAtEnd">Values used at the end of a block besides its own uses (a region's exit stubs).</param>
    bool TryAllocateSharedLocals(List<BasicBlock> blocks, HashSet<ValueNode>? preassigned,
        Dictionary<BasicBlock, List<ValueNode>>? usesAtEnd)
    {
        foreach (BasicBlock block in blocks)
        {
            if (!block.IsDead && block.IsExceptionHandler) return false;
        }
        var def = new Dictionary<ValueNode, int>(ReferenceEqualityComparer.Instance);
        var last = new Dictionary<ValueNode, int>(ReferenceEqualityComparer.Instance);
        var blockStart = new Dictionary<BasicBlock, int>(ReferenceEqualityComparer.Instance);
        var blockEnd = new Dictionary<BasicBlock, int>(ReferenceEqualityComparer.Instance);
        var unitLast = new Dictionary<MaglevCompilationUnit, int>(ReferenceEqualityComparer.Instance);
        int pos = 0;
        void Use(ValueNode v, int at)
        {
            if (v.IsConstant) return;
            if (!last.TryGetValue(v, out int l) || l < at) last[v] = at;
        }
        void UseFrame(DeoptFrame? frame, int at) => MaglevEscapeAnalysis.ForEachDeoptValue(frame, v => Use(v, at));
        foreach (BasicBlock block in blocks)
        {
            if (block.IsDead) continue;
            blockStart[block] = pos++;
            foreach (Node node in block.Nodes)
            {
                int at = pos++;
                foreach (ValueNode input in node.Inputs)
                {
                    Use(input, at);
                    // Stores read the untagged value under a tagging (UntaggedNumberSource).
                    if (node.Opcode is Opcode.StoreFixedArrayElement or Opcode.StoreContextSlot or Opcode.StoreMapTransition)
                    {
                        Use(UntaggedNumberSource(input), at);
                    }
                }
                UseFrame(node.EagerDeoptInfo?.TopFrame, at);
                UseFrame(node.LazyDeoptInfo?.TopFrame, at);
                if (node is ValueNode v) def[v] = at;
                for (MaglevCompilationUnit? u = node.Unit; u is { IsInline: true }; u = u.Caller) unitLast[u] = at;
            }
            ControlNode c = block.Control!;
            int end = pos++;
            blockEnd[block] = end;
            foreach (ValueNode input in c.Inputs) Use(input, end);
            UseFrame(c.EagerDeoptInfo?.TopFrame, end);
            if (usesAtEnd is not null && usesAtEnd.TryGetValue(block, out List<ValueNode>? atEnd))
            {
                foreach (ValueNode v in atEnd) Use(v, end);
            }
        }
        // Phis: inputs used at the end of their predecessor; the phi is defined
        // at the end of its first predecessor in emission order.
        var loops = new List<(int Start, int End)>();
        foreach (BasicBlock block in blocks)
        {
            if (block.IsDead) continue;
            int loopEnd = -1;
            if (block.IsLoopHeader)
            {
                foreach (BasicBlock pred in block.Predecessors)
                {
                    if (blockEnd.TryGetValue(pred, out int e) && e >= blockStart[block] && e > loopEnd) loopEnd = e;
                }
                if (loopEnd >= 0) loops.Add((blockStart[block], loopEnd));
            }
            foreach (Phi phi in block.Phis)
            {
                int first = blockStart[block];
                for (int k = 0; k < phi.Inputs.Length && k < block.Predecessors.Count; k++)
                {
                    if (!blockEnd.TryGetValue(block.Predecessors[k], out int e)) continue;
                    Use(phi.Inputs[k], e);
                    if (e < first) first = e;
                }
                // (A phi with its own local still reads its inputs at the predecessors' ends.)
                if (preassigned is not null && preassigned.Contains(phi)) continue;
                def[phi] = first;
                if (loopEnd >= 0) Use(phi, loopEnd);
            }
        }
        // Lazily pushed inlined frames read their call's receiver and arguments.
        foreach (BasicBlock block in blocks)
        {
            if (block.IsDead) continue;
            foreach (Node node in block.Nodes)
            {
                if (node.Opcode != Opcode.EnterInlinedFrame || node.Obj0 is not MaglevCompilationUnit { EagerFrame: false } unit) continue;
                if (!unitLast.TryGetValue(unit, out int l)) continue;
                foreach (ValueNode input in node.Inputs) Use(input, l);
            }
        }
        // Values live into a loop from outside are live through it (to a
        // fixed point: extending a range can reach an enclosing loop).
        bool changed = true;
        while (changed)
        {
            changed = false;
            foreach ((int start, int end) in loops)
            {
                foreach (KeyValuePair<ValueNode, int> d in def)
                {
                    if (d.Value < start && last.TryGetValue(d.Key, out int l) && l >= start && l < end)
                    {
                        last[d.Key] = end;
                        changed = true;
                    }
                }
            }
        }
        // Linear scan over the definitions, one free list per CLR type.
        var values = new List<(int Def, int Last, ValueNode Node)>();
        foreach (KeyValuePair<ValueNode, int> d in def)
        {
            ValueNode v = d.Key;
            if (v.IsConstant || v.UseCount <= 0 || preassigned is not null && preassigned.Contains(v)) continue;
            int l = last.TryGetValue(v, out int x) ? x : d.Value;
            values.Add((d.Value, Math.Max(l, d.Value), v));
        }
        values.Sort(static (a, b) => a.Def != b.Def ? a.Def.CompareTo(b.Def) : a.Last.CompareTo(b.Last));
        var free = new Dictionary<Type, Stack<LocalBuilder>>();
        // Active ranges ordered by their end.
        var active = new PriorityQueue<(LocalBuilder Local, Type Type), int>();
        int locals = 0;
        foreach ((int d, int l, ValueNode v) in values)
        {
            while (active.TryPeek(out (LocalBuilder Local, Type Type) a, out int activeEnd) && activeEnd < d)
            {
                active.Dequeue();
                if (!free.TryGetValue(a.Type, out Stack<LocalBuilder>? stack)) free[a.Type] = stack = new Stack<LocalBuilder>();
                stack.Push(a.Local);
            }
            Type type = ClrType(v.Representation);
            LocalBuilder local;
            if (free.TryGetValue(type, out Stack<LocalBuilder>? pool) && pool.Count > 0)
            {
                local = pool.Pop();
            }
            else
            {
                if (++locals > kMaxLocals) throw new MaglevBailoutException($"too many values for IL locals ({locals})");
                local = _il.DeclareLocal(type);
            }
            v.Local = local;
            active.Enqueue((local, type), l);
        }
        return true;
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
        if (_lazyFrame)
        {
            EmitLazyPrologue();
            return;
        }
        if (_frameless)
        {
            // No frame yet: the record a deopt pushes is the next one.
            _il.Emit(OpCodes.Ldarg_1);
            _il.Emit(OpCodes.Ldfld, s_interpreterFrameDepth);
            _il.Emit(OpCodes.Stloc, _baseFrameIndex);
            return;
        }
        // fp and the frame index come from the frame's builder (EnterFrame, the
        // direct entry, OSR), which checked them against the stacks' limits:
        // the slot and the record are addressed without bounds checks.
        _il.Emit(OpCodes.Ldarg_2);
        _il.Emit(OpCodes.Ldfld, s_stFp);
        _il.Emit(OpCodes.Stloc, _fp);
        _il.Emit(OpCodes.Ldarg_1);
        _il.Emit(OpCodes.Ldfld, s_registerStack);
        _il.Emit(OpCodes.Call, s_arrayDataReference.MakeGenericMethod(typeof(JSValue)));
        _il.Emit(OpCodes.Ldloc, _fp);
        _il.Emit(OpCodes.Call, s_unsafeAddInt.MakeGenericMethod(typeof(JSValue)));
        _il.Emit(OpCodes.Stloc, _fpRef);
        _il.Emit(OpCodes.Ldarg_2);
        _il.Emit(OpCodes.Ldfld, s_stFrameIndex);
        _il.Emit(OpCodes.Stloc, _baseFrameIndex);
        _il.Emit(OpCodes.Ldarg_1);
        _il.Emit(OpCodes.Call, s_interpreterFrames);
        _il.Emit(OpCodes.Call, s_arrayDataReference.MakeGenericMethod(typeof(InterpreterFrameRecord)));
        _il.Emit(OpCodes.Ldloc, _baseFrameIndex);
        _il.Emit(OpCodes.Call, s_unsafeAddInt.MakeGenericMethod(typeof(InterpreterFrameRecord)));
        _il.Emit(OpCodes.Stloc, _frame);
    }

    static readonly MethodInfo s_arrayDataReference = typeof(System.Runtime.InteropServices.MemoryMarshal).GetMethods()
        .First(m => m.Name == nameof(System.Runtime.InteropServices.MemoryMarshal.GetArrayDataReference) && m.IsGenericMethodDefinition);
    static readonly MethodInfo s_unsafeAddInt = typeof(Unsafe).GetMethods()
        .First(m => m.Name == nameof(Unsafe.Add) && m.IsGenericMethodDefinition && m.GetParameters().Length == 2 &&
                    m.GetParameters()[0].ParameterType.IsByRef && m.GetParameters()[1].ParameterType == typeof(int));

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
        if (_lazyFrame && (unit is null || !unit.IsInline))
        {
            // The parameters of a lazy frame are its activation's.
            LoadActivationSlotAddress(index);
            return;
        }
        if (_frameless && (unit is null || !unit.IsInline)) throw new FramelessUnsupportedException();
        if (unit is not null && _lazyUnits.Contains(unit))
        {
            LoadActivationSlotAddress(InlinedActivation(unit), unit.ParameterCount - 1, index);
            return;
        }
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
        if (_frameless && (unit is null || !unit.IsInline || _lazyUnits.Contains(unit))) throw new FramelessUnsupportedException();
        if (unit is null || !unit.IsInline) _il.Emit(OpCodes.Ldloc, _fp);
        else _il.Emit(OpCodes.Ldloc, unit.FpLocal!);
    }

    void LoadFrameRecord(MaglevCompilationUnit? unit)
    {
        if (_frameless && (unit is null || !unit.IsInline || _lazyUnits.Contains(unit))) throw new FramelessUnsupportedException();
        if (unit is null || !unit.IsInline) _il.Emit(OpCodes.Ldloc, _frame);
        else _il.Emit(OpCodes.Ldloc, unit.FrameRecordLocal!);
    }

    /// <summary>Records the node's bytecode offset in its frame's offset slot (for stack traces and messages).</summary>
    void StoreBytecodeOffset(NodeBase node)
    {
        if (node.BytecodeOffset < 0) return;
        if (_lazyFrame && (node.Unit is null || !node.Unit.IsInline || _lazyUnits.Contains(node.Unit)))
        {
            // A lazy frame's offset is its activation's.
            LocalBuilder activation = node.Unit is { IsInline: true } unit ? InlinedActivation(unit) : _activation!;
            _il.Emit(OpCodes.Ldloca, activation);
            _il.Emit(OpCodes.Ldc_I4, node.BytecodeOffset);
            _il.Emit(OpCodes.Stfld, AF(activation, "Pc"));
            return;
        }
        // InterpreterRuntime.SetFramePc as IL (a call is not inlined once
        // RyuJIT's inline budget of a big method is spent).
        LoadFrameSlotAddress(node.Unit, InterpreterRuntime.kBytecodeOffsetOffset);
        _il.Emit(OpCodes.Ldflda, s_bits);
        _il.Emit(OpCodes.Ldc_I4, node.BytecodeOffset);
        _il.Emit(OpCodes.Conv_I8);
        _il.Emit(OpCodes.Stind_I8);
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
        if (InRegionMode)
        {
            // The dispatcher returns Transfer.result.
            _il.Emit(OpCodes.Stloc, _tmpValue);
            _il.Emit(OpCodes.Ldarg_3);
            _il.Emit(OpCodes.Ldloc, _tmpValue);
            _il.Emit(OpCodes.Stfld, _plan!.ResultField);
            _il.Emit(OpCodes.Ldc_I4_M1);
            _il.Emit(OpCodes.Ret);
            return;
        }
        if (!_hasCatchBlocks && !_lazyFrame)
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
        int costBytes = _il.ILOffset, costInstructions = _il.Instructions, costBlocks = _il.BlockBoundaries, costRefs = _il.LocalReferences;
        EmitBlockBody(block);
        _blockCosts?.Add(block, (_il.ILOffset - costBytes, _il.Instructions - costInstructions, _il.BlockBoundaries - costBlocks,
            _il.LocalReferences - costRefs));
    }

    void EmitBlockBody(BasicBlock block)
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
            int ilBefore = _il.ILOffset, blocksBefore = _il.BlockBoundaries;
            _currentNode = node;
            EmitNode(node);
            if (s_ilHistogram is not null)
            {
                string key = node.Opcode == Opcode.CallBuiltin ? "CallBuiltin:" + ((CallBuiltinInfo)node.Obj0!).Method.Name : node.Opcode == Opcode.CheckMaps ? "CheckMaps:" + ((Map[])node.Obj0!).Length + (Array.TrueForAll((Map[])node.Obj0!, static m => m.IsStable) ? ":stable" : ":unstable") + (node.CheckType == CheckType.kOmitHeapObjectCheck || NodeTypes.Is(node.Inputs[0].Type, NodeType.kJSReceiver) ? ":recv" : ":in=" + node.Inputs[0].Opcode + (node.Inputs[0] is Phi ph ? (ph.IsLoopPhi ? "L" : "M") : "")) : node.Opcode == Opcode.LoadMap ? "LoadMap:in=" + node.Inputs[0].Opcode : node.Opcode.ToString();
                lock (s_ilHistogram)
                {
                    s_ilHistogram.TryGetValue(key, out (int, int, int) e);
                    s_ilHistogram[key] = (e.Item1 + 1, e.Item2 + _il.ILOffset - ilBefore, e.Item3 + _il.BlockBoundaries - blocksBefore);
                }
            }
        }
        EmitControl(block, block.Control!);
    }

    /// <summary>
    /// Whether a node of inlined code needs its interpreter frame: it calls
    /// out (the callee, a throw or a stack walk can observe the frame) or
    /// reads or writes the frame's slots.
    /// </summary>
    internal static bool NeedsFrame(Node node) =>
        node.Opcode is Opcode.LoadRegister or Opcode.StoreRegister or Opcode.SetCurrentContext or Opcode.HandleNoHeapWritesInterrupt ||
        node.Opcode == Opcode.CallBuiltin && node.Obj0 is not CallBuiltinInfo { NoFrame: true } && BuiltinNeedsFrame(node) ||
        node.Opcode != Opcode.EnterInlinedFrame &&
        (node.Properties & (OpProperties.kCall | OpProperties.kCanThrow | OpProperties.kLazyDeopt)) != 0;

    /// <summary>
    /// A builtin call that cannot call out, throw or deoptimize lazily, and
    /// reads nothing of the frame (a pure helper, V8's inline nodes), runs
    /// without the inlined frame: V8's inlined frames exist only in the deopt
    /// translation, and an eager deopt pushes the frames it needs.
    /// </summary>
    static bool BuiltinNeedsFrame(Node node)
    {
        if ((node.Properties & (OpProperties.kCall | OpProperties.kCanThrow | OpProperties.kLazyDeopt)) != 0) return true;
        var info = (CallBuiltinInfo)node.Obj0!;
        if (info.RegisterStores.Length != 0) return true;
        foreach (BuiltinArg arg in info.Args)
        {
            if (arg.Kind is BuiltinArgKind.State or BuiltinArgKind.RegisterIndex or BuiltinArgKind.RegisterRef or BuiltinArgKind.Closure)
            {
                return true;
            }
        }
        return false;
    }

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
        if (LeavesRegion(to)) return ExitLabel(from, to);
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

    /// <summary>The block emitted after the current one (a jump to it falls through), or null.</summary>
    BasicBlock? _nextBlock;

    static bool HasPhiMoves(BasicBlock to) => to.Phis.Exists(static p => p.Local is not null);

    void EmitControl(BasicBlock block, ControlNode c)
    {
        switch (c.Opcode)
        {
            case Opcode.Jump:
            case Opcode.JumpLoop:
                if (LeavesRegion(c.Target!))
                {
                    _il.Emit(OpCodes.Br, ExitLabel(block, c.Target!));
                    return;
                }
                EmitPhiMoves(block, c.Target!);
                if (!ReferenceEquals(c.Target, _nextBlock)) _il.Emit(OpCodes.Br, BlockLabel(c.Target!));
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
                if (c.Int1 == 1)
                {
                    // No fallthrough (the generator switch: every state is a case).
                    var cases = new Label[targets.Length];
                    for (int i = 0; i < cases.Length; i++) cases[i] = EdgeLabel(block, targets[i]!);
                    _il.Emit(OpCodes.Switch, cases);
                    _il.Emit(OpCodes.Call, B(nameof(MaglevBuiltins.Unreachable)));
                    LoadUndefined();
                    EmitReturn();
                    return;
                }
                var labels = new Label[targets.Length - 1];
                for (int i = 0; i < labels.Length; i++) labels[i] = EdgeLabel(block, targets[i]!);
                _il.Emit(OpCodes.Switch, labels);
                _il.Emit(OpCodes.Br, EdgeLabel(block, targets[^1]!));
                return;
            }
        }
        // Conditional branches (true -> Target). The target emitted next is
        // the fallthrough (when its edge has no phi moves).
        BasicBlock t = c.Target!, f = c.FalseTarget!;
        bool falseFallsThrough = ReferenceEquals(f, _nextBlock) && !HasPhiMoves(f);
        bool trueFallsThrough = !falseFallsThrough && ReferenceEquals(t, _nextBlock) && !HasPhiMoves(t);
        if (TryEmitCompareAndBranch(c, out OpCode branchIfTrue, out OpCode branchIfFalse))
        {
            // The compare and the branch in one instruction (blt, bge.un, ...).
            if (trueFallsThrough)
            {
                _il.Emit(branchIfFalse, EdgeLabel(block, f));
                return;
            }
            _il.Emit(branchIfTrue, EdgeLabel(block, t));
            if (!falseFallsThrough) _il.Emit(OpCodes.Br, EdgeLabel(block, f));
            return;
        }
        EmitBranchCondition(c);
        if (trueFallsThrough)
        {
            _il.Emit(OpCodes.Brfalse, EdgeLabel(block, f));
            return;
        }
        _il.Emit(OpCodes.Brtrue, EdgeLabel(block, t));
        if (!falseFallsThrough) _il.Emit(OpCodes.Br, EdgeLabel(block, f));
    }

    /// <summary>
    /// For an int32 or float64 compare branch: pushes the operands and
    /// returns the branch instructions taken when the comparison holds and
    /// when it does not (IEEE semantics: a comparison with NaN is false, so
    /// the negated float branches are the unordered forms).
    /// </summary>
    bool TryEmitCompareAndBranch(ControlNode c, out OpCode branchIfTrue, out OpCode branchIfFalse)
    {
        branchIfTrue = branchIfFalse = OpCodes.Nop;
        bool isFloat;
        if (c.Opcode == Opcode.BranchIfInt32Compare)
        {
            if (c.Unsigned && c.Operation != CompareOperation.kLessThan) return false;
            isFloat = false;
        }
        else if (c.Opcode == Opcode.BranchIfFloat64Compare)
        {
            isFloat = true;
        }
        else
        {
            return false;
        }
        ValueRepresentation repr = isFloat ? ValueRepresentation.kFloat64 : ValueRepresentation.kInt32;
        Load(c.Inputs[0], repr);
        Load(c.Inputs[1], repr);
        if (c.Unsigned)
        {
            (branchIfTrue, branchIfFalse) = (OpCodes.Blt_Un, OpCodes.Bge_Un);
            return true;
        }
        (branchIfTrue, branchIfFalse) = c.Operation switch
        {
            CompareOperation.kEqual or CompareOperation.kStrictEqual => (OpCodes.Beq, OpCodes.Bne_Un),
            CompareOperation.kLessThan => (OpCodes.Blt, isFloat ? OpCodes.Bge_Un : OpCodes.Bge),
            CompareOperation.kGreaterThan => (OpCodes.Bgt, isFloat ? OpCodes.Ble_Un : OpCodes.Ble),
            CompareOperation.kLessThanOrEqual => (OpCodes.Ble, isFloat ? OpCodes.Bgt_Un : OpCodes.Bgt),
            _ => (OpCodes.Bge, isFloat ? OpCodes.Blt_Un : OpCodes.Blt),
        };
        return true;
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
        _deoptIndex ??= _il.DeclareLocal(typeof(int));
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
            var spill = new List<ValueNode>();
            var point = new DeoptPoint { Kind = kind, Reason = reason };
            List<(InlinedAllocation, CapturedObjectData)>? capturedObjects = null;
            // Frames outermost first.
            var frames = new List<InterpretedDeoptFrame>();
            for (DeoptFrame? f = info.TopFrame; f is not null; f = f.Parent) frames.Add((InterpretedDeoptFrame)f);
            frames.Reverse();
            var data = new DeoptFrameData[frames.Count];
            for (int i = 0; i < frames.Count; i++)
            {
                InterpretedDeoptFrame f = frames[i];
                MaglevCompilationUnit unit = f.Unit;
                var registers = new Register[f.Values.Length];
                var slots = new int[f.Values.Length];
                ArgumentsObjectKind[]? materialize = null;
                int[]? captured = null;
                JSValue[]? constants = null;
                bool[]? isConstant = null;
                for (int k = 0; k < f.Values.Length; k++)
                {
                    registers[k] = f.Values[k].Register;
                    slots[k] = -1;
                    ValueNode value = f.Values[k].Value;
                    if (value.IsConstant)
                    {
                        // A literal of the translation (V8's StoreLiteral): the
                        // Deoptimizer writes it, the exit spills nothing.
                        (constants ??= new JSValue[f.Values.Length])[k] = value.ConstantValue();
                        (isConstant ??= new bool[f.Values.Length])[k] = true;
                        continue;
                    }
                    if (value is InlinedAllocation { IsElided: true } allocation)
                    {
                        // A captured object: the Deoptimizer materializes it from its fields.
                        (captured ??= NewCapturedRefs(f.Values.Length))[k] = CapturedObjectIndex(allocation, info, ref capturedObjects, spill);
                        continue;
                    }
                    if (IsElidedArguments(value))
                    {
                        // The Deoptimizer creates the elided arguments object.
                        (materialize ??= new ArgumentsObjectKind[f.Values.Length])[k] = ((CallBuiltinInfo)value.Obj0!).ArgumentsKind;
                        continue;
                    }
                    slots[k] = SpillSlot(value);
                    spill.Add(value);
                }
                data[i] = new DeoptFrameData
                {
                    InliningDepth = unit.InliningDepth,
                    Argc = unit.Argc,
                    IsConstruct = unit.IsConstruct,
                    Function = unit.IsInline ? unit.Function : unit.Function ?? _info.Function,
                    ClosureScratchSlot = unit.IsInline && unit.Function is null ? SpillSlot(f.Closure) : -1,
                    Shared = unit.SharedFunctionInfo,
                    Bytecode = unit.Bytecode,
                    FeedbackVector = unit.Feedback,
                    BytecodeOffset = f.BytecodeOffset,
                    NextOffset = f.NextOffset,
                    Registers = registers,
                    Materialize = materialize,
                    Captured = captured,
                    Constants = constants,
                    IsConstant = isConstant,
                    ScratchSlots = slots,
                };
                if (data[i].ClosureScratchSlot >= 0) spill.Add(f.Closure);
            }
            if (kind == DeoptimizeKind.kLazy && info is LazyDeoptInfo lazy)
            {
                point.ResultLocation = lazy.ResultLocation;
                if (result is not null && lazy.ResultSize == 1)
                {
                    point.ResultScratchIndex = SpillSlot(result);
                    spill.Add(result);
                }
            }
            point.Frames = data;
            if (capturedObjects is not null) point.CapturedObjects = capturedObjects.ConvertAll(static c => c.Item2).ToArray();
            int index = _deoptPoints.Count;
            _deoptPoints.Add(point);
            info.DeoptIndex = index;
            // The deopt point (and a lazy exit's reason) go to locals, and the
            // exit spills its values and ends in the Deoptimize call.
            if (kind == DeoptimizeKind.kLazy)
            {
                _il.Emit(OpCodes.Ldc_I4, (int)reason);
                _il.Emit(OpCodes.Stloc, _deoptReason);
            }
            _il.Emit(OpCodes.Ldc_I4, index);
            _il.Emit(OpCodes.Stloc, _deoptIndex);
            EmitSpillChain(spill);
        }
        _maxScratch = SpillSlotBase + _spillSlots.Count;
    }

    // ---- Spill chains ------------------------------------------------------------------------------------------

    // Every value a deopt exit spills has its own slot of the scratch buffer
    // (SpillSlot), so the code storing a value is the same for every exit.
    // An exit stores what a recent exit's spill block does not and jumps to
    // that block, which stores the rest and ends in the Deoptimize call (or
    // jumps on to its own predecessor): consecutive exits share most of
    // their live values, so the exits of a function with a big frame cost a
    // few stores each instead of a copy of the frame (V8's deopt exits are a
    // call each; the deoptimizer reads the values from the optimized frame).
    // A block may also store values the jumping exit does not need, from
    // locals that hold something else there by then: harmless, its
    // translation does not read those slots.

    readonly Dictionary<ValueNode, int> _spillSlots = new(ReferenceEqualityComparer.Instance);

    sealed class SpillBlock(Label label, System.Collections.BitArray stored)
    {
        public readonly Label Label = label;
        /// <summary>The slots stored from this block to the Deoptimize call.</summary>
        public readonly System.Collections.BitArray Stored = stored;
    }

    readonly List<SpillBlock> _spillChains = [];

    /// <summary>How many recent spill blocks an exit considers continuing in.</summary>
    const int kSpillChainCandidates = 16;

    /// <summary>
    /// The frameless entry keeps its receiver and arguments in the first
    /// scratch slots (EmitFramelessDeopt), the spilled values after them.
    /// </summary>
    int SpillSlotBase => _frameless && !_lazyFrame ? _fastCallArity + 1 : 0;

    int SpillSlot(ValueNode value)
    {
        if (!_spillSlots.TryGetValue(value, out int slot)) _spillSlots[value] = slot = SpillSlotBase + _spillSlots.Count;
        return slot;
    }

    void EmitSpillChain(List<ValueNode> spill)
    {
        // The recent block that already stores most of these values.
        SpillBlock? parent = null;
        int best = spill.Count;
        for (int c = _spillChains.Count - 1, n = 0; c >= 0 && n < kSpillChainCandidates; c--, n++)
        {
            SpillBlock candidate = _spillChains[c];
            int missing = 0;
            foreach (ValueNode v in spill)
            {
                int slot = _spillSlots[v];
                if (slot >= candidate.Stored.Length || !candidate.Stored[slot]) missing++;
            }
            if (missing < best || parent is null && missing <= best)
            {
                best = missing;
                parent = candidate;
                if (missing == 0) break;
            }
        }
        var own = new List<ValueNode>();
        foreach (ValueNode v in spill)
        {
            int slot = _spillSlots[v];
            if (parent is null || slot >= parent.Stored.Length || !parent.Stored[slot]) own.Add(v);
        }
        if (parent is not null && own.Count == 0)
        {
            _il.Emit(OpCodes.Br, parent.Label);
            return;
        }
        if (parent is not null && own.Count == spill.Count) parent = null;
        // Distinct values (a value can stand for several registers).
        own.Sort((a, b) => _spillSlots[a].CompareTo(_spillSlots[b]));
        for (int i = own.Count - 1; i > 0; i--)
        {
            if (ReferenceEquals(own[i], own[i - 1])) own.RemoveAt(i);
        }
        Label block = _il.DefineLabel();
        _il.MarkLabel(block);
        var stored = new System.Collections.BitArray(Math.Max(SpillSlotBase + _spillSlots.Count, parent?.Stored.Length ?? 0));
        if (parent is not null)
        {
            for (int i = 0; i < parent.Stored.Length; i++) stored[i] = parent.Stored[i];
        }
        foreach (ValueNode v in own) stored[_spillSlots[v]] = true;
        _spillChains.Add(new SpillBlock(block, stored));
        EmitSpill(own);
        _spilledValues += own.Count;
        if (parent is not null)
        {
            _il.Emit(OpCodes.Br, parent.Label);
            return;
        }
        if (_lazyFrame)
        {
            // MaglevCalls.DeoptimizeLazyFrame(isolate, code, index, reason, depth): the call's result.
            _il.Emit(OpCodes.Ldarg_1);
            _il.Emit(OpCodes.Ldarg_0);
            _il.Emit(OpCodes.Ldloc, _deoptIndex!);
            _il.Emit(OpCodes.Ldloc, _deoptReason!);
            _il.Emit(OpCodes.Ldloc, _baseFrameIndex);
            _il.Emit(OpCodes.Call, s_deoptimizeLazyFrame);
            EmitReturn();
            return;
        }
        if (_frameless)
        {
            EmitFramelessDeopt();
            return;
        }
        // Deoptimizer::Deoptimize(isolate, ref state, code, index, reason); return to MaglevExecution.Run.
        _il.Emit(OpCodes.Ldarg_1);
        _il.Emit(OpCodes.Ldarg_2);
        _il.Emit(OpCodes.Ldarg_0);
        _il.Emit(OpCodes.Ldloc, _deoptIndex!);
        _il.Emit(OpCodes.Ldloc, _deoptReason!);
        Call("Deopt0");
        EmitReturn();
    }

    /// <summary>
    /// Stores <paramref name="values"/> (sorted by slot) to their scratch
    /// slots, consecutive slots in chunks through MaglevBuiltins.Spill*: a few
    /// bytes of IL per value.
    /// </summary>
    void EmitSpill(List<ValueNode> values)
    {
        int i = 0;
        while (i < values.Count)
        {
            int first = _spillSlots[values[i]];
            int run = 1;
            while (i + run < values.Count && _spillSlots[values[i + run]] == first + run) run++;
            int chunk = run >= 8 ? 8 : run >= 4 ? 4 : run >= 2 ? 2 : 1;
            _il.Emit(OpCodes.Ldarg_1);
            _il.Emit(OpCodes.Ldfld, s_deoptScratch);
            _il.Emit(OpCodes.Ldc_I4, first);
            for (int k = 0; k < chunk; k++) Load(values[i + k], ValueRepresentation.kTagged);
            Call("Spill" + chunk);
            i += chunk;
        }
    }


    LocalBuilder? _storeAddress;

    /// <summary>
    /// Stores <paramref name="value"/> as a JSValue at the address on the IL
    /// stack (a field, a frame slot): the reference half only when it changes
    /// (JSValue.StoreSlot), so a number over a number, or the same object
    /// again, needs no GC write barrier; an untagged number stores its bits
    /// without being tagged first.
    /// </summary>
    /// <remarks>
    /// V8 elides the write barrier of a Smi store; V8Sharp's numbers are a
    /// shared tag object and the double's bits, and storing the tag object
    /// still runs the CLR's barrier unless the store is skipped.
    /// </remarks>
    void EmitStoreTagged(ValueNode value)
    {
        if (_hasCatchBlocks)
        {
            // Deviation (RyuJIT, not V8): in a method with exception handlers
            // the shared byref and value locals below make RyuJIT take seconds
            // (mjsunit compiler/constructor-inlining: 15 s per compile of a
            // 20 KB method); the inlined helper keeps them JIT temporaries.
            Load(value, ValueRepresentation.kTagged);
            _il.Emit(OpCodes.Call, s_storeSlot);
            return;
        }
        _storeAddress ??= _il.DeclareLocal(typeof(JSValue).MakeByRefType());
        _il.Emit(OpCodes.Stloc, _storeAddress);
        bool untaggedNumber = !value.IsConstant &&
            value.Representation is ValueRepresentation.kInt32 or ValueRepresentation.kUint32 or ValueRepresentation.kFloat64;
        bool constantNumber = value.IsConstant && value.TryGetFloat64Constant(out _);
        Label skip = _il.DefineLabel();
        if (untaggedNumber || constantNumber)
        {
            _il.Emit(OpCodes.Ldloc, _storeAddress);
            _il.Emit(OpCodes.Ldfld, s_obj);
            _il.Emit(OpCodes.Ldsfld, s_numberTag);
            _il.Emit(OpCodes.Beq, skip);
            _il.Emit(OpCodes.Ldloc, _storeAddress);
            _il.Emit(OpCodes.Ldsfld, s_numberTag);
            _il.Emit(OpCodes.Stfld, s_obj);
            _il.MarkLabel(skip);
            _il.Emit(OpCodes.Ldloc, _storeAddress);
            if (constantNumber)
            {
                value.TryGetFloat64Constant(out double d);
                _il.Emit(OpCodes.Ldc_I8, BitConverter.DoubleToInt64Bits(d));
            }
            else
            {
                Load(value, ValueRepresentation.kFloat64);
                _il.Emit(OpCodes.Call, s_doubleToInt64Bits);
            }
            _il.Emit(OpCodes.Stfld, s_bits);
            return;
        }
        Load(value, ValueRepresentation.kTagged);
        _il.Emit(OpCodes.Stloc, _tmpValue);
        _il.Emit(OpCodes.Ldloc, _storeAddress);
        _il.Emit(OpCodes.Ldfld, s_obj);
        _il.Emit(OpCodes.Ldloc, _tmpValue);
        _il.Emit(OpCodes.Ldfld, s_obj);
        _il.Emit(OpCodes.Beq, skip);
        _il.Emit(OpCodes.Ldloc, _storeAddress);
        _il.Emit(OpCodes.Ldloc, _tmpValue);
        _il.Emit(OpCodes.Ldfld, s_obj);
        _il.Emit(OpCodes.Stfld, s_obj);
        _il.MarkLabel(skip);
        _il.Emit(OpCodes.Ldloc, _storeAddress);
        _il.Emit(OpCodes.Ldloc, _tmpValue);
        _il.Emit(OpCodes.Ldfld, s_bits);
        _il.Emit(OpCodes.Stfld, s_bits);
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
            case Opcode.InitialValue when _frameless:
                EmitFramelessInitialValue(node.Int0);
                Store(v!);
                return;
            case Opcode.InitialValue:
                // (The closure and context included: every entry writes their slots.)
                LoadFrameSlotAddress(null, node.Int0);
                _il.Emit(OpCodes.Ldobj, typeof(JSValue));
                Store(v!);
                return;
            case Opcode.StoreRegister:
                LoadFrameSlotAddress(node.Unit, node.Int0);
                EmitStoreTagged(node.Inputs[0]);
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
            case Opcode.Int32Add:
            case Opcode.Int32Subtract:
            case Opcode.Int32Multiply:
                Load(node.Inputs[0], ValueRepresentation.kInt32);
                Load(node.Inputs[1], ValueRepresentation.kInt32);
                _il.Emit(node.Opcode == Opcode.Int32Add ? OpCodes.Add : node.Opcode == Opcode.Int32Subtract ? OpCodes.Sub : OpCodes.Mul);
                Store(v!);
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
                EmitLoadNumber();
                EmitTruncateToInt32();
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
                    EmitLoadNumber();
                }
                Store(v!);
                return;
            }
            case Opcode.UnsafeNumberToFloat64:
                Load(node.Inputs[0], ValueRepresentation.kTagged);
                EmitLoadNumber();
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
            {
                // DoubleToInt32: cvttsd2si, and the modular conversion only when
                // it reports NaN or out of range (int.MinValue).
                Label done = _il.DefineLabel();
                Load(node.Inputs[0], ValueRepresentation.kFloat64);
                _il.Emit(OpCodes.Stloc, _tmpDouble);
                _il.Emit(OpCodes.Ldloc, _tmpDouble);
                EmitTruncateToInt32();
                _il.Emit(OpCodes.Stloc, _tmpInt);
                _il.Emit(OpCodes.Ldloc, _tmpInt);
                _il.Emit(OpCodes.Ldc_I4, int.MinValue);
                _il.Emit(OpCodes.Bne_Un, done);
                _il.Emit(OpCodes.Ldloc, _tmpDouble);
                Call(nameof(MaglevBuiltins.TruncateFloat64ToInt32));
                _il.Emit(OpCodes.Stloc, _tmpInt);
                _il.MarkLabel(done);
                _il.Emit(OpCodes.Ldloc, _tmpInt);
                Store(v!);
                return;
            }
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
                // Not a number (undefined included): the values a field of
                // HeapObject representation holds (Object::FitsRepresentation's
                // V8Sharp deviation: numbers are unboxed, undefined is an oddball).
                Load(node.Inputs[0], ValueRepresentation.kTagged);
                _il.Emit(OpCodes.Ldfld, s_obj);
                _il.Emit(OpCodes.Ldsfld, s_numberTag);
                _il.Emit(OpCodes.Beq, EagerExit(node.EagerDeoptInfo!));
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
                    4 => nameof(MaglevBuiltins.IsJSReceiverOrNullOrUndefined),
                    5 => nameof(MaglevBuiltins.IsStringOrStringWrapper),
                    _ => nameof(MaglevBuiltins.IsWritableElements),
                });
                DeoptIfFalse(node);
                return;
            case Opcode.LoadTypedArrayLength:
                Load(node.Inputs[0], ValueRepresentation.kTagged);
                Call(node.Int0 == 1 ? nameof(MaglevBuiltins.TypedArrayLengthAsFloat64) : nameof(MaglevBuiltins.TypedArrayLength));
                Store(v!);
                return;
            case Opcode.CheckTypedArrayValid:
                Load(node.Inputs[0], ValueRepresentation.kTagged);
                _il.Emit(node.Int0 == 1 ? OpCodes.Ldc_I4_1 : OpCodes.Ldc_I4_0);
                Call(nameof(MaglevBuiltins.IsTypedArrayValid));
                DeoptIfFalse(node);
                return;
            case Opcode.LoadTypedArrayElement:
                Load(node.Inputs[0], ValueRepresentation.kTagged);
                Load(node.Inputs[1], ValueRepresentation.kInt32);
                Call(TypedLoadHelper((ElementsKind)node.Int0));
                Store(v!);
                return;
            case Opcode.StoreTypedArrayElement:
            {
                var kind = (ElementsKind)node.Int0;
                Label skip = _il.DefineLabel();
                if (node.Int1 != 0)
                {
                    Load(node.Inputs[0], ValueRepresentation.kTagged);
                    Load(node.Inputs[1], ValueRepresentation.kInt32);
                    Call(nameof(MaglevBuiltins.TypedArrayIndexInBounds));
                    _il.Emit(OpCodes.Brfalse, skip);
                }
                Load(node.Inputs[0], ValueRepresentation.kTagged);
                Load(node.Inputs[1], ValueRepresentation.kInt32);
                ValueNode stored = node.Inputs[2];
                bool isFloat = stored.Representation is ValueRepresentation.kFloat64 or ValueRepresentation.kHoleyFloat64 ||
                               stored.IsConstant && !stored.TryGetInt32Constant(out _);
                if (kind is ElementsKind.FLOAT32_ELEMENTS or ElementsKind.FLOAT64_ELEMENTS ||
                    kind == ElementsKind.UINT8_CLAMPED_ELEMENTS && isFloat)
                {
                    Load(stored, ValueRepresentation.kFloat64);
                }
                else
                {
                    Load(stored, ValueRepresentation.kInt32);
                }
                Call(TypedStoreHelper(kind, isFloat));
                _il.MarkLabel(skip);
                return;
            }
            case Opcode.TransitionElementsKind when node.Obj1 is Map[] sources:
            {
                // V8's TransitionElementsKind node: an object with one of the
                // source maps transitions to the target; others are unchanged.
                Label done = _il.DefineLabel(), transition = _il.DefineLabel();
                EmitLoadMapOrBranch(node, done);
                _il.Emit(OpCodes.Stloc, _tmpMap);
                foreach (Map source in sources)
                {
                    _il.Emit(OpCodes.Ldloc, _tmpMap);
                    LoadConstantObject(source, typeof(Map));
                    _il.Emit(OpCodes.Beq, transition);
                }
                _il.Emit(OpCodes.Br, done);
                _il.MarkLabel(transition);
                _il.Emit(OpCodes.Ldarg_1);
                Load(node.Inputs[0], ValueRepresentation.kTagged);
                LoadConstantObject(node.Obj0, typeof(Map));
                Call(nameof(MaglevBuiltins.TransitionElementsKind));
                _il.Emit(OpCodes.Pop);
                _il.MarkLabel(done);
                return;
            }
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
            case Opcode.CheckJSFunctionFeedbackCell:
            {
                Label exit = EagerExit(node.EagerDeoptInfo!);
                Load(node.Inputs[0], ValueRepresentation.kTagged);
                _il.Emit(OpCodes.Ldfld, s_obj);
                _il.Emit(OpCodes.Isinst, typeof(JSFunction));
                _il.Emit(OpCodes.Stloc, _tmpObject);
                _il.Emit(OpCodes.Ldloc, _tmpObject);
                _il.Emit(OpCodes.Brfalse, exit);
                _il.Emit(OpCodes.Ldloc, _tmpObject);
                _il.Emit(OpCodes.Ldfld, s_rawFeedbackCell);
                LoadConstantObject(node.Obj0, typeof(FeedbackCell));
                _il.Emit(OpCodes.Bne_Un, exit);
                return;
            }
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
                EmitLoadMapOrBranch(node, notReceiver);
                _il.Emit(OpCodes.Br, done);
                _il.MarkLabel(notReceiver);
                _il.Emit(OpCodes.Ldnull);
                _il.MarkLabel(done);
                _il.Emit(OpCodes.Call, s_jsValueFromObject);
                Store(v!);
                return;
            }
            case Opcode.UnwrapStringWrapper:
                Load(node.Inputs[0], ValueRepresentation.kTagged);
                Call(nameof(MaglevBuiltins.UnwrapStringWrapper));
                Store(v!);
                return;
            case Opcode.MigrateMapIfNeeded:
                _il.Emit(OpCodes.Ldarg_1);
                Load(node.Inputs[0], ValueRepresentation.kTagged);
                Load(node.Inputs[1], ValueRepresentation.kTagged);
                Call(nameof(MaglevBuiltins.MigrateMapIfNeeded));
                Store(v!);
                return;
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
                    EmitStoreTagged(node.Inputs[1]);
                    return;
                }
                Load(node.Inputs[0], ValueRepresentation.kTagged);
                _il.Emit(OpCodes.Ldc_I4, node.Int0);
                Load(node.Inputs[1], ValueRepresentation.kTagged);
                Call(node.Int1 != 0 ? nameof(MaglevBuiltins.StoreDoubleField) : nameof(MaglevBuiltins.StoreField));
                return;
            case Opcode.LoadDoubleField:
                // The field holds a number (a Double field's value or the hole NaN).
                if (TryLoadFieldAddress(node.Inputs[0], node.Int0))
                {
                    _il.Emit(OpCodes.Ldfld, s_bits);
                    _il.Emit(OpCodes.Call, s_int64BitsToDouble);
                }
                else
                {
                    Load(node.Inputs[0], ValueRepresentation.kTagged);
                    _il.Emit(OpCodes.Ldc_I4, node.Int0);
                    Call(nameof(MaglevBuiltins.LoadField));
                    EmitLoadNumber();
                }
                Store(v!);
                return;
            case Opcode.StoreDoubleField:
                if (TryLoadFieldAddress(node.Inputs[0], node.Int0))
                {
                    // The slot already holds a number: only its payload is
                    // written (no write barrier), as V8 writes the value of
                    // the field's HeapNumber.
                    _storeAddress ??= _il.DeclareLocal(typeof(JSValue).MakeByRefType());
                    _il.Emit(OpCodes.Stloc, _storeAddress);
                    Label tagged = _il.DefineLabel();
                    _il.Emit(OpCodes.Ldloc, _storeAddress);
                    _il.Emit(OpCodes.Ldfld, s_obj);
                    _il.Emit(OpCodes.Ldsfld, s_numberTag);
                    _il.Emit(OpCodes.Beq, tagged);
                    _il.Emit(OpCodes.Ldloc, _storeAddress);
                    _il.Emit(OpCodes.Ldsfld, s_numberTag);
                    _il.Emit(OpCodes.Stfld, s_obj);
                    _il.MarkLabel(tagged);
                    _il.Emit(OpCodes.Ldloc, _storeAddress);
                    Load(node.Inputs[1], ValueRepresentation.kFloat64);
                    Call(nameof(MaglevBuiltins.DoubleFieldBits));
                    _il.Emit(OpCodes.Stfld, s_bits);
                    return;
                }
                Load(node.Inputs[0], ValueRepresentation.kTagged);
                _il.Emit(OpCodes.Ldc_I4, node.Int0);
                Load(node.Inputs[1], ValueRepresentation.kFloat64);
                Call(nameof(MaglevBuiltins.StoreDoubleFieldFloat64));
                return;
            case Opcode.StoreMapTransition when node.Int0 < JSObject.kPropertyArrayStorageBase && InObjectLayout.IsContiguous && !s_noInlineTransitions && !_hasCatchBlocks:
            {
                // An in-object field (a constructor's this.x = ...): the value
                // into the slot the map already has, then the map (StoreMap +
                // StoreTaggedField, as StoreIC's TryStoreTransition).
                TryLoadFieldAddress(node.Inputs[0], node.Int0);
                if (node.Int1 != 0)
                {
                    _storeAddress ??= _il.DeclareLocal(typeof(JSValue).MakeByRefType());
                    _il.Emit(OpCodes.Stloc, _storeAddress);
                    _il.Emit(OpCodes.Ldloc, _storeAddress);
                    _il.Emit(OpCodes.Ldsfld, s_numberTag);
                    _il.Emit(OpCodes.Stfld, s_obj);
                    _il.Emit(OpCodes.Ldloc, _storeAddress);
                    ValueNode number = UntaggedNumberSource(node.Inputs[1]);
                    if (number.Representation == ValueRepresentation.kTagged)
                    {
                        // A checked number (the field's representation check).
                        Load(number, ValueRepresentation.kTagged);
                        EmitLoadNumber();
                    }
                    else
                    {
                        Load(number, ValueRepresentation.kFloat64);
                    }
                    Call(nameof(MaglevBuiltins.DoubleFieldBits));
                    _il.Emit(OpCodes.Stfld, s_bits);
                }
                else
                {
                    EmitStoreTagged(UntaggedNumberSource(node.Inputs[1]));
                }
                // (The next transition of the object writes the map: MarkOverwrittenMapStores.)
                if (node.Int2 != 0) return;
                Load(node.Inputs[0], ValueRepresentation.kTagged);
                _il.Emit(OpCodes.Ldfld, s_obj);
                LoadConstantObject(node.Obj0, typeof(Map));
                _il.Emit(OpCodes.Stfld, s_receiverMap);
                return;
            }
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
                // The element's address, then the store: a number (an untagged
                // value tagged for the store) writes its payload, and a
                // reference equal to the slot's leaves it (no write barrier),
                // as V8's StoreFixedArrayElementNoWriteBarrier for Smis.
                Load(node.Inputs[0], ValueRepresentation.kTagged);
                _il.Emit(OpCodes.Ldfld, s_obj);
                _il.Emit(OpCodes.Ldfld, s_fixedArrayData);
                Load(node.Inputs[1], ValueRepresentation.kInt32);
                _il.Emit(OpCodes.Ldelema, typeof(JSValue));
                EmitStoreTagged(UntaggedNumberSource(node.Inputs[2]));
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
                EmitContextSlotAddress(node.Inputs[0], node.Int1, node.Int0);
                _il.Emit(OpCodes.Ldobj, typeof(JSValue));
                Store(v!);
                return;
            case Opcode.StoreContextSlot:
                EmitContextSlotAddress(node.Inputs[0], node.Int1, node.Int0);
                EmitStoreTagged(UntaggedNumberSource(node.Inputs[1]));
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
            {
                // A number inline (an integral double, -0 as 0), strings
                // through the helper (CheckedObjectToIndex's deferred code).
                Label slow = _il.DefineLabel(), done = _il.DefineLabel();
                Load(node.Inputs[0], ValueRepresentation.kTagged);
                _il.Emit(OpCodes.Ldfld, s_obj);
                _il.Emit(OpCodes.Ldsfld, s_numberTag);
                _il.Emit(OpCodes.Bne_Un, slow);
                Load(node.Inputs[0], ValueRepresentation.kTagged);
                EmitLoadNumber();
                _il.Emit(OpCodes.Stloc, _tmpDouble);
                _il.Emit(OpCodes.Ldloc, _tmpDouble);
                EmitTruncateToInt32();
                _il.Emit(OpCodes.Stloc, _tmpInt);
                _il.Emit(OpCodes.Ldloc, _tmpInt);
                _il.Emit(OpCodes.Conv_R8);
                _il.Emit(OpCodes.Ldloc, _tmpDouble);
                _il.Emit(OpCodes.Bne_Un, EagerExit(node.EagerDeoptInfo!));
                _il.Emit(OpCodes.Br, done);
                _il.MarkLabel(slow);
                Load(node.Inputs[0], ValueRepresentation.kTagged);
                _il.Emit(OpCodes.Ldloca, _tmpInt);
                Call(nameof(MaglevBuiltins.TryObjectToIndex));
                DeoptIfFalse(node);
                _il.MarkLabel(done);
                _il.Emit(OpCodes.Ldloc, _tmpInt);
                Store(v!);
                return;
            }
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
                if (node.Int2 > 0)
                {
                    // Array.prototype.push: append Int2 elements at the old length.
                    _il.Emit(OpCodes.Ldc_I4, node.Int2);
                    Call(nameof(MaglevBuiltins.MaybeGrowFastElementsForPush));
                }
                else
                {
                    _il.Emit(OpCodes.Ldc_I4, node.Int0);
                    _il.Emit(OpCodes.Ldc_I4, node.Int1);
                    Call(nameof(MaglevBuiltins.MaybeGrowFastElements));
                }
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
                Call(node.Int0 != 0 ? nameof(MaglevBuiltins.TestUndefinedOrNull) : nameof(MaglevBuiltins.TestUndetectable));
                Store(v!);
                return;
            case Opcode.TestTypeOf:
                Load(node.Inputs[0], ValueRepresentation.kTagged);
                _il.Emit(OpCodes.Ldc_I4, node.Int0);
                Call(nameof(MaglevBuiltins.TestTypeOf));
                Store(v!);
                return;
            case Opcode.InlinedAllocation:
                EmitInlinedAllocation((InlinedAllocation)node);
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
            case Opcode.CallKnownJSFunction:
                EmitCallKnownJSFunction(node);
                return;

            // ---- Generators -------------------------------------------------------------------------
            case Opcode.LoadGeneratorField:
                Load(node.Inputs[0], ValueRepresentation.kTagged);
                Call((GeneratorField)node.Int0 switch
                {
                    GeneratorField.kContext => nameof(MaglevBuiltins.LoadGeneratorContext),
                    GeneratorField.kInputOrDebugPos => nameof(MaglevBuiltins.LoadGeneratorInputOrDebugPos),
                    _ => nameof(MaglevBuiltins.LoadGeneratorContinuation),
                });
                Store(v!);
                return;
            case Opcode.StoreGeneratorContinuation:
                Load(node.Inputs[0], ValueRepresentation.kTagged);
                Load(node.Inputs[1], ValueRepresentation.kInt32);
                Call(nameof(MaglevBuiltins.StoreGeneratorContinuation));
                return;
            case Opcode.GeneratorStore:
            {
                // The parameters and registers into the register file, then the fixed fields.
                LocalBuilder file = _il.DeclareLocal(typeof(JSValue[]));
                Load(node.Inputs[0], ValueRepresentation.kTagged);
                Call(nameof(MaglevBuiltins.GeneratorRegisterFile));
                _il.Emit(OpCodes.Stloc, file);
                for (int i = 2; i < node.Inputs.Length; i++)
                {
                    _il.Emit(OpCodes.Ldloc, file);
                    _il.Emit(OpCodes.Ldc_I4, i - 2);
                    Load(node.Inputs[i], ValueRepresentation.kTagged);
                    _il.Emit(OpCodes.Stelem, typeof(JSValue));
                }
                Load(node.Inputs[0], ValueRepresentation.kTagged);
                Load(node.Inputs[1], ValueRepresentation.kTagged);
                _il.Emit(OpCodes.Ldc_I4, node.Int0);
                _il.Emit(OpCodes.Ldc_I4, node.Int1);
                Call(nameof(MaglevBuiltins.GeneratorSuspend));
                return;
            }
            case Opcode.GeneratorRestoreRegister:
                Load(node.Inputs[0], ValueRepresentation.kTagged);
                _il.Emit(OpCodes.Ldc_I4, node.Int0);
                Call(nameof(MaglevBuiltins.GeneratorRestoreRegister));
                Store(v!);
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
        // The 64-bit result stays on the IL stack (no scratch local: a local
        // shared by every overflow check of a method is one long-lived
        // variable to RyuJIT's register allocator); the overflow branch goes
        // through a stub that pops it.
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
        Label exit = EagerExit(node.EagerDeoptInfo!);
        Label popAndExit = _il.DefineLabel(), done = _il.DefineLabel();
        _il.Emit(OpCodes.Dup);
        _il.Emit(OpCodes.Dup);
        _il.Emit(OpCodes.Conv_I4);
        _il.Emit(OpCodes.Conv_I8);
        _il.Emit(OpCodes.Bne_Un, popAndExit);
        if (node.Opcode == Opcode.Int32MultiplyWithOverflow)
        {
            // A zero product with a negative operand is -0.
            Label ok = _il.DefineLabel();
            _il.Emit(OpCodes.Dup);
            _il.Emit(OpCodes.Brtrue, ok);
            Load(node.Inputs[0], ValueRepresentation.kInt32);
            Load(node.Inputs[1], ValueRepresentation.kInt32);
            _il.Emit(OpCodes.Or);
            _il.Emit(OpCodes.Ldc_I4_0);
            _il.Emit(OpCodes.Blt, popAndExit);
            _il.MarkLabel(ok);
        }
        _il.Emit(OpCodes.Conv_I4);
        Store((ValueNode)node);
        _il.Emit(OpCodes.Br, done);
        _il.MarkLabel(popAndExit);
        _il.Emit(OpCodes.Pop);
        _il.Emit(OpCodes.Br, exit);
        _il.MarkLabel(done);
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
        _il.Emit(OpCodes.Ldfld, s_bits);
        _il.Emit(OpCodes.Stloc, _tmpLong);
        EmitCheckedBitsToInt32(exit);
        Store((ValueNode)node);
    }

    /// <summary>The int32 of _tmpDouble, deoptimizing for a fraction, NaN, out of range or -0. Leaves the int on the stack.</summary>
    void EmitCheckedFloat64ToInt32(Node node)
    {
        Label exit = EagerExit(node.EagerDeoptInfo!);
        _il.Emit(OpCodes.Ldloc, _tmpDouble);
        _il.Emit(OpCodes.Call, s_doubleToInt64Bits);
        _il.Emit(OpCodes.Stloc, _tmpLong);
        EmitCheckedBitsToInt32(exit);
    }

    /// <summary>
    /// The int32 of the double whose bits are in _tmpLong, branching to
    /// <paramref name="exit"/> unless the double is that int32 exactly: the
    /// truncation's round trip has the same bits only for an integral double
    /// in range that is not -0 (NaN and out of range truncate to int.MinValue,
    /// whose double differs), one integer compare (JSValue.IsSmiDouble).
    /// Leaves the int on the stack.
    /// </summary>
    void EmitCheckedBitsToInt32(Label exit)
    {
        _il.Emit(OpCodes.Ldloc, _tmpLong);
        _il.Emit(OpCodes.Call, s_int64BitsToDouble);
        EmitTruncateToInt32();
        _il.Emit(OpCodes.Stloc, _tmpInt);
        _il.Emit(OpCodes.Ldloc, _tmpInt);
        _il.Emit(OpCodes.Conv_R8);
        _il.Emit(OpCodes.Call, s_doubleToInt64Bits);
        _il.Emit(OpCodes.Ldloc, _tmpLong);
        _il.Emit(OpCodes.Bne_Un, exit);
        _il.Emit(OpCodes.Ldloc, _tmpInt);
    }

    static readonly MethodInfo? s_createScalarUnsafe = System.Runtime.Intrinsics.X86.Sse2.IsSupported
        ? typeof(System.Runtime.Intrinsics.Vector128).GetMethod(nameof(System.Runtime.Intrinsics.Vector128.CreateScalarUnsafe), [typeof(double)])
        : null;
    static readonly MethodInfo? s_cvttsd2si = System.Runtime.Intrinsics.X86.Sse2.IsSupported
        ? typeof(System.Runtime.Intrinsics.X86.Sse2).GetMethod(nameof(System.Runtime.Intrinsics.X86.Sse2.ConvertToInt32WithTruncation),
            [typeof(System.Runtime.Intrinsics.Vector128<double>)])
        : null;

    /// <summary>
    /// The double on the stack truncated to an int32, int.MinValue for NaN and
    /// out of range (cvttsd2si; JSValue.TruncateToInt32). IL's conv.i4
    /// saturates, which costs a NaN mask and a range compare; the callers
    /// check the result's round trip or range themselves.
    /// </summary>
    void EmitTruncateToInt32()
    {
        if (s_cvttsd2si is null)
        {
            _il.Emit(OpCodes.Conv_I4);
            return;
        }
        _il.Emit(OpCodes.Call, s_createScalarUnsafe!);
        _il.Emit(OpCodes.Call, s_cvttsd2si);
    }

    // V8SHARP_MAGLEV_NO_INLINE_TRANSITIONS=1: field-adding transitions through the helper (for comparison).
    static readonly bool s_noInlineTransitions = Environment.GetEnvironmentVariable("V8SHARP_MAGLEV_NO_INLINE_TRANSITIONS") == "1";

    static readonly FieldInfo s_fixedArrayData = typeof(FixedArray).GetField("_data", BindingFlags.NonPublic | BindingFlags.Instance)!;
    static readonly FieldInfo s_contextSlots = typeof(Context).GetField(nameof(Context.Slots))!;

    /// <summary>
    /// The address of slot <paramref name="index"/> of the context
    /// <paramref name="depth"/> levels up from <paramref name="context"/>
    /// (the previous-context walk unrolled).
    /// </summary>
    void EmitContextSlotAddress(ValueNode context, int depth, int index)
    {
        Load(context, ValueRepresentation.kTagged);
        _il.Emit(OpCodes.Ldfld, s_obj);
        for (int d = 0; d < depth; d++)
        {
            _il.Emit(OpCodes.Ldfld, s_contextSlots);
            _il.Emit(OpCodes.Ldc_I4, (int)Context.Field.PREVIOUS_INDEX);
            _il.Emit(OpCodes.Ldelema, typeof(JSValue));
            _il.Emit(OpCodes.Ldfld, s_obj);
        }
        _il.Emit(OpCodes.Ldfld, s_contextSlots);
        _il.Emit(OpCodes.Ldc_I4, index);
        _il.Emit(OpCodes.Ldelema, typeof(JSValue));
    }

    /// <summary>
    /// The value a store writes: the untagged number a tagging conversion
    /// converts (EmitStoreTagged then writes the payload), or the value.
    /// </summary>
    static ValueNode UntaggedNumberSource(ValueNode value) =>
        value.Opcode is Opcode.Int32ToNumber or Opcode.Uint32ToNumber or Opcode.Float64ToTagged && !value.IsConstant
            ? value.Inputs[0]
            : value;

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
    void EmitLoadMapOrBranch(Node node, Label notReceiver)
    {
        ValueNode value = node.Inputs[0];
        Load(value, ValueRepresentation.kTagged);
        _il.Emit(OpCodes.Ldfld, s_obj);
        if (node.CheckType == CheckType.kOmitHeapObjectCheck || NodeTypes.Is(value.Type, NodeType.kJSReceiver))
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
        // CheckMapsWithMigrationAndDeopt: an object with a deprecated map is
        // migrated (its new map marked as a migration target), then deoptimizes.
        bool migrateAndDeopt = !migrate && node.Int1 == 1;
        Label fail = migrate || migrateAndDeopt ? _il.DefineLabel() : exit;
        Label ok = _il.DefineLabel();
        EmitLoadMapOrBranch(node, exit);
        // The last map's compare branches to the failure, the others to ok.
        if (maps.Length > 1) _il.Emit(OpCodes.Stloc, _tmpMap);
        for (int i = 0; i < maps.Length; i++)
        {
            if (maps.Length > 1) _il.Emit(OpCodes.Ldloc, _tmpMap);
            LoadConstantObject(maps[i], typeof(Map));
            _il.Emit(i == maps.Length - 1 ? OpCodes.Bne_Un : OpCodes.Beq, i == maps.Length - 1 ? fail : ok);
        }
        if (!migrate && !migrateAndDeopt)
        {
            _il.MarkLabel(ok);
            return;
        }
        _il.Emit(OpCodes.Br, ok);
        if (migrate)
        {
            _il.MarkLabel(fail);
            _il.Emit(OpCodes.Ldarg_1);
            Load(node.Inputs[0], ValueRepresentation.kTagged);
            LoadConstantObject(maps, typeof(Map[]));
            Call(nameof(MaglevBuiltins.MigrateAndCheckMaps));
            _il.Emit(OpCodes.Brfalse, exit);
        }
        else if (migrateAndDeopt)
        {
            _il.MarkLabel(fail);
            _il.Emit(OpCodes.Ldarg_1);
            Load(node.Inputs[0], ValueRepresentation.kTagged);
            Call(nameof(MaglevBuiltins.TryMigrateInstanceAndMarkMapAsMigrationTarget));
            _il.Emit(OpCodes.Br, exit);
        }
        _il.MarkLabel(ok);
    }

    static string TypedLoadHelper(ElementsKind kind) => kind switch
    {
        ElementsKind.INT8_ELEMENTS => nameof(MaglevBuiltins.LoadInt8Element),
        ElementsKind.UINT8_ELEMENTS or ElementsKind.UINT8_CLAMPED_ELEMENTS => nameof(MaglevBuiltins.LoadUint8Element),
        ElementsKind.INT16_ELEMENTS => nameof(MaglevBuiltins.LoadInt16Element),
        ElementsKind.UINT16_ELEMENTS => nameof(MaglevBuiltins.LoadUint16Element),
        ElementsKind.INT32_ELEMENTS or ElementsKind.UINT32_ELEMENTS => nameof(MaglevBuiltins.LoadInt32Element),
        ElementsKind.FLOAT32_ELEMENTS => nameof(MaglevBuiltins.LoadFloat32Element),
        ElementsKind.FLOAT64_ELEMENTS => nameof(MaglevBuiltins.LoadFloat64Element),
        _ => throw new InvalidOperationException("typed array kind " + kind),
    };

    static string TypedStoreHelper(ElementsKind kind, bool floatValue) => kind switch
    {
        ElementsKind.INT8_ELEMENTS or ElementsKind.UINT8_ELEMENTS => nameof(MaglevBuiltins.StoreInt8Element),
        ElementsKind.UINT8_CLAMPED_ELEMENTS => floatValue ? nameof(MaglevBuiltins.StoreUint8ClampedFloat64) : nameof(MaglevBuiltins.StoreUint8ClampedInt32),
        ElementsKind.INT16_ELEMENTS or ElementsKind.UINT16_ELEMENTS => nameof(MaglevBuiltins.StoreInt16Element),
        ElementsKind.INT32_ELEMENTS or ElementsKind.UINT32_ELEMENTS => nameof(MaglevBuiltins.StoreInt32Element),
        ElementsKind.FLOAT32_ELEMENTS => nameof(MaglevBuiltins.StoreFloat32Element),
        ElementsKind.FLOAT64_ELEMENTS => nameof(MaglevBuiltins.StoreFloat64Element),
        _ => throw new InvalidOperationException("typed array kind " + kind),
    };

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
        if (_lazyUnits.Contains(unit))
        {
            EmitPushLazyInlinedFrame(unit);
            return;
        }
        // fp = EnterInlinedFrame(isolate, function, bytecode, argc, isConstruct)
        _il.Emit(OpCodes.Ldarg_1);
        // (An inlined closure of a feedback cell: the closure input of EnterInlinedFrame.)
        if (unit.Function is not null) LoadConstantObject(unit.Function, typeof(JSFunction));
        else LoadAsObject(node.Inputs[argc + 2], typeof(JSFunction));
        LoadConstantObject(unit.Bytecode, typeof(BytecodeArray));
        LoadConstantObject(unit.Feedback, typeof(FeedbackVector));
        _il.Emit(OpCodes.Ldc_I4, argc);
        _il.Emit(node.Int1 != 0 ? OpCodes.Ldc_I4_1 : OpCodes.Ldc_I4_0);
        Call(nameof(MaglevBuiltins.EnterInlinedFrame));
        _il.Emit(OpCodes.Stloc, unit.FpLocal!);
        // fpRef = ref isolate.RegisterStack[fp] (EnterInlinedFrame checked the limits)
        _il.Emit(OpCodes.Ldarg_1);
        _il.Emit(OpCodes.Ldfld, s_registerStack);
        _il.Emit(OpCodes.Call, s_arrayDataReference.MakeGenericMethod(typeof(JSValue)));
        _il.Emit(OpCodes.Ldloc, unit.FpLocal!);
        _il.Emit(OpCodes.Call, s_unsafeAddInt.MakeGenericMethod(typeof(JSValue)));
        _il.Emit(OpCodes.Stloc, unit.FpRefLocal!);
        // frame = ref isolate.InterpreterFrames[base + depth] (pushed by EnterInlinedFrame)
        _il.Emit(OpCodes.Ldarg_1);
        _il.Emit(OpCodes.Call, s_interpreterFrames);
        _il.Emit(OpCodes.Call, s_arrayDataReference.MakeGenericMethod(typeof(InterpreterFrameRecord)));
        _il.Emit(OpCodes.Ldloc, _baseFrameIndex);
        _il.Emit(OpCodes.Ldc_I4, unit.InliningDepth);
        _il.Emit(OpCodes.Add);
        _il.Emit(OpCodes.Call, s_unsafeAddInt.MakeGenericMethod(typeof(InterpreterFrameRecord)));
        _il.Emit(OpCodes.Stloc, unit.FrameRecordLocal!);
        // The receiver and the arguments (the new.target register of a construct).
        LoadFrameSlotAddress(unit, InterpreterRuntime.kReceiverOffset);
        EmitStoreTagged(node.Inputs[0]);
        for (int i = 0; i < argc; i++)
        {
            LoadFrameSlotAddress(unit, InterpreterRuntime.kFirstArgumentOffset - i);
            EmitStoreTagged(node.Inputs[1 + i]);
        }
        if (node.Int2 != 0)
        {
            Register incoming = unit.Bytecode.IncomingNewTargetOrGeneratorRegister;
            if (incoming.IsValid)
            {
                LoadFrameSlotAddress(unit, incoming.Index);
                EmitStoreTagged(node.Inputs[^1]);
            }
        }
    }

    // ---- Direct calls (MaglevCalls, "Direct calls") -----------------------------------------------------

    static readonly FieldInfo s_vectorMaglevCode = typeof(FeedbackVector).GetField(nameof(FeedbackVector.MaglevCode))!;
    static readonly FieldInfo s_codeFastCall = typeof(MaglevCode).GetField(nameof(MaglevCode.FastCall))!;
    static readonly FieldInfo s_rawFeedbackCell = typeof(JSFunction).GetField(nameof(JSFunction.RawFeedbackCell))!;
    static readonly FieldInfo s_codeFastCallArity = typeof(MaglevCode).GetField(nameof(MaglevCode.FastCallArity))!;
    static readonly MethodInfo s_unsafeAs = typeof(Unsafe).GetMethods()
        .First(m => m.Name == nameof(Unsafe.As) && m.GetGenericArguments().Length == 1 && m.GetParameters()[0].ParameterType == typeof(object));
    static readonly FieldInfo s_stIsolate = typeof(InterpreterState).GetField(nameof(InterpreterState.Isolate))!;
    static readonly FieldInfo s_stBaseFrameIndex = typeof(InterpreterState).GetField(nameof(InterpreterState.BaseFrameIndex))!;
    static readonly MethodInfo s_enterFastFrameAt = typeof(MaglevCalls).GetMethod(nameof(MaglevCalls.EnterFastFrameAt))!;
    static readonly MethodInfo s_initializeFastFrame = typeof(MaglevCalls).GetMethod(nameof(MaglevCalls.InitializeFastFrame))!;
    static readonly MethodInfo s_storeFrameSlot = typeof(MaglevCalls).GetMethod(nameof(MaglevCalls.StoreFrameSlot))!;
    static readonly MethodInfo s_leaveFastFrame = typeof(MaglevCalls).GetMethod(nameof(MaglevCalls.LeaveFastFrame))!;
    static readonly MethodInfo s_finishFastCall = typeof(MaglevCalls).GetMethod(nameof(MaglevCalls.FinishFastCall))!;
    static readonly MethodInfo s_callKnownSlow = typeof(MaglevBuiltins).GetMethod(nameof(MaglevBuiltins.CallKnownJSFunction))!;
    static readonly MethodInfo[] s_callValues =
    [
        typeof(MaglevCalls).GetMethod(nameof(MaglevCalls.CallValues0))!,
        typeof(MaglevCalls).GetMethod(nameof(MaglevCalls.CallValues1))!,
        typeof(MaglevCalls).GetMethod(nameof(MaglevCalls.CallValues2))!,
        typeof(MaglevCalls).GetMethod(nameof(MaglevCalls.CallValues3))!,
    ];

    LocalBuilder? _callResult;
    LocalBuilder? _calleeCode;
    int _fastCallArity;

    // ---- Frameless direct entries ----------------------------------------------------------------------
    //
    // V8's optimized frames are not interpreter frames: the deoptimizer builds
    // those from the translation when it needs them. A direct call into Maglev
    // code builds the callee's interpreter frame (MaglevCalls, "Direct calls")
    // because stack walks, arguments materialization and lazy deopts read it;
    // code that can do none of these (no calls, no throws, no lazy deopts, no
    // loops with interrupt checks, no frame reads or writes: a leaf) gets a
    // second direct entry that builds no frame at all. Its parameters are the
    // entry's arguments, and an eager deopt exit builds the frame first
    // (MaglevCalls.DeoptimizeFrameless), then continues as any deopt.

    bool _frameless;
    MaglevCodeGenerator? _framelessPrimary;

    sealed class FramelessUnsupportedException() : Exception("frame access in a frameless entry");

    static Type[] FastCallParameterTypes(int arity)
    {
        var parameters = new Type[4 + arity + 1];
        parameters[0] = typeof(MaglevCode);
        parameters[1] = typeof(Isolate);
        parameters[2] = typeof(JSFunction);
        parameters[3] = typeof(int);
        for (int i = 4; i < parameters.Length; i++) parameters[i] = typeof(JSValue);
        return parameters;
    }

    /// <summary>Whether the graph is a leaf: nothing in it needs the interpreter frame.</summary>
    bool IsFramelessCandidate()
    {
        if (_info.IsOsr || _hasCatchBlocks || Flags_NoFrameless) return false;
        BytecodeArray bytecode = _info.Toplevel.Bytecode;
        if (bytecode.IncomingNewTargetOrGeneratorRegister.IsValid) return false;
        if (_fastCallArity != bytecode.ParameterCount - 1) return false;
        foreach (BasicBlock block in _graph.Blocks)
        {
            if (block.IsDead) continue;
            foreach (Node node in block.Nodes)
            {
                if ((node.Properties & (OpProperties.kCall | OpProperties.kCanThrow | OpProperties.kLazyDeopt)) != 0) return false;
                if (node.ExceptionHandler is not null || node.LazyDeoptInfo is not null) return false;
                switch (node.Opcode)
                {
                    case Opcode.CallBuiltin when node.Obj0 is not CallBuiltinInfo { NoFrame: true } && BuiltinNeedsFrame(node):
                    case Opcode.StoreRegister:
                    case Opcode.LoadRegister:
                    case Opcode.CallKnownJSFunction:
                    case Opcode.HandleNoHeapWritesInterrupt:
                    case Opcode.SetCurrentContext:
                    case Opcode.LoadGeneratorField:
                    case Opcode.StoreGeneratorContinuation:
                    case Opcode.GeneratorStore:
                    case Opcode.GeneratorRestoreRegister:
                        return false;
                    case Opcode.EnterInlinedFrame when ((MaglevCompilationUnit)node.Obj0!).EagerFrame:
                        return false;
                }
            }
            if (block.Control is { Opcode: Opcode.JumpLoop }) return false;
        }
        return true;
    }

    static readonly bool Flags_NoFrameless = Environment.GetEnvironmentVariable("V8SHARP_MAGLEV_NO_FRAMELESS") == "1";

    // ---- Lazy frames ---------------------------------------------------------------------------------
    //
    // Code that calls out still needs to be found by stack walks (Error.stack,
    // function.arguments and .caller) and deoptimized lazily, but not its
    // interpreter frame: V8's optimized frame holds the closure, the receiver
    // and the arguments, and the deoptimizer builds interpreter frames from
    // the translation. A lazy direct entry (MaglevCalls, "Lazy optimized
    // frames") keeps those in a MaglevActivation local, pushes a frame record
    // pointing at it, reserves the frame's register window without writing it,
    // stores the bytecode offset of each call into the activation, and builds
    // the interpreter frame only at a deopt (MaglevCalls.DeoptimizeLazyFrame).
    // Inlined functions still push their interpreter frames lazily. Graphs
    // that read or write the outermost frame otherwise (register windows of
    // generic calls, arguments objects, builtins taking the frame state, block
    // contexts) keep the frameful entry.

    bool _lazyFrame;
    Node? _currentNode;

    static string Describe(Node? node) => node is null ? "(prologue or exits)"
        : node.Opcode + (node.Obj0 is CallBuiltinInfo info ? ":" + info.Method.Name : "") + (node.Unit is { IsInline: true } ? " (inlined)" : "");
    LocalBuilder? _activation;
    LocalBuilder? _lazyStart;
    LocalBuilder? _lazySaved;

    static readonly bool s_traceEntries = Environment.GetEnvironmentVariable("V8SHARP_MAGLEV_TRACE_ENTRIES") == "1";
    static readonly bool Flags_NoLazyFrames = Environment.GetEnvironmentVariable("V8SHARP_MAGLEV_NO_LAZY_FRAMES") == "1";
    /// <summary>A field of the activation local <paramref name="activation"/> (MaglevActivation.TypeFor).</summary>
    static FieldInfo AF(LocalBuilder activation, string name) => activation.LocalType.GetField(name)!;
    static readonly FieldInfo s_registerStackTop = typeof(Isolate).GetField(nameof(Isolate.RegisterStackTop))!;
    static readonly MethodInfo s_enterLazyFrame = typeof(MaglevCalls).GetMethod(nameof(MaglevCalls.EnterLazyFrame))!;
    static readonly MethodInfo s_deoptimizeLazyFrame = typeof(MaglevCalls).GetMethod(nameof(MaglevCalls.DeoptimizeLazyFrame))!;

    /// <summary>
    /// Whether the graph can run in a lazy frame: nothing reads or writes the
    /// outermost frame but its parameters (the emission finds the rest, as
    /// FramelessUnsupportedException).
    /// </summary>
    bool IsLazyFrameCandidate()
    {
        if (_info.IsOsr || _hasCatchBlocks || Flags_NoLazyFrames || Flags_NoFrameless) return Reject(null);
        BytecodeArray bytecode = _info.Toplevel.Bytecode;
        if (bytecode.IncomingNewTargetOrGeneratorRegister.IsValid) return Reject(null);
        if (_fastCallArity != bytecode.ParameterCount - 1) return Reject(null);
        foreach (BasicBlock block in _graph.Blocks)
        {
            if (block.IsDead) continue;
            foreach (Node node in block.Nodes)
            {
                switch (node.Opcode)
                {
                    case Opcode.LoadGeneratorField:
                    case Opcode.StoreGeneratorContinuation:
                    case Opcode.GeneratorStore:
                    case Opcode.GeneratorRestoreRegister:
                        return Reject(node);
                }
                if (node.Unit is { IsInline: true }) continue;
                switch (node.Opcode)
                {
                    case Opcode.LoadRegister:
                    case Opcode.SetCurrentContext:
                        return Reject(node);
                    case Opcode.StoreRegister when !new Register(node.Int0).IsParameter:
                        return Reject(node);
                    case Opcode.CallBuiltin when node.Obj0 is CallBuiltinInfo info && UsesFrame(info):
                        return Reject(node);
                }
            }
        }
        return true;

        bool Reject(Node? node)
        {
            if (s_traceEntries && node is not null)
            {
                Console.WriteLine($"[maglev lazy entry of {MaglevCompiler.DebugName(_info.Function.Shared)} rejected at {Describe(node)}]");
            }
            return false;
        }

        static bool UsesFrame(CallBuiltinInfo info)
        {
            if (info.RegisterStores.Length != 0) return true;
            foreach (BuiltinArg arg in info.Args)
            {
                if (arg.Kind is BuiltinArgKind.State or BuiltinArgKind.RegisterIndex or BuiltinArgKind.RegisterRef) return true;
            }
            return false;
        }
    }

    void GenerateLazy()
    {
        ChooseLazyInlinedFrames();
        AllocateLocals();
        _result = _il.DeclareLocal(typeof(JSValue));
        _end = _il.DefineLabel();
        EmitPrologue();
        foreach (BasicBlock block in _graph.Blocks)
        {
            if (block.IsDead) continue;
            EmitBlock(block);
        }
        EmitEdgeStubs();
        EmitDeoptExits();
        // The epilogue: a fault block for exceptions and inline code after the
        // try region (as the frameful direct entry).
        _il.BeginFaultBlock();
        EmitLeaveLazyFrame();
        _il.EndExceptionBlock();
        _il.MarkLabel(_end);
        EmitLeaveLazyFrame();
        _il.Emit(OpCodes.Ldloc, _result);
        _il.Emit(OpCodes.Ret);
        if (_optimizeFully || _il.ILOffset > kAggressiveILBytes) _method.SetImplementationFlags(MethodImplAttributes.AggressiveOptimization);
    }

    /// <summary>
    /// The lazy entry's prologue: the activation (closure, receiver,
    /// arguments, argc), then MaglevCalls.EnterLazyFrame, then the try region
    /// of the body.
    /// </summary>
    void EmitLazyPrologue()
    {
        _activation = _il.DeclareLocal(MaglevActivation.TypeFor(_info.Toplevel.Bytecode.ParameterCount - 1));
        _lazyStart = _il.DeclareLocal(typeof(int));
        _lazySaved = _il.DeclareLocal(typeof(Context));
        BytecodeArray bytecode = _info.Toplevel.Bytecode;
        int formal = bytecode.ParameterCount - 1;
        _il.Emit(OpCodes.Ldloca, _activation);
        _il.Emit(OpCodes.Ldarg_2);
        _il.Emit(OpCodes.Stfld, AF(_activation, "Function"));
        _il.Emit(OpCodes.Ldloca, _activation);
        _il.Emit(OpCodes.Ldarg, (short)4);
        _il.Emit(OpCodes.Stfld, AF(_activation, "Receiver"));
        for (int i = 0; i < formal; i++)
        {
            _il.Emit(OpCodes.Ldloca, _activation);
            _il.Emit(OpCodes.Ldarg, (short)(5 + i));
            _il.Emit(OpCodes.Stfld, AF(_activation, "A" + i));
        }
        _il.Emit(OpCodes.Ldloca, _activation);
        _il.Emit(OpCodes.Ldarg_3);
        _il.Emit(OpCodes.Ldc_I4, int.MaxValue);
        _il.Emit(OpCodes.And);
        _il.Emit(OpCodes.Stfld, AF(_activation, "Argc"));
        // depth = isolate.InterpreterFrameDepth; start = isolate.RegisterStackTop;
        // saved = EnterLazyFrame(isolate, depth, start, formal, registers, function, &activation, argc)
        _il.Emit(OpCodes.Ldarg_1);
        _il.Emit(OpCodes.Ldfld, s_interpreterFrameDepth);
        _il.Emit(OpCodes.Stloc, _baseFrameIndex);
        _il.Emit(OpCodes.Ldarg_1);
        _il.Emit(OpCodes.Ldfld, s_registerStackTop);
        _il.Emit(OpCodes.Stloc, _lazyStart);
        _il.Emit(OpCodes.Ldarg_1);
        _il.Emit(OpCodes.Ldloc, _baseFrameIndex);
        _il.Emit(OpCodes.Ldloc, _lazyStart);
        _il.Emit(OpCodes.Ldc_I4, formal);
        _il.Emit(OpCodes.Ldc_I4, bytecode.RegisterCount);
        _il.Emit(OpCodes.Ldarg_2);
        _il.Emit(OpCodes.Ldloca, _activation);
        _il.Emit(OpCodes.Conv_U);
        _il.Emit(OpCodes.Ldarg_3);
        _il.Emit(OpCodes.Call, s_enterLazyFrame);
        _il.Emit(OpCodes.Stloc, _lazySaved);
        _il.BeginExceptionBlock();
    }

    void EmitLeaveLazyFrame()
    {
        _il.Emit(OpCodes.Ldarg_1);
        _il.Emit(OpCodes.Ldloc, _baseFrameIndex);
        _il.Emit(OpCodes.Ldloc, _lazyStart!);
        _il.Emit(OpCodes.Ldloc, _lazySaved!);
        _il.Emit(OpCodes.Call, s_leaveFastFrame);
    }

    // Inlined functions of a lazy entry get lazy frame records too (V8 has no
    // frames for them at all; its stack walker and deoptimizer read them from
    // the translation): the record's activation (one MaglevActivation local
    // per inlining depth, as only one unit per depth is pushed at a time)
    // holds the closure, receiver, arguments and offset, and the register
    // window is reserved for a deopt to build the interpreter frame in. Units
    // whose code reads or writes their frame otherwise push interpreter
    // frames as before.

    readonly HashSet<MaglevCompilationUnit> _lazyUnits = new(ReferenceEqualityComparer.Instance);
    readonly List<LocalBuilder> _inlinedActivations = [];
    // The most formal parameters of the lazy units at each inlining depth (the size of its activation).
    readonly Dictionary<int, int> _depthArity = new();

    static readonly MethodInfo s_enterLazyInlinedFrame = typeof(MaglevCalls).GetMethod(nameof(MaglevCalls.EnterLazyInlinedFrame))!;
    static readonly bool Flags_NoLazyInlinedFrames = Environment.GetEnvironmentVariable("V8SHARP_MAGLEV_NO_LAZY_INLINED_FRAMES") == "1";

    LocalBuilder InlinedActivation(MaglevCompilationUnit unit)
    {
        int d = unit.InliningDepth;
        while (_inlinedActivations.Count < d)
        {
            int arity = _depthArity.TryGetValue(_inlinedActivations.Count + 1, out int a) ? a : MaglevFastCalls.kMaxArity;
            _inlinedActivations.Add(_il.DeclareLocal(MaglevActivation.TypeFor(arity)));
        }
        return _inlinedActivations[d - 1];
    }

    void ChooseLazyInlinedFrames()
    {
        if (Flags_NoLazyInlinedFrames) return;
        var units = new HashSet<MaglevCompilationUnit>(ReferenceEqualityComparer.Instance);
        var excluded = new HashSet<MaglevCompilationUnit>(ReferenceEqualityComparer.Instance);
        foreach (BasicBlock block in _graph.Blocks)
        {
            if (block.IsDead) continue;
            foreach (Node node in block.Nodes)
            {
                if (node.Opcode == Opcode.EnterInlinedFrame && node.Obj0 is MaglevCompilationUnit u)
                {
                    int formal = u.ParameterCount - 1;
                    bool newTarget = node.Int2 != 0 && u.Bytecode.IncomingNewTargetOrGeneratorRegister.IsValid;
                    if (u.EagerFrame || node.Int0 > formal || formal > MaglevFastCalls.kMaxArity || newTarget) excluded.Add(u);
                    else units.Add(u);
                }
                if (node.Unit is not { IsInline: true } unit) continue;
                switch (node.Opcode)
                {
                    case Opcode.LoadRegister:
                    case Opcode.SetCurrentContext:
                    case Opcode.LoadGeneratorField:
                    case Opcode.StoreGeneratorContinuation:
                    case Opcode.GeneratorStore:
                    case Opcode.GeneratorRestoreRegister:
                        excluded.Add(unit);
                        break;
                    case Opcode.StoreRegister when !new Register(node.Int0).IsParameter:
                        excluded.Add(unit);
                        break;
                    case Opcode.CallBuiltin when node.Obj0 is CallBuiltinInfo info && UsesFrameSlots(info):
                        excluded.Add(unit);
                        break;
                    case Opcode.CallKnownJSFunction when node.Obj0 is KnownCallInfo { ArgsFirst.IsValid: true } call && call.Argc >= s_callValues.Length:
                        excluded.Add(unit);
                        break;
                }
            }
        }
        foreach (MaglevCompilationUnit u in units)
        {
            if (excluded.Contains(u)) continue;
            _lazyUnits.Add(u);
            int formal = u.ParameterCount - 1;
            if (!_depthArity.TryGetValue(u.InliningDepth, out int max) || formal > max) _depthArity[u.InliningDepth] = formal;
        }

        static bool UsesFrameSlots(CallBuiltinInfo info)
        {
            if (info.RegisterStores.Length != 0) return true;
            foreach (BuiltinArg arg in info.Args)
            {
                if (arg.Kind is BuiltinArgKind.State or BuiltinArgKind.RegisterIndex or BuiltinArgKind.RegisterRef) return true;
            }
            return false;
        }
    }

    /// <summary>
    /// Pushes the lazy frame record of an inlined unit: its activation (the
    /// closure, receiver, arguments, argc) and MaglevCalls.EnterLazyInlinedFrame.
    /// </summary>
    void EmitPushLazyInlinedFrame(MaglevCompilationUnit unit)
    {
        Node node = unit.EntryNode!;
        int argc = node.Int0;
        int formal = unit.ParameterCount - 1;
        LocalBuilder activation = InlinedActivation(unit);
        _il.Emit(OpCodes.Ldloca, activation);
        if (unit.Function is not null) LoadConstantObject(unit.Function, typeof(JSFunction));
        else LoadAsObject(node.Inputs[argc + 2], typeof(JSFunction));
        _il.Emit(OpCodes.Stfld, AF(activation, "Function"));
        _il.Emit(OpCodes.Ldloca, activation);
        Load(node.Inputs[0], ValueRepresentation.kTagged);
        _il.Emit(OpCodes.Stfld, AF(activation, "Receiver"));
        for (int i = 0; i < formal; i++)
        {
            _il.Emit(OpCodes.Ldloca, activation);
            if (i < argc) Load(node.Inputs[1 + i], ValueRepresentation.kTagged);
            else LoadUndefined();
            _il.Emit(OpCodes.Stfld, AF(activation, "A" + i));
        }
        _il.Emit(OpCodes.Ldloca, activation);
        _il.Emit(OpCodes.Ldc_I4, argc);
        _il.Emit(OpCodes.Stfld, AF(activation, "Argc"));
        _il.Emit(OpCodes.Ldloca, activation);
        _il.Emit(OpCodes.Ldc_I4_0);
        _il.Emit(OpCodes.Stfld, AF(activation, "Pc"));
        // EnterLazyInlinedFrame(isolate, formal, registers, &activation, isConstruct)
        _il.Emit(OpCodes.Ldarg_1);
        _il.Emit(OpCodes.Ldc_I4, formal);
        _il.Emit(OpCodes.Ldc_I4, unit.RegisterCount);
        _il.Emit(OpCodes.Ldloca, activation);
        _il.Emit(OpCodes.Conv_U);
        _il.Emit(node.Int1 != 0 ? OpCodes.Ldc_I4_1 : OpCodes.Ldc_I4_0);
        _il.Emit(OpCodes.Call, s_enterLazyInlinedFrame);
    }

    /// <summary>The address of a lazy frame's parameter slot <paramref name="index"/> in its activation.</summary>
    void LoadActivationSlotAddress(int index) => LoadActivationSlotAddress(_activation!, _fastCallArity, index);

    void LoadActivationSlotAddress(LocalBuilder activation, int formal, int index)
    {
        for (int i = 0; i <= formal; i++)
        {
            if (Register.FromParameterIndex(i).Index != index) continue;
            _il.Emit(OpCodes.Ldloca, activation);
            _il.Emit(OpCodes.Ldflda, AF(activation, i == 0 ? "Receiver" : "A" + (i - 1)));
            return;
        }
        throw new FramelessUnsupportedException();
    }


    /// <summary>Generates the frameless direct entry of a leaf graph, or null.</summary>
    MaglevCodeGenerator? TryGenerateFramelessEntry()
    {
        bool lazy = false;
        if (!IsFramelessCandidate())
        {
            if (!IsLazyFrameCandidate()) return null;
            lazy = true;
        }
        // The second pass over the graph: block labels and inlined frames' locals start again.
        foreach (BasicBlock block in _graph.Blocks) block.LabelDefined = false;
        foreach (BasicBlock block in _graph.Blocks)
        {
            foreach (Node node in block.Nodes)
            {
                if (node.Opcode == Opcode.EnterInlinedFrame && node.Obj0 is MaglevCompilationUnit unit)
                {
                    unit.FpLocal = null;
                    unit.FpRefLocal = null;
                    unit.FrameRecordLocal = null;
                }
            }
        }
        int deoptPoints = _deoptPoints.Count, feedback = _speculationFeedback.Count;
        var generator = new MaglevCodeGenerator(_info, _code, _optimizeFully, this, lazy) { _framelessPrimary = this };
        try
        {
            if (lazy) generator.GenerateLazy();
            else generator.GenerateFrameless();
            return generator;
        }
        catch (FramelessUnsupportedException)
        {
            if (s_traceEntries) Console.WriteLine($"[maglev {(lazy ? "lazy" : "frameless")} entry of {MaglevCompiler.DebugName(_info.Function.Shared)} failed at {Describe(generator._currentNode)}]");
            _deoptPoints.RemoveRange(deoptPoints, _deoptPoints.Count - deoptPoints);
            _speculationFeedback.RemoveRange(feedback, _speculationFeedback.Count - feedback);
            return null;
        }
    }

    void GenerateFrameless()
    {
        AllocateLocals();
        EmitPrologue();
        foreach (BasicBlock block in _graph.Blocks)
        {
            if (block.IsDead) continue;
            EmitBlock(block);
        }
        EmitEdgeStubs();
        EmitDeoptExits();
        if (_optimizeFully || _il.ILOffset > kAggressiveILBytes) _method.SetImplementationFlags(MethodImplAttributes.AggressiveOptimization);
    }

    Delegate CreateFramelessDelegate()
    {
        Type type = BaselineCodeSpace.CreateType(_type);
        foreach ((FieldBuilder field, object? value) in _staticConstants) type.GetField(field.Name)!.SetValue(null, value);
        return type.GetMethod(_method.Name)!.CreateDelegate(MaglevFastCalls.DelegateTypes[_fastCallArity], _code);
    }

    /// <summary>An InitialValue of a frameless entry: the closure, its context, the receiver or a parameter.</summary>
    void EmitFramelessInitialValue(int register)
    {
        if (register == InterpreterRuntime.kClosureOffset)
        {
            _il.Emit(OpCodes.Ldarg_2);
            _il.Emit(OpCodes.Call, s_jsValueFromObject);
            return;
        }
        if (register == InterpreterRuntime.kContextOffset)
        {
            _il.Emit(OpCodes.Ldarg_2);
            _il.Emit(OpCodes.Ldfld, s_jsFunctionContext);
            _il.Emit(OpCodes.Call, s_jsValueFromObject);
            return;
        }
        for (int i = 0; i <= _fastCallArity; i++)
        {
            if (Register.FromParameterIndex(i).Index == register)
            {
                _il.Emit(OpCodes.Ldarg, (short)(4 + i));
                return;
            }
        }
        throw new FramelessUnsupportedException();
    }

    static readonly FieldInfo s_jsFunctionContext = typeof(JSFunction).GetField(nameof(JSFunction.Context))!;
    static readonly MethodInfo s_deoptimizeFrameless = typeof(MaglevCalls).GetMethod(nameof(MaglevCalls.DeoptimizeFrameless))!;

    /// <summary>
    /// The end of a frameless entry's spill chain: the receiver and the
    /// arguments to the first scratch slots (the spilled values follow,
    /// SpillSlotBase), then MaglevCalls.DeoptimizeFrameless
    /// builds the frame, deoptimizes and continues in the interpreter.
    /// </summary>
    void EmitFramelessDeopt()
    {
        const int argsAt = 0;
        for (int i = 0; i <= _fastCallArity; i++)
        {
            _il.Emit(OpCodes.Ldarg_1);
            _il.Emit(OpCodes.Ldfld, s_deoptScratch);
            _il.Emit(OpCodes.Ldc_I4, argsAt + i);
            _il.Emit(OpCodes.Ldelema, typeof(JSValue));
            _il.Emit(OpCodes.Ldarg, (short)(4 + i));
            _il.Emit(OpCodes.Stobj, typeof(JSValue));
        }
        _il.Emit(OpCodes.Ldarg_1);
        _il.Emit(OpCodes.Ldarg_0);
        _il.Emit(OpCodes.Ldloc, _deoptIndex!);
        _il.Emit(OpCodes.Ldloc, _deoptReason!);
        _il.Emit(OpCodes.Ldarg_2);
        _il.Emit(OpCodes.Ldarg_3);
        _il.Emit(OpCodes.Ldc_I4, argsAt);
        _il.Emit(OpCodes.Call, s_deoptimizeFrameless);
        _il.Emit(OpCodes.Ret);
    }

    /// <summary>Bytecodes that read the actual arguments beyond the formal parameters.</summary>
    internal static bool ReadsActualArguments(BytecodeArray bytecode)
    {
        for (var it = new BytecodeArrayIterator(bytecode); !it.Done(); it.Advance())
        {
            switch (it.CurrentBytecode())
            {
                case Bytecode.CreateMappedArguments:
                case Bytecode.CreateUnmappedArguments:
                case Bytecode.CreateRestParameter:
                case Bytecode.ConstructForwardAllArgs:
                    return true;
            }
        }
        return false;
    }
    readonly Dictionary<Type, LocalBuilder> _fastCallLocals = new();

    /// <summary>
    /// The direct call entry of the code (MaglevCalls, "Direct calls"): the
    /// callee half of a CallKnownJSFunction, as EnterFrame with the function's
    /// constants, followed by the code itself. None for OSR code, generators
    /// and functions with more than kMaxArity parameters.
    /// </summary>
    MethodBuilder? DefineFastCallEntry()
    {
        if (_info.IsOsr) return null;
        SharedFunctionInfo shared = _info.Toplevel.SharedFunctionInfo;
        if (shared.IsClassConstructor || Globals.IsResumableFunction(shared.Kind)) return null;
        BytecodeArray bytecode = _info.Toplevel.Bytecode;
        int formal = bytecode.ParameterCount - 1;
        if (formal > MaglevFastCalls.kMaxArity) return null;
        // A function that reads its actual arguments (arguments objects, rest
        // parameters) takes kMaxArity values, the frame keeping the argc first
        // ones (V8 pushes every argument).
        int arity = ReadsActualArguments(bytecode) ? MaglevFastCalls.kMaxArity : formal;
        _fastCallArity = arity;
        var parameters = new Type[5 + arity];
        parameters[0] = typeof(MaglevCode);
        parameters[1] = typeof(Isolate);
        parameters[2] = typeof(JSFunction);
        parameters[3] = typeof(int);
        for (int i = 4; i < parameters.Length; i++) parameters[i] = typeof(JSValue);
        MethodBuilder method = _type.DefineMethod("FastCall", MethodAttributes.Public | MethodAttributes.Static, typeof(JSValue), parameters);
        ILGenerator il = method.GetILGenerator(256);
        LocalBuilder fpRef = il.DeclareLocal(typeof(JSValue).MakeByRefType());
        LocalBuilder start = il.DeclareLocal(typeof(int));
        LocalBuilder fp = il.DeclareLocal(typeof(int));
        LocalBuilder depth = il.DeclareLocal(typeof(int));
        LocalBuilder saved = il.DeclareLocal(typeof(Context));
        LocalBuilder state = il.DeclareLocal(typeof(InterpreterState));
        LocalBuilder result = il.DeclareLocal(typeof(JSValue));
        LocalBuilder? paramSlots = null;
        if (arity > formal)
        {
            // paramSlots = max(argc & int.MaxValue, formal)
            paramSlots = il.DeclareLocal(typeof(int));
            Label atLeastFormal = il.DefineLabel();
            il.Emit(OpCodes.Ldarg_3);
            il.Emit(OpCodes.Ldc_I4, int.MaxValue);
            il.Emit(OpCodes.And);
            il.Emit(OpCodes.Stloc, paramSlots);
            il.Emit(OpCodes.Ldloc, paramSlots);
            il.Emit(OpCodes.Ldc_I4, formal);
            il.Emit(OpCodes.Bge, atLeastFormal);
            il.Emit(OpCodes.Ldc_I4, formal);
            il.Emit(OpCodes.Stloc, paramSlots);
            il.MarkLabel(atLeastFormal);
        }
        // depth = isolate.InterpreterFrameDepth; start = isolate.RegisterStackTop;
        // fp = start + paramSlots + kFixedSlotsAboveParams;
        // fpRef = EnterFastFrameAt(isolate, depth, fp, registers)
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Ldfld, s_interpreterFrameDepth);
        il.Emit(OpCodes.Stloc, depth);
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Ldfld, s_registerStackTop);
        il.Emit(OpCodes.Stloc, start);
        il.Emit(OpCodes.Ldloc, start);
        if (paramSlots is not null) il.Emit(OpCodes.Ldloc, paramSlots);
        else il.Emit(OpCodes.Ldc_I4, formal);
        il.Emit(OpCodes.Add);
        il.Emit(OpCodes.Ldc_I4, InterpreterRuntime.kFixedSlotsAboveParams);
        il.Emit(OpCodes.Add);
        il.Emit(OpCodes.Stloc, fp);
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Ldloc, depth);
        il.Emit(OpCodes.Ldloc, fp);
        il.Emit(OpCodes.Ldc_I4, bytecode.RegisterCount);
        il.Emit(OpCodes.Call, s_enterFastFrameAt);
        il.Emit(OpCodes.Stloc, fpRef);
        // The receiver and the arguments (V8's pushes).
        void StoreSlot(int index, int arg)
        {
            il.Emit(OpCodes.Ldloc, fpRef);
            il.Emit(OpCodes.Ldc_I4, index * kJSValueSize);
            il.Emit(OpCodes.Conv_I);
            il.Emit(OpCodes.Add);
            if (arg >= 0)
            {
                il.Emit(OpCodes.Ldarg, (short)arg);
            }
            else
            {
                il.Emit(OpCodes.Ldloca, result);
                il.Emit(OpCodes.Initobj, typeof(JSValue));
                il.Emit(OpCodes.Ldloc, result);
            }
            il.Emit(OpCodes.Call, s_storeFrameSlot);
        }
        StoreSlot(InterpreterRuntime.kReceiverOffset, 4);
        for (int i = 0; i < formal; i++) StoreSlot(InterpreterRuntime.kFirstArgumentOffset - i, 5 + i);
        for (int i = formal; i < arity; i++)
        {
            Label skip = il.DefineLabel();
            il.Emit(OpCodes.Ldloc, paramSlots!);
            il.Emit(OpCodes.Ldc_I4, i);
            il.Emit(OpCodes.Ble, skip);
            StoreSlot(InterpreterRuntime.kFirstArgumentOffset - i, 5 + i);
            il.MarkLabel(skip);
        }
        // saved = InitializeFastFrame(isolate, ref fpRef, fp, function, vector, bytecode, argc, newTargetRegister)
        // (a construct's argc has the sign bit set; new.target is undefined for a call)
        Register incoming = bytecode.IncomingNewTargetOrGeneratorRegister;
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Ldloc, fpRef);
        il.Emit(OpCodes.Ldloc, fp);
        il.Emit(OpCodes.Ldarg_2);
        EmitConstant(il, _info.Toplevel.Feedback, typeof(FeedbackVector));
        EmitConstant(il, bytecode, typeof(BytecodeArray));
        il.Emit(OpCodes.Ldarg_3);
        il.Emit(OpCodes.Ldc_I4, incoming.IsValid ? incoming.Index : int.MinValue);
        il.Emit(OpCodes.Call, s_initializeFastFrame);
        il.Emit(OpCodes.Stloc, saved);
        // The InterpreterState of the frame (a deopt continues it; the
        // method's locals start zeroed, .locals init).
        il.Emit(OpCodes.Ldloca, state);
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Stfld, s_stIsolate);
        il.Emit(OpCodes.Ldloca, state);
        il.Emit(OpCodes.Ldloc, fp);
        il.Emit(OpCodes.Stfld, s_stFp);
        il.Emit(OpCodes.Ldloca, state);
        il.Emit(OpCodes.Ldloc, depth);
        il.Emit(OpCodes.Stfld, s_stFrameIndex);
        il.Emit(OpCodes.Ldloca, state);
        il.Emit(OpCodes.Ldloc, depth);
        il.Emit(OpCodes.Stfld, s_stBaseFrameIndex);
        Label end = il.DefineLabel();
        il.BeginExceptionBlock();
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Ldloca, state);
        il.Emit(OpCodes.Call, _method);
        il.Emit(OpCodes.Stloc, result);
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Ldloca, state);
        il.Emit(OpCodes.Ldloc, result);
        il.Emit(OpCodes.Call, s_finishFastCall);
        il.Emit(OpCodes.Stloc, result);
        il.Emit(OpCodes.Leave, end);
        // The epilogue: a fault block for exceptions and inline code after the
        // try (RyuJIT calls a finally's funclet on the normal path too when it
        // does not clone it).
        void Leave()
        {
            il.Emit(OpCodes.Ldarg_1);
            il.Emit(OpCodes.Ldloc, depth);
            il.Emit(OpCodes.Ldloc, start);
            il.Emit(OpCodes.Ldloc, saved);
            il.Emit(OpCodes.Call, s_leaveFastFrame);
        }
        il.BeginFaultBlock();
        Leave();
        il.EndExceptionBlock();
        il.MarkLabel(end);
        Leave();
        il.Emit(OpCodes.Ldloc, result);
        il.Emit(OpCodes.Ret);
        return method;
    }

    /// <summary>A constant object from a static field of the code's type, in another method's IL.</summary>
    void EmitConstant(ILGenerator il, object value, Type type)
    {
        if (!_constantFields.TryGetValue((value, type), out FieldBuilder? field))
        {
            field = _type.DefineField("k" + _constantFields.Count.ToString(System.Globalization.CultureInfo.InvariantCulture), type,
                FieldAttributes.Public | FieldAttributes.Static);
            _constantFields[(value, type)] = field;
            _staticConstants.Add((field, value));
        }
        il.Emit(OpCodes.Ldsfld, field);
    }

    /// <summary>
    /// CallKnownJSFunction: when the callee's feedback vector has Maglev code
    /// with a direct entry, the receiver and the arguments go to it as values;
    /// otherwise the Call builtin's path (MaglevCalls.Call).
    /// </summary>
    void EmitCallKnownJSFunction(Node node)
    {
        var info = (KnownCallInfo)node.Obj0!;
        var v = (ValueNode)node;
        _callResult ??= _il.DeclareLocal(typeof(JSValue));
        _calleeCode ??= _il.DeclareLocal(typeof(MaglevCode));
        Type delegateType = MaglevFastCalls.DelegateTypes[info.FormalCount];
        if (!_fastCallLocals.TryGetValue(delegateType, out LocalBuilder? entry))
        {
            entry = _il.DeclareLocal(delegateType);
            _fastCallLocals[delegateType] = entry;
        }
        Label slow = _il.DefineLabel(), done = _il.DefineLabel();
        StoreBytecodeOffset(node);
        // code = vector.MaglevCode; entry = code?.FastCall as MaglevFastCallN
        LoadConstantObject(info.Vector, typeof(FeedbackVector));
        _il.Emit(OpCodes.Ldfld, s_vectorMaglevCode);
        _il.Emit(OpCodes.Stloc, _calleeCode);
        _il.Emit(OpCodes.Ldloc, _calleeCode);
        _il.Emit(OpCodes.Brfalse, slow);
        // The entry has the formal parameter count's delegate type when its
        // arity is that count (an int compare instead of a type test).
        _il.Emit(OpCodes.Ldloc, _calleeCode);
        _il.Emit(OpCodes.Ldfld, s_codeFastCallArity);
        _il.Emit(OpCodes.Ldc_I4, info.FormalCount);
        _il.Emit(OpCodes.Bne_Un, slow);
        _il.Emit(OpCodes.Ldloc, _calleeCode);
        _il.Emit(OpCodes.Ldfld, s_codeFastCall);
        _il.Emit(OpCodes.Call, s_unsafeAs.MakeGenericMethod(delegateType));
        _il.Emit(OpCodes.Stloc, entry);
        if (info.CheckReceiver)
        {
            Load(node.Inputs[0], ValueRepresentation.kTagged);
            Call(nameof(MaglevBuiltins.IsJSReceiver));
            _il.Emit(OpCodes.Brfalse, slow);
        }
        _il.Emit(OpCodes.Ldloc, entry);
        _il.Emit(OpCodes.Ldarg_1);
        LoadCallTarget(node, info, typeof(JSFunction));
        _il.Emit(OpCodes.Ldc_I4, info.Argc);
        Load(info.HasConvertedReceiver ? node.Inputs[info.ConvertedReceiverInput] : node.Inputs[0], ValueRepresentation.kTagged);
        for (int i = 0; i < info.FormalCount; i++)
        {
            if (i < info.Argc) Load(node.Inputs[1 + i], ValueRepresentation.kTagged);
            else LoadUndefined();
        }
        _il.Emit(OpCodes.Callvirt, delegateType.GetMethod("Invoke")!);
        _il.Emit(OpCodes.Stloc, _callResult);
        _il.Emit(OpCodes.Br, done);
        // The slow path: the arguments in the caller's registers (or as values).
        _il.MarkLabel(slow);
        if (info.ArgsFirst.IsValid && info.Argc >= s_callValues.Length)
        {
            for (int i = 0; i < info.Argc; i++)
            {
                LoadFrameSlotAddress(node.Unit, info.ArgsFirst.Index + i);
                EmitStoreTagged(node.Inputs[1 + i]);
            }
            _il.Emit(OpCodes.Ldarg_1);
            LoadCallTarget(node, info, typeof(JSValue));
            Load(node.Inputs[0], ValueRepresentation.kTagged);
            LoadFp(node.Unit);
            _il.Emit(OpCodes.Ldc_I4, info.ArgsFirst.Index);
            _il.Emit(OpCodes.Add);
            _il.Emit(OpCodes.Ldc_I4, info.Argc);
            _il.Emit(OpCodes.Ldc_I4, (int)info.Mode);
            _il.Emit(OpCodes.Call, s_callKnownSlow);
        }
        else
        {
            _il.Emit(OpCodes.Ldarg_1);
            LoadCallTarget(node, info, typeof(JSValue));
            Load(node.Inputs[0], ValueRepresentation.kTagged);
            for (int i = 0; i < info.Argc; i++) Load(node.Inputs[1 + i], ValueRepresentation.kTagged);
            _il.Emit(OpCodes.Ldc_I4, (int)info.Mode);
            _il.Emit(OpCodes.Call, s_callValues[info.Argc]);
        }
        _il.Emit(OpCodes.Stloc, _callResult);
        _il.MarkLabel(done);
        _il.Emit(OpCodes.Ldloc, _callResult);
        Store(v);
        if (node.LazyDeoptInfo is not null) EmitLazyDeoptCheck(node, v);
    }

    /// <summary>The callee of a CallKnownJSFunction: its constant, or its input (a closure of a feedback cell).</summary>
    void LoadCallTarget(Node node, KnownCallInfo info, Type type)
    {
        if (info.Target is { } target)
        {
            LoadConstantObject(target, type);
            return;
        }
        if (type == typeof(JSValue)) Load(node.Inputs[info.TargetInput], ValueRepresentation.kTagged);
        else LoadAsObject(node.Inputs[info.TargetInput], type);
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
            EmitStoreTagged(node.Inputs[storeInputBase + i]);
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
                    if (_frameless) throw new FramelessUnsupportedException();
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
                    if (node.Unit!.Function is not null)
                    {
                        LoadConstantObject(node.Unit.Function, type);
                        break;
                    }
                    if (_lazyFrame && node.Unit is not { IsInline: true })
                    {
                        // A lazy frame's closure: the entry's.
                        _il.Emit(OpCodes.Ldarg_2);
                        if (type == typeof(JSValue)) _il.Emit(OpCodes.Call, s_jsValueFromObject);
                        else if (type != typeof(JSFunction) && type != typeof(HeapObject) && type != typeof(object)) _il.Emit(OpCodes.Castclass, type);
                        break;
                    }
                    if (_lazyUnits.Contains(node.Unit!))
                    {
                        // A lazy inlined frame's closure: its activation's.
                        LocalBuilder unitActivation = InlinedActivation(node.Unit!);
                        _il.Emit(OpCodes.Ldloca, unitActivation);
                        _il.Emit(OpCodes.Ldfld, AF(unitActivation, "Function"));
                        if (type == typeof(JSValue)) _il.Emit(OpCodes.Call, s_jsValueFromObject);
                        else if (type != typeof(JSFunction) && type != typeof(HeapObject) && type != typeof(object)) _il.Emit(OpCodes.Castclass, type);
                        break;
                    }
                    // An inlined closure of a feedback cell: its frame's closure
                    // slot (the frame is pushed before any builtin call).
                    LoadFrameSlotAddress(node.Unit, InterpreterRuntime.kClosureOffset);
                    _il.Emit(OpCodes.Ldobj, typeof(JSValue));
                    if (type != typeof(JSValue))
                    {
                        _il.Emit(OpCodes.Ldfld, s_obj);
                        if (type != typeof(HeapObject) && type != typeof(object)) _il.Emit(OpCodes.Castclass, type);
                    }
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
