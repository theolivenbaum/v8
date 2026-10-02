// Port of src/maglev/maglev-interpreter-frame-state.{h,cc} and the parts of
// src/maglev/maglev-known-node-aspects.{h,cc} the graph builder uses:
//
//   InterpreterFrameState            the abstract interpreter frame while
//                                    building: one ValueNode per parameter,
//                                    register, the context and the accumulator
//   KnownNodeAspects / NodeInfo      what is known about values on the current
//                                    path: static type, possible maps (with
//                                    their stability), cached conversions
//   MergePointInterpreterFrameState  the frame at a bytecode with several
//                                    predecessors: creates Phis, loop phis for
//                                    the registers a loop assigns, and merges
//                                    the known node aspects
//
// The frame is indexed by "frame slot": parameters 0..n-1 (receiver first),
// then the context, the registers, and the accumulator last (V8's
// RegisterFrameArray uses the register index directly; the slot index is the
// same thing shifted to start at 0).
using V8Sharp.Interpreter;

namespace V8Sharp.Maglev;

/// <summary>NodeInfo.</summary>
public sealed class NodeInfo
{
    public NodeType Type = NodeType.kUnknown;
    /// <summary>The maps the value may have (null: unknown).</summary>
    public Map[]? PossibleMaps;
    /// <summary>Whether some possible map is unstable (the map set must be dropped after side effects).</summary>
    public bool AnyMapIsUnstable;
    // Cached conversions (NodeInfo::alternative()).
    public ValueNode? Int32Alternative;
    public ValueNode? Float64Alternative;
    /// <summary>The Float64 of ToNumber for a number or oddball (not the value itself for oddballs).</summary>
    public ValueNode? NumberOrOddballFloat64Alternative;
    public ValueNode? TaggedAlternative;
    public ValueNode? TruncatedInt32Alternative;

    public NodeInfo Clone() => (NodeInfo)MemberwiseClone();

    public bool HasMaps => PossibleMaps is not null;
}

/// <summary>KnownNodeAspects (the subset: node infos; loaded properties are not cached yet).</summary>
public sealed class KnownNodeAspects
{
    readonly Dictionary<ValueNode, NodeInfo> _infos;

    public KnownNodeAspects() => _infos = new Dictionary<ValueNode, NodeInfo>(ReferenceEqualityComparer.Instance);

    KnownNodeAspects(Dictionary<ValueNode, NodeInfo> infos) => _infos = infos;

    public KnownNodeAspects Clone()
    {
        var copy = new Dictionary<ValueNode, NodeInfo>(_infos.Count, ReferenceEqualityComparer.Instance);
        foreach (KeyValuePair<ValueNode, NodeInfo> e in _infos) copy[e.Key] = e.Value.Clone();
        return new KnownNodeAspects(copy);
    }

    public NodeInfo? TryGetInfoFor(ValueNode node) => _infos.GetValueOrDefault(node);

    public NodeInfo GetOrCreateInfoFor(ValueNode node)
    {
        if (!_infos.TryGetValue(node, out NodeInfo? info))
        {
            info = new NodeInfo { Type = node.Type };
            _infos[node] = info;
        }
        return info;
    }

    public NodeType GetType(ValueNode node)
    {
        NodeType type = node.Type;
        if (_infos.TryGetValue(node, out NodeInfo? info)) type &= info.Type;
        return type;
    }

    /// <summary>Records that <paramref name="node"/> has <paramref name="type"/> on this path.</summary>
    public void EnsureType(ValueNode node, NodeType type)
    {
        NodeInfo info = GetOrCreateInfoFor(node);
        info.Type &= type;
    }

    /// <summary>
    /// After a node with side effects: maps of unstable map sets can change
    /// (V8's KnownNodeAspects::ClearUnstableMaps / ResetUnstable...). Stable
    /// maps cannot (their dependency deoptimizes the code when they become
    /// unstable).
    /// </summary>
    public void ClearUnstableMaps()
    {
        foreach (NodeInfo info in _infos.Values)
        {
            if (info.AnyMapIsUnstable)
            {
                info.PossibleMaps = null;
                info.AnyMapIsUnstable = false;
                // A heap object stays a heap object; its finer type came from the maps.
                if (NodeTypes.Is(info.Type, NodeType.kJSReceiver)) info.Type = NodeType.kJSReceiver;
            }
        }
    }

    /// <summary>KnownNodeAspects::Merge: keep only what both paths know.</summary>
    public void Merge(KnownNodeAspects other)
    {
        List<ValueNode>? remove = null;
        foreach (KeyValuePair<ValueNode, NodeInfo> e in _infos)
        {
            if (!other._infos.TryGetValue(e.Key, out NodeInfo? theirs))
            {
                (remove ??= []).Add(e.Key);
                continue;
            }
            NodeInfo mine = e.Value;
            mine.Type |= theirs.Type;
            if (mine.PossibleMaps is null || theirs.PossibleMaps is null)
            {
                mine.PossibleMaps = null;
                mine.AnyMapIsUnstable = false;
            }
            else
            {
                mine.PossibleMaps = UnionMaps(mine.PossibleMaps, theirs.PossibleMaps);
                mine.AnyMapIsUnstable |= theirs.AnyMapIsUnstable;
            }
            if (!ReferenceEquals(mine.Int32Alternative, theirs.Int32Alternative)) mine.Int32Alternative = null;
            if (!ReferenceEquals(mine.Float64Alternative, theirs.Float64Alternative)) mine.Float64Alternative = null;
            if (!ReferenceEquals(mine.NumberOrOddballFloat64Alternative, theirs.NumberOrOddballFloat64Alternative))
            {
                mine.NumberOrOddballFloat64Alternative = null;
            }
            if (!ReferenceEquals(mine.TaggedAlternative, theirs.TaggedAlternative)) mine.TaggedAlternative = null;
            if (!ReferenceEquals(mine.TruncatedInt32Alternative, theirs.TruncatedInt32Alternative)) mine.TruncatedInt32Alternative = null;
        }
        if (remove is not null) foreach (ValueNode n in remove) _infos.Remove(n);
    }

    static Map[] UnionMaps(Map[] a, Map[] b)
    {
        var result = new List<Map>(a);
        foreach (Map m in b) if (!result.Contains(m)) result.Add(m);
        return result.ToArray();
    }

    /// <summary>Forget everything (loop headers: the back edge is not known yet).</summary>
    public void Clear() => _infos.Clear();
}

/// <summary>InterpreterFrameState.</summary>
public sealed class InterpreterFrameState
{
    public readonly MaglevCompilationUnit Unit;
    public readonly ValueNode?[] Values;
    public KnownNodeAspects Known;

    public InterpreterFrameState(MaglevCompilationUnit unit)
    {
        Unit = unit;
        Values = new ValueNode?[SlotCount(unit)];
        Known = new KnownNodeAspects();
    }

    public static int SlotCount(MaglevCompilationUnit unit) => unit.ParameterCount + unit.RegisterCount + 2;
    public static int ContextSlot(MaglevCompilationUnit unit) => unit.ParameterCount;
    public static int AccumulatorSlot(MaglevCompilationUnit unit) => unit.ParameterCount + 1 + unit.RegisterCount;

    /// <summary>The frame slot of an interpreter register (parameters, the context, registers, the accumulator).</summary>
    public static int SlotOf(MaglevCompilationUnit unit, Register reg)
    {
        if (reg == Register.VirtualAccumulator()) return AccumulatorSlot(unit);
        if (reg == Register.CurrentContext()) return ContextSlot(unit);
        if (reg.IsParameter) return reg.ToParameterIndex();
        return unit.ParameterCount + 1 + reg.Index;
    }

    /// <summary>The register of a frame slot.</summary>
    public static Register RegisterOf(MaglevCompilationUnit unit, int slot)
    {
        if (slot < unit.ParameterCount) return Register.FromParameterIndex(slot);
        if (slot == unit.ParameterCount) return Register.CurrentContext();
        if (slot == AccumulatorSlot(unit)) return Register.VirtualAccumulator();
        return new Register(slot - unit.ParameterCount - 1);
    }

    public ValueNode Get(Register reg)
    {
        // The function_closure register is not part of the frame state: it
        // holds the same value throughout (V8 initialises it once in
        // BuildRegisterFrameInitialization and never merges it).
        if (reg.IsFunctionClosure) return Unit.Closure!;
        return Values[SlotOf(Unit, reg)] ?? throw new InvalidOperationException($"register {reg} has no value");
    }

    public ValueNode? TryGet(Register reg) => Values[SlotOf(Unit, reg)];

    public void Set(Register reg, ValueNode value) => Values[SlotOf(Unit, reg)] = value;

    public ValueNode Accumulator
    {
        get => Values[AccumulatorSlot(Unit)] ?? throw new InvalidOperationException("accumulator has no value");
        set => Values[AccumulatorSlot(Unit)] = value;
    }

    public ValueNode Context
    {
        get => Values[ContextSlot(Unit)]!;
        set => Values[ContextSlot(Unit)] = value;
    }

    /// <summary>Whether a frame slot is live per <paramref name="liveness"/> (parameters and the context always are).</summary>
    public static bool IsLive(MaglevCompilationUnit unit, BytecodeLivenessState liveness, int slot)
    {
        if (slot <= unit.ParameterCount) return true;
        if (slot == AccumulatorSlot(unit)) return liveness.AccumulatorIsLive();
        return liveness.RegisterIsLive(slot - unit.ParameterCount - 1);
    }

    /// <summary>
    /// The frame state values for a deopt frame (CompactInterpreterFrameState):
    /// parameters, the context, the live registers and, when live, the accumulator.
    /// </summary>
    public (Register Register, ValueNode Value)[] Snapshot(BytecodeLivenessState liveness, bool includeAccumulator)
    {
        var result = new List<(Register, ValueNode)>(Unit.ParameterCount + 4);
        int accumulatorSlot = AccumulatorSlot(Unit);
        for (int slot = 0; slot < Values.Length; slot++)
        {
            if (slot == accumulatorSlot && !includeAccumulator) continue;
            if (!IsLive(Unit, liveness, slot)) continue;
            ValueNode? value = Values[slot];
            if (value is null) continue;
            result.Add((RegisterOf(Unit, slot), value));
        }
        return result.ToArray();
    }

    public void CopyFrom(MergePointInterpreterFrameState merge)
    {
        Array.Copy(merge.Values, Values, Values.Length);
        Known = merge.Known!.Clone();
    }
}

/// <summary>MergePointInterpreterFrameState.</summary>
public sealed class MergePointInterpreterFrameState
{
    public readonly MaglevCompilationUnit Unit;
    public readonly int MergeOffset;
    public int PredecessorCount;
    public int PredecessorsSoFar;
    public readonly List<BasicBlock> Predecessors = [];
    public readonly ValueNode?[] Values;
    public readonly List<Phi> Phis = [];
    public KnownNodeAspects? Known;
    public readonly BytecodeLivenessState Liveness;
    public readonly LoopInfo? Loop;
    public BasicBlock? Block;
    /// <summary>The loop's back edge has been merged.</summary>
    public bool LoopClosed;
    /// <summary>A catch block's state (NewForCatchBlock): merged from the throwing nodes (MergeThrow).</summary>
    public bool IsExceptionHandler;
    /// <summary>The register holding the context of the try block (the handler table's range data).</summary>
    public Interpreter.Register CatchBlockContextRegister;

    public MergePointInterpreterFrameState(MaglevCompilationUnit unit, int mergeOffset, int predecessorCount,
        BytecodeLivenessState liveness, LoopInfo? loop)
    {
        Unit = unit;
        MergeOffset = mergeOffset;
        PredecessorCount = predecessorCount;
        Liveness = liveness;
        Loop = loop;
        Values = new ValueNode?[InterpreterFrameState.SlotCount(unit)];
    }

    public bool IsLoop => Loop is not null;

    /// <summary>
    /// Merge: the first predecessor's frame is copied; a later predecessor
    /// with a different value for a live slot turns the slot into a Phi
    /// (inputs tagged in their predecessor blocks).
    /// </summary>
    public void Merge(MaglevGraphBuilder builder, InterpreterFrameState unmerged, BasicBlock predecessor)
    {
        int index = PredecessorsSoFar;
        if (index == 0)
        {
            for (int slot = 0; slot < Values.Length; slot++)
            {
                Values[slot] = InterpreterFrameState.IsLive(Unit, Liveness, slot) ? unmerged.Values[slot] : null;
            }
            Known = unmerged.Known.Clone();
        }
        else
        {
            for (int slot = 0; slot < Values.Length; slot++)
            {
                if (!InterpreterFrameState.IsLive(Unit, Liveness, slot))
                {
                    Values[slot] = null;
                    continue;
                }
                ValueNode? existing = Values[slot];
                ValueNode? incoming = unmerged.Values[slot];
                if (existing is null || incoming is null)
                {
                    // A slot with no value on some path is dead there (e.g. a
                    // register only written in a branch that does not reach here).
                    Values[slot] = null;
                    continue;
                }
                if (existing is Phi phi && ReferenceEquals(phi.Block, null) && phi.MergeOffset == MergeOffset && Phis.Contains(phi))
                {
                    phi.InputList.Add(builder.GetTaggedValueForPhi(incoming, predecessor));
                    continue;
                }
                if (ReferenceEquals(existing, incoming)) continue;
                var newPhi = new Phi(InterpreterFrameState.RegisterOf(Unit, slot), MergeOffset)
                {
                    Id = builder.Graph.NewNodeId(),
                    Type = NodeType.kUnknown,
                };
                // The existing value came from all previous predecessors.
                for (int i = 0; i < index; i++)
                {
                    newPhi.InputList.Add(builder.GetTaggedValueForPhi(existing, Predecessors[i]));
                }
                newPhi.InputList.Add(builder.GetTaggedValueForPhi(incoming, predecessor));
                Phis.Add(newPhi);
                Values[slot] = newPhi;
            }
            Known!.Merge(unmerged.Known);
        }
        Predecessors.Add(predecessor);
        PredecessorsSoFar++;
    }

    /// <summary>
    /// NewForCatchBlock: the state of the catch block at
    /// <paramref name="handlerOffset"/>; the accumulator, when live, is the
    /// exception (an exception phi without inputs).
    /// </summary>
    public static MergePointInterpreterFrameState NewForCatchBlock(MaglevGraphBuilder builder, MaglevCompilationUnit unit,
        BytecodeLivenessState liveness, int handlerOffset, Interpreter.Register contextRegister)
    {
        var state = new MergePointInterpreterFrameState(unit, handlerOffset, 0, liveness, null)
        {
            IsExceptionHandler = true,
            CatchBlockContextRegister = contextRegister,
        };
        if (liveness.AccumulatorIsLive())
        {
            var phi = new Phi(Interpreter.Register.VirtualAccumulator(), handlerOffset)
            {
                Id = builder.Graph.NewNodeId(),
                IsExceptionPhi = true,
            };
            state.Phis.Add(phi);
            state.Values[InterpreterFrameState.AccumulatorSlot(unit)] = phi;
        }
        return state;
    }

    /// <summary>
    /// MergeThrow: merges the frame at a throwing node into the catch block
    /// (the parameters, the live registers, and the context from the catch
    /// block's context register). A value that differs between throws becomes
    /// an exception phi whose inputs are the values at each throw.
    /// </summary>
    public void MergeThrow(MaglevGraphBuilder builder, InterpreterFrameState frame)
    {
        int index = PredecessorsSoFar;
        int accumulatorSlot = InterpreterFrameState.AccumulatorSlot(Unit);
        int contextSlot = InterpreterFrameState.ContextSlot(Unit);
        int contextRegisterSlot = InterpreterFrameState.SlotOf(Unit, CatchBlockContextRegister);
        for (int slot = 0; slot < Values.Length; slot++)
        {
            if (slot == accumulatorSlot) continue;
            ValueNode? incoming;
            if (slot == contextSlot) incoming = frame.Values[contextRegisterSlot];
            else if (!InterpreterFrameState.IsLive(Unit, Liveness, slot)) continue;
            else incoming = frame.Values[slot];
            if (index == 0)
            {
                Values[slot] = incoming;
                continue;
            }
            ValueNode? existing = Values[slot];
            if (existing is null || incoming is null)
            {
                Values[slot] = null;
                continue;
            }
            if (existing is Phi { IsExceptionPhi: true } phi && phi.MergeOffset == MergeOffset && Phis.Contains(phi))
            {
                phi.InputList.Add(incoming);
                continue;
            }
            if (ReferenceEquals(existing, incoming)) continue;
            var newPhi = new Phi(InterpreterFrameState.RegisterOf(Unit, slot), MergeOffset)
            {
                Id = builder.Graph.NewNodeId(),
                Type = NodeType.kUnknown,
                IsExceptionPhi = true,
            };
            for (int i = 0; i < index; i++) newPhi.InputList.Add(existing);
            newPhi.InputList.Add(incoming);
            Phis.Add(newPhi);
            Values[slot] = newPhi;
        }
        if (Known is null) Known = frame.Known.Clone();
        else Known.Merge(frame.Known);
        PredecessorsSoFar++;
    }

    /// <summary>
    /// InitializeLoop: the loop header's frame from the entry predecessor,
    /// with a Phi for every live slot the loop assigns (and the context when
    /// the loop changes it).
    /// </summary>
    public void InitializeLoop(MaglevGraphBuilder builder, InterpreterFrameState unmerged, BasicBlock? predecessor,
        bool loopChangesContext)
    {
        LoopInfo loop = Loop!;
        int accumulatorSlot = InterpreterFrameState.AccumulatorSlot(Unit);
        int contextSlot = InterpreterFrameState.ContextSlot(Unit);
        for (int slot = 0; slot < Values.Length; slot++)
        {
            if (!InterpreterFrameState.IsLive(Unit, Liveness, slot))
            {
                Values[slot] = null;
                continue;
            }
            ValueNode? entry = unmerged.Values[slot];
            bool assigned;
            if (slot == accumulatorSlot) assigned = true;
            else if (slot == contextSlot) assigned = loopChangesContext;
            else if (slot < Unit.ParameterCount) assigned = loop.Assignments.ContainsParameter(slot);
            else assigned = loop.Assignments.ContainsLocal(slot - Unit.ParameterCount - 1);
            if (!assigned || entry is null)
            {
                Values[slot] = entry;
                continue;
            }
            var phi = new Phi(InterpreterFrameState.RegisterOf(Unit, slot), MergeOffset)
            {
                Id = builder.Graph.NewNodeId(),
                IsLoopPhi = true,
            };
            if (predecessor is not null) phi.InputList.Add(builder.GetTaggedValueForPhi(entry, predecessor));
            Phis.Add(phi);
            Values[slot] = phi;
        }
        // The back edge can change anything the loop body changes: V8 keeps
        // the stable facts (LoopEffects); V8Sharp starts the loop with the
        // entry's facts about values the loop does not assign, minus unstable maps.
        Known = unmerged.Known.Clone();
        Known.ClearUnstableMaps();
        foreach (Phi phi in Phis)
        {
            // Nothing is known about a loop phi until the back edge is merged.
            NodeInfo? info = Known.TryGetInfoFor(phi);
            if (info is not null) info.Type = NodeType.kUnknown;
        }
        if (predecessor is not null)
        {
            Predecessors.Add(predecessor);
            PredecessorsSoFar++;
        }
    }

    /// <summary>MergeLoop: the back edge's values become the loop phis' last inputs.</summary>
    public void MergeLoop(MaglevGraphBuilder builder, InterpreterFrameState loopEndState, BasicBlock predecessor)
    {
        foreach (Phi phi in Phis)
        {
            int slot = InterpreterFrameState.SlotOf(Unit, phi.Owner);
            ValueNode? incoming = loopEndState.Values[slot];
            if (incoming is null) throw new MaglevBailoutException("loop phi without back-edge value");
            phi.InputList.Add(builder.GetTaggedValueForPhi(incoming, predecessor));
        }
        Predecessors.Add(predecessor);
        PredecessorsSoFar++;
        LoopClosed = true;
    }
}

/// <summary>Thrown by the graph builder when it cannot (yet) compile a function.</summary>
public sealed class MaglevBailoutException(string reason) : Exception(reason)
{
}
