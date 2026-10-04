// Port of InlinedAllocation::GenerateCode / AllocationBlock::GenerateCode
// (maglev-ir.cc) and of the captured objects of the deopt translation
// (MaglevCodeGenerator's BuildVirtualObject, maglev-code-generator.cc).
//
// V8 bumps the young generation's allocation top for the whole allocation
// block and writes the header and the fields; V8Sharp constructs the .NET
// object of the map's in-object slot class directly (the runtime's
// allocation fast path, RhpNewFast): the header is the map and the empty
// elements, and the in-object slots start as undefined (zeroed memory).
using System.Reflection;
using System.Reflection.Emit;

namespace V8Sharp.Maglev;

internal sealed partial class MaglevCodeGenerator
{
    static readonly FieldInfo s_fixedArrayEmpty = typeof(FixedArray).GetField(nameof(FixedArray.Empty))!;

    /// <summary>The constructor (Map, FixedArray emptyElements) of the class with room for <paramref name="inObjectCount"/> slots.</summary>
    internal static ConstructorInfo InObjectClassConstructor(int inObjectCount)
    {
        Type type = inObjectCount switch
        {
            0 => typeof(JSObject),
            1 => typeof(JSObjectInObject1),
            2 => typeof(JSObjectInObject2),
            3 => typeof(JSObjectInObject3),
            4 => typeof(JSObjectInObject4),
            <= 8 => typeof(JSObjectInObject8),
            <= 12 => typeof(JSObjectInObject12),
            <= 16 => typeof(JSObjectInObject16),
            <= 32 => typeof(JSObjectInObject32),
            <= 64 => typeof(JSObjectInObject64),
            <= 128 => typeof(JSObjectInObject128),
            _ => typeof(JSObjectInObject256),
        };
        return type.GetConstructor(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, [typeof(Map), typeof(FixedArray)])!;
    }

    /// <summary>InlinedAllocation: new JSObjectInObjectN(map, FixedArray.Empty).</summary>
    void EmitInlinedAllocation(InlinedAllocation node)
    {
        if (node.InitialFields is { } fields)
        {
            // An object literal: the object with the boilerplate's fields.
            LoadConstantObject(node.AllocatedMap, typeof(Map));
            LoadConstantObject(fields, typeof(JSValue[]));
            Call(nameof(MaglevBuiltins.AllocateObjectLiteral));
            Store(node);
            return;
        }
        LoadConstantObject(node.AllocatedMap, typeof(Map));
        _il.Emit(OpCodes.Ldsfld, s_fixedArrayEmpty);
        _il.Emit(OpCodes.Newobj, InObjectClassConstructor(node.InObjectCount));
        // JSValue.FromObject
        _il.Emit(OpCodes.Call, s_jsValueFromObject);
        Store(node);
    }


    static int[] NewCapturedRefs(int count)
    {
        var refs = new int[count];
        Array.Fill(refs, -1);
        return refs;
    }

    /// <summary>
    /// The index of the captured object of <paramref name="allocation"/> in
    /// the deopt point being built (one per allocation, however many registers
    /// hold it: V8's duplicated objects): its map and fields from the virtual
    /// object of the point, the fields' values spilled like the frames'.
    /// </summary>
    int CapturedObjectIndex(InlinedAllocation allocation, DeoptInfo info, ref List<(InlinedAllocation, CapturedObjectData)>? objects,
        List<ValueNode> spill)
    {
        objects ??= [];
        for (int i = 0; i < objects.Count; i++)
        {
            if (ReferenceEquals(objects[i].Item1, allocation)) return i;
        }
        VirtualObject vo = info.TopFrame.VirtualObjects.Find(allocation)
            ?? throw new InvalidOperationException($"no virtual object for elided allocation n{allocation.Id}");
        var data = new CapturedObjectData
        {
            AllocatedMap = allocation.AllocatedMap,
            Map = vo.Map,
            FieldSlots = new int[vo.Slots.Length],
            FieldConstants = new JSValue[vo.Slots.Length],
        };
        for (int f = 0; f < vo.Slots.Length; f++)
        {
            ValueNode value = vo.Slots[f];
            if (value.IsConstant)
            {
                data.FieldSlots[f] = -1;
                data.FieldConstants[f] = value.ConstantValue();
                continue;
            }
            data.FieldSlots[f] = SpillSlot(value);
            spill.Add(value);
        }
        objects.Add((allocation, data));
        return objects.Count - 1;
    }
}
