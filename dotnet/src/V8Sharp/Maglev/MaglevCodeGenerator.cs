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
    readonly ILGenerator _il;

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
    readonly Dictionary<(object, Type), FieldBuilder> _constantFields = new();
    readonly List<DeoptPoint> _deoptPoints = [];
    readonly Dictionary<(DeoptFrame, DeoptimizeReason, int), Label> _eagerExits = new();
    readonly List<(FeedbackVector Vector, int Slot)> _speculationFeedback = [];
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
    {
        _optimizeFully = optimizeFully;
        _info = info;
        _graph = info.Graph;
        _code = code;
        string name = "maglev:" + MaglevCompiler.DebugName(info.Function.Shared) + (info.IsOsr ? "@osr" + info.OsrOffset : "");
        (_type, _method) = BaselineCodeSpace.For(info.Isolate).DefineMethod(name, typeof(JSValue),
            [typeof(MaglevCode), typeof(Isolate), typeof(InterpreterState).MakeByRefType()]);
        _il = _method.GetILGenerator(4096);
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
    static readonly MethodInfo s_doubleToInt64Bits = typeof(BitConverter).GetMethod(nameof(BitConverter.DoubleToInt64Bits), [typeof(double)])!;

    /// <summary>Time spent creating the code's type and delegate (V8SHARP_JIT_STATS).</summary>
    internal static double CreateTypeMs;
    // Diagnostics: V8SHARP_IL_HISTOGRAM=1 prints the IL bytes per node kind at exit.
    static readonly Dictionary<string, (int Count, int Bytes)>? s_ilHistogram = InitHistogram();

    static Dictionary<string, (int, int)>? InitHistogram()
    {
        if (Environment.GetEnvironmentVariable("V8SHARP_IL_HISTOGRAM") != "1") return null;
        var h = new Dictionary<string, (int, int)>();
        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            var list = new List<(string, int, int)>();
            foreach (var e in h) list.Add((e.Key, e.Value.Item1, e.Value.Item2));
            list.Sort(static (a, b) => b.Item3.CompareTo(a.Item3));
            for (int i = 0; i < list.Count && i < 30; i++) Console.Error.WriteLine($"IL {list[i].Item3,8} {list[i].Item2,7} {list[i].Item1}");
        };
        return h;
    }

    static readonly System.Collections.Concurrent.ConcurrentDictionary<string, MethodInfo> s_builtins = new(StringComparer.Ordinal);

    static MethodInfo B(string name) => s_builtins.GetOrAdd(name, static n =>
        typeof(MaglevBuiltins).GetMethod(n) ?? throw new InvalidOperationException("no MaglevBuiltins." + n));

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
        if (_il.ILOffset > kMaxOptimizedILBytes && !_info.Isolate.Flags.allow_natives_syntax)
        {
            // RyuJIT would compile the method with MinOpts (compSetOptimizationLevel),
            // slower than the baseline code it replaces (V8Sharp's limit on top
            // of V8's max_maglev_optimized_bytecode_size; deviations.md).
            throw new MaglevBailoutException($"IL beyond RyuJIT's optimization limits ({_il.ILOffset} bytes)");
        }
        if (_info.Isolate.Flags.trace_opt_verbose)
        {
            Console.WriteLine($"[maglev code: {bodySize} bytes IL body, {_il.ILOffset - bodySize} bytes in {_pendingExits.Count} deopt exits " +
                              $"({_frameExits.Count} eager, {_pendingExits.Count - _frameExits.Count} lazy; {_eagerStubs.Count} eager checks, {_spilledValues} values)]");
        }

        MethodBuilder? fastCall = DefineFastCallEntry();
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
        _code.MaxScratchSize = _maxScratch;
        long createStart = System.Diagnostics.Stopwatch.GetTimestamp();
        Type type = BaselineCodeSpace.CreateType(_type);
        foreach ((FieldBuilder field, object? value) in _staticConstants)
        {
            type.GetField(field.Name)!.SetValue(null, value);
        }
        MethodInfo method = type.GetMethod(_method.Name)!;
        var entry = (MaglevCodeEntry)method.CreateDelegate(typeof(MaglevCodeEntry), _code);
        if (fastCall is not null)
        {
            _code.FastCall = type.GetMethod(fastCall.Name)!.CreateDelegate(MaglevFastCalls.DelegateTypes[_fastCallArity], _code);
            _code.FastCallArity = _fastCallArity;
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
    /// The most IL a tiering compile may produce. RyuJIT switches a tier-0
    /// compile of a method over 60000 IL bytes, 20000 IL instructions or 8000
    /// local references to MinOpts (it never tiers up when the code is an OSR
    /// loop); methods over <see cref="kAggressiveILBytes"/> are therefore
    /// compiled fully optimized at once (AggressiveOptimization, as the
    /// concurrent compiles are), which these limits do not apply to (measured:
    /// FullOpts up to 47000 bytes of Maglev IL). The limit bounds RyuJIT's
    /// compile time.
    /// </summary>
    /// <summary>The largest body inlined into its direct entry (RyuJIT's inlinee limits: no EH, few locals).</summary>
    static readonly int kInlineIntoFastCallILBytes =
        int.TryParse(Environment.GetEnvironmentVariable("V8SHARP_MAGLEV_INLINE_BODY_IL"), out int inlineBody) ? inlineBody : 1200;

    static readonly int kMaxOptimizedILBytes = int.TryParse(Environment.GetEnvironmentVariable("V8SHARP_MAGLEV_MAX_IL"), out int maxIL) ? maxIL : 60000;

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
    bool TryAllocateSharedLocals()
    {
        foreach (BasicBlock block in _graph.Blocks)
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
        void UseFrame(DeoptFrame? frame, int at)
        {
            for (DeoptFrame? f = frame; f is not null; f = f.Parent)
            {
                var i = (InterpretedDeoptFrame)f;
                foreach ((Register _, ValueNode value) in i.Values) Use(value, at);
                Use(i.Closure, at);
            }
        }
        foreach (BasicBlock block in _graph.Blocks)
        {
            if (block.IsDead) continue;
            blockStart[block] = pos++;
            foreach (Node node in block.Nodes)
            {
                int at = pos++;
                foreach (ValueNode input in node.Inputs) Use(input, at);
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
        }
        // Phis: inputs used at the end of their predecessor; the phi is defined
        // at the end of its first predecessor in emission order.
        var loops = new List<(int Start, int End)>();
        foreach (BasicBlock block in _graph.Blocks)
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
                def[phi] = first;
                if (loopEnd >= 0) Use(phi, loopEnd);
            }
        }
        // Lazily pushed inlined frames read their call's receiver and arguments.
        foreach (BasicBlock block in _graph.Blocks)
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
            if (v.IsConstant || v.UseCount <= 0) continue;
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

    /// <summary>Records the node's bytecode offset in its frame's offset slot (for stack traces and messages).</summary>
    void StoreBytecodeOffset(NodeBase node)
    {
        if (node.BytecodeOffset < 0) return;
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
            int ilBefore = _il.ILOffset;
            EmitNode(node);
            if (s_ilHistogram is not null)
            {
                string key = node.Opcode == Opcode.CallBuiltin ? "CallBuiltin:" + ((CallBuiltinInfo)node.Obj0!).Method.Name : node.Opcode == Opcode.CheckMaps ? "CheckMaps:" + ((Map[])node.Obj0!).Length + (Array.TrueForAll((Map[])node.Obj0!, static m => m.IsStable) ? ":stable" : ":unstable") + (NodeTypes.Is(node.Inputs[0].Type, NodeType.kJSReceiver) ? ":recv" : "") : node.Opcode.ToString();
                lock (s_ilHistogram) { s_ilHistogram.TryGetValue(key, out (int, int) e); s_ilHistogram[key] = (e.Item1 + 1, e.Item2 + _il.ILOffset - ilBefore); }
            }
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
                JSValue[]? constants = null;
                bool[]? isConstant = null;
                for (int k = 0; k < f.Values.Length; k++)
                {
                    registers[k] = f.Values[k].Register;
                    ValueNode value = f.Values[k].Value;
                    if (value.IsConstant)
                    {
                        // A literal of the translation (V8's StoreLiteral): the
                        // Deoptimizer writes it, the exit spills nothing.
                        (constants ??= new JSValue[f.Values.Length])[k] = value.ConstantValue();
                        (isConstant ??= new bool[f.Values.Length])[k] = true;
                        spill.Add(null);
                        continue;
                    }
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
                    Constants = constants,
                    IsConstant = isConstant,
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
            point.Frames = data;
            point.ScratchSize = scratch;
            _maxScratch = Math.Max(_maxScratch, scratch);
            int index = _deoptPoints.Count;
            _deoptPoints.Add(point);
            info.DeoptIndex = index;
            // The deopt point and the reason go to locals, and the exit jumps to
            // the spill code of its value sequence, which exits with the same
            // values in the same scratch slots share (the translations differ
            // in their frames' offsets and registers, which are data).
            var key = new SpillKey(spill);
            if (kind == DeoptimizeKind.kLazy)
            {
                _il.Emit(OpCodes.Ldc_I4, (int)reason);
                _il.Emit(OpCodes.Stloc, _deoptReason);
            }
            _il.Emit(OpCodes.Ldc_I4, index);
            _il.Emit(OpCodes.Stloc, _deoptIndex);
            if (!_spillBlocks.TryGetValue(key, out Label block))
            {
                block = _il.DefineLabel();
                _spillBlocks[key] = block;
                _pendingSpills.Add((block, spill));
            }
            _il.Emit(OpCodes.Br, block);
        }
        foreach ((Label block, List<ValueNode?> spill) in _pendingSpills)
        {
            _il.MarkLabel(block);
            _spilledValues += spill.Count;
            // The last (up to four) values go with the Deoptimize call
            // (MaglevBuiltins.DeoptN), the others through Spill*.
            int tail = spill.Count;
            int tailCount = 0;
            while (tail > 0 && tailCount < 4 && spill[tail - 1] is not null)
            {
                tail--;
                tailCount++;
            }
            EmitSpill(spill.GetRange(0, tail));
            // Deoptimizer::Deoptimize(isolate, ref state, code, index, reason); return to MaglevExecution.Run.
            _il.Emit(OpCodes.Ldarg_1);
            _il.Emit(OpCodes.Ldarg_2);
            _il.Emit(OpCodes.Ldarg_0);
            _il.Emit(OpCodes.Ldloc, _deoptIndex);
            _il.Emit(OpCodes.Ldloc, _deoptReason);
            if (tailCount > 0)
            {
                _il.Emit(OpCodes.Ldc_I4, tail);
                for (int k = 0; k < tailCount; k++) Load(spill[tail + k]!, ValueRepresentation.kTagged);
            }
            Call("Deopt" + tailCount);
            EmitReturn();
        }
    }

    readonly Dictionary<SpillKey, Label> _spillBlocks = new();
    readonly List<(Label Block, List<ValueNode?> Spill)> _pendingSpills = [];

    /// <summary>A deopt exit's spill sequence, compared by the values' identities.</summary>
    readonly struct SpillKey(List<ValueNode?> values) : IEquatable<SpillKey>
    {
        readonly List<ValueNode?> _values = values;

        public bool Equals(SpillKey other)
        {
            if (_values.Count != other._values.Count) return false;
            for (int i = 0; i < _values.Count; i++)
            {
                if (!ReferenceEquals(_values[i], other._values[i])) return false;
            }
            return true;
        }

        public override bool Equals(object? obj) => obj is SpillKey k && Equals(k);

        public override int GetHashCode()
        {
            var h = new HashCode();
            foreach (ValueNode? v in _values) h.Add(v is null ? 0 : RuntimeHelpers.GetHashCode(v));
            return h.ToHashCode();
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
            // Slots the Deoptimizer fills (literals, elided objects) are skipped.
            if (values[i] is null)
            {
                i++;
                continue;
            }
            int run = 0;
            while (i + run < values.Count && values[i + run] is not null) run++;
            int remaining = run;
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
                EmitLoadMapOrBranch(node.Inputs[0], done);
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
        // CheckMapsWithMigrationAndDeopt: an object with a deprecated map is
        // migrated (its new map marked as a migration target), then deoptimizes.
        bool migrateAndDeopt = !migrate && node.Int1 == 1;
        Label fail = migrate || migrateAndDeopt ? _il.DefineLabel() : exit;
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
        // fp = EnterInlinedFrame(isolate, function, bytecode, argc, isConstruct)
        _il.Emit(OpCodes.Ldarg_1);
        LoadConstantObject(unit.Function, typeof(JSFunction));
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
    static readonly FieldInfo s_codeFastCallArity = typeof(MaglevCode).GetField(nameof(MaglevCode.FastCallArity))!;
    static readonly MethodInfo s_unsafeAs = typeof(Unsafe).GetMethods()
        .First(m => m.Name == nameof(Unsafe.As) && m.GetGenericArguments().Length == 1 && m.GetParameters()[0].ParameterType == typeof(object));
    static readonly FieldInfo s_stIsolate = typeof(InterpreterState).GetField(nameof(InterpreterState.Isolate))!;
    static readonly FieldInfo s_stBaseFrameIndex = typeof(InterpreterState).GetField(nameof(InterpreterState.BaseFrameIndex))!;
    static readonly MethodInfo s_enterFastFrame = typeof(MaglevCalls).GetMethod(nameof(MaglevCalls.EnterFastFrame))!;
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
        // fpRef = EnterFastFrame(isolate, paramSlots, registers, out start, out fp, out depth)
        il.Emit(OpCodes.Ldarg_1);
        if (paramSlots is not null) il.Emit(OpCodes.Ldloc, paramSlots);
        else il.Emit(OpCodes.Ldc_I4, formal);
        il.Emit(OpCodes.Ldc_I4, bytecode.RegisterCount);
        il.Emit(OpCodes.Ldloca, start);
        il.Emit(OpCodes.Ldloca, fp);
        il.Emit(OpCodes.Ldloca, depth);
        il.Emit(OpCodes.Call, s_enterFastFrame);
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
        LoadConstantObject(info.Target, typeof(JSFunction));
        _il.Emit(OpCodes.Ldc_I4, info.Argc);
        Load(info.HasConvertedReceiver ? node.Inputs[^1] : node.Inputs[0], ValueRepresentation.kTagged);
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
            LoadConstantObject(info.Target, typeof(JSValue));
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
            LoadConstantObject(info.Target, typeof(JSValue));
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
