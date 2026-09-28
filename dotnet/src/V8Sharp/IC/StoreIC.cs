// Port of the store ICs of src/ic/ic.cc (StoreIC, StoreGlobalIC,
// KeyedStoreIC, StoreInArrayLiteralIC, and the DefineNamedOwnIC /
// DefineKeyedOwnIC flavours) with their miss handlers (Runtime_StoreIC_Miss,
// Runtime_KeyedStoreIC_Miss, Runtime_StoreGlobalIC_Miss ...), and of the
// store handler dispatch of src/ic/accessor-assembler.cc
// (HandleStoreICHandlerCase, HandleStoreICTransitionMapHandlerCase,
// HandleStoreICProtoHandler, EmitElementStore).
using System.Runtime.CompilerServices;
using V8Sharp.Interpreter;
using V8Sharp.Runtime;

namespace V8Sharp.IC;

public sealed class StoreIC : IC
{
    public StoreIC(Isolate isolate, FeedbackVector? vector, int slot, FeedbackSlotKind kind)
        : base(isolate, vector, slot, kind) { }

    // ---- Interpreter entry points (AccessorAssembler) -------------------------------------

    /// <summary>SetNamedProperty: StoreIC_BytecodeHandler.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void StoreNamed(Isolate isolate, FeedbackVector? vector, int slot, JSValue receiver, Name name, JSValue value)
    {
        if (vector is not null && receiver._obj is JSObject obj)
        {
            JSValue[] slots = vector.Slots;
            if (ReferenceEquals(slots[slot]._obj, obj.Map) && slots[slot + 1]._obj is StoreHandler handler &&
                TryStoreOwnField(obj, handler, value))
            {
                return;
            }
        }
        StoreNamedSlow(isolate, vector, slot, receiver, name, value, FeedbackSlotKind.kSetNamedStrict);
    }

    /// <summary>DefineNamedOwnProperty: DefineNamedOwnIC.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void DefineNamedOwn(Isolate isolate, FeedbackVector? vector, int slot, JSValue receiver, Name name, JSValue value)
    {
        if (vector is not null && receiver._obj is JSObject obj)
        {
            JSValue[] slots = vector.Slots;
            if (ReferenceEquals(slots[slot]._obj, obj.Map) && slots[slot + 1]._obj is StoreHandler handler &&
                TryStoreOwnField(obj, handler, value))
            {
                return;
            }
        }
        StoreNamedSlow(isolate, vector, slot, receiver, name, value, FeedbackSlotKind.kDefineNamedOwn);
    }

    /// <summary>
    /// The inline part of HandleStoreICHandlerCase: a field store or a field
    /// adding transition on the receiver itself.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static bool TryStoreOwnField(JSObject obj, StoreHandler handler, JSValue value)
    {
        switch (handler.HandlerKind)
        {
            case StoreHandler.Kind.kField:
                if (!FitsField(handler, value)) return false;
                obj._fields[handler.FieldIndex] = handler.Representation.IsDouble ? CanonicalizeDouble(value) : value;
                return true;
            case StoreHandler.Kind.kTransitionToField:
                return TryStoreTransition(obj, handler, value);
            default:
                return false;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static bool FitsField(StoreHandler handler, in JSValue value)
    {
        Representation representation = handler.Representation;
        if (representation.IsTagged) return true;
        if (!ObjectOps.FitsRepresentation(value, representation)) return false;
        Map? fieldClass = handler.FieldTypeClass;
        if (fieldClass is not null)
        {
            // A field with a class field type accepts only objects with that map.
            return value._obj is JSReceiver r && ReferenceEquals(r.Map, fieldClass);
        }
        return true;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static JSValue CanonicalizeDouble(in JSValue value) => double.IsNaN(value._num) && value.IsNumber ? JSValue.NaN : value;

    /// <summary>HandleStoreICTransitionMapHandlerCase for a transition that adds a field.</summary>
    static bool TryStoreTransition(JSObject obj, StoreHandler handler, JSValue value)
    {
        Map transition = handler.TransitionMap!;
        if (transition.IsDeprecated || !handler.IsValid) return false;
        if (!FitsField(handler, value)) return false;
        int index = handler.FieldIndex;
        if (index >= obj._fields.Length) obj.EnsureFieldCapacity(index + transition.UnusedPropertyFields() + 1);
        obj._fields[index] = handler.Representation.IsDouble ? CanonicalizeDouble(value) : value;
        obj.Map = transition;
        return true;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static void StoreNamedSlow(Isolate isolate, FeedbackVector? vector, int slot, JSValue receiver, Name name, JSValue value,
        FeedbackSlotKind defaultKind)
    {
        if (vector is not null && isolate.Flags.use_ic)
        {
            Map? map = ICMaps.MapOf(isolate, receiver);
            if (map is not null)
            {
                JSValue feedback = vector.Slots[slot];
                HeapObject? handler = null;
                if (ReferenceEquals(feedback._obj, map))
                {
                    handler = vector.Slots[slot + 1].HeapObjectOrNull;
                }
                else if (feedback._obj is FixedArray polymorphic)
                {
                    handler = LoadIC.FindPolymorphicHandler(polymorphic, map);
                }
                else if (ReferenceEquals(feedback._obj, ReadOnlyRoots.megamorphic_symbol))
                {
                    ICIsolateState state = ICIsolateState.Get(isolate);
                    handler = FeedbackMetadata.IsDefineNamedOwnICKind(vector.GetKind(slot))
                        ? state.DefineOwnStubCache.Get(name, map)
                        : state.StoreStubCache.Get(name, map);
                }
                if (handler is StoreHandler storeHandler &&
                    TryHandleStore(isolate, storeHandler, receiver, name, value, vector.GetKind(slot)))
                {
                    return;
                }
            }
        }
        Miss(isolate, vector, slot, receiver, name, value, defaultKind);
    }

    /// <summary>Runtime_StoreIC_Miss / Runtime_DefineNamedOwnIC_Miss.</summary>
    public static void Miss(Isolate isolate, FeedbackVector? vector, int slot, JSValue receiver, Name name, JSValue value,
        FeedbackSlotKind defaultKind)
    {
        var ic = new StoreIC(isolate, vector, slot, defaultKind);
        ic.UpdateState(receiver, name);
        ic.Store(receiver, name, value);
    }

    static ShouldThrow ShouldThrowFor(FeedbackSlotKind kind) =>
        FeedbackMetadata.GetLanguageModeFromSlotKind(kind) == LanguageMode.Strict ? ShouldThrow.ThrowOnError : ShouldThrow.DontThrow;

    /// <summary>
    /// Executes a store handler (HandleStoreICHandlerCase). Returns false
    /// when the handler does not apply (the caller misses).
    /// </summary>
    public static bool TryHandleStore(Isolate isolate, StoreHandler handler, JSValue receiver, Name name, JSValue value,
        FeedbackSlotKind kind)
    {
        if (!handler.IsValid) return false;
        switch (handler.HandlerKind)
        {
            case StoreHandler.Kind.kField:
            case StoreHandler.Kind.kTransitionToField:
                return receiver._obj is JSObject o0 && TryStoreOwnField(o0, handler, value);
            case StoreHandler.Kind.kConstField:
            {
                if (receiver._obj is not JSObject o) return false;
                // A const field store succeeds only when the value does not change.
                JSValue current = o._fields[handler.FieldIndex];
                if (current.IsNumber && value.IsNumber)
                {
                    return BitConverter.DoubleToInt64Bits(current._num) == BitConverter.DoubleToInt64Bits(value._num);
                }
                return current.IsIdenticalTo(value);
            }
            case StoreHandler.Kind.kTransitionToConstant:
            {
                if (receiver._obj is not JSObject o) return false;
                Map transition = handler.TransitionMap!;
                if (transition.IsDeprecated) return false;
                JSValue constant = transition.InstanceDescriptors.GetStrongValue(transition.LastAdded());
                if (!constant.IsIdenticalTo(value)) return false;
                o.Map = transition;
                return true;
            }
            case StoreHandler.Kind.kNormal:
            {
                if (receiver._obj is not JSObject o || o.HasFastProperties || o is JSGlobalObject) return false;
                NameDictionary dictionary = o.PropertyDictionary;
                InternalIndex entry = dictionary.FindEntry(name);
                if (!entry.IsFound) return false;
                PropertyDetails details = dictionary.DetailsAt(entry);
                if (details.Kind != PropertyKind.Data || details.IsReadOnly) return false;
                dictionary.ValueAtPut(entry, value);
                return true;
            }
            case StoreHandler.Kind.kGlobalCell:
            {
                var cell = handler.Data.As<PropertyCell>();
                PropertyDetails details = cell.PropertyDetails;
                if (details.IsReadOnly || details.Kind != PropertyKind.Data) return false;
                JSValue current = cell.Value;
                if (current.IsTheHole || ReferenceEquals(current.HeapObjectOrNull, Oddball.PropertyCellHole)) return false;
                if (details.CellType != PropertyCellType.Mutable &&
                    PropertyCell.UpdatedType(isolate, cell, value, details) != details.CellType)
                {
                    return false;
                }
                cell.Value = value;
                return true;
            }
            case StoreHandler.Kind.kAccessorPair:
            {
                JSValue setter = handler.Data.As<AccessorPair>().Setter;
                if (!setter.IsJSReceiver) return false;
                ObjectOps.SetPropertyWithDefinedSetter(isolate, receiver, setter.As<JSReceiver>(), value, ShouldThrowFor(kind));
                return true;
            }
            case StoreHandler.Kind.kAccessorFromPrototype:
                ObjectOps.SetPropertyWithDefinedSetter(isolate, receiver, handler.Data.As<JSReceiver>(), value, ShouldThrowFor(kind));
                return true;
            case StoreHandler.Kind.kNativeDataProperty:
            {
                var info = handler.Data.As<AccessorInfo>();
                JSReceiver target = handler.Holder ?? receiver.As<JSReceiver>();
                if (info.Setter is null || target is not JSObject holder) return false;
                info.Setter(isolate, receiver, holder, name, value, ShouldThrowFor(kind));
                return true;
            }
            case StoreHandler.Kind.kProxy:
            {
                JSReceiver target = handler.Holder ?? receiver.As<JSReceiver>();
                if (target is not JSProxy proxy) return false;
                JSProxy.SetProperty(isolate, proxy, name, value, receiver, ShouldThrowFor(kind));
                return true;
            }
            case StoreHandler.Kind.kSlow:
                if (FeedbackMetadata.IsDefineNamedOwnICKind(kind))
                {
                    // Runtime_DefineNamedOwnIC_Slow.
                    JSReceiver.CreateDataProperty(isolate, receiver, new PropertyKey(isolate, name), value, null);
                }
                else
                {
                    RuntimeObject.SetObjectProperty(isolate, receiver, name, value, StoreOrigin.MaybeKeyed);
                }
                return true;
            default:
                return false;
        }
    }

    // ---- IC proper ------------------------------------------------------------------------

    /// <summary>StoreIC::LookupForWrite.</summary>
    bool LookupForWrite(ref LookupIterator it, JSValue value, StoreOrigin storeOrigin)
    {
        // Disable ICs for non-JSObjects for now.
        JSValue obj = it.GetReceiver();
        if (obj.HeapObjectOrNull is JSProxy) return true;
        if (obj.HeapObjectOrNull is not JSObject receiver) return false;

        for (;; it.Next())
        {
            switch (it.State)
            {
                case LookupIterator.StateKind.TRANSITION:
                    throw new UnreachableException();
                case LookupIterator.StateKind.WASM_OBJECT:
                    continue;
                case LookupIterator.StateKind.JSPROXY:
                    return true;
                case LookupIterator.StateKind.MODULE_NAMESPACE:
                    return false;
                case LookupIterator.StateKind.INTERCEPTOR:
                    return true;
                case LookupIterator.StateKind.ACCESS_CHECK:
                    continue;
                case LookupIterator.StateKind.ACCESSOR:
                    return !it.IsReadOnly;
                case LookupIterator.StateKind.TYPED_ARRAY_INDEX_NOT_FOUND:
                    return false;
                case LookupIterator.StateKind.DATA:
                {
                    if (it.IsReadOnly) return false;
                    if (IsAnyDefineOwn && it.PropertyAttributes() != PropertyAttributes.NONE)
                    {
                        // IC doesn't support reconfiguration of property attributes,
                        // so just bail out to the slow handler.
                        return false;
                    }
                    JSReceiver holder = it.Holder!;
                    if (ReferenceEquals(receiver, holder))
                    {
                        it.PrepareForDataProperty(value);
                        // The previous receiver map might just have been deprecated,
                        // so reload it.
                        UpdateLookupStartObjectMap(receiver);
                        return true;
                    }

                    // Receiver != holder.
                    if (receiver is JSGlobalProxy)
                    {
                        var iter = new PrototypeIterator(_isolate, receiver);
                        return ReferenceEquals(holder, iter.GetCurrent());
                    }

                    if (it.HolderIsReceiverOrHiddenPrototype()) return false;

                    if (it.ExtendingNonExtensible(receiver)) return false;

                    it.PrepareTransitionToDataProperty(receiver, value, PropertyAttributes.NONE, storeOrigin);
                    return it.IsCacheableTransition();
                }
                case LookupIterator.StateKind.STRING_LOOKUP_START_OBJECT:
                    throw new UnreachableException();
                case LookupIterator.StateKind.NOT_FOUND:
                {
                    // If we are in StoreGlobal then check if we should throw on
                    // non-existent properties.
                    if (IsStoreGlobalIC && ObjectOps.GetShouldThrow(_isolate, null) == ShouldThrow.ThrowOnError)
                    {
                        // ICs typically does the store in two steps: prepare receiver for the
                        // transition followed by the actual store. For global objects we
                        // create a property cell when preparing for transition and install
                        // this cell in the handler. In strict mode, we throw and never
                        // initialize this property cell. The IC handler assumes that the
                        // property cell it is holding is for a property that is existing.
                        // This case violates this assumption, so use a slow stub.
                        return false;
                    }
                    JSReceiver target = it.GetStoreTarget<JSReceiver>();
                    if (it.ExtendingNonExtensible(target)) return false;
                    it.PrepareTransitionToDataProperty(target, value, PropertyAttributes.NONE, storeOrigin);
                    return it.IsCacheableTransition();
                }
            }
        }
    }

    /// <summary>StoreIC::Store.</summary>
    public JSValue Store(JSValue obj, Name name, JSValue value, StoreOrigin storeOrigin = StoreOrigin.Named)
    {
        // TODO(verwaest): Let SetProperty do the migration, since storing a property
        // might deprecate the current map again, if value does not fit.
        if (MigrateDeprecated(_isolate, obj))
        {
            var migratedKey = new PropertyKey(_isolate, name);
            if (IsDefineNamedOwnIC)
            {
                JSReceiver.CreateDataProperty(_isolate, obj, migratedKey, value, null);
            }
            else
            {
                var migrated = new LookupIterator(_isolate, obj, migratedKey);
                ObjectOps.SetProperty(ref migrated, value, StoreOrigin.Named);
            }
            return value;
        }

        bool useIc = _state != InlineCacheState.NO_FEEDBACK && _isolate.Flags.use_ic;
        // If the object is undefined or null it's illegal to try to set any
        // properties on it; throw a TypeError in that case.
        if (obj.IsNullOrUndefined)
        {
            if (useIc)
            {
                // Ensure the IC state progresses.
                UpdateLookupStartObjectMap(obj);
                if (_lookupStartObjectMap is not null) SetCache(name, StoreHandler.StoreSlow(_isolate));
            }
            return _isolate.ThrowTypeError(MessageTemplate.NonObjectPropertyStoreWithProperty, obj, name);
        }

        JSObject.MakePrototypesFast(obj, WhereToStart.StartAtPrototype, _isolate);
        var key = new PropertyKey(_isolate, name);
        var it = new LookupIterator(_isolate, obj, key,
            IsAnyDefineOwn ? LookupIterator.Configuration.OWN : LookupIterator.Configuration.DEFAULT);

        if (name.IsAnyPrivate)
        {
            if (name.IsAnyPrivateName)
            {
                if (!JSReceiver.CheckPrivateNameStore(ref it, IsDefineKeyedOwnIC)) return JSValue.Undefined;
            }

            // IC handling of private fields/symbols stores on JSProxy is not
            // supported.
            if (obj.HeapObjectOrNull is JSProxy) useIc = false;
        }

        // For IsAnyDefineOwn(), we can't simply do CreateDataProperty below
        // because we need to check the attributes before UpdateCaches updates
        // the state of the LookupIterator.
        LookupIterator.StateKind originalState = it.State;
        // We'll defer the check for JSProxy and objects with named interceptors,
        // because the defineProperty traps need to be called first if they are
        // present. We can also skip this for private names since they are not
        // bound by configurability or extensibility checks, and errors would've
        // been thrown if the private field already exists in the object.
        if (IsAnyDefineOwn && !name.IsAnyPrivateName && obj.HeapObjectOrNull is JSObject)
        {
            if (!JSObject.CheckIfCanDefineAsConfigurable(_isolate, ref it, value, null)) return JSValue.Undefined;
        }

        if (useIc) UpdateCaches(ref it, value, storeOrigin);

        // https://tc39.es/ecma262/#sec-definefield
        // https://tc39.es/ecma262/#sec-runtime-semantics-propertydefinitionevaluation
        // IsAnyDefineOwn() can be true when this method is reused by KeyedStoreIC.
        if (IsAnyDefineOwn)
        {
            if (name.IsAnyPrivateName)
            {
                // We should define private fields without triggering traps or checking
                // extensibility.
                JSReceiver.AddPrivateField(ref it, value, null);
            }
            else
            {
                DefineOwnDataProperty(ref it, originalState, value, null, storeOrigin);
            }
        }
        else
        {
            ObjectOps.SetProperty(ref it, value, storeOrigin);
        }
        return value;
    }

    /// <summary>DefineOwnDataProperty (ic.cc).</summary>
    static bool DefineOwnDataProperty(ref LookupIterator it, LookupIterator.StateKind originalState, JSValue value,
        ShouldThrow? shouldThrow, StoreOrigin storeOrigin)
    {
        // Handle special cases that can't be handled by
        // DefineOwnPropertyIgnoreAttributes first.
        switch (it.State)
        {
            case LookupIterator.StateKind.JSPROXY:
            {
                var desc = new PropertyDescriptor();
                desc.SetValue(value);
                desc.SetWritable(true);
                desc.SetEnumerable(true);
                desc.SetConfigurable(true);
                return JSProxy.DefineOwnProperty(it.Isolate, it.GetHolder<JSProxy>(), it.GetName(), ref desc, shouldThrow);
            }
            case LookupIterator.StateKind.WASM_OBJECT:
                it.Isolate.ThrowTypeError(MessageTemplate.WasmObjectsAreOpaque);
                return false;
            // When lazy feedback is disabled, the original state could be different
            // while the object is already prepared for TRANSITION.
            case LookupIterator.StateKind.TRANSITION:
                switch (originalState)
                {
                    case LookupIterator.StateKind.ACCESS_CHECK:
                    case LookupIterator.StateKind.NOT_FOUND:
                        return ObjectOps.AddDataProperty(ref it, value, PropertyAttributes.NONE, null, storeOrigin,
                            EnforceDefineSemantics.Define);
                    default:
                        throw new UnreachableException();
                }
        }

        // We need to restart to handle interceptors properly.
        it.Restart();

        return JSObject.DefineOwnPropertyIgnoreAttributes(ref it, value, PropertyAttributes.NONE, shouldThrow,
            JSObject.AccessorInfoHandling.DONT_FORCE_FIELD, EnforceDefineSemantics.Define, storeOrigin);
    }

    /// <summary>MayHaveTypedArrayInPrototypeChain (ic.cc).</summary>
    internal static bool MayHaveTypedArrayInPrototypeChain(Isolate isolate, JSObject obj)
    {
        for (var iter = new PrototypeIterator(isolate, obj, WhereToStart.StartAtReceiver); !iter.IsAtEnd; iter.Advance())
        {
            // Be conservative, don't walk into proxies.
            if (iter.GetCurrent() is JSProxy or JSTypedArray) return true;
        }
        return false;
    }

    /// <summary>StoreIC::UpdateCaches.</summary>
    void UpdateCaches(ref LookupIterator lookup, JSValue value, StoreOrigin storeOrigin)
    {
        if (_lookupStartObjectMap is null) return;
        HeapObject handler;
        if (lookup.IsElement() && !IsAnyDefineOwn && lookup.GetReceiver().HeapObjectOrNull is JSObject receiverObject &&
            MayHaveTypedArrayInPrototypeChain(_isolate, receiverObject))
        {
            // Make sure we don't handle this in IC if there's any JSTypedArray in
            // the {receiver}'s prototype chain, since that prototype is going to
            // swallow all stores that are out-of-bounds for said prototype, and we
            // just let the runtime deal with the complexity of this.
            handler = StoreHandler.StoreSlow(_isolate);
        }
        else if (LookupForWrite(ref lookup, value, storeOrigin))
        {
            if (IsStoreGlobalIC)
            {
                if (lookup.State == LookupIterator.StateKind.DATA &&
                    ReferenceEquals(lookup.GetReceiver().HeapObjectOrNull, lookup.Holder))
                {
                    // Now update the cell in the feedback vector.
                    _nexus.ConfigurePropertyCellMode(lookup.GetPropertyCell());
                    return;
                }
            }
            handler = ComputeHandler(ref lookup);
        }
        else
        {
            handler = StoreHandler.StoreSlow(_isolate);
        }
        // Can't use {lookup->name()} because the LookupIterator might be in
        // "elements" mode for keys that are strings representing integers above
        // JSArray::kMaxIndex.
        SetCache(lookup.GetName(), handler);
    }

    /// <summary>StoreIC::ComputeHandler.</summary>
    HeapObject ComputeHandler(ref LookupIterator lookup)
    {
        switch (lookup.State)
        {
            case LookupIterator.StateKind.TRANSITION:
            {
                JSObject storeTarget = lookup.GetStoreTarget<JSObject>();
                if (storeTarget is JSGlobalObject)
                {
                    if (LookupStartObjectMap.InstanceType == InstanceType.JSGlobalObjectType)
                    {
                        return StoreHandler.StoreGlobal(_isolate, lookup.TransitionCell);
                    }
                    // V8 installs a StoreGlobalProxy handler through the prototype;
                    // V8Sharp stores through the global proxy in the runtime.
                    return StoreHandler.StoreSlow(_isolate);
                }
                Map transition = lookup.TransitionMap;
                if (transition.IsDictionaryMap) return StoreHandler.StoreSlow(_isolate);
                PropertyDetails details = transition.GetLastDescriptorDetails();
                if (details.Location != PropertyLocation.Field)
                {
                    return StoreHandler.StoreTransitionToConstant(_isolate, LookupStartObjectMap, transition);
                }
                FieldIndex index = FieldIndex.ForDetails(transition, details);
                return StoreHandler.StoreTransitionToField(_isolate, LookupStartObjectMap, transition, index,
                    details.Representation, transition.InstanceDescriptors.GetFieldType(transition.LastAdded()));
            }

            case LookupIterator.StateKind.INTERCEPTOR:
                return StoreHandler.StoreSlow(_isolate);

            case LookupIterator.StateKind.ACCESSOR:
            {
                // This is currently guaranteed by checks in StoreIC::Store.
                JSReceiver receiver = lookup.GetReceiver().As<JSReceiver>();
                if (lookup.Holder is not JSObject holder) return StoreHandler.StoreSlow(_isolate);

                if (IsAnyDefineOwn) return StoreHandler.StoreSlow(_isolate);
                if (!holder.HasFastProperties) return StoreHandler.StoreSlow(_isolate);
                HeapObject accessors = lookup.GetAccessors();
                if (accessors is AccessorInfo info)
                {
                    if (!info.HasSetter) return StoreHandler.StoreSlow(_isolate);
                    if (!lookup.HolderIsReceiverOrHiddenPrototype()) return StoreHandler.StoreSlow(_isolate);
                    if (ReferenceEquals(receiver, holder))
                    {
                        // The receiver is the holder: the handler is shared by every
                        // receiver with this map, so it must not capture this one.
                        return StoreHandler.StoreNativeDataProperty(_isolate, null, info, null);
                    }
                    return StoreHandler.StoreNativeDataProperty(_isolate, holder, info,
                        Map.GetOrCreatePrototypeChainValidityCell(LookupStartObjectMap, _isolate));
                }
                if (accessors is AccessorPair accessorPair)
                {
                    JSValue setter = accessorPair.Setter;
                    if (!(setter.HeapObjectOrNull is JSFunction setterFunction && setterFunction.Map.IsCallable))
                    {
                        return StoreHandler.StoreSlow(_isolate);
                    }
                    if (ReferenceEquals(receiver, holder)) return StoreHandler.StoreAccessorPair(_isolate, accessorPair);
                    return StoreHandler.StoreAccessorFromPrototype(_isolate, LookupStartObjectMap, holder, setter);
                }
                return StoreHandler.StoreSlow(_isolate);
            }

            case LookupIterator.StateKind.DATA:
            {
                JSObject holder = lookup.GetHolder<JSObject>();
                // -------------- Elements (for TypedArrays) -------------
                if (lookup.IsElement(holder)) return StoreHandler.StoreSlow(_isolate);
                if (lookup.IsDictionaryHolder)
                {
                    if (holder is JSGlobalObject)
                    {
                        return StoreHandler.StoreGlobal(_isolate, lookup.GetPropertyCell());
                    }
                    return StoreHandler.StoreNormal(_isolate);
                }

                // -------------- Fields --------------
                if (lookup.PropertyDetails.Location == PropertyLocation.Field)
                {
                    InternalIndex descriptor = lookup.GetFieldDescriptorIndex();
                    FieldIndex index = lookup.GetFieldIndex();
                    PropertyConstness constness = lookup.Constness;
                    if (constness == PropertyConstness.Const && FeedbackMetadata.IsDefineNamedOwnICKind(_nexus.Kind))
                    {
                        // DefineNamedOwnICs are used for initializing object literals
                        // therefore we must store the value unconditionally even to
                        // VariableMode::kConst fields.
                        constness = PropertyConstness.Mutable;
                    }
                    return StoreHandler.StoreField(_isolate, index, lookup.Representation,
                        holder.Map.InstanceDescriptors.GetFieldType(descriptor), constness == PropertyConstness.Const);
                }

                // -------------- Constant properties --------------
                return StoreHandler.StoreSlow(_isolate);
            }

            case LookupIterator.StateKind.JSPROXY:
                // IsDefineNamedOwnIC() is true when we are defining public fields on a
                // Proxy. IsDefineKeyedOwnIC() is true when we are defining computed
                // fields in a Proxy. In these cases use the slow stub to invoke the
                // define trap.
                if (IsDefineNamedOwnIC || IsDefineKeyedOwnIC) return StoreHandler.StoreSlow(_isolate);
                return StoreHandler.StoreProxy(_isolate);

            default:
                throw new UnreachableException();
        }
    }
}

/// <summary>StoreGlobalIC: SetGlobal / StaGlobal.</summary>
public static class StoreGlobalIC
{
    /// <summary>StaGlobal: StoreGlobalIC_BytecodeHandler.</summary>
    public static void Store(Isolate isolate, FeedbackVector? vector, int slot, Context context, Name name, JSValue value)
    {
        if (vector is not null)
        {
            JSValue feedback = vector.Slots[slot];
            if (feedback._obj is PropertyCell cell)
            {
                PropertyDetails details = cell.PropertyDetails;
                JSValue current = cell.Value;
                if (!details.IsReadOnly && details.Kind == PropertyKind.Data && !current.IsTheHole &&
                    !ReferenceEquals(current.HeapObjectOrNull, Oddball.PropertyCellHole) &&
                    (details.CellType == PropertyCellType.Mutable ||
                     PropertyCell.UpdatedType(isolate, cell, value, details) == details.CellType))
                {
                    cell.Value = value;
                    return;
                }
            }
            else if (feedback.IsNumber)
            {
                // Lexical variable mode: (script context index, slot index, immutability).
                int config = (int)feedback._num;
                if (((config >> FeedbackNexus.kImmutabilityShift) & 1) == 0)
                {
                    int contextIndex = config & FeedbackNexus.kContextIndexMask;
                    int slotIndex = (config >> FeedbackNexus.kSlotIndexShift) & FeedbackNexus.kSlotIndexMask;
                    JSValue[] slots = context.NativeContext.ScriptContextTable.Get(contextIndex).Slots;
                    if (!slots[slotIndex].IsTheHole)
                    {
                        slots[slotIndex] = value;
                        return;
                    }
                }
            }
        }
        Miss(isolate, vector, slot, context, name, value);
    }

    /// <summary>Runtime_StoreGlobalIC_Miss / Runtime_StoreGlobalICNoFeedback_Miss.</summary>
    public static void Miss(Isolate isolate, FeedbackVector? vector, int slot, Context context, Name name, JSValue value)
    {
        FeedbackSlotKind kind = vector is null ? FeedbackSlotKind.kStoreGlobalStrict : vector.GetKind(slot);
        var ic = new StoreGlobalICImpl(isolate, vector, slot, kind);
        JSGlobalObject global = context.NativeContext.GlobalObject;
        if (vector is not null) ic.UpdateStateForGlobal(global, name);
        ic.Store(context.NativeContext, name, value);
    }

    sealed class StoreGlobalICImpl(Isolate isolate, FeedbackVector? vector, int slot, FeedbackSlotKind kind)
        : IC(isolate, vector, slot, kind)
    {
        public void UpdateStateForGlobal(JSGlobalObject global, Name name) => UpdateState(global, name);

        /// <summary>StoreGlobalIC::Store.</summary>
        public void Store(NativeContext nativeContext, Name name, JSValue value)
        {
            // Look up in script context table.
            var strName = (JSString)name;
            JSGlobalObject global = nativeContext.GlobalObject;
            ScriptContextTable scriptContexts = nativeContext.ScriptContextTable;

            if (scriptContexts.Lookup(strName, out ScopeInfo.VariableLookupResult lookupResult))
            {
                Context scriptContext = scriptContexts.Get(lookupResult.ContextIndex);
                if (Globals.IsImmutableLexicalVariableMode(lookupResult.Mode))
                {
                    _isolate.ThrowTypeError(MessageTemplate.ConstAssign, name, global);
                    return;
                }

                if (scriptContext.Get(lookupResult.SlotIndex).IsTheHole)
                {
                    // Do not install stubs and stay pre-monomorphic for uninitialized
                    // accesses.
                    _isolate.ThrowReferenceError(MessageTemplate.AccessedUninitializedVariable, name);
                    return;
                }

                bool useIc = _state != InlineCacheState.NO_FEEDBACK && _isolate.Flags.use_ic;
                if (useIc)
                {
                    if (!_nexus.ConfigureLexicalVarMode(lookupResult.ContextIndex, lookupResult.SlotIndex,
                            Globals.IsImmutableLexicalVariableMode(lookupResult.Mode)))
                    {
                        // Given combination of indices can't be encoded, so use slow stub.
                        SetCache(name, StoreHandler.StoreSlow(_isolate));
                    }
                }
                scriptContext.Set(lookupResult.SlotIndex, value);
                return;
            }

            var storeIc = new StoreIC(_isolate, _vector, _slot, _kind);
            storeIc.ShareState(this);
            storeIc.Store(global, name, value);
        }
    }
}

/// <summary>KeyedStoreIC, DefineKeyedOwnIC and StoreInArrayLiteralIC.</summary>
public sealed class KeyedStoreIC : IC
{
    public KeyedStoreIC(Isolate isolate, FeedbackVector? vector, int slot, FeedbackSlotKind kind)
        : base(isolate, vector, slot, kind) { }

    // ---- Interpreter entry points ---------------------------------------------------------

    /// <summary>SetKeyedProperty: KeyedStoreIC_Megamorphic / the element store handlers.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Store(Isolate isolate, FeedbackVector? vector, int slot, JSValue obj, JSValue key, JSValue value)
    {
        if (vector is not null && key.IsNumber && obj._obj is JSObject jsObject)
        {
            JSValue[] slots = vector.Slots;
            if (ReferenceEquals(slots[slot]._obj, jsObject.Map) && slots[slot + 1]._obj is StoreHandler handler &&
                handler.HandlerKind == StoreHandler.Kind.kElement &&
                ElementAccess.TryStoreFastElement(isolate, jsObject, key._num, handler, value))
            {
                return;
            }
        }
        StoreSlow(isolate, vector, slot, obj, key, value, FeedbackSlotKind.kSetKeyedStrict);
    }

    /// <summary>DefineKeyedOwnProperty: DefineKeyedOwnIC.</summary>
    public static void DefineKeyedOwn(Isolate isolate, FeedbackVector? vector, int slot, JSValue obj, JSValue key, JSValue value,
        DefineKeyedOwnPropertyFlags flags)
    {
        if ((flags & DefineKeyedOwnPropertyFlags.SetFunctionName) != 0)
        {
            RuntimeObject.SetFunctionName(isolate, value, key);
        }
        StoreSlow(isolate, vector, slot, obj, key, value, FeedbackSlotKind.kDefineKeyedOwn);
    }

    /// <summary>StaInArrayLiteral: StoreInArrayLiteralIC.</summary>
    public static void StoreInArrayLiteral(Isolate isolate, FeedbackVector? vector, int slot, JSValue array, JSValue index,
        JSValue value)
    {
        if (vector is not null && index.IsNumber && array._obj is JSArray jsArray)
        {
            JSValue[] slots = vector.Slots;
            if (ReferenceEquals(slots[slot]._obj, jsArray.Map) && slots[slot + 1]._obj is StoreHandler handler &&
                handler.HandlerKind == StoreHandler.Kind.kElement &&
                ElementAccess.TryStoreFastElement(isolate, jsArray, index._num, handler, value))
            {
                return;
            }
        }
        var ic = new KeyedStoreIC(isolate, vector, slot, FeedbackSlotKind.kStoreInArrayLiteral);
        ic.StoreInArrayLiteralImpl(array.As<JSArray>(), index, value);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static void StoreSlow(Isolate isolate, FeedbackVector? vector, int slot, JSValue obj, JSValue key, JSValue value,
        FeedbackSlotKind defaultKind)
    {
        if (vector is not null && isolate.Flags.use_ic)
        {
            JSValue feedback = vector.Slots[slot];
            FeedbackSlotKind kind = vector.GetKind(slot);
            if (key.HeapObjectOrNull is Name name)
            {
                // A named key that matches the keyed IC's name: named store handlers.
                if (ReferenceEquals(feedback._obj, name) && ICMaps.MapOf(isolate, obj) is Map map &&
                    vector.Slots[slot + 1]._obj is FixedArray namedFeedback &&
                    LoadIC.FindPolymorphicHandler(namedFeedback, map) is StoreHandler namedHandler &&
                    StoreIC.TryHandleStore(isolate, namedHandler, obj, name, value, kind))
                {
                    return;
                }
            }
            else if (obj._obj is JSObject jsObject && key.IsNumber && feedback._obj is FixedArray polymorphic &&
                     LoadIC.FindPolymorphicHandler(polymorphic, jsObject.Map) is StoreHandler { HandlerKind: StoreHandler.Kind.kElement } elementHandler &&
                     ElementAccess.TryStoreFastElement(isolate, jsObject, key._num, elementHandler, value))
            {
                return;
            }
        }
        Miss(isolate, vector, slot, obj, key, value, defaultKind);
    }

    /// <summary>Runtime_KeyedStoreIC_Miss / Runtime_DefineKeyedOwnIC_Miss.</summary>
    public static void Miss(Isolate isolate, FeedbackVector? vector, int slot, JSValue obj, JSValue key, JSValue value,
        FeedbackSlotKind defaultKind)
    {
        var ic = new KeyedStoreIC(isolate, vector, slot, defaultKind);
        ic.UpdateState(obj, key);
        ic.Store(obj, key, value);
    }

    // ---- IC proper ----------------------------------------------------------------------------

    KeyedAccessStoreMode GetKeyedAccessStoreMode()
    {
        // Iterate over the maps/handlers and return the first store mode found.
        var mapsAndHandlers = new List<(Map Map, JSValue Handler)>();
        _nexus.ExtractMapsAndHandlers(mapsAndHandlers);
        foreach ((Map _, JSValue handler) in mapsAndHandlers)
        {
            if (handler.HeapObjectOrNull is StoreHandler { HandlerKind: StoreHandler.Kind.kElement } h) return h.StoreMode;
        }
        return KeyedAccessStoreMode.kInBounds;
    }

    static bool StoreModeIsInBounds(KeyedAccessStoreMode mode) => mode == KeyedAccessStoreMode.kInBounds;

    static bool StoreModeCanGrow(KeyedAccessStoreMode mode) => mode == KeyedAccessStoreMode.kGrowAndHandleCOW;

    /// <summary>GetStoreMode (ic.cc).</summary>
    static KeyedAccessStoreMode GetStoreMode(JSObject receiver, ulong index)
    {
        bool oobAccess = KeyedLoadIC.IsOutOfBoundsAccess(receiver, index);
        // Don't consider this a growing store if the store would send the receiver to
        // dictionary mode.
        bool allowGrowth = receiver is JSArray && oobAccess && index <= JSArray.kMaxArrayIndex &&
                           !receiver.WouldConvertToSlowElements((uint)index);
        if (allowGrowth) return KeyedAccessStoreMode.kGrowAndHandleCOW;
        if (receiver is JSTypedArray && oobAccess) return KeyedAccessStoreMode.kIgnoreTypedArrayOOB;
        return receiver.Elements.IsCowArray ? KeyedAccessStoreMode.kHandleCOW : KeyedAccessStoreMode.kInBounds;
    }

    enum KeyType { kIntPtr, kName, kBailout }

    /// <summary>TryConvertKey (ic.cc), shared with KeyedLoadIC.</summary>
    static KeyType TryConvertKey(JSValue key, Isolate isolate, out long indexOut, out Name? nameOut)
    {
        indexOut = 0;
        nameOut = null;
        if (key.IsUndefined) { nameOut = ReadOnlyRoots.undefined_string; return KeyType.kName; }
        if (key.IsNull) { nameOut = ReadOnlyRoots.null_string; return KeyType.kName; }
        if (key.IsTrue) { nameOut = ReadOnlyRoots.true_string; return KeyType.kName; }
        if (key.IsFalse) { nameOut = ReadOnlyRoots.false_string; return KeyType.kName; }
        if (key.IsNumber)
        {
            double num = key.Number;
            if (!(num >= -EngineGlobals.kMaxSafeInteger)) return KeyType.kBailout;
            if (num > EngineGlobals.kMaxSafeInteger) return KeyType.kBailout;
            indexOut = (long)num;
            if (indexOut != num) return KeyType.kBailout;
            return KeyType.kIntPtr;
        }
        if (key.HeapObjectOrNull is JSString str)
        {
            JSString internalized = isolate.Factory.InternalizeString(str);
            if (internalized.AsArrayIndex(out uint maybeArrayIndex))
            {
                if (maybeArrayIndex <= int.MaxValue)
                {
                    indexOut = maybeArrayIndex;
                    return KeyType.kIntPtr;
                }
                return KeyType.kBailout;
            }
            nameOut = internalized;
            return KeyType.kName;
        }
        if (key.HeapObjectOrNull is Symbol symbol)
        {
            nameOut = symbol;
            return KeyType.kName;
        }
        return KeyType.kBailout;
    }

    bool VectorNeedsUpdate =>
        _state != InlineCacheState.NO_FEEDBACK && _state != InlineCacheState.MEGAMORPHIC && !_vectorSet;

    JSValue RuntimeStore(JSValue obj, JSValue key, JSValue value) =>
        IsDefineKeyedOwnIC
            ? RuntimeObject.DefineObjectOwnProperty(_isolate, obj, key, value, StoreOrigin.Named)
            : RuntimeObject.SetObjectProperty(_isolate, obj, key, value, StoreOrigin.MaybeKeyed);

    /// <summary>KeyedStoreIC::Store.</summary>
    public JSValue Store(JSValue obj, JSValue key, JSValue value)
    {
        // TODO(verwaest): Let SetProperty do the migration, since storing a property
        // might deprecate the current map again, if value does not fit.
        if (MigrateDeprecated(_isolate, obj)) return RuntimeStore(obj, key, value);

        KeyType keyType = TryConvertKey(key, _isolate, out long maybeIndex, out Name? maybeName);

        if (keyType == KeyType.kName)
        {
            var storeIc = new StoreIC(_isolate, _vector, _slot, _kind);
            storeIc.ShareState(this);
            JSValue storeResult = storeIc.Store(obj, maybeName!, value, StoreOrigin.MaybeKeyed);
            _vectorSet |= storeIc._vectorSet;
            if (VectorNeedsUpdate) ConfigureVectorStateMegamorphic(key);
            return storeResult;
        }

        JSObject.MakePrototypesFast(obj, WhereToStart.StartAtPrototype, _isolate);

        bool useIc = _state != InlineCacheState.NO_FEEDBACK && _isolate.Flags.use_ic &&
                     !ObjectOps.IsStringWrapper(obj) && obj.HeapObjectOrNull is not JSGlobalProxy;
        if (useIc && obj.HeapObjectOrNull is JSReceiver heapObject)
        {
            // Don't use ICs for maps of the objects in Array's prototype chain. We
            // expect to be able to trap element sets to objects with those maps in
            // the runtime to enable optimization of element hole access.
            if (heapObject.Map.IsMapInArrayPrototypeChain(_isolate)) useIc = false;
        }

        Map? oldReceiverMap = null;
        bool isArguments = false;
        bool keyIsValidIndex = keyType == KeyType.kIntPtr;
        KeyedAccessStoreMode storeMode = KeyedAccessStoreMode.kInBounds;
        if (useIc && obj.HeapObjectOrNull is JSReceiver receiver && keyIsValidIndex)
        {
            oldReceiverMap = receiver.Map;
            isArguments = receiver is JSArgumentsObject;
            keyIsValidIndex = maybeIndex >= 0 && (receiver is not JSTypedArray || true);
            if (receiver is JSObject receiverObject && !isArguments && keyIsValidIndex)
            {
                storeMode = GetStoreMode(receiverObject, (ulong)maybeIndex);
            }
        }

        JSValue result = RuntimeStore(obj, key, value);
        if (useIc)
        {
            if (oldReceiverMap is not null)
            {
                if (isArguments)
                {
                    // Slow stub: arguments receiver.
                }
                else if (obj.HeapObjectOrNull is JSArray array && StoreModeCanGrow(storeMode) && JSArray.HasReadOnlyLength(array))
                {
                    // Slow stub: array has read only length.
                }
                else if (obj.HeapObjectOrNull is JSObject objectReceiver && MayHaveTypedArrayInPrototypeChain(objectReceiver))
                {
                    // Slow stub: typed array in the prototype chain.
                }
                else if (keyIsValidIndex)
                {
                    if (oldReceiverMap.IsAbandonedPrototypeMap)
                    {
                        // Slow stub: receiver with prototype map.
                    }
                    else if (ElementsKinds.IsDictionaryElementsKind(oldReceiverMap.ElementsKind) ||
                             !oldReceiverMap.ShouldCheckForReadOnlyElementsInPrototypeChain(_isolate))
                    {
                        // We should go generic if receiver isn't a dictionary, but our
                        // prototype chain does have dictionary elements. This ensures that
                        // other non-dictionary receivers in the polymorphic case benefit
                        // from fast path keyed stores.
                        UpdateStoreElement(oldReceiverMap, storeMode, obj.As<JSReceiver>().Map);
                    }
                }
            }
        }

        if (VectorNeedsUpdate) ConfigureVectorStateMegamorphic(key);
        return result;
    }

    bool MayHaveTypedArrayInPrototypeChain(JSObject obj) => StoreIC.MayHaveTypedArrayInPrototypeChain(_isolate, obj);

    /// <summary>StoreOwnElement (ic.cc).</summary>
    static JSValue StoreOwnElement(Isolate isolate, JSArray array, JSValue index, JSValue value)
    {
        var key = new PropertyKey(isolate, index);
        var it = new LookupIterator(isolate, array, key, LookupIterator.Configuration.OWN);
        JSObject.DefineOwnPropertyIgnoreAttributes(ref it, value, PropertyAttributes.NONE);
        return value;
    }

    /// <summary>StoreInArrayLiteralIC::Store.</summary>
    JSValue StoreInArrayLiteralImpl(JSArray array, JSValue index, JSValue value)
    {
        if (!_isolate.Flags.use_ic || _state == InlineCacheState.NO_FEEDBACK || MigrateDeprecated(_isolate, array))
        {
            return StoreOwnElement(_isolate, array, index, value);
        }

        KeyedAccessStoreMode storeMode = KeyedAccessStoreMode.kInBounds;
        if (index.IsSmi) storeMode = GetStoreMode(array, (uint)(int)index._num);

        Map oldArrayMap = array.Map;
        StoreOwnElement(_isolate, array, index, value);

        if (index.IsSmi) UpdateStoreElement(oldArrayMap, storeMode, array.Map);

        if (VectorNeedsUpdate) ConfigureVectorStateMegamorphic(index);
        return value;
    }

    /// <summary>KeyedStoreIC::UpdateStoreElement.</summary>
    void UpdateStoreElement(Map receiverMap, KeyedAccessStoreMode storeMode, Map newReceiverMap)
    {
        var targetMapsAndHandlers = new List<(Map Map, JSValue Handler)>();
        _nexus.ExtractMapsAndHandlers(targetMapsAndHandlers);
        for (int i = targetMapsAndHandlers.Count - 1; i >= 0; i--)
        {
            // Map::TryUpdate: drop deprecated maps that cannot be updated.
            Map? updated = Map.TryUpdate(_isolate, targetMapsAndHandlers[i].Map);
            if (updated is null) targetMapsAndHandlers.RemoveAt(i);
            else targetMapsAndHandlers[i] = (updated, targetMapsAndHandlers[i].Handler);
        }
        if (targetMapsAndHandlers.Count == 0)
        {
            Map monomorphicMap = receiverMap;
            // If we transitioned to a map that is a more general map than incoming
            // then use the new map.
            if (IsTransitionOfMonomorphicTargetForElements(receiverMap, newReceiverMap)) monomorphicMap = newReceiverMap;
            HeapObject monomorphicHandler = StoreElementHandler(monomorphicMap, storeMode, null);
            ConfigureVectorState(null, monomorphicMap, monomorphicHandler);
            return;
        }

        foreach ((Map map, JSValue _) in targetMapsAndHandlers)
        {
            if (map.InstanceType == InstanceType.JSPrimitiveWrapperType) return;
        }

        // There are several special cases where an IC that is MONOMORPHIC can still
        // transition to a different IC that handles a superset of the original IC.
        // Handle those here if the receiver map hasn't changed or it has transitioned
        // to a more general kind.
        KeyedAccessStoreMode oldStoreMode = GetKeyedAccessStoreMode();
        Map previousReceiverMap = targetMapsAndHandlers[0].Map;
        if (_state == InlineCacheState.MONOMORPHIC)
        {
            Map transitionedReceiverMap = newReceiverMap;
            if (IsTransitionOfMonomorphicTargetForElements(previousReceiverMap, transitionedReceiverMap))
            {
                // If the "old" and "new" maps are in the same elements map family, or
                // if they at least come from the same origin for a transitioning store,
                // stay MONOMORPHIC and use the map for the most generic ElementsKind.
                HeapObject transitionedHandler = StoreElementHandler(transitionedReceiverMap, storeMode, null);
                ConfigureVectorState(null, transitionedReceiverMap, transitionedHandler);
                return;
            }
            // If there is no transition and if we have seen the same map earlier and
            // there is only a change in the store_mode we can still stay monomorphic.
            if (ReferenceEquals(receiverMap, previousReceiverMap) && ReferenceEquals(newReceiverMap, receiverMap) &&
                StoreModeIsInBounds(oldStoreMode) && !StoreModeIsInBounds(storeMode))
            {
                if (receiverMap.InstanceType == InstanceType.JSArrayType && JSArray.MayHaveReadOnlyLength(receiverMap)) return;
                // A "normal" IC that handles stores can switch to a version that can
                // grow at the end of the array, handle OOB accesses or copy COW arrays
                // and still stay MONOMORPHIC.
                HeapObject growHandler = StoreElementHandler(receiverMap, storeMode, null);
                ConfigureVectorState(null, receiverMap, growHandler);
                return;
            }
        }

        bool mapAdded = AddOneReceiverMapIfMissing(targetMapsAndHandlers, receiverMap);

        if (IsTransitionOfMonomorphicTargetForElements(receiverMap, newReceiverMap))
        {
            mapAdded |= AddOneReceiverMapIfMissing(targetMapsAndHandlers, newReceiverMap);
        }

        // If the miss wasn't due to an unseen map, a polymorphic stub
        // won't help, use the megamorphic stub which can handle everything.
        if (!mapAdded) return;

        // If the maximum number of receiver maps has been exceeded, use the
        // megamorphic version of the IC.
        if (targetMapsAndHandlers.Count > _isolate.Flags.max_valid_polymorphic_map_count) return;

        // Make sure all polymorphic handlers have the same store mode, otherwise the
        // megamorphic stub must be used.
        if (!StoreModeIsInBounds(oldStoreMode))
        {
            if (StoreModeIsInBounds(storeMode)) storeMode = oldStoreMode;
            else if (storeMode != oldStoreMode) return;
        }

        // If the store mode isn't the standard mode, make sure that all polymorphic
        // receivers are either external arrays, or all "normal" arrays with writable
        // length. Otherwise, use the megamorphic stub.
        if (!StoreModeIsInBounds(storeMode))
        {
            int externalArrays = 0;
            foreach ((Map map, JSValue _) in targetMapsAndHandlers)
            {
                if (map.InstanceType == InstanceType.JSArrayType && JSArray.MayHaveReadOnlyLength(map)) return;
                if (ElementsKinds.IsTypedArrayOrRabGsabTypedArrayElementsKind(map.ElementsKind)) externalArrays++;
            }
            if (externalArrays != 0 && externalArrays != targetMapsAndHandlers.Count) return;
        }

        StoreElementPolymorphicHandlers(targetMapsAndHandlers, storeMode);
        if (targetMapsAndHandlers.Count == 0)
        {
            HeapObject handler = StoreElementHandler(receiverMap, storeMode, null);
            ConfigureVectorState(null, receiverMap, handler);
        }
        else if (targetMapsAndHandlers.Count == 1)
        {
            (Map map, JSValue handler) = targetMapsAndHandlers[0];
            ConfigureVectorState(null, map, handler);
        }
        else
        {
            ConfigureVectorState(null, targetMapsAndHandlers);
        }
    }

    static bool AddOneReceiverMapIfMissing(List<(Map Map, JSValue Handler)> mapsAndHandlers, Map newReceiverMap)
    {
        foreach ((Map map, JSValue _) in mapsAndHandlers)
        {
            if (ReferenceEquals(map, newReceiverMap)) return false;
        }
        mapsAndHandlers.Add((newReceiverMap, JSValue.Undefined));
        return true;
    }

    /// <summary>IC::IsTransitionOfMonomorphicTarget.</summary>
    bool IsTransitionOfMonomorphicTargetForElements(Map? sourceMap, Map? targetMap)
    {
        if (sourceMap is null) return true;
        if (targetMap is null) return false;
        if (sourceMap.IsAbandonedPrototypeMap) return false;
        if (!ElementsKinds.IsMoreGeneralElementsKindTransition(sourceMap.ElementsKind, targetMap.ElementsKind)) return false;
        Map? transitionedMap = sourceMap.FindElementsKindTransitionedMap(_isolate, [targetMap]);
        return ReferenceEquals(transitionedMap, targetMap);
    }

    /// <summary>KeyedStoreIC::StoreElementHandler.</summary>
    HeapObject StoreElementHandler(Map receiverMap, KeyedAccessStoreMode storeMode, Cell? prevValidityCell)
    {
        if (!InstanceTypeChecks.IsJSObject(receiverMap.InstanceType))
        {
            // DefineKeyedOwnIC, which is used to define computed fields in instances,
            // should handled by the slow stub below instead of the proxy stub.
            if (receiverMap.InstanceType == InstanceType.JSProxyType && !IsDefineKeyedOwnIC) return StoreHandler.StoreProxy(_isolate);
            return StoreHandler.StoreSlow(_isolate);
        }

        ElementsKind kind = receiverMap.ElementsKind;
        if (ElementsKinds.IsSloppyArgumentsElementsKind(kind))
        {
            return StoreHandler.StoreSlow(_isolate);
        }
        if (ElementsKinds.IsFastElementsKind(kind))
        {
            if (receiverMap.InstanceType == InstanceType.JSArgumentsObjectType && ElementsKinds.IsFastPackedElementsKind(kind))
            {
                // Allow fast behaviour for in-bounds stores while making it miss and
                // properly handle the out of bounds store case.
                storeMode = KeyedAccessStoreMode.kInBounds;
            }
        }
        else
        {
            // Sealed, non-extensible, frozen, typed array and dictionary elements
            // go through the runtime in V8Sharp.
            return StoreHandler.StoreSlow(_isolate);
        }
        if (IsAnyDefineOwn || IsStoreInArrayLiteralIC) return StoreHandler.StoreElement(_isolate, kind, null, storeMode);
        // A store into a hole (or past the end) consults the prototype chain:
        // the handler is only valid while the chain is unchanged.
        Cell? validityCell = prevValidityCell ?? Map.GetOrCreatePrototypeChainValidityCell(receiverMap, _isolate);
        return StoreHandler.StoreElement(_isolate, kind, null, storeMode, validityCell);
    }

    /// <summary>KeyedStoreIC::StoreElementPolymorphicHandlers.</summary>
    void StoreElementPolymorphicHandlers(List<(Map Map, JSValue Handler)> receiverMapsAndHandlers, KeyedAccessStoreMode storeMode)
    {
        var receiverMaps = new Map[receiverMapsAndHandlers.Count];
        for (int i = 0; i < receiverMaps.Length; i++) receiverMaps[i] = receiverMapsAndHandlers[i].Map;

        for (int i = 0; i < receiverMapsAndHandlers.Count; i++)
        {
            Map receiverMap = receiverMapsAndHandlers[i].Map;
            HeapObject handler;
            if (!InstanceTypeChecks.IsJSReceiver(receiverMap.InstanceType) ||
                receiverMap.ShouldCheckForReadOnlyElementsInPrototypeChain(_isolate))
            {
                handler = StoreHandler.StoreSlow(_isolate);
            }
            else
            {
                Map? transition = receiverMap.FindElementsKindTransitionedMap(_isolate, receiverMaps);
                if (transition is not null)
                {
                    if (receiverMap.IsStable) receiverMap.NotifyLeafMapLayoutChange(_isolate);
                }
                // Keep the old handler's validity cell: if the prototype chain
                // changed since, the recomputed handler stays invalid and misses.
                Cell? validityCell = receiverMapsAndHandlers[i].Handler._obj is StoreHandler oldHandler ? oldHandler.ValidityCell : null;
                if (transition is not null)
                {
                    // ElementsTransitionAndStore (ElementAccess.TryStoreFastElement).
                    handler = StoreHandler.StoreElement(_isolate, receiverMap.ElementsKind, transition, storeMode,
                        validityCell ?? Map.GetOrCreatePrototypeChainValidityCell(receiverMap, _isolate));
                }
                else
                {
                    handler = StoreElementHandler(receiverMap, storeMode, validityCell);
                }
            }
            receiverMapsAndHandlers[i] = (receiverMap, handler);
        }
    }
}
