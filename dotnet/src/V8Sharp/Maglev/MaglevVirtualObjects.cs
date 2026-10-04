// Port of Maglev's inlined allocations and escape analysis:
//
//   InlinedAllocation, VirtualObject       maglev-ir.h: an object the code
//                                          allocates itself, and the graph
//                                          builder's model of its fields
//   VirtualObjectList                      maglev-interpreter-frame-state.h:
//                                          the versions of the virtual objects
//                                          on the current path (captured by
//                                          deopt frames)
//   MaglevEscapeAnalysis.Run               AnyUseMarkingProcessor::
//                                          RunEscapeAnalysis and
//                                          DeadNodeSweepingProcessor's removal
//                                          of elided allocations and their
//                                          stores (maglev-post-hoc-
//                                          optimizations-processors.h)
//
// As in V8, the graph builder records the stores into an allocation that
// has not escaped yet in its VirtualObject (TryBuildStoreTaggedFieldToAllocation)
// and answers loads from it (TryBuildLoadTaggedFieldFromAllocation); deopt
// frames capture the virtual objects of their point. After the graph is
// built, an allocation whose only uses are those stores, deopt frames and
// the frames of inlined calls that are never pushed is elided: the
// allocation and its stores are removed, and a deopt exit materializes it
// from the virtual object of its point (the translation's captured object,
// Deoptimizer.MaterializeCapturedObjects).
//
// Deviation: V8's VirtualObject is a slot per tagged word of the object
// (header included) and V8 never changes its map, so a map transition
// (StoreMap) escapes the object; V8Sharp's records the in-object fields and
// the current map, so a constructor's this.x = ... transitions keep the
// object elidable (V8's Turbofan escape analysis does the same).
using V8Sharp.Interpreter;

namespace V8Sharp.Maglev;

/// <summary>EscapeAnalysisResult.</summary>
public enum EscapeAnalysisResult : byte
{
    kUnknown,
    kElided,
    kEscaped,
}

/// <summary>
/// InlinedAllocation: an ordinary JSObject of <see cref="AllocatedMap"/>
/// (fast mode, empty elements, its in-object fields undefined), allocated by
/// the code (the IL constructs the object of its in-object slot class).
/// </summary>
public sealed class InlinedAllocation : ValueNode
{
    public InlinedAllocation(Map map, int inObjectCount) : base(Opcode.InlinedAllocation, ValueRepresentation.kTagged)
    {
        AllocatedMap = map;
        InObjectCount = inObjectCount;
        Obj0 = map;
        Type = NodeTypes.ForMap(map);
        Properties = OpProperties.kCanAllocate | OpProperties.kNotIdempotent;
    }

    /// <summary>The map the object is allocated with (it picks the object's in-object slot class).</summary>
    public readonly Map AllocatedMap;
    /// <summary>The in-object property count of the allocated map (the fields a VirtualObject models).</summary>
    public readonly int InObjectCount;
    /// <summary>
    /// The allocation had an escaping use when the builder last looked
    /// (V8: InlinedAllocation::HasEscapingUses while building): its fields
    /// are no longer tracked.
    /// </summary>
    public bool EscapedDuringBuild;
    /// <summary>The innermost loop being built when the object was allocated (V8: loop_effects_->allocations).</summary>
    public object? Loop;
    public EscapeAnalysisResult Result;

    public bool IsElided => Result == EscapeAnalysisResult.kElided;

    public override string ToString() => $"n{Id}: InlinedAllocation";
}

/// <summary>
/// VirtualObject: the fields of an InlinedAllocation at a point of the graph
/// (an immutable version: a store makes a new one). Slot i is in-object
/// field i (FieldIndex.StorageIndex i).
/// </summary>
public sealed class VirtualObject(InlinedAllocation allocation, Map map, ValueNode[] slots)
{
    public readonly InlinedAllocation Allocation = allocation;
    /// <summary>The object's current map (the allocated map after the transitions recorded so far).</summary>
    public readonly Map Map = map;
    public readonly ValueNode[] Slots = slots;

    public VirtualObject WithSlot(int index, ValueNode value, Map map)
    {
        var slots = (ValueNode[])Slots.Clone();
        slots[index] = value;
        return new VirtualObject(Allocation, map, slots);
    }
}

/// <summary>
/// VirtualObjectList: the current version of each tracked allocation on a
/// path. Immutable, so deopt frames capture it by reference.
/// </summary>
public sealed class VirtualObjectList
{
    public static readonly VirtualObjectList Empty = new([]);

    readonly VirtualObject[] _objects;

    VirtualObjectList(VirtualObject[] objects) => _objects = objects;

    public int Count => _objects.Length;

    public VirtualObject this[int index] => _objects[index];

    /// <summary>FindAllocatedWith.</summary>
    public VirtualObject? Find(InlinedAllocation allocation)
    {
        foreach (VirtualObject vo in _objects)
        {
            if (ReferenceEquals(vo.Allocation, allocation)) return vo;
        }
        return null;
    }

    /// <summary>The list with <paramref name="vo"/> as its allocation's current version.</summary>
    public VirtualObjectList With(VirtualObject vo)
    {
        for (int i = 0; i < _objects.Length; i++)
        {
            if (!ReferenceEquals(_objects[i].Allocation, vo.Allocation)) continue;
            var copy = (VirtualObject[])_objects.Clone();
            copy[i] = vo;
            return new VirtualObjectList(copy);
        }
        var grown = new VirtualObject[_objects.Length + 1];
        _objects.CopyTo(grown, 0);
        grown[^1] = vo;
        return new VirtualObjectList(grown);
    }

    /// <summary>
    /// The versions both paths have (KnownNodeAspects::Merge). V8 merges
    /// differing versions slot by slot with phis; V8Sharp drops them, and a
    /// deopt frame after the merge that needs the object then makes it escape.
    /// </summary>
    public VirtualObjectList Intersect(VirtualObjectList other)
    {
        if (ReferenceEquals(this, other)) return this;
        List<VirtualObject>? kept = null;
        for (int i = 0; i < _objects.Length; i++)
        {
            VirtualObject vo = _objects[i];
            bool keep = Array.IndexOf(other._objects, vo) >= 0;
            if (keep && kept is null) continue;
            if (!keep && kept is null)
            {
                kept = new List<VirtualObject>(_objects.Length);
                for (int j = 0; j < i; j++) kept.Add(_objects[j]);
                continue;
            }
            if (keep) kept!.Add(vo);
        }
        return kept is null ? this : kept.Count == 0 ? Empty : new VirtualObjectList(kept.ToArray());
    }
}

/// <summary>
/// The escape analysis (AnyUseMarkingProcessor::RunEscapeAnalysis) and the
/// removal of the elided allocations and of the stores into them
/// (DeadNodeSweepingProcessor).
/// </summary>
internal static class MaglevEscapeAnalysis
{
    /// <summary>
    /// Decides which allocations escape. An allocation is elided when every
    /// use is a tracked store into it, a deopt frame whose virtual objects
    /// have it, or the frame of an inlined call that is never pushed. Returns
    /// whether anything was elided (the graph changed).
    /// </summary>
    public static bool Run(Graph graph, bool enabled, bool trace)
    {
        List<InlinedAllocation>? allocations = null;
        foreach (BasicBlock block in graph.Blocks)
        {
            if (block.IsDead) continue;
            foreach (Node node in block.Nodes)
            {
                if (node is InlinedAllocation a) (allocations ??= []).Add(a);
            }
        }
        if (allocations is null) return false;
        if (!enabled)
        {
            foreach (InlinedAllocation a in allocations) a.Result = EscapeAnalysisResult.kEscaped;
            return false;
        }
        foreach (InlinedAllocation a in allocations) a.Result = EscapeAnalysisResult.kUnknown;

        // The inlined frames the code pushes: a frame is pushed (lazily) before
        // the first node of its function, or of a function inlined into it,
        // that needs it, and on entry when it is eager.
        var pushed = new HashSet<MaglevCompilationUnit>(ReferenceEqualityComparer.Instance);
        foreach (BasicBlock block in graph.Blocks)
        {
            if (block.IsDead) continue;
            foreach (Node node in block.Nodes)
            {
                if (node.Opcode == Opcode.EnterInlinedFrame && node.Obj0 is MaglevCompilationUnit { EagerFrame: true } eager) pushed.Add(eager);
                if (node.Unit is not { IsInline: true } unit || !MaglevCodeGenerator.NeedsFrame(node)) continue;
                for (MaglevCompilationUnit? u = unit; u is { IsInline: true } && pushed.Add(u); u = u.Caller)
                {
                }
            }
        }

        foreach (BasicBlock block in graph.Blocks)
        {
            if (block.IsDead) continue;
            foreach (Phi phi in block.Phis)
            {
                foreach (ValueNode input in phi.Inputs) Escape(input);
            }
            foreach (Node node in block.Nodes)
            {
                for (int i = 0; i < node.Inputs.Length; i++)
                {
                    if (node.Inputs[i] is not InlinedAllocation) continue;
                    if (i == 0 && node.TrackedStore) continue;
                    if (node.Opcode == Opcode.EnterInlinedFrame && !pushed.Contains((MaglevCompilationUnit)node.Obj0!)) continue;
                    Escape(node.Inputs[i]);
                }
                CheckDeoptFrame(node.EagerDeoptInfo);
                CheckDeoptFrame(node.LazyDeoptInfo);
            }
            ControlNode control = block.Control!;
            foreach (ValueNode input in control.Inputs) Escape(input);
            CheckDeoptFrame(control.EagerDeoptInfo);
        }

        bool elided = false;
        foreach (InlinedAllocation a in allocations)
        {
            if (a.Result == EscapeAnalysisResult.kUnknown)
            {
                a.Result = EscapeAnalysisResult.kElided;
                elided = true;
                if (trace) Console.WriteLine($"[maglev] escape analysis: eliding allocation n{a.Id}");
            }
        }
        if (!elided) return false;

        // Remove the elided allocations and the stores into them.
        foreach (BasicBlock block in graph.Blocks)
        {
            if (block.IsDead) continue;
            block.Nodes.RemoveAll(static node =>
                node is InlinedAllocation { IsElided: true } ||
                node.TrackedStore && node.Inputs[0] is InlinedAllocation { IsElided: true });
        }
        return true;
    }

    static void Escape(ValueNode value)
    {
        if (value is InlinedAllocation a) a.Result = EscapeAnalysisResult.kEscaped;
    }

    /// <summary>
    /// A deopt frame can materialize an allocation only from the virtual
    /// objects of its point; one it does not have there escapes.
    /// </summary>
    static void CheckDeoptFrame(DeoptInfo? info)
    {
        if (info is null) return;
        VirtualObjectList objects = info.TopFrame.VirtualObjects;
        for (DeoptFrame? f = info.TopFrame; f is not null; f = f.Parent)
        {
            var frame = (InterpretedDeoptFrame)f;
            foreach ((Register _, ValueNode value) in frame.Values)
            {
                if (value is not InlinedAllocation a || a.Result == EscapeAnalysisResult.kEscaped) continue;
                VirtualObject? vo = objects.Find(a);
                if (vo is null)
                {
                    a.Result = EscapeAnalysisResult.kEscaped;
                    continue;
                }
                // The builder records no allocation in a virtual object's slot.
                foreach (ValueNode slot in vo.Slots) Escape(slot);
            }
        }
    }

    /// <summary>
    /// The values a deopt of <paramref name="top"/> reads: every frame's
    /// values and closure and, for an elided allocation, the slots of its
    /// virtual object (the captured object's fields) instead of the allocation.
    /// </summary>
    public static void ForEachDeoptValue(DeoptFrame? top, Action<ValueNode> use)
    {
        if (top is null) return;
        VirtualObjectList objects = top.VirtualObjects;
        for (DeoptFrame? f = top; f is not null; f = f.Parent)
        {
            var frame = (InterpretedDeoptFrame)f;
            foreach ((Register _, ValueNode value) in frame.Values)
            {
                if (value is InlinedAllocation { IsElided: true } a)
                {
                    foreach (ValueNode slot in objects.Find(a)!.Slots) use(slot);
                    continue;
                }
                use(value);
            }
            use(frame.Closure);
        }
    }
}
