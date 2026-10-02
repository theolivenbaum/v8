// Port of the property access parts of src/maglev/maglev-graph-builder.cc:
// TryBuildNamedAccess / TryBuildPropertyAccess (PropertyAccessInfo from the
// IC feedback, map checks, field loads and stores, map transitions,
// constants from the prototype chain), BuildPolymorphicAccess (a map
// dispatch whose cases join with a Phi), and the element accesses of
// TryBuildElementAccess (fast elements kinds: bounds checks, holes, growing
// stores).
//
// Deviation: V8 computes PropertyAccessInfos from the feedback maps through
// the heap broker (AccessInfoFactory); V8Sharp reads them from the IC
// handlers the feedback holds (the same information: field index,
// representation, holder, constant, transition map). Prototype-chain
// validity is checked at run time through the handler's validity cell
// (CheckValidityCell) instead of a compile-time dependency on stable
// prototype maps.
using V8Sharp.Deoptimizer;
using V8Sharp.IC;
using V8Sharp.Interpreter;

namespace V8Sharp.Maglev;

public sealed partial class MaglevGraphBuilder
{
    /// <summary>A PropertyAccessInfo for one receiver map.</summary>
    sealed class PropertyAccessInfo
    {
        public enum Kind { DataField, DataConstant, NotFound, ArrayLength, StringLength, FieldStore, ConstFieldStore, TransitionStore }
        public Kind AccessKind;
        public Map Map = null!;
        public int StorageIndex = -1;
        /// <summary>The holder of a prototype-chain field (null: the receiver).</summary>
        public JSReceiver? Holder;
        public JSValue Constant;
        public Cell? ValidityCell;
        public Representation Representation = Representation.Tagged;
        public Map? FieldTypeClass;
        public Map? TransitionMap;
    }

    /// <summary>The (map, handler) pairs of a property IC (FeedbackNexus::ExtractMapsAndHandlers), without deprecated maps.</summary>
    List<(Map Map, JSValue Handler)>? MapsAndHandlers(int slot)
    {
        var nexus = new FeedbackNexus(Isolate, _unit.Feedback, slot);
        InlineCacheState state = nexus.IcState();
        if (state is not (InlineCacheState.MONOMORPHIC or InlineCacheState.POLYMORPHIC)) return null;
        var result = new List<(Map, JSValue)>();
        nexus.ExtractMapsAndHandlers(result);
        result.RemoveAll(static e => e.Item1.IsDeprecated);
        return result.Count == 0 ? null : result;
    }

    bool IsUninitializedIC(int slot) => new FeedbackNexus(Isolate, _unit.Feedback, slot).IcState() == InlineCacheState.UNINITIALIZED;

    // ---- Named loads -----------------------------------------------------------------------------------

    void VisitGetNamedProperty()
    {
        ValueNode receiver = LoadRegister(0);
        JSValue name = Constant(ConstantPoolIndex(1));
        int slot = FeedbackSlot(2);
        if (IsUninitializedIC(slot))
        {
            EmitUnconditionalDeopt(DeoptimizeReason.kInsufficientTypeFeedbackForGenericNamedAccess);
            return;
        }
        if (MapsAndHandlers(slot) is { } feedback && TryBuildNamedLoad(receiver, feedback) is { } result)
        {
            SetAccumulator(result);
            return;
        }
        SetAccumulator(CallBaseline("GetNamedProperty", [receiver],
            [BuiltinArg.Isolate, Fv, BuiltinArg.I(slot), BuiltinArg.In(0), BuiltinArg.C(name)])!);
    }

    /// <summary>ComputePropertyAccessInfo for a load handler, or null when Maglev does not handle it.</summary>
    static PropertyAccessInfo? LoadAccessInfo(Map map, JSValue handlerValue)
    {
        if (handlerValue.HeapObjectOrNull is not LoadHandler h) return null;
        if (h.LookupOnLookupStartObject) return null;
        var info = new PropertyAccessInfo { Map = map, ValidityCell = h.ValidityCell };
        switch (h.HandlerKind)
        {
            case LoadHandler.Kind.kField:
                if (ICMaps.IsPrimitiveMap(map) && h.Holder is null) return null;
                info.AccessKind = PropertyAccessInfo.Kind.DataField;
                info.StorageIndex = h.FieldIndex;
                info.Holder = h.Holder;
                return info;
            case LoadHandler.Kind.kConstantFromPrototype:
                info.AccessKind = PropertyAccessInfo.Kind.DataConstant;
                info.Constant = h.Data;
                return info;
            case LoadHandler.Kind.kNonExistent:
                info.AccessKind = PropertyAccessInfo.Kind.NotFound;
                return info;
            case LoadHandler.Kind.kArrayLength:
                info.AccessKind = PropertyAccessInfo.Kind.ArrayLength;
                return info;
            case LoadHandler.Kind.kStringLength:
                info.AccessKind = PropertyAccessInfo.Kind.StringLength;
                return info;
            default:
                return null;
        }
    }

    /// <summary>TryBuildNamedAccess for loads: map checks and the access per map (polymorphic: a map dispatch).</summary>
    ValueNode? TryBuildNamedLoad(ValueNode receiver, List<(Map Map, JSValue Handler)> feedback)
    {
        var infos = new List<PropertyAccessInfo>(feedback.Count);
        foreach ((Map map, JSValue handler) in feedback)
        {
            PropertyAccessInfo? info = LoadAccessInfo(map, handler);
            if (info is null) return null;
            infos.Add(info);
        }
        // Primitive maps: only a single string (or number) map, checked by type.
        bool anyPrimitive = infos.Exists(static i => ICMaps.IsPrimitiveMap(i.Map));
        if (anyPrimitive && infos.Count != 1) return null;
        if (anyPrimitive)
        {
            PropertyAccessInfo info = infos[0];
            if (!BuildCheckPrimitiveMap(receiver, info.Map)) return null;
            return BuildPropertyLoad(receiver, info);
        }
        if (receiver.Representation != ValueRepresentation.kTagged) return null;

        // Group maps with the same access (V8 merges compatible access infos).
        var groups = new List<(List<Map> Maps, PropertyAccessInfo Info)>();
        foreach (PropertyAccessInfo info in infos)
        {
            bool merged = false;
            foreach ((List<Map> maps, PropertyAccessInfo other) in groups)
            {
                if (SameLoad(info, other))
                {
                    maps.Add(info.Map);
                    merged = true;
                    break;
                }
            }
            if (!merged) groups.Add(([info.Map], info));
        }
        if (groups.Count == 1)
        {
            BuildCheckMaps(receiver, groups[0].Maps.ToArray());
            return BuildPropertyLoad(receiver, groups[0].Info);
        }
        var cases = new List<(Map[] Maps, Func<ValueNode?> Build)>();
        foreach ((List<Map> maps, PropertyAccessInfo info) in groups)
        {
            cases.Add((maps.ToArray(), () => BuildPropertyLoad(receiver, info)));
        }
        return BuildPolymorphicAccess(receiver, cases, hasResult: true);
    }

    static bool SameLoad(PropertyAccessInfo a, PropertyAccessInfo b) =>
        a.AccessKind == b.AccessKind && a.StorageIndex == b.StorageIndex && ReferenceEquals(a.Holder, b.Holder) &&
        a.Constant.IsIdenticalTo(b.Constant) && ReferenceEquals(a.ValidityCell, b.ValidityCell);

    /// <summary>A map check for a primitive stand-in map (string, number, symbol receivers).</summary>
    bool BuildCheckPrimitiveMap(ValueNode receiver, Map map)
    {
        switch (map.InstanceType)
        {
            case InstanceType.SeqStringType:
                BuildCheckString(receiver);
                return true;
            case InstanceType.HeapNumberType:
                if (receiver.Representation == ValueRepresentation.kTagged) BuildCheckNumber(receiver);
                return true;
            case InstanceType.SymbolType:
                BuildCheckSymbol(receiver);
                return true;
            default:
                return false;
        }
    }

    /// <summary>The load for one access info, after the map check.</summary>
    ValueNode BuildPropertyLoad(ValueNode receiver, PropertyAccessInfo info)
    {
        if (info.ValidityCell is { } cell) BuildCheckValidityCell(cell);
        switch (info.AccessKind)
        {
            case PropertyAccessInfo.Kind.DataField:
            {
                ValueNode holder = info.Holder is null ? receiver : GetConstant(info.Holder);
                return AddNewNode(new ValueNode(Opcode.LoadTaggedField, ValueRepresentation.kTagged)
                {
                    Inputs = [GetTaggedValue(holder)],
                    Int0 = info.StorageIndex,
                    Properties = OpProperties.kCanRead,
                });
            }
            case PropertyAccessInfo.Kind.DataConstant:
                return GetConstant(info.Constant);
            case PropertyAccessInfo.Kind.NotFound:
                return GetRootConstant(RootIndex.kUndefinedValue);
            case PropertyAccessInfo.Kind.ArrayLength:
                return AddNewNode(new ValueNode(Opcode.LoadJSArrayLength, ValueRepresentation.kTagged)
                {
                    Inputs = [receiver],
                    Type = NodeType.kNumber,
                    Properties = OpProperties.kCanRead,
                });
            case PropertyAccessInfo.Kind.StringLength:
                return AddNewNode(new ValueNode(Opcode.StringLength, ValueRepresentation.kInt32)
                {
                    Inputs = [GetTaggedValue(receiver)],
                    Type = NodeType.kSmi,
                });
            default:
                throw new InvalidOperationException();
        }
    }

    /// <summary>
    /// The prototype chain a handler relies on is unchanged (V8: a dependency
    /// on stable prototype maps; V8Sharp: the handler's validity cell).
    /// </summary>
    void BuildCheckValidityCell(Cell cell)
    {
        if (!cell.Value.IsIdenticalTo(Cell.kPrototypeChainValid))
        {
            EmitUnconditionalDeoptAndAbort(DeoptimizeReason.kWrongMap);
        }
        AddNewNode(new Node(Opcode.CheckValidityCell) { Obj0 = cell, Properties = OpProperties.kEagerDeopt }, DeoptimizeReason.kWrongMap);
    }

    /// <summary>
    /// BuildPolymorphicAccess: a map dispatch over <paramref name="cases"/>
    /// (the last case checks its maps, deoptimizing otherwise); the results
    /// join in a Phi.
    /// </summary>
    ValueNode? BuildPolymorphicAccess(ValueNode receiver, List<(Map[] Maps, Func<ValueNode?> Build)> cases, bool hasResult)
    {
        ValueNode map = AddNewNode(new ValueNode(Opcode.LoadMap, ValueRepresentation.kTagged)
        {
            Inputs = [receiver],
            Type = NodeType.kOtherHeapObject,
            Properties = OpProperties.kCanRead,
        });
        KnownNodeAspects entryKnown = _frame.Known.Clone();
        BasicBlock join = _graph.NewBlock();
        var results = new List<(BasicBlock Block, ValueNode? Value, KnownNodeAspects Known)>();
        // Flatten to one map per test; a case with several maps tests each of them.
        for (int c = 0; c < cases.Count; c++)
        {
            (Map[] maps, Func<ValueNode?> build) = cases[c];
            bool last = c == cases.Count - 1;
            BasicBlock caseBlock;
            if (!last)
            {
                caseBlock = _graph.NewBlock();
                BasicBlock next = _graph.NewBlock();
                for (int m = 0; m < maps.Length; m++)
                {
                    BasicBlock test = _currentBlock!;
                    BasicBlock otherwise = m == maps.Length - 1 ? next : _graph.NewBlock();
                    var branch = new ControlNode(Opcode.BranchIfReferenceEqual)
                    {
                        Inputs = [map, GetConstant(maps[m])],
                        Target = caseBlock,
                        FalseTarget = otherwise,
                    };
                    FinishBlock(branch);
                    caseBlock.Predecessors.Add(test);
                    otherwise.Predecessors.Add(test);
                    if (m < maps.Length - 1)
                    {
                        _graph.Blocks.Add(otherwise);
                        _currentBlock = otherwise;
                    }
                }
                _graph.Blocks.Add(caseBlock);
                _currentBlock = caseBlock;
                _frame.Known = entryKnown.Clone();
                RecordKnownMaps(receiver, maps);
                try
                {
                    ValueNode? value = build();
                    if (hasResult) value = GetTaggedValue(value!);
                    results.Add((_currentBlock!, value, _frame.Known));
                    FinishBlock(new ControlNode(Opcode.Jump) { Target = join });
                }
                catch (AbortBytecodeException)
                {
                }
                _graph.Blocks.Add(next);
                _currentBlock = next;
                _frame.Known = entryKnown.Clone();
            }
            else
            {
                try
                {
                    BuildCheckMaps(receiver, maps);
                    ValueNode? value = build();
                    if (hasResult) value = GetTaggedValue(value!);
                    results.Add((_currentBlock!, value, _frame.Known));
                    FinishBlock(new ControlNode(Opcode.Jump) { Target = join });
                }
                catch (AbortBytecodeException)
                {
                }
            }
        }
        if (results.Count == 0)
        {
            _currentBlock = null;
            throw new AbortBytecodeException();
        }
        _graph.Blocks.Add(join);
        KnownNodeAspects known = results[0].Known;
        foreach ((BasicBlock block, ValueNode? _, KnownNodeAspects k) in results)
        {
            join.Predecessors.Add(block);
            if (!ReferenceEquals(k, known)) known.Merge(k);
        }
        _currentBlock = join;
        _frame.Known = known;
        if (!hasResult) return null;
        ValueNode first = results[0].Value!;
        if (results.TrueForAll(r => ReferenceEquals(r.Value, first))) return first;
        var phi = new Phi(Register.VirtualAccumulator(), -1) { Id = _graph.NewNodeId(), Block = join, Unit = _unit };
        foreach ((BasicBlock _, ValueNode? value, KnownNodeAspects _) in results) phi.InputList.Add(value!);
        join.Phis.Add(phi);
        return phi;
    }

    // ---- Named stores -----------------------------------------------------------------------------------

    void VisitSetNamedProperty(bool defineOwn)
    {
        ValueNode receiver = LoadRegister(0);
        JSValue name = Constant(ConstantPoolIndex(1));
        int slot = FeedbackSlot(2);
        ValueNode value = GetAccumulator();
        if (IsUninitializedIC(slot))
        {
            EmitUnconditionalDeopt(DeoptimizeReason.kInsufficientTypeFeedbackForGenericNamedAccess);
            return;
        }
        if (MapsAndHandlers(slot) is { } feedback && TryBuildNamedStore(receiver, value, feedback)) return;
        CallBaseline(defineOwn ? "DefineNamedOwnProperty" : "SetNamedProperty", [receiver, value],
            [BuiltinArg.Isolate, Fv, BuiltinArg.I(slot), BuiltinArg.In(0), BuiltinArg.C(name), BuiltinArg.In(1)]);
    }

    static PropertyAccessInfo? StoreAccessInfo(Map map, JSValue handlerValue)
    {
        if (ICMaps.IsPrimitiveMap(map)) return null;
        if (handlerValue.HeapObjectOrNull is not StoreHandler h) return null;
        var info = new PropertyAccessInfo { Map = map, ValidityCell = h.ValidityCell };
        switch (h.HandlerKind)
        {
            case StoreHandler.Kind.kField:
                info.AccessKind = PropertyAccessInfo.Kind.FieldStore;
                break;
            case StoreHandler.Kind.kConstField:
                info.AccessKind = PropertyAccessInfo.Kind.ConstFieldStore;
                break;
            case StoreHandler.Kind.kTransitionToField:
                if (h.TransitionMap is null || h.TransitionMap.IsDeprecated) return null;
                info.AccessKind = PropertyAccessInfo.Kind.TransitionStore;
                info.TransitionMap = h.TransitionMap;
                break;
            default:
                return null;
        }
        info.StorageIndex = h.FieldIndex;
        info.Representation = h.Representation;
        info.FieldTypeClass = h.FieldTypeClass;
        if (info.Representation.IsNone || info.Representation.IsWasmValue) return null;
        return info;
    }

    bool TryBuildNamedStore(ValueNode receiver, ValueNode value, List<(Map Map, JSValue Handler)> feedback)
    {
        if (receiver.Representation != ValueRepresentation.kTagged) return false;
        var infos = new List<PropertyAccessInfo>(feedback.Count);
        foreach ((Map map, JSValue handler) in feedback)
        {
            PropertyAccessInfo? info = StoreAccessInfo(map, handler);
            if (info is null) return false;
            infos.Add(info);
        }
        var groups = new List<(List<Map> Maps, PropertyAccessInfo Info)>();
        foreach (PropertyAccessInfo info in infos)
        {
            bool merged = false;
            foreach ((List<Map> maps, PropertyAccessInfo other) in groups)
            {
                if (info.AccessKind != PropertyAccessInfo.Kind.TransitionStore && other.AccessKind == info.AccessKind &&
                    other.StorageIndex == info.StorageIndex && other.Representation.Equals(info.Representation) &&
                    ReferenceEquals(other.FieldTypeClass, info.FieldTypeClass))
                {
                    maps.Add(info.Map);
                    merged = true;
                    break;
                }
            }
            if (!merged) groups.Add(([info.Map], info));
        }
        if (groups.Count == 1)
        {
            BuildCheckMaps(receiver, groups[0].Maps.ToArray());
            BuildPropertyStore(receiver, value, groups[0].Info);
            return true;
        }
        var cases = new List<(Map[] Maps, Func<ValueNode?> Build)>();
        foreach ((List<Map> maps, PropertyAccessInfo info) in groups)
        {
            cases.Add((maps.ToArray(), () =>
            {
                BuildPropertyStore(receiver, value, info);
                return null;
            }));
        }
        BuildPolymorphicAccess(receiver, cases, hasResult: false);
        return true;
    }

    /// <summary>
    /// The value of a field store, checked against the field's representation
    /// and field type (V8: BuildStoreField's CheckSmi / CheckNumber / CheckHeapObject / CheckMaps).
    /// </summary>
    ValueNode BuildCheckedFieldValue(ValueNode value, PropertyAccessInfo info)
    {
        Representation rep = info.Representation;
        if (rep.IsSmi)
        {
            BuildCheckSmi(value);
            return GetTaggedValue(value);
        }
        if (rep.IsDouble)
        {
            if (value.Representation == ValueRepresentation.kTagged) BuildCheckNumber(value);
            else if (value.Representation == ValueRepresentation.kHoleyFloat64) value = GetFloat64(value);
            return GetTaggedValue(value);
        }
        if (rep.IsHeapObject)
        {
            BuildCheckHeapObject(value);
            if (info.FieldTypeClass is { } fieldClass) BuildCheckMaps(value, [fieldClass]);
            return value;
        }
        return GetTaggedValue(value);
    }

    void BuildPropertyStore(ValueNode receiver, ValueNode value, PropertyAccessInfo info)
    {
        if (info.ValidityCell is { } cell && info.AccessKind == PropertyAccessInfo.Kind.TransitionStore) BuildCheckValidityCell(cell);
        ValueNode stored = BuildCheckedFieldValue(value, info);
        switch (info.AccessKind)
        {
            case PropertyAccessInfo.Kind.FieldStore:
                AddNewNode(new Node(Opcode.StoreTaggedField)
                {
                    Inputs = [receiver, stored],
                    Int0 = info.StorageIndex,
                    Int1 = info.Representation.IsDouble ? 1 : 0,
                    Properties = OpProperties.kCanWrite | OpProperties.kNotIdempotent,
                });
                break;
            case PropertyAccessInfo.Kind.ConstFieldStore:
                // A store to a const field must not change its value (StoreHandler kConstField).
                CallMaglev("CheckConstFieldValue", [receiver, stored],
                    [BuiltinArg.In(0), BuiltinArg.I(info.StorageIndex), BuiltinArg.In(1)], OpProperties.kEagerDeopt,
                    DeoptimizeReason.kStoreToConstant);
                break;
            case PropertyAccessInfo.Kind.TransitionStore:
                AddNewNode(new Node(Opcode.StoreMapTransition)
                {
                    Inputs = [receiver, stored],
                    Int0 = info.StorageIndex,
                    Int1 = info.Representation.IsDouble ? 1 : 0,
                    Obj0 = info.TransitionMap,
                    Properties = OpProperties.kCanWrite | OpProperties.kCanAllocate | OpProperties.kNotIdempotent,
                });
                // The object now has the transition map.
                NodeInfo nodeInfo = _frame.Known.GetOrCreateInfoFor(receiver);
                nodeInfo.PossibleMaps = [info.TransitionMap!];
                nodeInfo.AnyMapIsUnstable = !info.TransitionMap!.IsStable;
                break;
        }
    }

    // ---- Keyed loads --------------------------------------------------------------------------------------

    void VisitGetKeyedProperty()
    {
        ValueNode obj = LoadRegister(0);
        ValueNode key = GetAccumulator();
        int slot = FeedbackSlot(1);
        var nexus = new FeedbackNexus(Isolate, _unit.Feedback, slot);
        if (nexus.IcState() == InlineCacheState.UNINITIALIZED)
        {
            EmitUnconditionalDeopt(DeoptimizeReason.kInsufficientTypeFeedbackForGenericKeyedAccess);
            return;
        }
        if (nexus.GetName() is { } name && MapsAndHandlers(slot) is { } namedFeedback)
        {
            // A keyed load with a constant name (KeyedLoadIC in the one-name state).
            ValueNode nameNode = GetConstant(name);
            if (name is JSString { IsInternalized: true } or Symbol)
            {
                BuildCheckValue(key, name, DeoptimizeReason.kKeyedAccessChanged);
                if (TryBuildNamedLoad(obj, namedFeedback) is { } named)
                {
                    SetAccumulator(named);
                    return;
                }
            }
            _ = nameNode;
        }
        else if (MapsAndHandlers(slot) is { } feedback && TryBuildElementLoad(obj, key, feedback) is { } element)
        {
            SetAccumulator(element);
            return;
        }
        SetAccumulator(CallBaseline("GetKeyedProperty", [obj, key],
            [BuiltinArg.Isolate, Fv, BuiltinArg.I(slot), BuiltinArg.In(0), BuiltinArg.In(1)])!);
    }

    /// <summary>The elements kinds of element handlers for these maps, if all are fast kinds of one family.</summary>
    static bool CollectElementAccess(List<(Map Map, JSValue Handler)> feedback, bool load, out ElementsKind kind, out bool isJSArray,
        out bool anyHoley)
    {
        kind = default;
        isJSArray = false;
        anyHoley = false;
        bool first = true;
        foreach ((Map map, JSValue handler) in feedback)
        {
            ElementsKind k;
            bool array;
            if (load)
            {
                if (handler.HeapObjectOrNull is not LoadHandler { HandlerKind: LoadHandler.Kind.kElement } lh || lh.FastElementsMode == 0) return false;
                k = lh.ElementsKind;
                array = lh.IsJSArray;
            }
            else
            {
                if (handler.HeapObjectOrNull is not StoreHandler { IsSimpleElementStore: true } sh) return false;
                k = sh.ElementsKind;
                array = map.InstanceType == InstanceType.JSArrayType;
            }
            if (!ElementsKinds.IsFastElementsKind(k) || k != map.ElementsKind) return false;
            if (array != (map.InstanceType == InstanceType.JSArrayType)) return false;
            if (first)
            {
                kind = k;
                isJSArray = array;
                first = false;
            }
            else
            {
                if (array != isJSArray) return false;
                if (ElementsKinds.IsDoubleElementsKind(k) != ElementsKinds.IsDoubleElementsKind(kind)) return false;
                if (!load && ElementsKinds.IsSmiElementsKind(k) != ElementsKinds.IsSmiElementsKind(kind)) return false;
                // The most general kind of the group.
                if (ElementsKinds.IsMoreGeneralElementsKindTransition(kind, k)) kind = k;
            }
            if (ElementsKinds.IsHoleyElementsKind(k)) anyHoley = true;
        }
        return !first;
    }

    ValueNode? TryBuildElementLoad(ValueNode obj, ValueNode key, List<(Map Map, JSValue Handler)> feedback)
    {
        if (obj.Representation != ValueRepresentation.kTagged) return null;
        if (!CollectElementAccess(feedback, load: true, out ElementsKind kind, out bool isJSArray, out bool anyHoley)) return null;
        var maps = new Map[feedback.Count];
        for (int i = 0; i < maps.Length; i++) maps[i] = feedback[i].Map;
        BuildCheckMaps(obj, maps);
        ValueNode index = GetInt32(key);
        ValueNode elements = BuildLoadElements(obj);
        BuildBoundsCheck(obj, elements, index, isJSArray);
        // BuildElementLoadOnJSArrayOrJSObject: holes are undefined when the
        // prototype chain has no elements and the IC handled holes.
        bool convertHole = anyHoley && CanTreatHoleAsUndefined(maps) && LoadModeHandlesHoles(feedback);
        if (ElementsKinds.IsDoubleElementsKind(kind))
        {
            if (anyHoley)
            {
                // BuildLoadHoleyFixedDoubleArrayElement.
                ValueNode holey = AddNewNode(new ValueNode(Opcode.LoadHoleyFixedDoubleArrayElement, ValueRepresentation.kHoleyFloat64)
                {
                    Inputs = [elements, index],
                    Properties = OpProperties.kCanRead,
                });
                return convertHole ? holey : GetFloat64(holey);
            }
            return AddNewNode(new ValueNode(Opcode.LoadFixedDoubleArrayElement, ValueRepresentation.kFloat64)
            {
                Inputs = [elements, index],
                Type = NodeType.kNumber,
                Properties = OpProperties.kCanRead,
            });
        }
        ValueNode value = AddNewNode(new ValueNode(Opcode.LoadFixedArrayElement, ValueRepresentation.kTagged)
        {
            Inputs = [elements, index],
            Type = ElementsKinds.IsSmiElementsKind(kind) && !anyHoley ? NodeType.kSmi : NodeType.kUnknown,
            Properties = OpProperties.kCanRead,
        });
        if (anyHoley)
        {
            if (convertHole)
            {
                return AddNewNode(new ValueNode(Opcode.ConvertHoleToUndefined, ValueRepresentation.kTagged) { Inputs = [value] });
            }
            AddCheck(Opcode.CheckNotHole, value, DeoptimizeReason.kHole);
            if (ElementsKinds.IsSmiElementsKind(kind)) EnsureType(value, NodeType.kSmi);
        }
        return value;
    }

    /// <summary>
    /// MaglevGraphBuilder::CanTreatHoleAsUndefined: every map's prototype is the
    /// initial Array.prototype or Object.prototype, and the NoElements
    /// protector holds (the code depends on it).
    /// </summary>
    bool CanTreatHoleAsUndefined(Map[] maps)
    {
        NativeContext nativeContext = Isolate.NativeContext;
        foreach (Map map in maps)
        {
            HeapObject? prototype = map.Prototype;
            if (!ReferenceEquals(prototype, nativeContext.InitialArrayPrototype) &&
                !ReferenceEquals(prototype, nativeContext.InitialObjectPrototype))
            {
                return false;
            }
        }
        return _info.DependOnProtector(Protectors.IsNoElementsIntact(Isolate), "NoElements");
    }

    /// <summary>LoadModeHandlesHoles over the element handlers.</summary>
    static bool LoadModeHandlesHoles(List<(Map Map, JSValue Handler)> feedback)
    {
        foreach ((Map _, JSValue handler) in feedback)
        {
            if (handler.HeapObjectOrNull is not LoadHandler { AllowHandlingHole: true }) return false;
        }
        return true;
    }

    ValueNode BuildLoadElements(ValueNode obj) =>
        AddNewNode(new ValueNode(Opcode.LoadElements, ValueRepresentation.kTagged)
        {
            Inputs = [obj],
            Type = NodeType.kOtherHeapObject,
            Properties = OpProperties.kCanRead,
        });

    /// <summary>index &lt; length (unsigned), deoptimizing out of bounds.</summary>
    void BuildBoundsCheck(ValueNode obj, ValueNode elements, ValueNode index, bool isJSArray)
    {
        ValueNode length = isJSArray
            ? GetInt32(AddNewNode(new ValueNode(Opcode.LoadJSArrayLength, ValueRepresentation.kTagged)
            {
                Inputs = [obj],
                Type = NodeType.kSmi,
                Properties = OpProperties.kCanRead,
            }))
            : AddNewNode(new ValueNode(Opcode.LoadFixedArrayLength, ValueRepresentation.kInt32)
            {
                Inputs = [elements],
                Type = NodeType.kSmi,
                Properties = OpProperties.kCanRead,
            });
        AddNewNode(new Node(Opcode.CheckInt32Condition)
        {
            Inputs = [index, length],
            Int0 = (int)CompareOperation.kLessThan,
            Int1 = 1, // unsigned
            Properties = OpProperties.kEagerDeopt,
        }, DeoptimizeReason.kOutOfBounds);
    }

    // ---- Keyed stores -------------------------------------------------------------------------------------

    void VisitSetKeyedProperty()
    {
        ValueNode obj = LoadRegister(0);
        ValueNode key = LoadRegister(1);
        ValueNode value = GetAccumulator();
        int slot = FeedbackSlot(2);
        var nexus = new FeedbackNexus(Isolate, _unit.Feedback, slot);
        if (nexus.IcState() == InlineCacheState.UNINITIALIZED)
        {
            EmitUnconditionalDeopt(DeoptimizeReason.kInsufficientTypeFeedbackForGenericKeyedAccess);
            return;
        }
        if (nexus.GetName() is null && MapsAndHandlers(slot) is { } feedback && TryBuildElementStore(obj, key, value, feedback)) return;
        CallBaseline("SetKeyedProperty", [obj, key, value],
            [BuiltinArg.Isolate, Fv, BuiltinArg.I(slot), BuiltinArg.In(0), BuiltinArg.In(1), BuiltinArg.In(2)]);
    }

    bool TryBuildElementStore(ValueNode obj, ValueNode key, ValueNode value, List<(Map Map, JSValue Handler)> feedback)
    {
        if (obj.Representation != ValueRepresentation.kTagged) return false;
        if (!CollectElementAccess(feedback, load: false, out ElementsKind kind, out bool isJSArray, out bool _)) return false;
        KeyedAccessStoreMode mode = KeyedAccessStoreMode.kInBounds;
        foreach ((Map _, JSValue handler) in feedback)
        {
            KeyedAccessStoreMode m = ((StoreHandler)handler.Object).StoreMode;
            if (m == KeyedAccessStoreMode.kIgnoreTypedArrayOOB) return false;
            if (m != KeyedAccessStoreMode.kInBounds) mode = KeyedAccessStoreMode.kGrowAndHandleCOW;
        }
        var maps = new Map[feedback.Count];
        for (int i = 0; i < maps.Length; i++) maps[i] = feedback[i].Map;
        BuildCheckMaps(obj, maps);
        ValueNode index = GetInt32(key);
        // The value must fit the elements kind.
        ValueNode stored;
        if (ElementsKinds.IsSmiElementsKind(kind))
        {
            BuildCheckSmi(value);
            stored = GetTaggedValue(value);
        }
        else if (ElementsKinds.IsDoubleElementsKind(kind))
        {
            stored = GetFloat64(value);
        }
        else
        {
            stored = GetTaggedValue(value);
        }
        ValueNode elements;
        if (mode == KeyedAccessStoreMode.kGrowAndHandleCOW)
        {
            // MaybeGrowFastElements + EnsureWritableFastElements; appends update the array length.
            elements = AddNewNode(new ValueNode(Opcode.MaybeGrowFastElements, ValueRepresentation.kTagged)
            {
                Inputs = [obj, index],
                Int0 = isJSArray ? 1 : 0,
                Int1 = (int)kind,
                Properties = OpProperties.kEagerDeopt | OpProperties.kCanAllocate | OpProperties.kCanWrite | OpProperties.kNotIdempotent,
                Type = NodeType.kOtherHeapObject,
            }, DeoptimizeReason.kCouldNotGrowElements);
        }
        else
        {
            elements = BuildLoadElements(obj);
            BuildBoundsCheck(obj, elements, index, isJSArray);
            if (!ElementsKinds.IsDoubleElementsKind(kind))
            {
                // Copy-on-write elements need the runtime.
                AddCheck(Opcode.CheckInstanceType, elements, DeoptimizeReason.kCowArrayElementsChanged, int0: 3);
            }
        }
        AddNewNode(new Node(ElementsKinds.IsDoubleElementsKind(kind) ? Opcode.StoreFixedDoubleArrayElement : Opcode.StoreFixedArrayElement)
        {
            Inputs = [elements, index, stored],
            Properties = OpProperties.kCanWrite | OpProperties.kNotIdempotent,
        });
        return true;
    }
}
