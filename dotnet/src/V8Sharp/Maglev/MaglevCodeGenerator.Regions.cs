// V8Sharp-specific (no V8 counterpart): the code of a graph too big for
// RyuJIT's optimization limits is split into several IL methods ("regions"),
// so that each is compiled fully optimized instead of the whole method with
// MinOpts (RyuJIT compiles a method of more than 60000 IL bytes, 20000
// instructions, 2000 basic blocks, 2000 locals or 8000 local references
// without optimization). V8's code generator has no such limit; the
// baseline tier's chunked compilation (BaselineCompiler) is the analogue for
// unoptimized code. See deviations.md (Maglev, "Region splitting").
//
// The blocks, in emission order, are cut into contiguous regions at points
// of the least loop depth (a hot loop stays in one region) and outside
// inlined function bodies, each under a fraction of RyuJIT's limits as
// measured by a first, whole-method emission. Each region is a static method
//
//   int Region(MaglevCode, Isolate, ref InterpreterState, ref Transfer, int entry)
//
// on the code's type. The code's own method (the entry the callers invoke)
// becomes a dispatcher: it calls the region holding the current entry block
// until a region returns -1 (the function returned, or deoptimized: the
// result is in Transfer.result). An edge into another region's block exits
// the region: the values live into the target block (SSA liveness over the
// graph) and the target's phis are stored into fields of the Transfer struct
// (a value type local to the dispatcher, so recursion and re-entry are
// safe), and the region returns the target's entry id; the target region
// loads them into its locals and jumps to the block. Every region has its
// own deopt exits; the deopt points are shared.
using System.Reflection;
using System.Reflection.Emit;
using V8Sharp.Baseline;
using V8Sharp.Interpreter;

namespace V8Sharp.Maglev;

internal sealed partial class MaglevCodeGenerator
{
    /// <summary>V8SHARP_MAGLEV_NO_REGIONS=1: never split (methods over RyuJIT's limits get MinOpts).</summary>
    static readonly bool s_noRegions = Environment.GetEnvironmentVariable("V8SHARP_MAGLEV_NO_REGIONS") == "1";

    /// <summary>V8SHARP_MAGLEV_SPLIT_FILTER: split only the function of this name (for debugging).</summary>
    static readonly int s_splitNth = int.TryParse(Environment.GetEnvironmentVariable("V8SHARP_MAGLEV_SPLIT_NTH"), out int nth) ? nth : -1;
    static int s_splitCandidates;
    static readonly string? s_splitFilter = Environment.GetEnvironmentVariable("V8SHARP_MAGLEV_SPLIT_FILTER");

    /// <summary>
    /// The share of RyuJIT's limits a region is planned for (V8SHARP_MAGLEV_REGION_BUDGET, percent):
    /// the entry and exit stubs and the region's deopt exits come on top of the
    /// blocks' measured code.
    /// </summary>
    static readonly double s_regionBudget =
        int.TryParse(Environment.GetEnvironmentVariable("V8SHARP_MAGLEV_REGION_BUDGET"), out int budget) && budget > 0 ? budget / 100.0 : 0.4;

    // RyuJIT's optimization limits (DEFAULT_MIN_OPTS_*).
    const int kJitMaxILBytes = 60000;
    const int kJitMaxInstructions = 20000;
    const int kJitMaxBlocks = 2000;
    const int kJitMaxLocalReferences = 8000;
    const int kJitMaxLocals = 2000;

    /// <summary>The first pass's cost of each block (IL bytes, instructions, block boundaries, local references).</summary>
    Dictionary<BasicBlock, (int Bytes, int Instructions, int Blocks, int LocalRefs)>? _blockCosts;

    /// <summary>The split plan (on the dispatcher's generator and on each region's).</summary>
    RegionPlan? _plan;
    /// <summary>A region's generator: its index in the plan; -1 otherwise.</summary>
    int _regionIndex = -1;
    /// <summary>A region's generator: its blocks.</summary>
    HashSet<BasicBlock>? _regionBlocks;
    readonly Dictionary<(BasicBlock From, BasicBlock To), Label> _exitStubs = new();

    bool InRegionMode => _regionIndex >= 0;

    sealed class RegionPlan
    {
        public required List<BasicBlock>[] Regions;
        public required Dictionary<BasicBlock, int> RegionOf;
        /// <summary>The entry blocks (the first block, and the targets of edges between regions), grouped by region.</summary>
        public required List<BasicBlock> Entries;
        public required Dictionary<BasicBlock, int> EntryId;
        /// <summary>The values live into each entry block (its phis excluded).</summary>
        public required Dictionary<BasicBlock, List<ValueNode>> LiveIn;
        public TypeBuilder Transfer = null!;
        public FieldBuilder ResultField = null!;
        public readonly Dictionary<ValueNode, FieldBuilder> Fields = new(ReferenceEqualityComparer.Instance);
        public MaglevCodeGenerator[] Generators = [];
    }

    /// <summary>Whether the method just emitted is beyond RyuJIT's optimization limits.</summary>
    bool ExceedsJitLimits() =>
        _info.Isolate.Flags.MaglevSplitILBytes is > 0 and int forced ? _il.ILOffset > forced :
        _il.ILOffset > kJitMaxILBytes || _il.Instructions > kJitMaxInstructions || _il.BlockBoundaries > kJitMaxBlocks ||
        _il.LocalReferences > kJitMaxLocalReferences || _il.Locals > kJitMaxLocals;

    /// <summary>The generator of region <paramref name="index"/> of <paramref name="primary"/>'s plan.</summary>
    MaglevCodeGenerator(MaglevCodeGenerator primary, int index)
    {
        _optimizeFully = primary._optimizeFully;
        _info = primary._info;
        _graph = primary._graph;
        _code = primary._code;
        _plan = primary._plan;
        _regionIndex = index;
        _regionBlocks = new HashSet<BasicBlock>(_plan!.Regions[index], ReferenceEqualityComparer.Instance);
        _type = primary._type;
        _staticConstants = primary._staticConstants;
        _constantFields = primary._constantFields;
        _deoptPoints = primary._deoptPoints;
        _speculationFeedback = primary._speculationFeedback;
        _method = BaselineCodeSpace.DefineMethod(_type, primary._method.Name + ":r" + index.ToString(System.Globalization.CultureInfo.InvariantCulture),
            typeof(int), [typeof(MaglevCode), typeof(Isolate), typeof(InterpreterState).MakeByRefType(), _plan.Transfer.MakeByRefType(), typeof(int)]);
        _il = new MaglevILEmitter(_method.GetILGenerator(4096));
        _fpRef = _il.DeclareLocal(typeof(JSValue).MakeByRefType());
        _fp = _il.DeclareLocal(typeof(int));
        _frame = _il.DeclareLocal(typeof(InterpreterFrameRecord).MakeByRefType());
        _baseFrameIndex = _il.DeclareLocal(typeof(int));
    }

    /// <summary>
    /// After a whole-method emission over RyuJIT's limits: the code again, split
    /// into regions; null when no useful split exists.
    /// </summary>
    (MaglevCodeEntry Entry, int ILSize)? TryGenerateSplit()
    {
        if (s_noRegions || _frameless || _hasCatchBlocks || _blockCosts is null) return null;
        if (s_splitFilter is not null && MaglevCompiler.DebugName(_info.Function.Shared) != s_splitFilter) return null;
        if (s_splitNth >= 0 && Interlocked.Increment(ref s_splitCandidates) - 1 != s_splitNth) return null;
        RegionPlan? plan = PlanRegions();
        if (plan is null) return null;
        if (s_splitFilter is not null)
        {
            // (Debugging: the graph and the plan.)
            MaglevGraphPrinter.Print(_info, Console.Out);
            for (int k = 0; k < plan.Regions.Length; k++)
            {
                Console.WriteLine($"region {k}: b{plan.Regions[k][0].Id}..b{plan.Regions[k][^1].Id}");
            }
            foreach (BasicBlock e in plan.Entries)
            {
                Console.WriteLine($"entry {plan.EntryId[e]} b{e.Id}: live " + string.Join(" ", plan.LiveIn[e].Select(v => "n" + v.Id)));
            }
        }
        ResetForAnotherPass(_graph);
        var split = new MaglevCodeGenerator(_info, _code, _optimizeFully) { _plan = plan };
        return split.Generate();
    }

    /// <summary>Block labels, value locals and inlined frames' locals start again for another emission of the graph.</summary>
    static void ResetForAnotherPass(Graph graph)
    {
        foreach (BasicBlock block in graph.Blocks)
        {
            block.LabelDefined = false;
            foreach (Phi phi in block.Phis) phi.Local = null;
            foreach (Node node in block.Nodes)
            {
                if (node is ValueNode v) v.Local = null;
                if (node.Obj0 is MaglevCompilationUnit unit)
                {
                    unit.FpLocal = null;
                    unit.FpRefLocal = null;
                    unit.FrameRecordLocal = null;
                }
            }
        }
    }

    // ---- Planning -----------------------------------------------------------------------------------------

    RegionPlan? PlanRegions()
    {
        var blocks = new List<BasicBlock>();
        foreach (BasicBlock block in _graph.Blocks) if (!block.IsDead) blocks.Add(block);
        int n = blocks.Count;
        if (n < 2) return null;
        var index = new Dictionary<BasicBlock, int>(n, ReferenceEqualityComparer.Instance);
        for (int i = 0; i < n; i++) index[blocks[i]] = i;

        // Loops in emission order: [header, last back edge source].
        var loops = new List<(int Start, int End)>();
        for (int i = 0; i < n; i++)
        {
            if (!blocks[i].IsLoopHeader) continue;
            int end = -1;
            foreach (BasicBlock pred in blocks[i].Predecessors)
            {
                if (index.TryGetValue(pred, out int p) && p >= i && p > end) end = p;
            }
            if (end >= 0) loops.Add((i, end));
        }
        // The loop depth of a cut before block i: the loops containing blocks i - 1 and i.
        var depth = new int[n + 1];
        foreach ((int s, int e) in loops)
        {
            for (int i = s + 1; i <= e; i++) depth[i]++;
        }
        // No cut inside an inlined function's blocks (its frame's locals live in one method).
        var forbidden = new bool[n + 1];
        var unitRange = new Dictionary<MaglevCompilationUnit, (int First, int Last)>(ReferenceEqualityComparer.Instance);
        void Mark(MaglevCompilationUnit? unit, int i)
        {
            for (MaglevCompilationUnit? u = unit; u is { IsInline: true }; u = u.Caller)
            {
                unitRange[u] = unitRange.TryGetValue(u, out (int First, int Last) r) ? (Math.Min(r.First, i), Math.Max(r.Last, i)) : (i, i);
            }
        }
        for (int i = 0; i < n; i++)
        {
            foreach (Node node in blocks[i].Nodes)
            {
                Mark(node.Unit, i);
                if (node.Obj0 is MaglevCompilationUnit u) Mark(u, i);
            }
            Mark(blocks[i].Control?.Unit, i);
        }
        foreach ((int first, int last) in unitRange.Values)
        {
            for (int i = first + 1; i <= last; i++) forbidden[i] = true;
        }

        // Costs, with the edge stubs and deopt exits spread over the blocks.
        long sumBytes = 0;
        foreach (BasicBlock b in blocks) sumBytes += _blockCosts!.TryGetValue(b, out var c) ? c.Bytes : 0;
        double overhead = sumBytes > 0 ? Math.Max(1.0, (double)_il.ILOffset / sumBytes) : 1.0;
        double maxBytes = kJitMaxILBytes * s_regionBudget, maxInstr = kJitMaxInstructions * s_regionBudget;
        double maxBlocks = kJitMaxBlocks * s_regionBudget, maxRefs = kJitMaxLocalReferences * s_regionBudget;
        if (_info.Isolate.Flags.MaglevSplitILBytes is > 0 and int forced)
        {
            maxBytes = forced / 2.0;
            maxInstr = maxBlocks = maxRefs = double.MaxValue;
        }
        (double, double, double, double) Cost(int i)
        {
            _blockCosts!.TryGetValue(blocks[i], out var c);
            return (c.Bytes * overhead, c.Instructions * overhead, c.Blocks * overhead, c.LocalRefs * overhead);
        }

        var cuts = new List<int> { 0 };
        int start = 0;
        double aBytes = 0, aInstr = 0, aBlocks = 0, aRefs = 0;
        for (int i = 0; i < n; i++)
        {
            (double b, double ins, double bl, double r) = Cost(i);
            if (i > start && (aBytes + b > maxBytes || aInstr + ins > maxInstr || aBlocks + bl > maxBlocks || aRefs + r > maxRefs))
            {
                // Cut at the least loop depth since the region's start (the latest such point).
                int best = -1, bestDepth = int.MaxValue;
                for (int j = start + 1; j <= i; j++)
                {
                    if (forbidden[j]) continue;
                    if (depth[j] <= bestDepth)
                    {
                        best = j;
                        bestDepth = depth[j];
                    }
                }
                if (best > start)
                {
                    cuts.Add(best);
                    start = best;
                    aBytes = aInstr = aBlocks = aRefs = 0;
                    for (int j = best; j < i; j++)
                    {
                        (double b2, double i2, double bl2, double r2) = Cost(j);
                        aBytes += b2;
                        aInstr += i2;
                        aBlocks += bl2;
                        aRefs += r2;
                    }
                }
            }
            aBytes += b;
            aInstr += ins;
            aBlocks += bl;
            aRefs += r;
        }
        if (cuts.Count < 2) return null;

        var regions = new List<BasicBlock>[cuts.Count];
        var regionOf = new Dictionary<BasicBlock, int>(n, ReferenceEqualityComparer.Instance);
        for (int k = 0; k < cuts.Count; k++)
        {
            int end = k + 1 < cuts.Count ? cuts[k + 1] : n;
            regions[k] = blocks.GetRange(cuts[k], end - cuts[k]);
            foreach (BasicBlock b in regions[k]) regionOf[b] = k;
        }

        // Entry blocks: the first block, and every block entered from another region.
        var entrySet = new HashSet<BasicBlock>(ReferenceEqualityComparer.Instance) { blocks[0] };
        foreach (BasicBlock b in blocks)
        {
            foreach (BasicBlock s in b.Successors())
            {
                if (!s.IsDead && regionOf[s] != regionOf[b]) entrySet.Add(s);
            }
        }
        var entries = new List<BasicBlock>(entrySet);
        entries.Sort((x, y) => index[x].CompareTo(index[y]));
        // Grouped by region (emission order is region order), the first block first.
        var entryId = new Dictionary<BasicBlock, int>(ReferenceEqualityComparer.Instance);
        for (int i = 0; i < entries.Count; i++) entryId[entries[i]] = i;

        Dictionary<BasicBlock, List<ValueNode>> liveIn = ComputeLiveIn(blocks, index, entrySet);
        return new RegionPlan
        {
            Regions = regions,
            RegionOf = regionOf,
            Entries = entries,
            EntryId = entryId,
            LiveIn = liveIn,
        };
    }

    /// <summary>
    /// SSA liveness over the graph: the values live into each of
    /// <paramref name="wanted"/> (phis of the block excluded; a phi's input is
    /// used at the end of its predecessor; deopt frames use their values).
    /// </summary>
    static Dictionary<BasicBlock, List<ValueNode>> ComputeLiveIn(List<BasicBlock> blocks, Dictionary<BasicBlock, int> index,
        HashSet<BasicBlock> wanted)
    {
        var ids = new Dictionary<ValueNode, int>(ReferenceEqualityComparer.Instance);
        var values = new List<ValueNode>();
        int Id(ValueNode v)
        {
            if (!ids.TryGetValue(v, out int id))
            {
                id = values.Count;
                ids[v] = id;
                values.Add(v);
            }
            return id;
        }
        int n = blocks.Count;
        // Per block: uses before definition (gen) and definitions (kill), as id lists first.
        var gen = new List<int>[n];
        var kill = new HashSet<int>[n];
        var phiUses = new List<(int Pred, int Value)>();
        for (int i = 0; i < n; i++)
        {
            BasicBlock block = blocks[i];
            var g = new List<int>();
            var k = new HashSet<int>();
            void Use(ValueNode v)
            {
                if (v.IsConstant) return;
                int id = Id(v);
                if (!k.Contains(id)) g.Add(id);
            }
            void UseFrame(DeoptFrame? frame)
            {
                for (DeoptFrame? f = frame; f is not null; f = f.Parent)
                {
                    var interp = (InterpretedDeoptFrame)f;
                    foreach ((Register _, ValueNode value) in interp.Values) Use(value);
                    Use(interp.Closure);
                }
            }
            foreach (Phi phi in block.Phis) k.Add(Id(phi));
            foreach (Node node in block.Nodes)
            {
                if (IsDeadNode(node)) continue;
                foreach (ValueNode input in node.Inputs)
                {
                    Use(input);
                    if (node.Opcode is Opcode.StoreFixedArrayElement or Opcode.StoreContextSlot or Opcode.StoreMapTransition)
                    {
                        Use(UntaggedNumberSource(input));
                    }
                }
                UseFrame(node.EagerDeoptInfo?.TopFrame);
                UseFrame(node.LazyDeoptInfo?.TopFrame);
                if (node is ValueNode v) k.Add(Id(v));
            }
            ControlNode c = block.Control!;
            foreach (ValueNode input in c.Inputs) Use(input);
            UseFrame(c.EagerDeoptInfo?.TopFrame);
            gen[i] = g;
            kill[i] = k;
        }
        // Phi inputs: live out of their predecessor.
        var phiOut = new List<int>[n];
        for (int i = 0; i < n; i++)
        {
            BasicBlock block = blocks[i];
            foreach (Phi phi in block.Phis)
            {
                for (int p = 0; p < phi.Inputs.Length && p < block.Predecessors.Count; p++)
                {
                    if (!index.TryGetValue(block.Predecessors[p], out int pi) || phi.Inputs[p].IsConstant) continue;
                    (phiOut[pi] ??= []).Add(Id(phi.Inputs[p]));
                }
            }
        }
        int words = (values.Count + 63) / 64;
        var liveInBits = new ulong[n][];
        for (int i = 0; i < n; i++) liveInBits[i] = new ulong[words];
        var outBits = new ulong[words];
        var successors = new int[n][];
        for (int i = 0; i < n; i++)
        {
            var list = new List<int>();
            foreach (BasicBlock s in blocks[i].Successors()) if (index.TryGetValue(s, out int si)) list.Add(si);
            successors[i] = list.ToArray();
        }
        var phiIds = new HashSet<int>[n];
        for (int i = 0; i < n; i++)
        {
            var set = new HashSet<int>();
            foreach (Phi phi in blocks[i].Phis) set.Add(Id(phi));
            phiIds[i] = set;
        }
        bool changed = true;
        while (changed)
        {
            changed = false;
            for (int i = n - 1; i >= 0; i--)
            {
                Array.Clear(outBits);
                foreach (int s in successors[i])
                {
                    ulong[] sin = liveInBits[s];
                    for (int w = 0; w < words; w++) outBits[w] |= sin[w];
                }
                if (phiOut[i] is { } po) foreach (int id in po) outBits[id >> 6] |= 1UL << (id & 63);
                // in = gen | (out - kill)
                foreach (int id in kill[i]) outBits[id >> 6] &= ~(1UL << (id & 63));
                foreach (int id in gen[i]) outBits[id >> 6] |= 1UL << (id & 63);
                ulong[] cur = liveInBits[i];
                for (int w = 0; w < words; w++)
                {
                    if (cur[w] != outBits[w])
                    {
                        cur[w] = outBits[w];
                        changed = true;
                    }
                }
            }
        }
        var result = new Dictionary<BasicBlock, List<ValueNode>>(ReferenceEqualityComparer.Instance);
        foreach (BasicBlock b in wanted)
        {
            int i = index[b];
            var list = new List<ValueNode>();
            ulong[] bits = liveInBits[i];
            for (int w = 0; w < words; w++)
            {
                ulong word = bits[w];
                while (word != 0)
                {
                    int bit = System.Numerics.BitOperations.TrailingZeroCount(word);
                    word &= word - 1;
                    int id = (w << 6) + bit;
                    if (!phiIds[i].Contains(id)) list.Add(values[id]);
                }
            }
            result[b] = list;
        }
        return result;
    }

    // ---- The dispatcher -------------------------------------------------------------------------------------

    /// <summary>
    /// The split code: the Transfer struct, the region methods, and this
    /// method as the dispatcher.
    /// </summary>
    /// <summary>The number of split compilations (for tests).</summary>
    internal static int SplitCompilations;

    void EmitSplit()
    {
        Interlocked.Increment(ref SplitCompilations);
        RegionPlan plan = _plan!;
        plan.Transfer = BaselineCodeSpace.DefineNestedValueType(_type, "Transfer");
        plan.ResultField = plan.Transfer.DefineField("result", typeof(JSValue), FieldAttributes.Public);
        FieldBuilder Field(ValueNode v)
        {
            if (!plan.Fields.TryGetValue(v, out FieldBuilder? f))
            {
                f = plan.Transfer.DefineField("v" + plan.Fields.Count.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    ClrType(v.Representation), FieldAttributes.Public);
                plan.Fields[v] = f;
            }
            return f;
        }
        foreach (BasicBlock entry in plan.Entries)
        {
            foreach (ValueNode v in plan.LiveIn[entry]) Field(v);
            foreach (Phi phi in entry.Phis) if (phi.UseCount > 0) Field(phi);
        }

        plan.Generators = new MaglevCodeGenerator[plan.Regions.Length];
        for (int k = 0; k < plan.Regions.Length; k++)
        {
            ResetForAnotherPass(_graph);
            var region = new MaglevCodeGenerator(this, k);
            plan.Generators[k] = region;
            region.GenerateRegion();
            if (region._maxScratch > _maxScratch) _maxScratch = region._maxScratch;
        }

        if (_info.Isolate.Flags.trace_opt_verbose)
        {
            var sizes = new System.Text.StringBuilder();
            foreach (MaglevCodeGenerator g in plan.Generators)
            {
                sizes.Append(' ').Append(g._il.ILOffset).Append('/').Append(g._il.Instructions).Append('/').Append(g._il.BlockBoundaries)
                    .Append('/').Append(g._il.LocalReferences).Append('/').Append(g._il.Locals);
            }
            Console.WriteLine($"[maglev code split into {plan.Regions.Length} regions, {plan.Entries.Count} entries, {plan.Fields.Count} transfer values; " +
                              $"IL bytes/instructions/blocks/local references/locals:{sizes}]");
        }
        // The dispatcher: entry = 0; loop { entry = Region(entry); } until -1.
        LocalBuilder transfer = _il.DeclareLocal(plan.Transfer);
        LocalBuilder entryLocal = _il.DeclareLocal(typeof(int));
        Label loop = _il.DefineLabel(), check = _il.DefineLabel();
        var callLabels = new Label[plan.Regions.Length];
        for (int k = 0; k < callLabels.Length; k++) callLabels[k] = _il.DefineLabel();
        var cases = new Label[plan.Entries.Count];
        for (int e = 0; e < cases.Length; e++) cases[e] = callLabels[plan.RegionOf[plan.Entries[e]]];
        _il.Emit(OpCodes.Ldc_I4_0);
        _il.Emit(OpCodes.Stloc, entryLocal);
        _il.MarkLabel(loop);
        _il.Emit(OpCodes.Ldloc, entryLocal);
        _il.Emit(OpCodes.Switch, cases);
        _il.Emit(OpCodes.Call, B(nameof(MaglevBuiltins.Unreachable)));
        for (int k = 0; k < callLabels.Length; k++)
        {
            _il.MarkLabel(callLabels[k]);
            _il.Emit(OpCodes.Ldarg_0);
            _il.Emit(OpCodes.Ldarg_1);
            _il.Emit(OpCodes.Ldarg_2);
            _il.Emit(OpCodes.Ldloca, transfer);
            _il.Emit(OpCodes.Ldloc, entryLocal);
            _il.Emit(OpCodes.Call, plan.Generators[k]._method);
            _il.Emit(OpCodes.Stloc, entryLocal);
            _il.Emit(OpCodes.Br, check);
        }
        _il.MarkLabel(check);
        _il.Emit(OpCodes.Ldloc, entryLocal);
        _il.Emit(OpCodes.Ldc_I4_0);
        _il.Emit(OpCodes.Bge, loop);
        _il.Emit(OpCodes.Ldloca, transfer);
        _il.Emit(OpCodes.Ldfld, plan.ResultField);
        _il.Emit(OpCodes.Ret);
    }

    // ---- A region -----------------------------------------------------------------------------------------

    void GenerateRegion()
    {
        RegionPlan plan = _plan!;
        List<BasicBlock> blocks = plan.Regions[_regionIndex];
        // Values loaded at an entry keep their own local; the others share.
        var dedicated = new HashSet<ValueNode>(ReferenceEqualityComparer.Instance);
        var entries = new List<BasicBlock>();
        foreach (BasicBlock entry in plan.Entries)
        {
            if (plan.RegionOf[entry] != _regionIndex) continue;
            entries.Add(entry);
            foreach (ValueNode v in plan.LiveIn[entry]) dedicated.Add(v);
            foreach (Phi phi in entry.Phis) if (phi.UseCount > 0) dedicated.Add(phi);
        }
        foreach (ValueNode v in dedicated) v.Local = _il.DeclareLocal(ClrType(v.Representation));
        AllocateRegionLocals(blocks, dedicated);
        EmitPrologue();

        // The entry: the stub of the entry id loads the block's live values.
        int firstEntry = plan.EntryId[entries[0]];
        var stubs = new Label[entries.Count];
        for (int i = 0; i < stubs.Length; i++) stubs[i] = _il.DefineLabel();
        _il.Emit(OpCodes.Ldarg_S, (byte)4);
        if (firstEntry != 0)
        {
            _il.Emit(OpCodes.Ldc_I4, firstEntry);
            _il.Emit(OpCodes.Sub);
        }
        _il.Emit(OpCodes.Switch, stubs);
        _il.Emit(OpCodes.Call, B(nameof(MaglevBuiltins.Unreachable)));
        for (int i = 0; i < stubs.Length; i++)
        {
            _il.MarkLabel(stubs[i]);
            BasicBlock entry = entries[i];
            foreach (ValueNode v in plan.LiveIn[entry]) EmitLoadTransfer(v);
            foreach (Phi phi in entry.Phis) if (phi.UseCount > 0) EmitLoadTransfer(phi);
            _il.Emit(OpCodes.Br, BlockLabel(entry));
        }

        for (int i = 0; i < blocks.Count; i++)
        {
            _nextBlock = i + 1 < blocks.Count ? blocks[i + 1] : null;
            EmitBlock(blocks[i]);
        }
        _nextBlock = null;
        EmitEdgeStubs();
        EmitExitStubs();
        EmitDeoptExits();
        if (_optimizeFully || _il.ILOffset > kAggressiveILBytes || _il.ILOffset <= s_aggressiveMaxIL)
        {
            _method.SetImplementationFlags(MethodImplAttributes.AggressiveOptimization);
        }
    }

    void EmitLoadTransfer(ValueNode v)
    {
        _il.Emit(OpCodes.Ldarg_3);
        _il.Emit(OpCodes.Ldfld, _plan!.Fields[v]);
        _il.Emit(OpCodes.Stloc, v.Local!);
    }

    /// <summary>Whether a branch to <paramref name="block"/> leaves the region being emitted.</summary>
    bool LeavesRegion(BasicBlock block) => _regionBlocks is not null && !_regionBlocks.Contains(block);

    /// <summary>The exit stub of the edge <paramref name="from"/> -> <paramref name="to"/> (another region's block).</summary>
    Label ExitLabel(BasicBlock from, BasicBlock to)
    {
        if (!_exitStubs.TryGetValue((from, to), out Label label))
        {
            label = _il.DefineLabel();
            _exitStubs[(from, to)] = label;
        }
        return label;
    }

    /// <summary>
    /// Exit stubs: the target's phis (from the edge's inputs) and the values
    /// live into the target go to the Transfer struct; the region returns the
    /// target's entry id.
    /// </summary>
    void EmitExitStubs()
    {
        RegionPlan plan = _plan!;
        foreach (KeyValuePair<(BasicBlock From, BasicBlock To), Label> e in _exitStubs)
        {
            (BasicBlock from, BasicBlock to) = e.Key;
            _il.MarkLabel(e.Value);
            if (to.Phis.Count > 0)
            {
                int index = to.PredecessorIndexOf(from);
                foreach (Phi phi in to.Phis)
                {
                    if (phi.UseCount <= 0) continue;
                    _il.Emit(OpCodes.Ldarg_3);
                    Load(phi.Inputs[index], phi.Representation);
                    _il.Emit(OpCodes.Stfld, plan.Fields[phi]);
                }
            }
            foreach (ValueNode v in plan.LiveIn[to])
            {
                _il.Emit(OpCodes.Ldarg_3);
                Load(v);
                _il.Emit(OpCodes.Stfld, plan.Fields[v]);
            }
            _il.Emit(OpCodes.Ldc_I4, plan.EntryId[to]);
            _il.Emit(OpCodes.Ret);
        }
    }

    /// <summary>
    /// Locals of a region: the values defined in its blocks (not loaded at an
    /// entry) share locals by their live ranges in the region, the values an
    /// exit stores counting as used at the end of the exit's block.
    /// </summary>
    void AllocateRegionLocals(List<BasicBlock> blocks, HashSet<ValueNode> dedicated)
    {
        var exitUses = new Dictionary<BasicBlock, List<ValueNode>>(ReferenceEqualityComparer.Instance);
        foreach (BasicBlock block in blocks)
        {
            foreach (BasicBlock s in block.Successors())
            {
                if (s.IsDead || _regionBlocks!.Contains(s)) continue;
                var list = exitUses.TryGetValue(block, out List<ValueNode>? l) ? l : exitUses[block] = [];
                list.AddRange(_plan!.LiveIn[s]);
                if (s.Phis.Count > 0)
                {
                    int index = s.PredecessorIndexOf(block);
                    foreach (Phi phi in s.Phis) if (phi.UseCount > 0) list.Add(phi.Inputs[index]);
                }
            }
        }
        if (s_shareLocals && TryAllocateSharedLocals(blocks, dedicated, exitUses)) return;
        foreach (BasicBlock block in blocks)
        {
            foreach (Phi phi in block.Phis)
            {
                if (phi.UseCount > 0 && phi.Local is null) phi.Local = _il.DeclareLocal(ClrType(phi.Representation));
            }
            foreach (Node node in block.Nodes)
            {
                if (node is ValueNode { IsConstant: false, UseCount: > 0 } v && v.Local is null) v.Local = _il.DeclareLocal(ClrType(v.Representation));
            }
        }
    }

    // ---- Debugging (V8SHARP_MAGLEV_SPLIT_FILTER) --------------------------------------------------------------

    static Dictionary<short, OpCode>? s_opcodes;

    static void DumpIL(MethodInfo method)
    {
        if (s_opcodes is null)
        {
            var table = new Dictionary<short, OpCode>();
            foreach (FieldInfo f in typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static))
            {
                if (f.GetValue(null) is OpCode op) table[op.Value] = op;
            }
            s_opcodes = table;
        }
        byte[] il = method.GetMethodBody()!.GetILAsByteArray()!;
        Console.WriteLine("IL of " + method.Name);
        int pc = 0;
        while (pc < il.Length)
        {
            int start = pc;
            short code = il[pc++];
            if (code == 0xFE) code = unchecked((short)(0xFE00 | il[pc++]));
            OpCode op = s_opcodes[code];
            string arg = "";
            switch (op.OperandType)
            {
                case System.Reflection.Emit.OperandType.InlineNone: break;
                case System.Reflection.Emit.OperandType.ShortInlineI:
                case System.Reflection.Emit.OperandType.ShortInlineVar: arg = ((sbyte)il[pc]).ToString(); pc += 1; break;
                case System.Reflection.Emit.OperandType.ShortInlineBrTarget: arg = "L" + (pc + 1 + (sbyte)il[pc]); pc += 1; break;
                case System.Reflection.Emit.OperandType.InlineVar: arg = BitConverter.ToInt16(il, pc).ToString(); pc += 2; break;
                case System.Reflection.Emit.OperandType.InlineBrTarget: arg = "L" + (pc + 4 + BitConverter.ToInt32(il, pc)); pc += 4; break;
                case System.Reflection.Emit.OperandType.InlineI: arg = BitConverter.ToInt32(il, pc).ToString(); pc += 4; break;
                case System.Reflection.Emit.OperandType.InlineI8:
                case System.Reflection.Emit.OperandType.InlineR: arg = BitConverter.ToInt64(il, pc).ToString(); pc += 8; break;
                case System.Reflection.Emit.OperandType.ShortInlineR: pc += 4; break;
                case System.Reflection.Emit.OperandType.InlineSwitch:
                {
                    int n = BitConverter.ToInt32(il, pc);
                    pc += 4;
                    int bas = pc + 4 * n;
                    var targets = new List<string>();
                    for (int k = 0; k < n; k++) targets.Add("L" + (bas + BitConverter.ToInt32(il, pc + 4 * k)));
                    pc += 4 * n;
                    arg = string.Join(",", targets);
                    break;
                }
                default:
                {
                    int token = BitConverter.ToInt32(il, pc);
                    pc += 4;
                    try { arg = method.Module.ResolveMember(token)?.Name ?? token.ToString(); } catch { arg = token.ToString("x"); }
                    break;
                }
            }
            Console.WriteLine($"  L{start}: {op.Name} {arg}");
        }
    }
}
