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
    /// The initial field values (an object literal's boilerplate fields), or
    /// null: all undefined.
    /// </summary>
    public JSValue[]? InitialFields;
    /// <summary>
    /// The allocation had an escaping use when the builder last looked
    /// (V8: InlinedAllocation::HasEscapingUses while building): its fields
    /// are no longer tracked.
    /// </summary>
    public bool EscapedDuringBuild;
    /// <summary>The innermost loop being built when the object was allocated (V8: loop_effects_->allocations).</summary>
    public object? Loop;
    public EscapeAnalysisResult Result;
    /// <summary>
    /// The allocations tracked stores put into this one's fields
    /// (allocations_escape_map: if this one escapes, they do).
    /// </summary>
    public List<InlinedAllocation>? StoredAllocations;

    /// <summary>Whether <paramref name="other"/> is reachable from this allocation through tracked stores.</summary>
    public bool Reaches(InlinedAllocation other)
    {
        if (ReferenceEquals(this, other)) return true;
        if (StoredAllocations is null) return false;
        foreach (InlinedAllocation a in StoredAllocations) if (a.Reaches(other)) return true;
        return false;
    }

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
    [ThreadStatic] static bool Trace;

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
        Trace = trace;

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
                if (node.Obj0 is CallBuiltinInfo { Elided: true }) continue;
                if (node.Unit is not { IsInline: true } unit || !MaglevCodeGenerator.NeedsFrame(node)) continue;
                if (trace && !pushed.Contains(unit))
                {
                    string what = node.Obj0 is CallBuiltinInfo b ? "CallBuiltin " + b.Name : node.Opcode.ToString();
                    Console.WriteLine($"[maglev] escape analysis: frame of {unit} pushed for n{node.Id} {what}");
                }
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
                foreach (ValueNode input in phi.Inputs) Escape(input, phi, trace);
            }
            foreach (Node node in block.Nodes)
            {
                // (An elided arguments object's parameter stores do not run.)
                if (node.Obj0 is CallBuiltinInfo { Elided: true }) continue;
                for (int i = 0; i < node.Inputs.Length; i++)
                {
                    if (node.Inputs[i] is not InlinedAllocation) continue;
                    // (The value of a tracked store escapes with its container.)
                    if (i <= 1 && node.TrackedStore) continue;
                    if (node.Opcode == Opcode.EnterInlinedFrame && !pushed.Contains((MaglevCompilationUnit)node.Obj0!)) continue;
                    Escape(node.Inputs[i], node, trace);
                }
                CheckDeoptFrame(node.EagerDeoptInfo);
                CheckDeoptFrame(node.LazyDeoptInfo);
            }
            ControlNode control = block.Control!;
            foreach (ValueNode input in control.Inputs) Escape(input, control, trace);
            CheckDeoptFrame(control.EagerDeoptInfo);
        }

        // EscapeAllocation: what an escaping allocation holds escapes.
        foreach (InlinedAllocation a in allocations)
        {
            if (a.Result == EscapeAnalysisResult.kEscaped) EscapeStored(a);
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

    /// <summary>
    /// V8Sharp: a map transition whose map is overwritten by the next
    /// transition of the same object before anything can observe the object
    /// (a deopt, a call, a throw) writes only its field (Int2 = 1): a
    /// constructor's this.x = ...; this.y = ... writes the map once. V8
    /// writes each map (its stores are cheap, without write barriers for
    /// objects of the current allocation block).
    /// </summary>
    public static void MarkOverwrittenMapStores(Graph graph)
    {
        foreach (BasicBlock block in graph.Blocks)
        {
            if (block.IsDead) continue;
            List<Node> nodes = block.Nodes;
            for (int i = 0; i < nodes.Count; i++)
            {
                Node store = nodes[i];
                if (store.Opcode != Opcode.StoreMapTransition) continue;
                for (int j = i + 1; j < nodes.Count; j++)
                {
                    Node next = nodes[j];
                    if (next.Opcode == Opcode.StoreMapTransition && ReferenceEquals(next.Inputs[0], store.Inputs[0]))
                    {
                        store.Int2 = 1;
                        break;
                    }
                    if ((next.Properties & ~(OpProperties.kCanRead | OpProperties.kCanAllocate)) != 0 || next.Opcode == Opcode.LoadMap) break;
                }
            }
        }
    }

    static void Escape(ValueNode value, NodeBase user, bool trace)
    {
        if (trace && value is InlinedAllocation { Result: not EscapeAnalysisResult.kEscaped } escaping)
        {
            string what = user.Obj0 is CallBuiltinInfo b ? "CallBuiltin " + b.Name : user.Opcode.ToString();
            Console.WriteLine($"[maglev] escape analysis: allocation n{escaping.Id} ({escaping.AllocatedMap.InstanceType}) escapes through n{user.Id} {what} ({user.Unit})");
        }
        Escape(value);
    }

    static void EscapeStored(InlinedAllocation a)
    {
        if (a.StoredAllocations is null) return;
        foreach (InlinedAllocation stored in a.StoredAllocations)
        {
            if (stored.Result == EscapeAnalysisResult.kEscaped) continue;
            stored.Result = EscapeAnalysisResult.kEscaped;
            EscapeStored(stored);
        }
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
                CheckCaptured(a, objects);
            }
        }
    }

    /// <summary>The virtual objects of an allocation a deopt captures and of the allocations in its fields.</summary>
    static void CheckCaptured(InlinedAllocation a, VirtualObjectList objects)
    {
        VirtualObject? vo = objects.Find(a);
        if (vo is null)
        {
            if (Trace) Console.WriteLine($"[maglev] escape analysis: allocation n{a.Id} escapes: a deopt frame without its virtual object");
            a.Result = EscapeAnalysisResult.kEscaped;
            return;
        }
        foreach (ValueNode slot in vo.Slots)
        {
            if (slot is InlinedAllocation nested && nested.Result != EscapeAnalysisResult.kEscaped) CheckCaptured(nested, objects);
        }
    }

    /// <summary>
    /// The values a deopt of <paramref name="top"/> reads: every frame's
    /// values and closure and, for an elided allocation, the slots of its
    /// virtual object (the captured object's fields) instead of the allocation.
    /// </summary>
    /// <summary>A deopt value, or the fields of an elided allocation (recursively).</summary>
    public static void UseCaptured(ValueNode value, VirtualObjectList objects, Action<ValueNode> use)
    {
        if (value is InlinedAllocation { IsElided: true } a)
        {
            foreach (ValueNode slot in objects.Find(a)!.Slots) UseCaptured(slot, objects, use);
            return;
        }
        use(value);
    }

    public static void ForEachDeoptValue(DeoptFrame? top, Action<ValueNode> use)
    {
        if (top is null) return;
        VirtualObjectList objects = top.VirtualObjects;
        for (DeoptFrame? f = top; f is not null; f = f.Parent)
        {
            var frame = (InterpretedDeoptFrame)f;
            foreach ((Register _, ValueNode value) in frame.Values)
            {
                UseCaptured(value, objects, use);
            }
            use(frame.Closure);
        }
    }
}
