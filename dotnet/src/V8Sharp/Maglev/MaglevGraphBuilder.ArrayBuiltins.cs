// Port of the Array.prototype.push / pop reductions of
// src/maglev/maglev-graph-builder.cc (TryReduceArrayPrototypePush,
// TryReduceArrayPrototypePop, CanInlineArrayResizingBuiltin,
// BuildJSArrayBuiltinMapSwitchOnElementsKind) and of
// src/compiler/heap-refs.cc SupportsFastArrayResize: with the receiver's
// maps known (from the map check of the `push`/`pop` load) and the
// NoElements protector intact, the call becomes graph nodes, dispatched on
// the elements kind.
using V8Sharp.Deoptimizer;

namespace V8Sharp.Maglev;

public sealed partial class MaglevGraphBuilder
{
    /// <summary>SupportsFastArrayResize: a fast-elements, extensible JSArray map with the initial prototypes and a writable length.</summary>
    bool SupportsFastArrayResize(Map map)
    {
        if (map.InstanceType != InstanceType.JSArrayType || !ElementsKinds.IsFastElementsKind(map.ElementsKind)) return false;
        // SupportsFastArrayIteration: the prototype is the initial Array.prototype
        // (IsArrayOrObjectPrototype; the NoElements protector covers its chain).
        NativeContext nativeContext = Isolate.NativeContext;
        if (!ReferenceEquals(map.Prototype, nativeContext.InitialArrayPrototype)) return false;
        return map.IsExtensible && !map.IsDictionaryMap && !JSArray.MayHaveReadOnlyLength(map);
    }

    /// <summary>
    /// CanInlineArrayResizingBuiltin: the receiver's known maps grouped by
    /// <paramref name="kindIndex"/>; null when a map does not support fast resizing.
    /// </summary>
    List<Map>[]? GroupMapsForArrayResizing(Map[] maps, int groups, Func<ElementsKind, int> kindIndex, bool isLoading)
    {
        var result = new List<Map>[groups];
        foreach (Map map in maps)
        {
            if (!SupportsFastArrayResize(map)) return null;
            if (isLoading && map.ElementsKind == ElementsKind.HOLEY_DOUBLE_ELEMENTS) return null;
            (result[kindIndex(map.ElementsKind)] ??= []).Add(map);
        }
        return result;
    }

    /// <summary>
    /// BuildJSArrayBuiltinMapSwitchOnElementsKind: <paramref name="build"/> for
    /// each group of maps (by elements kind), dispatched on the receiver's map.
    /// </summary>
    ValueNode? BuildJSArrayBuiltinMapSwitchOnElementsKind(ValueNode receiver, List<Map>[] groups, Func<int, ValueNode?> build,
        bool hasResult)
    {
        var cases = new List<(Map[] Maps, Func<ValueNode?> Build)>();
        for (int g = 0; g < groups.Length; g++)
        {
            if (groups[g] is not { } maps) continue;
            int group = g;
            cases.Add((maps.ToArray(), () => build(group)));
        }
        if (cases.Count == 1)
        {
            // The maps are known: no dispatch.
            return build(Array.FindIndex(groups, static m => m is not null));
        }
        return BuildPolymorphicAccess(receiver, cases, hasResult);
    }

    /// <summary>TryReduceArrayPrototypePush.</summary>
    ValueNode? TryReduceArrayPrototypePush(ValueNode receiver, ValueNode[] args)
    {
        if (args.Length == 0) return null;
        if (receiver.Representation != ValueRepresentation.kTagged) return null;
        // MapInference: the maps of the receiver must be known.
        Map[]? maps = KnownMaps(receiver);
        if (maps is null) return null;
        if (maps.Length == 0) EmitUnconditionalDeoptAndAbort(DeoptimizeReason.kWrongMap);
        // Group maps by elements kind, ignoring packedness (it does not matter for push).
        static int KindIndex(ElementsKind kind) => (int)kind / 2;
        List<Map>[]? groups = GroupMapsForArrayResizing(maps, 3, KindIndex, isLoading: false);
        if (groups is null) return null;
        if (!_info.DependOnProtector(Protectors.IsNoElementsIntact(Isolate), "NoElements")) return null;

        ValueNode oldLength = GetInt32(AddNewNode(new ValueNode(Opcode.LoadJSArrayLength, ValueRepresentation.kTagged)
        {
            Inputs = [receiver],
            Type = NodeType.kSmi,
            Properties = OpProperties.kCanRead,
        }));
        ValueNode newLength = AddNewNode(new ValueNode(Opcode.Int32AddWithOverflow, ValueRepresentation.kInt32)
        {
            Inputs = [oldLength, GetInt32Constant(args.Length)],
            Properties = OpProperties.kEagerDeopt,
            Type = NodeType.kSmi,
        }, DeoptimizeReason.kOverflow);

        ValueNode? BuildArrayPush(int group)
        {
            ElementsKind kind = (ElementsKind)(group * 2);
            // First make sure that all inputs have the expected type, so that a
            // deopt happens before anything is written to the array.
            var values = new ValueNode[args.Length];
            for (int i = 0; i < args.Length; i++)
            {
                if (ElementsKinds.IsSmiElementsKind(kind))
                {
                    BuildCheckSmi(args[i]);
                    values[i] = GetTaggedValue(args[i]);
                }
                else if (ElementsKinds.IsDoubleElementsKind(kind))
                {
                    // ConvertForStoring: a number (deopts otherwise).
                    values[i] = GetFloat64(args[i]);
                }
                else
                {
                    values[i] = GetTaggedValue(args[i]);
                }
            }
            // All inputs have the right type: extend the array (and its length), then write.
            ValueNode elements = AddNewNode(new ValueNode(Opcode.MaybeGrowFastElements, ValueRepresentation.kTagged)
            {
                Inputs = [receiver, oldLength],
                Int0 = 1,
                Int1 = (int)kind,
                // Push mode: append this many elements at the old length.
                Int2 = args.Length,
                Properties = OpProperties.kEagerDeopt | OpProperties.kCanAllocate | OpProperties.kCanWrite | OpProperties.kNotIdempotent,
                Type = NodeType.kOtherHeapObject,
            }, DeoptimizeReason.kCouldNotGrowElements);
            for (int i = 0; i < values.Length; i++)
            {
                ValueNode index = i == 0 ? oldLength : AddNewNode(new ValueNode(Opcode.Int32AddWithOverflow, ValueRepresentation.kInt32)
                {
                    Inputs = [oldLength, GetInt32Constant(i)],
                    Properties = OpProperties.kEagerDeopt,
                    Type = NodeType.kSmi,
                }, DeoptimizeReason.kOverflow);
                AddNewNode(new Node(ElementsKinds.IsDoubleElementsKind(kind) ? Opcode.StoreFixedDoubleArrayElement : Opcode.StoreFixedArrayElement)
                {
                    Inputs = [elements, index, values[i]],
                    Properties = OpProperties.kCanWrite | OpProperties.kNotIdempotent,
                });
            }
            return null;
        }

        BuildJSArrayBuiltinMapSwitchOnElementsKind(receiver, groups, BuildArrayPush, hasResult: false);
        return newLength;
    }

    /// <summary>
    /// TryReduceArrayPrototypePop: the pop of a fast array (undefined for an
    /// empty one), the hole stored in its place.
    /// </summary>
    /// <remarks>
    /// Deviation (structural): V8 builds the length test, the COW check, the
    /// element load and the hole store as nodes per elements kind; V8Sharp
    /// calls one Maglev builtin for them (MaglevBuiltins.ArrayPop), after the
    /// same map inference and protector dependency.
    /// </remarks>
    ValueNode? TryReduceArrayPrototypePop(ValueNode receiver)
    {
        if (receiver.Representation != ValueRepresentation.kTagged) return null;
        Map[]? maps = KnownMaps(receiver);
        if (maps is null) return null;
        if (maps.Length == 0) EmitUnconditionalDeoptAndAbort(DeoptimizeReason.kWrongMap);
        if (GroupMapsForArrayResizing(maps, 1, static _ => 0, isLoading: true) is null) return null;
        if (!_info.DependOnProtector(Protectors.IsNoElementsIntact(Isolate), "NoElements")) return null;
        return CallMaglev("ArrayPop", [receiver], [BuiltinArg.Isolate, BuiltinArg.In(0)],
            OpProperties.kCanWrite | OpProperties.kCanAllocate | OpProperties.kNotIdempotent);
    }
}
