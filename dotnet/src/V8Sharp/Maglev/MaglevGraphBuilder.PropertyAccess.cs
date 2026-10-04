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
        public enum Kind { DataField, DataConstant, NotFound, ArrayLength, StringLength, TypedArrayLength, FieldStore, ConstFieldStore, TransitionStore }
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
        /// <summary>
        /// Loads: the map whose descriptor owns the field (Map::FindFieldOwner),
        /// when the field's representation and type are known from the
        /// descriptor (ComputeDataFieldAccessInfo); null otherwise.
        /// </summary>
        public Map? FieldOwner;
        /// <summary>Loads: the map the field's descriptor was read from, and whether the field is const (IsFastDataConstant).</summary>
        public Map? DescriptorMap;
        public bool IsConstField;
    }

    /// <summary>
    /// The (map, handler) pairs of a property IC (FeedbackNexus::ExtractMapsAndHandlers),
    /// without deprecated maps. As JSHeapBroker::ReadFeedbackForPropertyAccess,
    /// a deprecated map whose updated map is not a migration target sets
    /// <see cref="_hasDeprecatedMapWithoutMigrationTarget"/>: the map checks of
    /// the access then migrate such an object before they deoptimize
    /// (CheckMapsWithMigrationAndDeopt), so the interpreter's IC learns the
    /// updated map.
    /// </summary>
    /// <remarks>
    /// As V8, a deprecated map is replaced by its updated map (Map::TryUpdate).
    /// Deviation: V8 computes the access from the updated map; V8Sharp's
    /// accesses come from the IC handlers, which belong to the deprecated map,
    /// so the updated map keeps the handler only for a named load whose handler
    /// applies to it unchanged (<see cref="LoadHandlerAppliesToUpdatedMap"/>);
    /// otherwise the map is dropped.
    /// </remarks>
    List<(Map Map, JSValue Handler)>? MapsAndHandlers(int slot, Name? loadName = null)
    {
        var nexus = new FeedbackNexus(Isolate, _unit.Feedback, slot);
        InlineCacheState state = nexus.IcState();
        if (state is not (InlineCacheState.MONOMORPHIC or InlineCacheState.POLYMORPHIC)) return null;
        var result = new List<(Map Map, JSValue Handler)>();
        nexus.ExtractMapsAndHandlers(result);
        for (int i = 0; i < result.Count; i++)
        {
            (Map map, JSValue handler) = result[i];
            if (!map.IsDeprecated) continue;
            Map? updated = Map.TryUpdate(Isolate, map);
            if (updated is null) continue;
            if (!updated.IsMigrationTarget) _hasDeprecatedMapWithoutMigrationTarget = true;
            if (loadName is not null && !result.Exists(e => ReferenceEquals(e.Map, updated)) &&
                LoadHandlerAppliesToUpdatedMap(map, updated, loadName, handler))
            {
                result[i] = (updated, handler);
            }
        }
        result.RemoveAll(static e => e.Map.IsDeprecated);
        return result.Count == 0 ? null : result;
    }

    /// <summary>
    /// Whether the load handler of a deprecated map gives the same access for
    /// its updated map: an own field at the same storage index, or a lookup on
    /// the (same) prototype chain of a property the map does not have.
    /// </summary>
    static bool LoadHandlerAppliesToUpdatedMap(Map deprecated, Map updated, Name name, JSValue handlerValue)
    {
        if (handlerValue.HeapObjectOrNull is not LoadHandler h || h.LookupOnLookupStartObject) return false;
        if (!ReferenceEquals(updated.Prototype, deprecated.Prototype) || updated.InstanceType != deprecated.InstanceType) return false;
        InternalIndex descriptor = updated.InstanceDescriptors.Search(name, updated);
        switch (h.HandlerKind)
        {
            case LoadHandler.Kind.kField when h.Holder is null:
                return descriptor.IsFound &&
                       updated.InstanceDescriptors.GetDetails(descriptor).Location == PropertyLocation.Field &&
                       FieldIndex.ForDescriptor(updated, descriptor).StorageIndex == h.FieldIndex;
            case LoadHandler.Kind.kField:
            case LoadHandler.Kind.kConstantFromPrototype:
            case LoadHandler.Kind.kNonExistent:
            case LoadHandler.Kind.kAccessorFromPrototype:
                return descriptor.IsNotFound;
            default:
                return false;
        }
    }

    /// <summary>The feedback of the current access had a deprecated map without migration target.</summary>
    bool _hasDeprecatedMapWithoutMigrationTarget;

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
        if (MapsAndHandlers(slot, (Name)name.Object) is { } polymorphic &&
            TryBuildPolymorphicLoadWithContinuation(receiver, polymorphic, (Name)name.Object))
        {
            return;
        }
        if (MapsAndHandlers(slot, (Name)name.Object) is { } feedback && TryBuildNamedLoad(receiver, feedback, (Name)name.Object) is { } result)
        {
            SetAccumulator(result);
            return;
        }
        SetAccumulator(CallBaseline("GetNamedProperty", [receiver],
            [BuiltinArg.Isolate, Fv, BuiltinArg.I(slot), BuiltinArg.In(0), BuiltinArg.C(name)])!);
    }

    /// <summary>ComputePropertyAccessInfo for a load handler, or null when Maglev does not handle it.</summary>
    PropertyAccessInfo? LoadAccessInfo(Map map, JSValue handlerValue, Name? name)
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
                if (name is not null) ComputeDataFieldInfo(info, h.Holder?.Map ?? map, name);
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
            case LoadHandler.Kind.kAccessorFromPrototype:
                // AccessInfoFactory::LookupSpecialFieldAccessorInHolder: the
                // TypedArray.prototype.length getter of a (non-RAB/GSAB) typed array.
                if (Flags.typed_array_length_loading && map.InstanceType == InstanceType.JSTypedArrayType &&
                    !ElementsKinds.IsRabGsabTypedArrayElementsKind(map.ElementsKind) &&
                    h.Data.HeapObjectOrNull is JSFunction { Shared.BuiltinId: Builtins.Builtin.TypedArrayPrototypeLength } &&
                    h.Holder is not null)
                {
                    info.AccessKind = PropertyAccessInfo.Kind.TypedArrayLength;
                    info.Holder = h.Holder;
                    return info;
                }
                return null;
            default:
                return null;
        }
    }

    /// <summary>
    /// AccessInfoFactory::ComputeDataFieldAccessInfo: the representation and
    /// field type of the field the handler loads, from the descriptor of
    /// <paramref name="map"/> (the receiver's map, or the holder's), and its
    /// field owner (where the dependencies on them are registered).
    /// </summary>
    static void ComputeDataFieldInfo(PropertyAccessInfo info, Map map, Name name)
    {
        if (map.IsDictionaryMap || map.IsDeprecated) return;
        DescriptorArray descriptors = map.InstanceDescriptors;
        InternalIndex descriptor = descriptors.Search(name, map);
        if (!descriptor.IsFound) return;
        PropertyDetails details = descriptors.GetDetails(descriptor);
        if (details.Kind != PropertyKind.Data || details.Location != PropertyLocation.Field) return;
        if (FieldIndex.ForDescriptor(map, descriptor).StorageIndex != info.StorageIndex) return;
        Representation rep = details.Representation;
        if (!(rep.IsSmi || rep.IsDouble || rep.IsHeapObject || rep.IsTagged)) return;
        info.Representation = rep;
        info.FieldOwner = map.FindFieldOwner(descriptor);
        info.DescriptorMap = map;
        info.IsConstField = details.Constness == PropertyConstness.Const;
        if (rep.IsHeapObject && descriptors.GetFieldType(descriptor) is Map fieldClass) info.FieldTypeClass = fieldClass;
    }

    /// <summary>
    /// BuildLoadField: the load of a data field of <paramref name="holder"/>,
    /// with what its descriptor says about the value: a Double field is
    /// loaded untagged (LoadDoubleField, no number check); a Smi field's
    /// value is a Smi; a HeapObject field with a stable class field type has
    /// that map (no map check), with the field representation and field type
    /// dependencies of the access info.
    /// </summary>
    ValueNode BuildLoadField(ValueNode holder, PropertyAccessInfo info)
    {
        Map? owner = info.FieldOwner;
        Representation rep = info.Representation;
        if (owner is not null && TryFoldLoadConstantDataField(holder, info) is { } folded) return folded;
        if (owner is not null && rep.IsDouble)
        {
            _info.AddDependency(owner, Objects.DependentCode.DependencyGroups.FieldRepresentation);
            return AddNewNode(new ValueNode(Opcode.LoadDoubleField, ValueRepresentation.kFloat64)
            {
                Inputs = [holder],
                Int0 = info.StorageIndex,
                Type = NodeType.kNumber,
                Properties = OpProperties.kCanRead,
            });
        }
        var value = AddNewNode(new ValueNode(Opcode.LoadTaggedField, ValueRepresentation.kTagged)
        {
            Inputs = [holder],
            Int0 = info.StorageIndex,
            Properties = OpProperties.kCanRead,
        });
        if (owner is null) return value;
        if (rep.IsSmi)
        {
            _info.AddDependency(owner, Objects.DependentCode.DependencyGroups.FieldRepresentation);
            value.Type = NodeType.kSmi;
        }
        else if (rep.IsHeapObject)
        {
            _info.AddDependency(owner, Objects.DependentCode.DependencyGroups.FieldRepresentation |
                                       Objects.DependentCode.DependencyGroups.FieldType);
            if (info.FieldTypeClass is { IsStable: true, IsDeprecated: false } fieldMap &&
                fieldMap.InstanceType >= InstanceTypeChecks.FirstJSReceiver)
            {
                value.Type = NodeType.kAnyHeapObject;
                RecordKnownMaps(value, [fieldMap]);
            }
            else
            {
                value.Type = NodeType.kAnyHeapObject;
            }
        }
        return value;
    }

    /// <summary>
    /// TryGetConstantDataFieldHolder + TryFoldLoadConstantDataField
    /// (maglev-graph-builder.cc): a const field (PropertyConstness::kConst) of
    /// a constant object is its current value, with a dependency on the
    /// field's constness (a store of another value generalizes it to mutable
    /// and deoptimizes the code).
    /// </summary>
    ValueNode? TryFoldLoadConstantDataField(ValueNode holder, PropertyAccessInfo info)
    {
        if (!info.IsConstField || holder.Opcode != Opcode.Constant || holder.Value0.HeapObjectOrNull is not JSObject obj) return null;
        if (!ReferenceEquals(obj.Map, info.DescriptorMap)) return null;
        JSValue value = obj.FieldAt(info.StorageIndex);
        if (ReferenceEquals(value._obj, Oddball.Uninitialized) || value.IsTheHole) return null;
        _info.AddDependency(info.FieldOwner!, Objects.DependentCode.DependencyGroups.FieldConst);
        if (info.Representation.IsDouble) return GetFloat64Constant(value.Number);
        return GetConstant(value);
    }

    /// <summary>
    /// CompilationDependencies::DependOnStablePrototypeChain: the maps of the
    /// prototypes of <paramref name="map"/> up to <paramref name="holder"/> stay
    /// stable (false when one is not stable now).
    /// </summary>
    bool DependOnStablePrototypeChain(Map map, JSReceiver holder)
    {
        var maps = new List<Map>();
        for (JSReceiver? prototype = map.Prototype; prototype is not null; prototype = prototype.Map.Prototype)
        {
            Map prototypeMap = prototype.Map;
            if (!prototypeMap.IsStable) return false;
            maps.Add(prototypeMap);
            if (ReferenceEquals(prototype, holder)) break;
        }
        foreach (Map m in maps) _info.AddDependency(m, Objects.DependentCode.DependencyGroups.PrototypeCheck);
        return true;
    }

    /// <summary>TryBuildNamedAccess for loads: map checks and the access per map (polymorphic: a map dispatch).</summary>
    ValueNode? TryBuildNamedLoad(ValueNode receiver, List<(Map Map, JSValue Handler)> feedback, Name? name)
    {
        var infos = new List<PropertyAccessInfo>(feedback.Count);
        foreach ((Map map, JSValue handler) in feedback)
        {
            PropertyAccessInfo? info = LoadAccessInfo(map, handler, name);
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
                    // PropertyAccessInfo::Merge: a field known differently for
                    // the merged maps is loaded as a plain tagged field.
                    if (!SameFieldInfo(info, other)) other.FieldOwner = null;
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
            // The results merge in a phi of tagged inputs.
            cases.Add((maps.ToArray(), () => GetTaggedValue(BuildPropertyLoad(receiver, info))));
        }
        return BuildPolymorphicAccess(receiver, cases, hasResult: true);
    }

    // ---- Polymorphic property load continuations ---------------------------------------------------------
    //
    // TryBuildPolymorphicPropertyAccess with FindContinuationForPolymorphicPropertyLoad
    // (maglev-graph-builder.cc): for a polymorphic load whose value is stored
    // to a register and called as a method a few bytecodes later
    //   GetNamedProperty; Star r; <straight-line bytecodes>; CallProperty r ...
    // each map's arm builds the load and then the bytecodes up to and including
    // the call, so the callee is the arm's constant and the call is direct (or
    // inlined, small functions only); the arms' frames merge after the call.

    /// <summary>Calls inside a continuation inline small functions only (only_inline_small_).</summary>
    bool _onlyInlineSmall;

    bool TryBuildPolymorphicLoadWithContinuation(ValueNode receiver, List<(Map Map, JSValue Handler)> feedback, Name name)
    {
        if (feedback.Count < 2 || receiver.Representation != ValueRepresentation.kTagged || _onlyInlineSmall) return false;
        var groups = new List<(List<Map> Maps, PropertyAccessInfo Info)>();
        foreach ((Map map, JSValue handler) in feedback)
        {
            PropertyAccessInfo? info = LoadAccessInfo(map, handler, name);
            if (info is null || ICMaps.IsPrimitiveMap(map)) return false;
            bool merged = false;
            foreach ((List<Map> maps, PropertyAccessInfo other) in groups)
            {
                if (SameLoad(info, other))
                {
                    maps.Add(map);
                    if (!SameFieldInfo(info, other)) other.FieldOwner = null;
                    merged = true;
                    break;
                }
            }
            if (!merged) groups.Add(([map], info));
        }
        // Only arms that load different constants (methods) make different calls.
        if (groups.Count < 2 || !groups.TrueForAll(static g => g.Info.AccessKind == PropertyAccessInfo.Kind.DataConstant)) return false;
        int callOffset = FindContinuationForPolymorphicPropertyLoad();
        if (callOffset < 0) return false;
        BuildPolymorphicLoadContinuation(receiver, groups, callOffset);
        return true;
    }

    /// <summary>
    /// FindContinuationForPolymorphicPropertyLoadImpl: the offset of the call
    /// that ends the continuation after the current GetNamedProperty, or -1.
    /// </summary>
    int FindContinuationForPolymorphicPropertyLoad()
    {
        // Try block starts and ends end the continuation (the handler stack is
        // not replayed).
        int nextHandlerChange = int.MaxValue;
        byte[] tableBytes = _unit.Bytecode.HandlerTable;
        if (tableBytes.Length != 0)
        {
            var table = new Codegen.HandlerTable(tableBytes);
            if (_nextHandlerTableIndex < table.NumberOfRangeEntries()) nextHandlerChange = table.GetRangeStart((uint)_nextHandlerTableIndex);
        }
        if (_catchBlockStack.Count > 0) nextHandlerChange = Math.Min(nextHandlerChange, _catchBlockStack.Peek().End);

        var it = new BytecodeArrayIterator(_unit.Bytecode);
        it.SetOffset(_it.CurrentOffset());
        it.Advance();
        if (it.Done() || InterruptsContinuation(it.CurrentOffset(), nextHandlerChange)) return -1;
        if (!Bytecodes.IsShortStar(it.CurrentBytecode())) return -1;
        Register loaded = Register.FromShortStar(it.CurrentBytecode());
        for (int limit = 20; --limit > 0;)
        {
            it.Advance();
            if (it.Done() || InterruptsContinuation(it.CurrentOffset(), nextHandlerChange)) return -1;
            Bytecode bytecode = it.CurrentBytecode();
            switch (bytecode)
            {
                case Bytecode.CallProperty:
                case Bytecode.CallProperty0:
                case Bytecode.CallProperty1:
                case Bytecode.CallProperty2:
                    if (it.GetRegisterOperand(0).Index == loaded.Index) return it.CurrentOffset();
                    continue;
                case Bytecode.Star:
                    if (it.GetRegisterOperand(0).Index == loaded.Index) return -1;
                    continue;
            }
            if (Bytecodes.IsShortStar(bytecode))
            {
                if (Register.FromShortStar(bytecode).Index == loaded.Index) return -1;
                continue;
            }
            if (Bytecodes.IsJump(bytecode) || Bytecodes.IsSwitch(bytecode) || Bytecodes.Returns(bytecode) ||
                Bytecodes.UnconditionallyThrows(bytecode))
            {
                return -1;
            }
        }
        return -1;
    }

    /// <summary>A merge point, loop header or try block boundary ends a continuation.</summary>
    bool InterruptsContinuation(int offset, int nextHandlerChange) =>
        NeedsMergeState(offset) || _catchStates[offset] is not null || _analysis.IsLoopHeader(offset) || offset >= nextHandlerChange;

    /// <summary>
    /// The arms of a polymorphic load with a continuation: per map group the
    /// map test, the load, then the bytecodes up to the call at
    /// <paramref name="callOffset"/>; the arms' frames merge after the call
    /// (LabelForTrackingInterpreterFrameState), where the builder continues.
    /// </summary>
    void BuildPolymorphicLoadContinuation(ValueNode receiver, List<(List<Map> Maps, PropertyAccessInfo Info)> groups, int callOffset)
    {
        int startOffset = _it.CurrentOffset();
        FlushDirtyParameters();
        ValueNode map = AddNewNode(new ValueNode(Opcode.LoadMap, ValueRepresentation.kTagged)
        {
            Inputs = [receiver],
            Type = NodeType.kOtherHeapObject,
            Properties = OpProperties.kCanRead,
        });
        bool needsMigration = false;
        foreach ((List<Map> maps, PropertyAccessInfo _) in groups)
        {
            foreach (Map m in maps) needsMigration |= m.IsMigrationTarget;
        }
        if (needsMigration)
        {
            map = AddNewNode(new ValueNode(Opcode.MigrateMapIfNeeded, ValueRepresentation.kTagged)
            {
                Inputs = [map, receiver],
                Type = NodeType.kOtherHeapObject,
                Properties = OpProperties.kCanWrite | OpProperties.kCanAllocate | OpProperties.kNotIdempotent,
            });
        }
        // What every arm starts from: the frame and checkpoint at the load.
        var entryValues = (ValueNode?[])_frame.Values.Clone();
        KnownNodeAspects entryKnown = _frame.Known.Clone();
        ulong entryDirty = _frame.DirtyParameters;
        var entryCheckpoint = (ValueNode?[])_frameAtBytecodeStart.Clone();
        bool entryDeprecated = _hasDeprecatedMapWithoutMigrationTarget;
        var ends = new List<(BasicBlock Block, InterpreterFrameState Frame, ControlNode Jump)>();
        for (int c = 0; c < groups.Count; c++)
        {
            (List<Map> mapList, PropertyAccessInfo info) = groups[c];
            Map[] maps = mapList.ToArray();
            bool last = c == groups.Count - 1;
            BasicBlock? next = null;
            if (!last)
            {
                BasicBlock caseBlock = _graph.NewBlock();
                next = _graph.NewBlock();
                for (int m = 0; m < maps.Length; m++)
                {
                    BasicBlock test = _currentBlock!;
                    BasicBlock otherwise = m == maps.Length - 1 ? next : _graph.NewBlock();
                    FinishBlock(new ControlNode(Opcode.BranchIfReferenceEqual)
                    {
                        Inputs = [map, GetConstant(maps[m])],
                        Target = caseBlock,
                        FalseTarget = otherwise,
                    });
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
            }
            // Each arm replays the bytecodes from the load with the entry frame.
            _it.SetOffset(startOffset);
            Array.Copy(entryCheckpoint, _frameAtBytecodeStart, entryCheckpoint.Length);
            _latestCheckpointedFrame = null;
            _hasDeprecatedMapWithoutMigrationTarget = entryDeprecated;
            _frame = new InterpreterFrameState(_unit) { Known = entryKnown.Clone(), DirtyParameters = entryDirty };
            Array.Copy(entryValues, _frame.Values, entryValues.Length);
            try
            {
                if (last) BuildCheckMaps(receiver, maps);
                else RecordKnownMaps(receiver, maps);
                SetAccumulator(BuildPropertyLoad(receiver, info));
                bool saved = _onlyInlineSmall;
                _onlyInlineSmall = true;
                try
                {
                    while (_currentBlock is not null && _it.CurrentOffset() < callOffset)
                    {
                        _it.Advance();
                        VisitSingleBytecode();
                    }
                }
                finally
                {
                    _onlyInlineSmall = saved;
                }
                if (_currentBlock is not null)
                {
                    var jump = new ControlNode(Opcode.Jump);
                    BasicBlock end = _currentBlock;
                    FinishBlock(jump);
                    ends.Add((end, _frame, jump));
                }
            }
            catch (AbortBytecodeException)
            {
            }
            if (next is not null)
            {
                _graph.Blocks.Add(next);
                _currentBlock = next;
            }
        }
        _it.SetOffset(callOffset);
        _latestCheckpointedFrame = null;
        if (ends.Count == 0)
        {
            _currentBlock = null;
            return;
        }
        // The arms' frames merge at the bytecode after the call.
        int nextOffset = _it.NextOffset();
        var state = new MergePointInterpreterFrameState(_unit, nextOffset, ends.Count, _analysis.GetInLivenessFor(nextOffset), null);
        foreach ((BasicBlock block, InterpreterFrameState frame, ControlNode _) in ends) state.Merge(this, frame, block);
        BasicBlock join = StartBlockFromMergeState(state);
        join.Offset = nextOffset;
        foreach ((BasicBlock _, InterpreterFrameState _, ControlNode jump) in ends) jump.Target = join;
    }


    static bool SameLoad(PropertyAccessInfo a, PropertyAccessInfo b) =>
        a.AccessKind == b.AccessKind && a.StorageIndex == b.StorageIndex && ReferenceEquals(a.Holder, b.Holder) &&
        a.Constant.IsIdenticalTo(b.Constant) && ReferenceEquals(a.ValidityCell, b.ValidityCell);

    static bool SameFieldInfo(PropertyAccessInfo a, PropertyAccessInfo b) =>
        a.FieldOwner is not null && ReferenceEquals(a.FieldOwner, b.FieldOwner) && a.Representation.Equals(b.Representation) &&
        ReferenceEquals(a.FieldTypeClass, b.FieldTypeClass);

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
                ValueNode holder = GetTaggedValue(info.Holder is null ? receiver : GetConstant(info.Holder));
                return BuildLoadProperty(holder, info.StorageIndex, () => BuildLoadField(holder, info));
            }
            case PropertyAccessInfo.Kind.DataConstant:
                return GetConstant(info.Constant);
            case PropertyAccessInfo.Kind.NotFound:
                return GetRootConstant(RootIndex.kUndefinedValue);
            case PropertyAccessInfo.Kind.ArrayLength:
                return BuildLoadProperty(receiver, PropertyKeys.kJSArrayLength, () => AddNewNode(new ValueNode(Opcode.LoadJSArrayLength, ValueRepresentation.kTagged)
                {
                    Inputs = [receiver],
                    Type = NodeType.kNumber,
                    Properties = OpProperties.kCanRead,
                }));
            case PropertyAccessInfo.Kind.StringLength:
                return AddNewNode(new ValueNode(Opcode.StringLength, ValueRepresentation.kInt32)
                {
                    Inputs = [GetTaggedValue(receiver)],
                    Type = NodeType.kSmi,
                });
            case PropertyAccessInfo.Kind.TypedArrayLength:
                // TryBuildPropertyLoad's kTypedArrayLength: with the prototype
                // chain stable (the getter cannot change without a deopt) and
                // without detached buffers (a detached array's length is 0,
                // which LoadTypedArrayLength also returns).
                DependOnStablePrototypeChain(info.Map, info.Holder!);
                _info.DependOnProtector(Protectors.IsArrayBufferDetachingIntact(Isolate), "ArrayBufferDetaching");
                return BuildLoadTypedArrayLengthAsNumber(receiver);
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
        // (The cases share the frame: no parameter stays dirty in one of them only.)
        FlushDirtyParameters();
        ValueNode map = AddNewNode(new ValueNode(Opcode.LoadMap, ValueRepresentation.kTagged)
        {
            Inputs = [receiver],
            Type = NodeType.kOtherHeapObject,
            Properties = OpProperties.kCanRead,
        });
        // TryBuildPolymorphicPropertyAccess: when a map is a migration target,
        // an object with a deprecated map is migrated before the dispatch
        // (MigrateMapIfNeeded).
        bool needsMigration = false;
        foreach ((Map[] maps, Func<ValueNode?> _) in cases)
        {
            foreach (Map m in maps) needsMigration |= m.IsMigrationTarget;
        }
        if (needsMigration)
        {
            map = AddNewNode(new ValueNode(Opcode.MigrateMapIfNeeded, ValueRepresentation.kTagged)
            {
                Inputs = [map, receiver],
                Type = NodeType.kOtherHeapObject,
                Properties = OpProperties.kCanWrite | OpProperties.kCanAllocate | OpProperties.kNotIdempotent,
            });
        }
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
        if (info.AccessKind == PropertyAccessInfo.Kind.FieldStore && info.Representation.IsDouble)
        {
            // BuildStoreField of a Double field: StoreDoubleField writes the
            // untagged value into the field (V8: the field's HeapNumber), no
            // tagging and no write barrier; a non-number deoptimizes.
            AddNewNode(new Node(Opcode.StoreDoubleField)
            {
                Inputs = [receiver, GetFloat64(value)],
                Int0 = info.StorageIndex,
                Properties = OpProperties.kCanWrite | OpProperties.kNotIdempotent,
            });
            return;
        }
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
                if (name is JSString nameString) BuildCheckInternalizedStringValueOrByReference(key, nameString, DeoptimizeReason.kKeyedAccessChanged);
                else BuildCheckValue(key, name, DeoptimizeReason.kKeyedAccessChanged);
                if (TryBuildNamedLoad(obj, namedFeedback, name as Name) is { } named)
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
        if (_info.IsTracing) TraceGenericAccess("keyed load", nexus, slot);
        SetAccumulator(CallBaseline("GetKeyedProperty", [obj, key],
            [BuiltinArg.Isolate, Fv, BuiltinArg.I(slot), BuiltinArg.In(0), BuiltinArg.In(1)])!);
    }

    /// <summary>--trace-maglev-graph-building: why a property access is generic (its feedback).</summary>
    void TraceGenericAccess(string what, FeedbackNexus nexus, int slot)
    {
        var sb = new System.Text.StringBuilder();
        sb.Append($"[maglev] generic {what} in {_unit} @{_it.CurrentOffset()}: {nexus.IcState()}");
        var all = new List<(Map Map, JSValue Handler)>();
        if (nexus.IcState() is InlineCacheState.MONOMORPHIC or InlineCacheState.POLYMORPHIC) nexus.ExtractMapsAndHandlers(all);
        foreach ((Map map, JSValue handler) in all)
        {
            sb.Append($" [{map.InstanceType} {map.ElementsKind}{(map.IsDeprecated ? " deprecated" : "")} -> ");
            sb.Append(handler.HeapObjectOrNull switch
            {
                LoadHandler lh => $"load {lh.HandlerKind} {lh.ElementsKind} oob={lh.AllowOutOfBounds} holes={lh.AllowHandlingHole} fast={lh.FastElementsMode}",
                StoreHandler sh => $"store {sh.HandlerKind} {sh.ElementsKind} {sh.StoreMode}",
                { } o => o.GetType().Name,
                null => "none",
            });
            sb.Append(']');
        }
        _ = slot;
        Console.WriteLine(sb.ToString());
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
                // The handler's prototype validity cell is replaced by the
                // prototype map checks in TryBuildElementStore, as V8 builds
                // element access from the feedback maps, not the handlers.
                if (handler.HeapObjectOrNull is not StoreHandler { HandlerKind: StoreHandler.Kind.kElement, ElementsTransitionMap: null } sh)
                    return false;
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
        if (TryApplyElementLoadTransitions(obj, feedback) is not { } refined) return null;
        feedback = refined;
        if (CollectTypedArrayAccess(feedback, load: true, out ElementsKind typedKind, out bool typedHandlesOOB))
        {
            return BuildTypedArrayElementLoad(obj, key, feedback, typedKind, typedHandlesOOB);
        }
        if (!CollectElementAccess(feedback, load: true, out ElementsKind kind, out bool isJSArray, out bool anyHoley))
        {
            return TryBuildPolymorphicElementLoad(obj, key, feedback);
        }
        var maps = new Map[feedback.Count];
        for (int i = 0; i < maps.Length; i++) maps[i] = feedback[i].Map;
        BuildCheckMaps(obj, maps);
        ValueNode index = GetInt32ElementIndex(key);
        ValueNode elements = BuildLoadElements(obj);
        // BuildElementLoadOnJSArrayOrJSObject: out of bounds loads are
        // undefined when the IC handled them and the prototype chain has no
        // elements; otherwise they deoptimize.
        if (LoadModeHandlesOOB(feedback) && CanTreatHoleAsUndefined(maps))
        {
            // GetUint32ElementIndex.
            AddNewNode(new Node(Opcode.CheckInt32Condition)
            {
                Inputs = [GetInt32Constant(-1), index],
                Int0 = (int)CompareOperation.kLessThan,
                Properties = OpProperties.kEagerDeopt,
            }, DeoptimizeReason.kNotUint32);
            ValueNode length = BuildLoadLength(obj, elements, isJSArray);
            var branch = new ControlNode(Opcode.BranchIfInt32Compare)
            {
                Inputs = [index, length],
                Operation = CompareOperation.kLessThan,
                Unsigned = true,
            };
            return Select(branch, () => BuildElementLoad(elements, index, kind, anyHoley, maps, feedback),
                () => GetRootConstant(RootIndex.kUndefinedValue));
        }
        BuildBoundsCheck(obj, elements, index, isJSArray);
        return BuildElementLoad(elements, index, kind, anyHoley, maps, feedback);
    }

    /// <summary>
    /// ElementAccessFeedback::Refine's transition groups for a keyed load: a
    /// map whose handler transitions the elements kind
    /// (LoadHandler::TransitionAndLoadElement) is a transition source; the
    /// access transitions such an object to the target map
    /// (TransitionElementsKind with transition_sources) and then loads as for
    /// the target. Returns the feedback with each source replaced by its
    /// target (the feedback as it is when there is no transition), or null
    /// when a transition cannot be built.
    /// </summary>
    List<(Map Map, JSValue Handler)>? TryApplyElementLoadTransitions(ValueNode obj, List<(Map Map, JSValue Handler)> feedback)
    {
        bool any = false;
        foreach ((Map _, JSValue handler) in feedback)
        {
            if (handler.HeapObjectOrNull is LoadHandler { HandlerKind: LoadHandler.Kind.kElementWithTransition }) any = true;
        }
        if (!any) return feedback;
        var refined = new List<(Map Map, JSValue Handler)>(feedback.Count);
        var sources = new List<Map>();
        Map? target = null;
        foreach ((Map map, JSValue handler) in feedback)
        {
            if (handler.HeapObjectOrNull is not LoadHandler { HandlerKind: LoadHandler.Kind.kElementWithTransition } lh)
            {
                refined.Add((map, handler));
                continue;
            }
            if (map.InstanceType != InstanceType.JSArrayType || !ElementsKinds.IsFastElementsKind(map.ElementsKind) ||
                !ElementsKinds.IsFastElementsKind(lh.ElementsKind))
            {
                return null;
            }
            ElementsKind toKind = ElementsKinds.IsHoleyElementsKind(map.ElementsKind)
                ? ElementsKinds.GetHoleyElementsKind(lh.ElementsKind)
                : lh.ElementsKind;
            Map? to = Map.TryAsElementsKind(Isolate, map, toKind);
            // One target per access (V8: one per transition group).
            if (to is null || to.IsDeprecated || target is not null && !ReferenceEquals(to, target)) return null;
            target = to;
            sources.Add(map);
        }
        // The target loads with an element handler of its kind (the
        // transitioning handler's, without the transition).
        LoadHandler? targetHandler = null;
        foreach ((Map map, JSValue handler) in refined)
        {
            if (ReferenceEquals(map, target)) targetHandler = handler.HeapObjectOrNull as LoadHandler;
        }
        if (targetHandler is null)
        {
            var lh = (LoadHandler)feedback.Find(e => ReferenceEquals(e.Map, sources[0])).Handler.Object;
            targetHandler = LoadHandler.LoadElement(Isolate, target!.ElementsKind, isJSArray: true, lh.AllowOutOfBounds, lh.AllowHandlingHole);
            refined.Add((target, JSValue.FromObject(targetHandler)));
        }
        if (targetHandler.HandlerKind != LoadHandler.Kind.kElement) return null;
        AddNewNode(new Node(Opcode.TransitionElementsKind)
        {
            Inputs = [obj],
            Obj0 = target,
            Obj1 = sources.ToArray(),
            Properties = OpProperties.kCanAllocate | OpProperties.kCanWrite | OpProperties.kNotIdempotent,
        });
        return refined;
    }

    /// <summary>
    /// TryBuildPolymorphicElementAccess for loads: a map dispatch with an
    /// element load per map, when the maps are of different families (arrays
    /// and objects, Smi/object and double elements). Out of bounds loads are
    /// not handled (V8 neither).
    /// </summary>
    ValueNode? TryBuildPolymorphicElementLoad(ValueNode obj, ValueNode key, List<(Map Map, JSValue Handler)> feedback)
    {
        if (feedback.Count < 2 || LoadModeHandlesOOB(feedback)) return null;
        var cases = new List<(Map[] Maps, Func<ValueNode?> Build)>(feedback.Count);
        var entries = new List<(Map Map, JSValue Handler)>[feedback.Count];
        for (int i = 0; i < feedback.Count; i++)
        {
            entries[i] = [feedback[i]];
            if (!CollectElementAccess(entries[i], load: true, out _, out _, out _)) return null;
        }
        ValueNode index = GetInt32ElementIndex(key);
        for (int i = 0; i < feedback.Count; i++)
        {
            List<(Map Map, JSValue Handler)> entry = entries[i];
            Map[] maps = [entry[0].Map];
            CollectElementAccess(entry, load: true, out ElementsKind kind, out bool isJSArray, out bool anyHoley);
            cases.Add((maps, () =>
            {
                ValueNode elements = BuildLoadElements(obj);
                BuildBoundsCheck(obj, elements, index, isJSArray);
                return BuildElementLoad(elements, index, kind, anyHoley, maps, entry);
            }));
        }
        return BuildPolymorphicAccess(obj, cases, hasResult: true);
    }

    // ---- Typed arrays (TryBuildElementAccessOnTypedArray) ----------------------------------------------

    /// <summary>
    /// Element accesses on typed arrays of one (non-BigInt, non-Float16,
    /// fixed-length buffer) kind. V8 builds them from the feedback maps; a
    /// V8Sharp store IC leaves typed arrays a slow handler, so for stores
    /// only the maps count. <paramref name="handlesOOB"/>: loads of the IC
    /// allowed out of bounds indices (undefined), stores ignore them.
    /// </summary>
    static bool CollectTypedArrayAccess(List<(Map Map, JSValue Handler)> feedback, bool load, out ElementsKind kind, out bool handlesOOB)
    {
        kind = default;
        handlesOOB = true;
        bool first = true;
        foreach ((Map map, JSValue handler) in feedback)
        {
            ElementsKind k = map.ElementsKind;
            if (map.InstanceType != InstanceType.JSTypedArrayType || !ElementsKinds.IsTypedArrayElementsKind(k) ||
                ElementsKinds.IsBigIntTypedArrayElementsKind(k) || k == ElementsKind.FLOAT16_ELEMENTS)
            {
                return false;
            }
            if (load)
            {
                if (handler.HeapObjectOrNull is not LoadHandler { HandlerKind: LoadHandler.Kind.kElement } lh || lh.ElementsKind != k) return false;
                handlesOOB &= lh.AllowOutOfBounds;
            }
            else
            {
                handlesOOB &= handler.HeapObjectOrNull is StoreHandler { StoreMode: KeyedAccessStoreMode.kIgnoreTypedArrayOOB };
            }
            if (!first && k != kind) return false;
            kind = k;
            first = false;
        }
        return !first;
    }

    /// <summary>
    /// TryBuildElementAccessOnTypedArray: the access depends on no buffer being
    /// detached (and, for a store, none being immutable), or checks the buffer
    /// (CheckTypedArrayValid) when a protector is invalid.
    /// </summary>
    void BuildCheckTypedArrayValidOrDepend(ValueNode obj, bool write)
    {
        bool dependOnDetaching = _info.DependOnProtector(Protectors.IsArrayBufferDetachingIntact(Isolate), "ArrayBufferDetaching");
        bool dependOnMutable = !write || _info.DependOnProtector(Protectors.IsArrayBufferMutableIntact(Isolate), "ArrayBufferMutable");
        if (dependOnDetaching && dependOnMutable) return;
        AddNewNode(new Node(Opcode.CheckTypedArrayValid)
        {
            Inputs = [obj],
            Int0 = write ? 1 : 0,
            Properties = OpProperties.kEagerDeopt,
        }, DeoptimizeReason.kArrayBufferWasDetached);
    }

    static ValueRepresentation TypedArrayElementRepresentation(ElementsKind kind) => kind switch
    {
        ElementsKind.FLOAT32_ELEMENTS or ElementsKind.FLOAT64_ELEMENTS => ValueRepresentation.kFloat64,
        ElementsKind.UINT32_ELEMENTS => ValueRepresentation.kUint32,
        _ => ValueRepresentation.kInt32,
    };

    /// <summary>
    /// BuildLoadTypedArrayLength for the length property: the whole length as a number.
    /// </summary>
    /// <remarks>
    /// Deviation: V8's LoadTypedArrayLength is an IntPtr value (converted by
    /// CheckedIntPtrToInt32, TruncateIntPtrToInt32 or IntPtrToNumber where it
    /// is used); V8Sharp has no IntPtr representation and gives it Float64
    /// representation, which holds any typed array length exactly and has the
    /// same conversions (checked to int32, truncated, tagged).
    /// </remarks>
    ValueNode BuildLoadTypedArrayLengthAsNumber(ValueNode obj) =>
        AddNewNode(new ValueNode(Opcode.LoadTypedArrayLength, ValueRepresentation.kFloat64)
        {
            Inputs = [GetTaggedValue(obj)],
            Int0 = 1,
            Type = NodeType.kNumber,
            Properties = OpProperties.kCanRead,
        });

    ValueNode BuildLoadTypedArrayLength(ValueNode obj) =>
        AddNewNode(new ValueNode(Opcode.LoadTypedArrayLength, ValueRepresentation.kInt32)
        {
            Inputs = [obj],
            Type = NodeType.kSmi,
            Properties = OpProperties.kCanRead,
        });

    ValueNode BuildTypedArrayElementLoad(ValueNode obj, ValueNode key, List<(Map Map, JSValue Handler)> feedback, ElementsKind kind,
        bool handlesOOB)
    {
        var maps = new Map[feedback.Count];
        for (int i = 0; i < maps.Length; i++) maps[i] = feedback[i].Map;
        BuildCheckMaps(obj, maps);
        BuildCheckTypedArrayValidOrDepend(obj, write: false);
        ValueNode index = GetInt32ElementIndex(key);
        ValueNode length = BuildLoadTypedArrayLength(obj);
        ValueNode BuildLoad() => AddNewNode(new ValueNode(Opcode.LoadTypedArrayElement, TypedArrayElementRepresentation(kind))
        {
            Inputs = [obj, index],
            Int0 = (int)kind,
            Type = NodeType.kNumber,
            Properties = OpProperties.kCanRead,
        });
        if (handlesOOB && _info.DependOnProtector(Protectors.IsNoElementsIntact(Isolate), "NoElements"))
        {
            // GetUint32ElementIndex, then a Select of the bounds check.
            AddNewNode(new Node(Opcode.CheckInt32Condition)
            {
                Inputs = [GetInt32Constant(-1), index],
                Int0 = (int)CompareOperation.kLessThan,
                Properties = OpProperties.kEagerDeopt,
            }, DeoptimizeReason.kNotUint32);
            var branch = new ControlNode(Opcode.BranchIfInt32Compare)
            {
                Inputs = [index, length],
                Operation = CompareOperation.kLessThan,
                Unsigned = true,
            };
            return Select(branch, BuildLoad, () => GetRootConstant(RootIndex.kUndefinedValue));
        }
        AddNewNode(new Node(Opcode.CheckInt32Condition)
        {
            Inputs = [index, length],
            Int0 = (int)CompareOperation.kLessThan,
            Int1 = 1,
            Properties = OpProperties.kEagerDeopt,
        }, DeoptimizeReason.kOutOfBounds);
        return BuildLoad();
    }

    void BuildTypedArrayElementStore(ValueNode obj, ValueNode key, ValueNode value, List<(Map Map, JSValue Handler)> feedback,
        ElementsKind kind, bool ignoreOOB)
    {
        var maps = new Map[feedback.Count];
        for (int i = 0; i < maps.Length; i++) maps[i] = feedback[i].Map;
        BuildCheckMaps(obj, maps);
        BuildCheckTypedArrayValidOrDepend(obj, write: true);
        ValueNode index = GetInt32ElementIndex(key);
        // The value as the kind's number type (ToNumber of an oddball is fine: typed arrays store it).
        ValueNode stored = kind switch
        {
            ElementsKind.FLOAT32_ELEMENTS or ElementsKind.FLOAT64_ELEMENTS => GetFloat64(value, NodeType.kNumberOrOddball),
            ElementsKind.UINT8_CLAMPED_ELEMENTS => value.IsInt32 ? value : GetFloat64(value, NodeType.kNumberOrOddball),
            _ => GetTruncatedInt32ForToNumber(value, NodeType.kNumberOrOddball),
        };
        if (!ignoreOOB)
        {
            ValueNode length = BuildLoadTypedArrayLength(obj);
            AddNewNode(new Node(Opcode.CheckInt32Condition)
            {
                Inputs = [index, length],
                Int0 = (int)CompareOperation.kLessThan,
                Int1 = 1,
                Properties = OpProperties.kEagerDeopt,
            }, DeoptimizeReason.kOutOfBounds);
        }
        AddNewNode(new Node(Opcode.StoreTypedArrayElement)
        {
            Inputs = [obj, index, stored],
            Int0 = (int)kind,
            // Out of bounds (and detached) stores are ignored (kIgnoreTypedArrayOOB).
            Int1 = ignoreOOB ? 1 : 0,
            Properties = OpProperties.kCanWrite | OpProperties.kNotIdempotent,
        });
    }

    /// <summary>LoadModeHandlesOOB over the element handlers.</summary>
    static bool LoadModeHandlesOOB(List<(Map Map, JSValue Handler)> feedback)
    {
        foreach ((Map _, JSValue handler) in feedback)
        {
            if (handler.HeapObjectOrNull is not LoadHandler { AllowOutOfBounds: true }) return false;
        }
        return true;
    }

    /// <summary>The element load of BuildElementLoadOnJSArrayOrJSObject (emit_load), after the bounds check.</summary>
    ValueNode BuildElementLoad(ValueNode elements, ValueNode index, ElementsKind kind, bool anyHoley, Map[] maps,
        List<(Map Map, JSValue Handler)> feedback)
    {
        // Holes are undefined when the prototype chain has no elements and the
        // IC handled holes (double elements: whatever the IC saw, as V8's
        // BuildLoadHoleyFixedDoubleArrayElement).
        bool convertHole = anyHoley && CanTreatHoleAsUndefined(maps) &&
            (ElementsKinds.IsDoubleElementsKind(kind) || LoadModeHandlesHoles(feedback));
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
                // The hole is undefined: as a tagged value (an untagged HoleyFloat64
                // would be taken for a number by the reductions of its uses).
                return convertHole ? GetTaggedValue(holey) : GetFloat64(holey);
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
        BuildLoadProperty(obj, PropertyKeys.kElements, () => AddNewNode(new ValueNode(Opcode.LoadElements, ValueRepresentation.kTagged)
        {
            Inputs = [obj],
            Type = NodeType.kOtherHeapObject,
            Properties = OpProperties.kCanRead,
        }));

    /// <summary>index &lt; length (unsigned), deoptimizing out of bounds.</summary>
    void BuildBoundsCheck(ValueNode obj, ValueNode elements, ValueNode index, bool isJSArray)
    {
        ValueNode length = BuildLoadLength(obj, elements, isJSArray);
        AddNewNode(new Node(Opcode.CheckInt32Condition)
        {
            Inputs = [index, length],
            Int0 = (int)CompareOperation.kLessThan,
            Int1 = 1, // unsigned
            Properties = OpProperties.kEagerDeopt,
        }, DeoptimizeReason.kOutOfBounds);
    }

    /// <summary>The length elements accesses check: a JSArray's length, else the backing store's.</summary>
    ValueNode BuildLoadLength(ValueNode obj, ValueNode elements, bool isJSArray) =>
        isJSArray
            ? GetInt32(BuildLoadProperty(obj, PropertyKeys.kJSArrayLength, () => AddNewNode(new ValueNode(Opcode.LoadJSArrayLength, ValueRepresentation.kTagged)
            {
                Inputs = [obj],
                Type = NodeType.kSmi,
                Properties = OpProperties.kCanRead,
            })))
            : BuildLoadProperty(elements, PropertyKeys.kFixedArrayLength, () => AddNewNode(new ValueNode(Opcode.LoadFixedArrayLength, ValueRepresentation.kInt32)
            {
                Inputs = [elements],
                Type = NodeType.kSmi,
                Properties = OpProperties.kCanRead,
            }));

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
        if (nexus.IcState() == InlineCacheState.MEGAMORPHIC)
        {
            // BuildCallBuiltin<KeyedStoreIC_Megamorphic>.
            CallMaglev("KeyedStoreICMegamorphic", [obj, key, value],
                [BuiltinArg.Isolate, Fv, BuiltinArg.I(slot), BuiltinArg.In(0), BuiltinArg.In(1), BuiltinArg.In(2)], OpProperties.kGenericCall);
            return;
        }
        CallBaseline("SetKeyedProperty", [obj, key, value],
            [BuiltinArg.Isolate, Fv, BuiltinArg.I(slot), BuiltinArg.In(0), BuiltinArg.In(1), BuiltinArg.In(2)]);
    }

    /// <summary>MapRef::PrototypesElementsDoNotHaveAccessorsOrThrow.</summary>
    static bool PrototypesElementsDoNotHaveAccessorsOrThrow(Map map, ref List<Map>? prototypeMaps)
    {
        for (JSReceiver? prototype = map.Prototype; prototype is not null; prototype = prototype.Map.Prototype)
        {
            // Non-extensible and sealed fast elements behave like fast elements
            // for stores into holes on the receiver; frozen ones do not (the
            // "override mistake").
            Map prototypeMap = prototype.Map;
            if (!InstanceTypeChecks.IsJSObject(prototypeMap.InstanceType) || !prototypeMap.IsStable ||
                !ElementsKinds.IsFastOrNonextensibleOrSealedElementsKind(prototypeMap.ElementsKind))
            {
                return false;
            }
            (prototypeMaps ??= []).Add(prototypeMap);
        }
        return true;
    }

    bool TryBuildElementStore(ValueNode obj, ValueNode key, ValueNode value, List<(Map Map, JSValue Handler)> feedback)
    {
        if (obj.Representation != ValueRepresentation.kTagged) return false;
        if (CollectTypedArrayAccess(feedback, load: false, out ElementsKind typedKind, out bool ignoreOOB))
        {
            BuildTypedArrayElementStore(obj, key, value, feedback, typedKind, ignoreOOB);
            return true;
        }
        bool grouped = CollectElementAccess(feedback, load: false, out ElementsKind kind, out bool isJSArray, out bool _);
        if (!grouped)
        {
            // TryBuildPolymorphicElementAccess: one store per map (a map with
            // an elements kind transition first transitions the object).
            if (feedback.Count < 2 && !IsElementsTransitionStore(feedback[0], out _)) return false;
            foreach ((Map Map, JSValue Handler) entry in feedback)
            {
                if (!CollectElementAccess([entry], load: false, out _, out _, out _) && !IsElementsTransitionStore(entry, out _)) return false;
            }
        }
        KeyedAccessStoreMode mode = KeyedAccessStoreMode.kInBounds;
        foreach ((Map _, JSValue handler) in feedback)
        {
            KeyedAccessStoreMode m = ((StoreHandler)handler.Object).StoreMode;
            if (m == KeyedAccessStoreMode.kIgnoreTypedArrayOOB) return false;
            if (m != KeyedAccessStoreMode.kInBounds) mode = KeyedAccessStoreMode.kGrowAndHandleCOW;
        }
        var maps = new Map[feedback.Count];
        for (int i = 0; i < maps.Length; i++) maps[i] = feedback[i].Map;
        var checkedMaps = new List<Map>(maps);
        foreach ((Map Map, JSValue Handler) entry in feedback)
        {
            if (IsElementsTransitionStore(entry, out Map? target)) checkedMaps.Add(target!);
        }
        // For holey stores or growing stores, the prototype chain must have no
        // element setters, guarded by dependencies on the stable prototype maps.
        List<Map>? prototypeMaps = null;
        foreach (Map map in checkedMaps)
        {
            if ((ElementsKinds.IsHoleyOrDictionaryElementsKind(map.ElementsKind) || mode != KeyedAccessStoreMode.kInBounds) &&
                !PrototypesElementsDoNotHaveAccessorsOrThrow(map, ref prototypeMaps))
            {
                return false;
            }
        }
        if (prototypeMaps is not null)
        {
            foreach (Map m in prototypeMaps) _info.AddDependency(m, Objects.DependentCode.DependencyGroups.PrototypeCheck);
        }
        if (!grouped)
        {
            ValueNode polymorphicIndex = GetInt32ElementIndex(key);
            var cases = new List<(Map[] Maps, Func<ValueNode?> Build)>(feedback.Count);
            foreach ((Map Map, JSValue Handler) entry in feedback)
            {
                if (IsElementsTransitionStore(entry, out Map? target))
                {
                    // BuildTransitionElementsKindOrCheckMap: the source map transitions to the target.
                    Map to = target!;
                    cases.Add(([entry.Map], () =>
                    {
                        AddNewNode(new Node(Opcode.TransitionElementsKind)
                        {
                            Inputs = [obj],
                            Obj0 = to,
                            Properties = OpProperties.kEagerDeopt | OpProperties.kCanAllocate | OpProperties.kCanWrite |
                                         OpProperties.kNotIdempotent,
                        }, DeoptimizeReason.kWrongMap);
                        RecordKnownMaps(obj, [to]);
                        BuildElementStore(obj, polymorphicIndex, value, to.ElementsKind, to.InstanceType == InstanceType.JSArrayType, mode);
                        return null;
                    }));
                    continue;
                }
                CollectElementAccess([entry], load: false, out ElementsKind entryKind, out bool entryIsJSArray, out bool _);
                cases.Add(([entry.Map], () =>
                {
                    BuildElementStore(obj, polymorphicIndex, value, entryKind, entryIsJSArray, mode);
                    return null;
                }));
            }
            BuildPolymorphicAccess(obj, cases, hasResult: false);
            return true;
        }
        BuildCheckMaps(obj, maps);
        BuildElementStore(obj, GetInt32ElementIndex(key), value, kind, isJSArray, mode);
        return true;
    }

    /// <summary>
    /// A store handler that transitions the elements kind of a fast map to a
    /// more general fast kind (StoreHandler::StoreElementTransition).
    /// </summary>
    static bool IsElementsTransitionStore((Map Map, JSValue Handler) entry, out Map? target)
    {
        target = null;
        if (entry.Handler.HeapObjectOrNull is not StoreHandler
            {
                HandlerKind: StoreHandler.Kind.kElement, ElementsTransitionMap: { } to,
            } handler)
        {
            return false;
        }
        Map from = entry.Map;
        if (!ElementsKinds.IsFastElementsKind(from.ElementsKind) || !ElementsKinds.IsFastElementsKind(to.ElementsKind) ||
            from.InstanceType != to.InstanceType || handler.StoreMode == KeyedAccessStoreMode.kIgnoreTypedArrayOOB)
        {
            return false;
        }
        target = to;
        return true;
    }

    /// <summary>TryBuildElementStoreOnJSArrayOrJSObject after the map check.</summary>
    void BuildElementStore(ValueNode obj, ValueNode index, ValueNode value, ElementsKind kind, bool isJSArray,
        KeyedAccessStoreMode mode)
    {
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
    }
}
