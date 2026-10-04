// Port of the inlined allocation and object tracking parts of
// src/maglev/maglev-graph-builder.cc and maglev-reducer-inl.h:
// BuildInlinedAllocation / CreateJSObject / CreateJSConstructor (an object of
// a known initial map allocated by the code), CanTrackObjectChanges,
// TryBuildStoreTaggedFieldToAllocation and
// TryBuildLoadTaggedFieldFromAllocation (--maglev-object-tracking: the
// fields of an allocation that has not escaped are SSA values), and the
// bookkeeping the escape analysis (MaglevEscapeAnalysis) relies on.
using V8Sharp.Interpreter;

namespace V8Sharp.Maglev;

public sealed partial class MaglevGraphBuilder
{
    /// <summary>The innermost loop being built (null outside loops): allocations are tracked only in their own loop iteration.</summary>
    object? CurrentLoop => _info.ActiveLoops.Count > 0 ? _info.ActiveLoops[^1] : null;

    /// <summary>
    /// BuildInlinedAllocation(CreateJSObject(map)): an ordinary object of
    /// <paramref name="map"/> (a constructor's initial map, or the Object
    /// function's), allocated by the code, or null when the map does not
    /// allow it (slack tracking in progress, dictionary or non-ordinary maps).
    /// The allocation's fields start undefined and are tracked in its
    /// VirtualObject while it has not escaped.
    /// </summary>
    InlinedAllocation? TryBuildInlinedAllocation(Map map)
    {
        if (!Flags.inline_new) return null;
        if (map.InstanceType != InstanceType.JSObjectType || map.IsDictionaryMap || !map.HasFastElements || map.IsDeprecated) return null;
        // V8 depends on the instance size prediction, finishing slack tracking
        // (InitialMapInstanceSizePredictionDependency); V8Sharp leaves objects
        // allocated during slack tracking to FastNewObject, which steps it.
        if (map.IsInobjectSlackTrackingInProgress()) return null;
        int count = map.GetInObjectProperties();
        if (count > 0 && !map.HasInObjectSlots) return null;
        _info.InstanceSizePredictions.Add((map, count));
        var allocation = new InlinedAllocation(map, count) { Loop = CurrentLoop };
        AddNewNode(allocation);
        var slots = new ValueNode[count];
        ValueNode undefined = GetRootConstant(RootIndex.kUndefinedValue);
        Array.Fill(slots, undefined);
        _frame.Known.VirtualObjects = _frame.Known.VirtualObjects.With(new VirtualObject(allocation, map, slots));
        RecordKnownMaps(allocation, [map]);
        return allocation;
    }

    /// <summary>
    /// CanTrackObjectChanges: the virtual object of <paramref name="receiver"/>
    /// when it is an allocation whose fields can be read from (or, for
    /// <paramref name="store"/>, recorded in) its virtual object: it has not
    /// escaped, it was allocated in the current loop iteration (no loop phis
    /// in virtual objects), and stores are not in a try block (a catch block
    /// could deoptimize with either version).
    /// </summary>
    VirtualObject? TryGetTrackedObject(ValueNode receiver, bool store)
    {
        if (!Flags.maglev_object_tracking || receiver is not InlinedAllocation allocation) return null;
        if (allocation.EscapedDuringBuild || !ReferenceEquals(allocation.Loop, CurrentLoop)) return null;
        if (store && (IsInsideTryBlock || CallerInsideTryBlock() is not null)) return null;
        return _frame.Known.VirtualObjects.Find(allocation);
    }

    /// <summary>
    /// TryBuildLoadTaggedFieldFromAllocation: the value of in-object field
    /// <paramref name="storageIndex"/> of a tracked allocation, or null.
    /// </summary>
    ValueNode? TryBuildLoadFieldFromAllocation(ValueNode holder, int storageIndex)
    {
        if (TryGetTrackedObject(holder, store: false) is not { } vo || storageIndex >= vo.Slots.Length) return null;
        ValueNode value = vo.Slots[storageIndex];
        if (_info.IsTracing) Console.WriteLine($"[maglev] reusing field {storageIndex} of virtual object n{vo.Allocation.Id}: n{value.Id}");
        return value;
    }

    /// <summary>
    /// TryBuildStoreTaggedFieldToAllocation: records the store
    /// <paramref name="store"/> (of <paramref name="value"/> into in-object
    /// field <paramref name="storageIndex"/>, with the map
    /// <paramref name="newMap"/> after it) in the receiver's virtual object.
    /// The store stays in the graph (it runs if the allocation escapes) as a
    /// non-escaping use. False when the receiver is not tracked.
    /// </summary>
    bool TryRecordStoreToAllocation(Node store, ValueNode receiver, int storageIndex, ValueNode value, Map? newMap)
    {
        if (TryGetTrackedObject(receiver, store: true) is not { } vo || storageIndex >= vo.Slots.Length) return false;
        // This avoids loops in the object graph (V8 does not track a store of an allocation either).
        if (value is InlinedAllocation) return false;
        store.TrackedStore = true;
        _frame.Known.VirtualObjects = _frame.Known.VirtualObjects.With(vo.WithSlot(storageIndex, value, newMap ?? vo.Map));
        if (_info.IsTracing) Console.WriteLine($"[maglev] setting field {storageIndex} of virtual object n{vo.Allocation.Id}: n{value.Id}");
        return true;
    }

    /// <summary>
    /// AddNewNode's use accounting for the escape analysis (V8 counts an
    /// allocation's uses as they are added; HasEscapingUses then stops the
    /// tracking of its fields): a node that takes an allocation as an input
    /// other than as the object of a tracked store escapes it, and a node
    /// that needs the frame of an inlined function escapes the receiver and
    /// arguments the frame push stores.
    /// </summary>
    void NoteAllocationUses(Node node)
    {
        ValueNode[] inputs = node.Inputs;
        if (node.Opcode != Opcode.EnterInlinedFrame)
        {
            for (int i = node.TrackedStore ? 1 : 0; i < inputs.Length; i++) EscapeDuringBuild(inputs[i]);
        }
        if (_unit.IsInline && MaglevCodeGenerator.NeedsFrame(node))
        {
            for (MaglevCompilationUnit? u = _unit; u is { IsInline: true }; u = u.Caller)
            {
                if (u.EntryNode is { } entry)
                {
                    foreach (ValueNode input in entry.Inputs) EscapeDuringBuild(input);
                }
            }
        }
    }

    /// <summary>A use that lets the object escape (a phi input, a call argument ...): its fields are no longer tracked.</summary>
    internal static void EscapeDuringBuild(ValueNode value)
    {
        if (value is InlinedAllocation allocation) allocation.EscapedDuringBuild = true;
    }
}
