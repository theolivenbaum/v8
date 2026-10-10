// Port of the for-in parts of src/maglev/maglev-graph-builder.cc:
// VisitForInPrepare, VisitForInNext (the enum cache fast paths of their
// ForInHint) and TryBuildGetKeyedPropertyWithEnumeratedKey, with the
// builder's ForInState (maglev-graph-builder.h).
using V8Sharp.Deoptimizer;
using V8Sharp.Interpreter;

namespace V8Sharp.Maglev;

public sealed partial class MaglevGraphBuilder
{
    /// <summary>MaglevGraphBuilder::ForInState: what the current for-in loop knows.</summary>
    struct ForInState
    {
        public ValueNode? Receiver;
        public ValueNode? CacheType;
        public ValueNode? EnumCacheIndices;
        public ValueNode? Key;
        public ValueNode? Index;
        public bool ReceiverNeedsMapCheck;
    }

    ForInState _forInState;
    /// <summary>may_have_changed_maps_: a node of this graph (or an inlined one) may have changed a map.</summary>
    bool _mayHaveChangedMaps;
    /// <summary>in_peeled_iteration(): the builder is in the first, peeled iteration of a loop.</summary>
    bool _inPeeledIteration;

    /// <summary>JSHeapBroker::GetFeedbackForForIn: the ForInHint of the slot's combined ForInFeedback.</summary>
    ForInFeedback ForInHint(int slot)
    {
        JSValue feedback = _unit.Feedback.Slots[slot];
        int value = feedback.IsNumber ? (int)feedback.Number : 0;
        return value switch
        {
            0 => ForInFeedback.kNone,
            (int)ForInFeedback.kEnumCacheKeysAndIndices => ForInFeedback.kEnumCacheKeysAndIndices,
            (int)ForInFeedback.kEnumCacheKeys => ForInFeedback.kEnumCacheKeys,
            _ => ForInFeedback.kAny,
        };
    }

    /// <summary>VisitForInPrepare: ForInPrepare &lt;cache_info_triple&gt;.</summary>
    void VisitForInPrepare()
    {
        ValueNode enumerator = GetAccumulator();
        // Catch the receiver value passed from ForInEnumerate.
        ValueNode? receiver = _forInState.Receiver;
        int slot = FeedbackSlot(1);
        Register cacheTypeReg = _it.GetRegisterOperand(0);
        var cacheArrayReg = new Register(cacheTypeReg.Index + 1);
        var cacheLengthReg = new Register(cacheTypeReg.Index + 2);
        ForInFeedback hint = ForInHint(slot);
        _forInState = default;
        if (hint != ForInFeedback.kAny && receiver is not null && receiver.Representation == ValueRepresentation.kTagged &&
            enumerator.Representation == ValueRepresentation.kTagged)
        {
            // Check that the {enumerator} is a Map: compare it with the
            // receiver's map (by definition the enumerator is either the
            // receiver's map or a FixedArray).
            ValueNode receiverMap = BuildLoadMapForForIn(receiver);
            AddNewNode(new Node(Opcode.CheckDynamicValue)
            {
                Inputs = [receiverMap, enumerator],
                Properties = OpProperties.kEagerDeopt,
            }, DeoptimizeReason.kWrongMapDynamic);
            ValueNode cacheArray = AddNewNode(new ValueNode(Opcode.LoadEnumCacheKeys, ValueRepresentation.kTagged)
            {
                Inputs = [enumerator],
                Type = NodeType.kOtherHeapObject,
                Properties = OpProperties.kCanRead,
            });
            ValueNode cacheLength = AddNewNode(new ValueNode(Opcode.LoadEnumCacheLength, ValueRepresentation.kInt32)
            {
                Inputs = [enumerator],
                Type = NodeType.kSmi,
                Properties = OpProperties.kCanRead,
            });
            if (hint == ForInFeedback.kEnumCacheKeysAndIndices)
            {
                ValueNode cacheIndices = AddNewNode(new ValueNode(Opcode.LoadEnumCacheIndices, ValueRepresentation.kTagged)
                {
                    Inputs = [enumerator],
                    Type = NodeType.kOtherHeapObject,
                    Properties = OpProperties.kCanRead,
                });
                AddNewNode(new Node(Opcode.CheckCacheIndicesNotCleared)
                {
                    Inputs = [cacheIndices, cacheLength],
                    Properties = OpProperties.kEagerDeopt,
                }, DeoptimizeReason.kWrongEnumIndices);
                _forInState.EnumCacheIndices = cacheIndices;
            }
            StoreRegister(cacheTypeReg, enumerator);
            StoreRegister(cacheArrayReg, cacheArray);
            StoreRegister(cacheLengthReg, cacheLength);
            return;
        }
        // kAny: the builtin fills the register triple (the cache type first, so
        // a lazy deopt of the call has it in the translation frame).
        WithLazyResult<ValueNode?>(cacheTypeReg, 3, () =>
        {
            CallBaseline("ForInPrepare", [enumerator],
                [BuiltinArg.Isolate, Fv, BuiltinArg.I(slot), BuiltinArg.In(0), BuiltinArg.RegRef(cacheTypeReg),
                 BuiltinArg.RegRef(cacheArrayReg), BuiltinArg.RegRef(cacheLengthReg)]);
            return null!;
        });
        LoadRegisterOutputs(cacheTypeReg, 3);
        // The cache length is a Smi.
        EnsureType(_frame.Get(cacheLengthReg), NodeType.kSmi);
        SetAccumulator(GetSmiConstant(0));
    }

    /// <summary>VisitForInNext: ForInNext &lt;receiver&gt; &lt;index&gt; &lt;cache_info_pair&gt;.</summary>
    void VisitForInNext()
    {
        ValueNode receiver = LoadRegister(0);
        Register pair = _it.GetRegisterOperand(2);
        ValueNode cacheType = _frame.Get(pair);
        ValueNode cacheArray = _frame.Get(new Register(pair.Index + 1));
        int slot = FeedbackSlot(3);
        ForInFeedback hint = ForInHint(slot);
        if (hint != ForInFeedback.kAny && receiver.Representation == ValueRepresentation.kTagged &&
            cacheType.Representation == ValueRepresentation.kTagged && cacheArray.Opcode == Opcode.LoadEnumCacheKeys)
        {
            ValueNode index = GetInt32(LoadRegister(1));
            // Ensure that the expected map still matches that of the {receiver}.
            ValueNode receiverMap = BuildLoadMapForForIn(receiver);
            AddNewNode(new Node(Opcode.CheckDynamicValue)
            {
                Inputs = [receiverMap, cacheType],
                Properties = OpProperties.kEagerDeopt,
            }, DeoptimizeReason.kWrongMapDynamic);
            ValueNode key = AddNewNode(new ValueNode(Opcode.LoadFixedArrayElement, ValueRepresentation.kTagged)
            {
                Inputs = [cacheArray, index],
                Type = NodeType.kInternalizedString,
                Properties = OpProperties.kCanRead,
            });
            SetAccumulator(key);
            // The receiver of the keyed loads in the body is the object the
            // ToObject before ForInEnumerate converted.
            _forInState.Receiver = receiver.Opcode == Opcode.CallBuiltin && receiver.Obj0 is CallBuiltinInfo { Name: "ToObject" }
                ? receiver.Inputs[0]
                : receiver;
            _forInState.ReceiverNeedsMapCheck = false;
            _forInState.CacheType = cacheType;
            _forInState.Key = key;
            if (hint == ForInFeedback.kEnumCacheKeysAndIndices) _forInState.Index = index;
            // (V8 also skips the JumpIfUndefined after it: the key is never
            // undefined. Here the key's type folds that branch.)
            return;
        }
        SetAccumulator(CallBaseline("ForInNext", [receiver, LoadRegister(1), cacheType, cacheArray],
            [BuiltinArg.Isolate, Fv, BuiltinArg.I(slot), BuiltinArg.In(0), BuiltinArg.In(1), BuiltinArg.In(2),
             BuiltinArg.In(3)])!);
    }

    /// <summary>
    /// TryBuildGetKeyedPropertyWithEnumeratedKey: a keyed load in the body of
    /// a for-in whose key is the loop's key loads the field through the enum
    /// cache's field indices (LoadTaggedFieldByFieldIndex), after a map check
    /// when something may have changed the receiver's map since ForInNext.
    /// </summary>
    bool TryBuildGetKeyedPropertyWithEnumeratedKey(ValueNode obj, int slot)
    {
        if (_forInState.Index is null || _forInState.EnumCacheIndices is null || _forInState.CacheType is null ||
            !ReferenceEquals(_forInState.Key, GetAccumulator()) || obj.Representation != ValueRepresentation.kTagged)
        {
            return false;
        }
        bool speculatingReceiverMapMatches = false;
        if (!ReferenceEquals(_forInState.Receiver, obj))
        {
            // When the feedback is uninitialized, it is either a keyed load that
            // always hits the enum cache, or one that was never reached: check
            // the receiver's map against the cache type.
            if (!_unit.Feedback.Slots[slot].IsIdenticalTo(JSValue.FromObject(ReadOnlyRoots.uninitialized_symbol))) return false;
            speculatingReceiverMapMatches = true;
        }
        if (_forInState.ReceiverNeedsMapCheck || speculatingReceiverMapMatches)
        {
            ValueNode receiverMap = BuildLoadMapForForIn(obj);
            AddNewNode(new Node(Opcode.CheckDynamicValue)
            {
                Inputs = [receiverMap, _forInState.CacheType],
                Properties = OpProperties.kEagerDeopt,
            }, DeoptimizeReason.kWrongMapDynamic);
            if (ReferenceEquals(_forInState.Receiver, obj)) _forInState.ReceiverNeedsMapCheck = false;
        }
        ValueNode fieldIndex = AddNewNode(new ValueNode(Opcode.LoadFixedArrayElement, ValueRepresentation.kTagged)
        {
            Inputs = [_forInState.EnumCacheIndices, _forInState.Index],
            Type = NodeType.kSmi,
            Properties = OpProperties.kCanRead,
        });
        SetAccumulator(AddNewNode(new ValueNode(Opcode.LoadTaggedFieldByFieldIndex, ValueRepresentation.kTagged)
        {
            Inputs = [obj, GetInt32(fieldIndex)],
            Properties = OpProperties.kCanRead,
        }));
        return true;
    }

    /// <summary>BuildLoadMap of a for-in receiver: its map as a tagged value (anything else's is not a map).</summary>
    ValueNode BuildLoadMapForForIn(ValueNode receiver) => AddNewNode(new ValueNode(Opcode.LoadMap, ValueRepresentation.kTagged)
    {
        Inputs = [receiver],
        Type = NodeType.kOtherHeapObject,
        Properties = OpProperties.kCanRead,
    });
}
