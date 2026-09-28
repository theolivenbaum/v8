// Port of the load ICs of src/ic/ic.cc (LoadIC, LoadGlobalIC, KeyedLoadIC and
// the KeyedHasIC flavour) with their miss handlers (Runtime_LoadIC_Miss,
// Runtime_KeyedLoadIC_Miss, Runtime_LoadGlobalIC_Miss ...), and of the load
// handler dispatch of src/ic/accessor-assembler.cc (HandleLoadICHandlerCase,
// HandleLoadICSmiHandlerCase, HandleLoadICProtoHandler, LoadIC_BytecodeHandler,
// the megamorphic stub cache probe).
using System.Runtime.CompilerServices;
using V8Sharp.Interpreter;
using V8Sharp.Runtime;

namespace V8Sharp.IC;

public sealed class LoadIC : IC
{
    public LoadIC(Isolate isolate, FeedbackVector? vector, int slot, FeedbackSlotKind kind)
        : base(isolate, vector, slot, kind) { }

    bool ShouldThrowReferenceError() => IsLoadGlobalIC && _kind == FeedbackSlotKind.kLoadGlobalNotInsideTypeof;

    // ---- Interpreter entry points (AccessorAssembler) -------------------------------------

    /// <summary>
    /// GetNamedProperty: LoadIC_BytecodeHandler. Tries the monomorphic and
    /// polymorphic feedback, then the stub cache when megamorphic, then misses.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static JSValue LoadNamed(Isolate isolate, FeedbackVector? vector, int slot, JSValue receiver, Name name)
    {
        if (vector is not null)
        {
            HeapObject? o = receiver._obj;
            if (o is not null && o.InstanceType >= InstanceTypeChecks.FirstJSReceiver)
            {
                JSValue[] slots = vector.Slots;
                var r = Unsafe.As<JSReceiver>(o);
                if (ReferenceEquals(slots[slot]._obj, r.Map) && slots[slot + 1]._obj is LoadHandler handler &&
                    handler.HandlerKind == LoadHandler.Kind.kField && handler.Holder is null)
                {
                    return r._fields[handler.FieldIndex];
                }
            }
        }
        return LoadNamedSlow(isolate, vector, slot, receiver, name);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static JSValue LoadNamedSlow(Isolate isolate, FeedbackVector? vector, int slot, JSValue receiver, Name name)
    {
        if (vector is not null)
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
                    handler = FindPolymorphicHandler(polymorphic, map);
                }
                else if (ReferenceEquals(feedback._obj, ReadOnlyRoots.megamorphic_symbol))
                {
                    handler = ICIsolateState.Get(isolate).LoadStubCache.Get(name, map);
                }
                if (handler is LoadHandler loadHandler && TryHandleLoad(isolate, loadHandler, receiver, name, out JSValue result))
                {
                    return result;
                }
            }
        }
        else if (isolate.Flags.use_ic)
        {
            // LoadIC_NoFeedback: probe the megamorphic cache, then the runtime.
            Map? map = ICMaps.MapOf(isolate, receiver);
            if (map is not null && ICIsolateState.Get(isolate).LoadStubCache.Get(name, map) is LoadHandler cached &&
                TryHandleLoad(isolate, cached, receiver, name, out JSValue result))
            {
                return result;
            }
        }
        return Miss(isolate, vector, slot, receiver, name);
    }

    /// <summary>The handler for <paramref name="map"/> in a polymorphic (map, handler) array.</summary>
    public static HeapObject? FindPolymorphicHandler(FixedArray array, Map map)
    {
        JSValue[] data = array.Data;
        for (int i = 0; i + 1 < data.Length; i += 2)
        {
            if (ReferenceEquals(data[i]._obj, map)) return data[i + 1].HeapObjectOrNull;
        }
        return null;
    }

    /// <summary>Runtime_LoadIC_Miss / LoadNoFeedbackIC_Miss.</summary>
    public static JSValue Miss(Isolate isolate, FeedbackVector? vector, int slot, JSValue receiver, Name name)
    {
        var ic = new LoadIC(isolate, vector, slot, FeedbackSlotKind.kLoadProperty);
        ic.UpdateState(receiver, name);
        return ic.Load(receiver, name);
    }

    /// <summary>
    /// GetNamedPropertyFromSuper: LoadSuperIC with the home object's prototype
    /// as the lookup start object.
    /// </summary>
    public static JSValue LoadSuper(Isolate isolate, FeedbackVector? vector, int slot, JSValue receiver, JSValue homeObject, Name name)
    {
        JSReceiver? prototype = homeObject.As<JSReceiver>().Map.Prototype;
        JSValue lookupStart = prototype is null ? JSValue.Null : prototype;
        if (lookupStart.IsNullOrUndefined)
        {
            // In V8 the LoadSuperIC throws the "load from null" error on the lookup start object.
            return ErrorUtils.ThrowLoadFromNullOrUndefined(isolate, lookupStart, name);
        }
        var ic = new LoadIC(isolate, vector, slot, FeedbackSlotKind.kLoadProperty);
        // Deviation: the super load does not update feedback (V8's LoadSuperIC
        // caches handlers for the lookup start object map, with the receiver
        // passed to getters); V8Sharp performs the generic lookup.
        return RuntimeObject.GetObjectProperty(isolate, lookupStart, name, receiver, out _);
    }

    /// <summary>
    /// Executes a load handler (AccessorAssembler::HandleLoadICHandlerCase).
    /// Returns false when the handler does not apply (the caller misses).
    /// </summary>
    public static bool TryHandleLoad(Isolate isolate, LoadHandler handler, JSValue receiver, Name name, out JSValue result)
    {
        result = default;
        if (!handler.IsValid) return false;
        JSReceiver? holder = handler.Holder;
        if (handler.LookupOnLookupStartObject && receiver.HeapObjectOrNull is JSObject startObject &&
            !startObject.HasFastProperties)
        {
            // A dictionary-mode lookup start object may have the property itself.
            if (startObject is JSGlobalObject global)
            {
                if (global.GlobalDictionary.FindEntry(name).IsFound) return false;
            }
            else if (startObject.PropertyDictionary.FindEntry(name).IsFound)
            {
                return false;
            }
        }
        switch (handler.HandlerKind)
        {
            case LoadHandler.Kind.kField:
            {
                JSReceiver target = holder ?? receiver.As<JSReceiver>();
                result = target._fields[handler.FieldIndex];
                if (ReferenceEquals(result.HeapObjectOrNull, Oddball.Uninitialized)) return false;
                return true;
            }
            case LoadHandler.Kind.kConstantFromPrototype:
                result = handler.Data;
                return true;
            case LoadHandler.Kind.kNonExistent:
                result = JSValue.Undefined;
                return true;
            case LoadHandler.Kind.kAccessorFromPrototype:
                result = ObjectOps.GetPropertyWithDefinedGetter(isolate, receiver, handler.Data.As<JSReceiver>());
                return true;
            case LoadHandler.Kind.kAccessorPair:
            {
                JSValue getter = handler.Data.As<AccessorPair>().Getter;
                if (!getter.IsJSReceiver)
                {
                    result = JSValue.Undefined;
                    return true;
                }
                result = ObjectOps.GetPropertyWithDefinedGetter(isolate, receiver, getter.As<JSReceiver>());
                return true;
            }
            case LoadHandler.Kind.kNormal:
            {
                JSReceiver target = holder ?? receiver.As<JSReceiver>();
                if (target.HasFastProperties || target is JSGlobalObject) return false;
                NameDictionary dictionary = target.PropertyDictionary;
                InternalIndex entry = dictionary.FindEntry(name);
                if (!entry.IsFound) return false;
                PropertyDetails details = dictionary.DetailsAt(entry);
                if (details.Kind != PropertyKind.Data) return false;
                result = dictionary.ValueAt(entry);
                return true;
            }
            case LoadHandler.Kind.kGlobal:
            {
                var cell = handler.Data.As<PropertyCell>();
                result = cell.Value;
                if (result.IsTheHole || ReferenceEquals(result.HeapObjectOrNull, Oddball.PropertyCellHole)) return false;
                if (cell.PropertyDetails.Kind != PropertyKind.Data) return false;
                return true;
            }
            case LoadHandler.Kind.kStringLength:
                if (receiver.HeapObjectOrNull is not JSString s) return false;
                result = JSValue.FromInt(s.Length);
                return true;
            case LoadHandler.Kind.kStringWrapperLength:
                if (receiver.HeapObjectOrNull is not JSPrimitiveWrapper { Value.IsString: true } wrapper) return false;
                result = JSValue.FromInt(wrapper.Value.As<JSString>().Length);
                return true;
            case LoadHandler.Kind.kArrayLength:
                if (receiver.HeapObjectOrNull is not JSArray array) return false;
                result = array.Length;
                return true;
            case LoadHandler.Kind.kFunctionPrototype:
            {
                if (receiver.HeapObjectOrNull is not JSFunction function || function.PrototypeRequiresRuntimeLookup()) return false;
                result = function.Prototype;
                return true;
            }
            case LoadHandler.Kind.kNativeDataProperty:
            {
                // Call the AccessorInfo getter with the holder.
                var info = handler.Data.As<AccessorInfo>();
                JSReceiver target = holder ?? receiver.As<JSReceiver>();
                if (!info.HasGetter || target is not JSObject targetObject) return false;
                result = info.Getter!(isolate, receiver, targetObject, name);
                return true;
            }
            case LoadHandler.Kind.kModuleExport:
            {
                result = handler.Data.As<Cell>().Value;
                if (result.IsTheHole) return false;
                return true;
            }
            case LoadHandler.Kind.kProxy:
            {
                JSReceiver target = holder ?? receiver.As<JSReceiver>();
                if (target is not JSProxy proxy) return false;
                result = JSProxy.GetProperty(isolate, proxy, name, receiver, out _);
                return true;
            }
            case LoadHandler.Kind.kSlow:
                result = RuntimeObject.GetObjectProperty(isolate, receiver, name, receiver, out _);
                return true;
            default:
                return false;
        }
    }

    // ---- IC proper ------------------------------------------------------------------------

    /// <summary>LookupForRead (ic.cc).</summary>
    static void LookupForRead(ref LookupIterator it, bool isHasProperty)
    {
        for (;; it.Next())
        {
            switch (it.State)
            {
                case LookupIterator.StateKind.TRANSITION:
                    throw new UnreachableException();
                case LookupIterator.StateKind.JSPROXY:
                    return;
                case LookupIterator.StateKind.WASM_OBJECT:
                    continue;
                case LookupIterator.StateKind.INTERCEPTOR:
                    // Interceptors are not ported (no embedder API).
                    continue;
                case LookupIterator.StateKind.ACCESS_CHECK:
                    continue;
                case LookupIterator.StateKind.MODULE_NAMESPACE:
                    continue;
                case LookupIterator.StateKind.ACCESSOR:
                case LookupIterator.StateKind.TYPED_ARRAY_INDEX_NOT_FOUND:
                case LookupIterator.StateKind.DATA:
                case LookupIterator.StateKind.STRING_LOOKUP_START_OBJECT:
                case LookupIterator.StateKind.NOT_FOUND:
                    return;
            }
        }
    }

    /// <summary>LoadIC::Load.</summary>
    public JSValue Load(JSValue obj, Name name, bool updateFeedback = true, JSValue? receiverOrNull = null)
    {
        bool useIc = _state != InlineCacheState.NO_FEEDBACK && _isolate.Flags.use_ic && updateFeedback;
        JSValue receiver = receiverOrNull ?? obj;

        // If the object is undefined or null it's illegal to try to get any
        // of its properties; throw a TypeError in that case.
        if (IsAnyHas ? !obj.IsJSReceiver : obj.IsNullOrUndefined)
        {
            if (useIc && !obj.IsNullOrUndefined)
            {
                // Ensure the IC state progresses.
                UpdateLookupStartObjectMap(obj);
                if (_lookupStartObjectMap is not null) SetCache(name, LoadHandler.LoadSlow(_isolate));
            }
            if (IsAnyHas)
            {
                return _isolate.ThrowTypeError(MessageTemplate.InvalidInOperatorUse, name, obj);
            }
            return ErrorUtils.ThrowLoadFromNullOrUndefined(_isolate, obj, name);
        }

        // If we encounter an object with a deprecated map, we want to update the
        // feedback vector with the migrated map.
        // Mark ourselves as RECOMPUTE_HANDLER so that we don't turn megamorphic due
        // to seeing the same map and handler.
        if (MigrateDeprecated(_isolate, obj)) UpdateState(obj, name);

        JSObject.MakePrototypesFast(obj, WhereToStart.StartAtReceiver, _isolate);
        UpdateLookupStartObjectMap(obj);

        var key = new PropertyKey(_isolate, name);
        var it = new LookupIterator(_isolate, receiver, key, obj);

        // Named lookup in the object.
        LookupForRead(ref it, IsAnyHas);

        if (it.IsFound || !ShouldThrowReferenceError())
        {
            // Update inline cache and stub cache.
            if (useIc) UpdateCaches(ref it);

            if (IsAnyHas)
            {
                // Named lookup in the object.
                return JSValue.FromBoolean(JSReceiver.HasProperty(ref it));
            }

            // Get the property.
            JSValue result = ObjectOps.GetProperty(ref it, IsLoadGlobalIC);
            if (it.IsFound || !ShouldThrowReferenceError()) return result;
        }
        return _isolate.ThrowReferenceError(MessageTemplate.NotDefined, name);
    }

    /// <summary>LoadIC::UpdateCaches.</summary>
    void UpdateCaches(ref LookupIterator lookup)
    {
        if (_lookupStartObjectMap is null) return;
        HeapObject handler;
        if (lookup.State == LookupIterator.StateKind.ACCESS_CHECK)
        {
            handler = LoadHandler.LoadSlow(_isolate);
        }
        else if (!lookup.IsFound)
        {
            if (lookup.IsAnyPrivateName())
            {
                handler = LoadHandler.LoadSlow(_isolate);
            }
            else
            {
                handler = LoadHandler.LoadNonExistent(_isolate, LookupStartObjectMap);
            }
        }
        else if (IsLoadGlobalIC && lookup.State == LookupIterator.StateKind.JSPROXY)
        {
            // If there is proxy just install the slow stub since we need to call the
            // HasProperty trap for global loads.
            handler = LoadHandler.LoadSlow(_isolate);
        }
        else if (lookup.State == LookupIterator.StateKind.MODULE_NAMESPACE)
        {
            return;
        }
        else
        {
            if (IsLoadGlobalIC)
            {
                if (lookup.State == LookupIterator.StateKind.DATA &&
                    ReferenceEquals(lookup.GetReceiver().HeapObjectOrNull, lookup.Holder) &&
                    lookup.Holder is JSGlobalObject)
                {
                    // Now update the cell in the feedback vector.
                    _nexus.ConfigurePropertyCellMode(lookup.GetPropertyCell());
                    return;
                }
            }
            handler = ComputeHandler(ref lookup);
        }
        // Can't use {lookup->name()} because the LookupIterator might be in
        // "elements" mode for keys that are strings representing integers above
        // JSArray::kMaxIndex.
        SetCache(lookup.GetName(), handler);
    }

    /// <summary>LoadIC::ComputeHandler.</summary>
    HeapObject ComputeHandler(ref LookupIterator lookup)
    {
        JSValue receiver = lookup.GetReceiver();
        JSValue lookupStartObject = lookup.LookupStartObject;
        // `in` cannot be called on strings, and will always return true for string
        // wrapper length and function prototypes.
        if (!IsAnyHas && !lookup.IsElement())
        {
            Name lookupName = lookup.Name;
            if (lookupStartObject.IsString && ReferenceEquals(lookupName, ReadOnlyRoots.length_string))
            {
                return LoadHandler.LoadStringLength;
            }
            if (ObjectOps.IsStringWrapper(lookupStartObject) && ReferenceEquals(lookupName, ReadOnlyRoots.length_string))
            {
                return LoadHandler.LoadStringWrapperLength;
            }
            // Use specialized code for getting prototype of functions.
            if (lookupStartObject.HeapObjectOrNull is JSFunction function &&
                ReferenceEquals(lookupName, ReadOnlyRoots.prototype_string) && !function.PrototypeRequiresRuntimeLookup())
            {
                return LoadHandler.LoadFunctionPrototype;
            }
        }

        Map map = LookupStartObjectMap;
        bool holderIsLookupStartObject = ReferenceEquals(lookupStartObject.HeapObjectOrNull, lookup.Holder);

        switch (lookup.State)
        {
            case LookupIterator.StateKind.INTERCEPTOR:
                return LoadHandler.LoadSlow(_isolate);

            case LookupIterator.StateKind.ACCESSOR:
            {
                if (lookup.Holder is not JSObject holder) return LoadHandler.LoadSlow(_isolate);
                // Use simple field loads for some well-known callback properties.
                // The method will only return true for absolute truths based on the
                // lookup start object maps.
                if (Accessors.IsJSObjectFieldAccessor(_isolate, map, lookup.Name, out _))
                {
                    return LoadHandler.LoadArrayLength;
                }
                if (holder is JSModuleNamespace ns)
                {
                    JSValue cell = ns.Exports.Lookup(_isolate, lookup.Name);
                    if (cell.HeapObjectOrNull is not Cell exportCell) return LoadHandler.LoadSlow(_isolate);
                    LoadHandler exportHandler = LoadHandler.LoadModuleExport(_isolate, exportCell);
                    if (holderIsLookupStartObject) return exportHandler;
                    return LoadHandler.LoadFromPrototype(_isolate, map, holder, LoadHandler.Kind.kModuleExport,
                        data: exportCell);
                }

                HeapObject accessors = lookup.GetAccessors();
                if (accessors is AccessorPair accessorPair)
                {
                    JSValue getter = accessorPair.Getter;
                    if (!(getter.HeapObjectOrNull is JSFunction getterFunction && getterFunction.Map.IsCallable))
                    {
                        return LoadHandler.LoadSlow(_isolate);
                    }
                    if (holder.HasFastProperties)
                    {
                        if (holderIsLookupStartObject) return LoadHandler.LoadAccessorPair(_isolate, accessorPair);
                        return LoadHandler.LoadFromPrototype(_isolate, map, holder, LoadHandler.Kind.kAccessorFromPrototype,
                            data: getter);
                    }
                    // Dictionary-mode holders: the slow path (V8 uses LoadNormal/LoadGlobal handlers that
                    // call the accessor through the runtime).
                    return LoadHandler.LoadSlow(_isolate);
                }

                if (accessors is AccessorInfo info)
                {
                    if (info.ReplaceOnAccess || !info.HasGetter || !holder.HasFastProperties ||
                        !receiver.IsJSReceiver)
                    {
                        return LoadHandler.LoadSlow(_isolate);
                    }
                    LoadHandler nativeHandler = LoadHandler.LoadNativeDataProperty(_isolate, info);
                    if (holderIsLookupStartObject) return nativeHandler;
                    return LoadHandler.LoadFromPrototype(_isolate, map, holder, LoadHandler.Kind.kNativeDataProperty, data: info);
                }
                return LoadHandler.LoadSlow(_isolate);
            }

            case LookupIterator.StateKind.DATA:
            {
                JSReceiver holder = lookup.Holder!;
                if (lookup.IsElement(holder)) return LoadHandler.LoadSlow(_isolate);
                if (lookup.IsDictionaryHolder)
                {
                    if (holder is JSGlobalObject)
                    {
                        return LoadHandler.LoadFromPrototype(_isolate, map, holder, LoadHandler.Kind.kGlobal,
                            data: lookup.GetPropertyCell());
                    }
                    if (holderIsLookupStartObject) return LoadHandler.LoadNormal(_isolate);
                    if (lookup.Constness == PropertyConstness.Const)
                    {
                        // Deviation: V8 --dict-property-const-tracking is off, so this
                        // is not reached for dictionary holders in V8 either.
                    }
                    return LoadHandler.LoadFromPrototype(_isolate, map, holder, LoadHandler.Kind.kNormal);
                }

                FieldIndex field = lookup.GetFieldIndex();
                if (holderIsLookupStartObject) return LoadHandler.LoadField(_isolate, field);
                if (lookup.Constness == PropertyConstness.Const)
                {
                    JSValue value = lookup.GetDataValue();
                    // Non internalized strings could turn into thin/cons strings
                    // when internalized.
                    if (!(value.HeapObjectOrNull is JSString vs && !vs.IsInternalized) &&
                        !ReferenceEquals(value.HeapObjectOrNull, Oddball.Uninitialized))
                    {
                        return LoadHandler.LoadFromPrototype(_isolate, map, holder, LoadHandler.Kind.kConstantFromPrototype,
                            data: value);
                    }
                }
                return LoadHandler.LoadFromPrototype(_isolate, map, holder, LoadHandler.Kind.kField, field.PropertyIndex);
            }

            case LookupIterator.StateKind.TYPED_ARRAY_INDEX_NOT_FOUND:
                return LoadHandler.LoadNonExistent(_isolate);

            case LookupIterator.StateKind.JSPROXY:
            {
                // Private names on JSProxy is currently not supported.
                if (lookup.Name is { IsAnyPrivate: true }) return LoadHandler.LoadSlow(_isolate);
                if (holderIsLookupStartObject) return LoadHandler.LoadProxy(_isolate);
                return LoadHandler.LoadFromPrototype(_isolate, map, lookup.Holder!, LoadHandler.Kind.kProxy);
            }

            default:
                return LoadHandler.LoadSlow(_isolate);
        }
    }
}

/// <summary>V8's LoadGlobalIC: loads of global variables (script context lexicals, then the global object).</summary>
public static class LoadGlobalIC
{
    /// <summary>
    /// LdaGlobal: the LoadGlobalIC feedback is a script-context slot (lexical
    /// variable), a PropertyCell of the global object, or a handler.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static JSValue Load(Isolate isolate, FeedbackVector? vector, int slot, Context context, Name name, TypeofMode typeofMode)
    {
        if (vector is not null)
        {
            JSValue feedback = vector.Slots[slot];
            if (feedback._obj is PropertyCell cell)
            {
                JSValue value = cell.Value;
                if (!value.IsTheHole && !ReferenceEquals(value.HeapObjectOrNull, Oddball.PropertyCellHole) &&
                    cell.PropertyDetails.Kind == PropertyKind.Data)
                {
                    return value;
                }
            }
            else if (feedback.IsNumber)
            {
                // Lexical variable in a script context.
                int config = (int)feedback._num;
                int contextIndex = config & FeedbackNexus.kContextIndexMask;
                int slotIndex = (config >> FeedbackNexus.kSlotIndexShift) & FeedbackNexus.kSlotIndexMask;
                ScriptContextTable table = context.NativeContext.ScriptContextTable;
                JSValue value = table.Get(contextIndex).Slots[slotIndex];
                if (!value.IsTheHole) return value;
            }
        }
        return Miss(isolate, vector, slot, context, name, typeofMode);
    }

    /// <summary>Runtime_LoadGlobalIC_Miss.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static JSValue Miss(Isolate isolate, FeedbackVector? vector, int slot, Context context, Name name, TypeofMode typeofMode)
    {
        FeedbackSlotKind kind = typeofMode == TypeofMode.Inside
            ? FeedbackSlotKind.kLoadGlobalInsideTypeof
            : FeedbackSlotKind.kLoadGlobalNotInsideTypeof;
        var ic = new LoadGlobalICImpl(isolate, vector, slot, kind);
        ic.UpdateState(context.GlobalObject, name);
        return ic.Load(context, name);
    }

    sealed class LoadGlobalICImpl(Isolate isolate, FeedbackVector? vector, int slot, FeedbackSlotKind kind)
        : IC(isolate, vector, slot, kind)
    {
        public new void UpdateState(JSValue lookupStartObject, JSValue name) => base.UpdateState(lookupStartObject, name);

        /// <summary>LoadGlobalIC::Load.</summary>
        public JSValue Load(Context context, Name name)
        {
            NativeContext nativeContext = context.NativeContext;
            JSGlobalObject global = nativeContext.GlobalObject;
            if (name is JSString strName)
            {
                // Look up in script context table.
                ScriptContextTable scriptContexts = nativeContext.ScriptContextTable;
                if (scriptContexts.Lookup(strName, out ScopeInfo.VariableLookupResult lookupResult))
                {
                    Context scriptContext = scriptContexts.Get(lookupResult.ContextIndex);
                    if (scriptContext.IsElementTdzHole(lookupResult.SlotIndex))
                    {
                        // Do not install stubs and stay pre-monomorphic for
                        // uninitialized accesses.
                        return _isolate.ThrowReferenceError(MessageTemplate.AccessedUninitializedVariable, name);
                    }
                    bool useIc = _state != InlineCacheState.NO_FEEDBACK && _isolate.Flags.use_ic;
                    if (useIc)
                    {
                        // 'const' Variables are mutable if REPL mode is enabled.
                        if (!_nexus.ConfigureLexicalVarMode(lookupResult.ContextIndex, lookupResult.SlotIndex,
                                Globals.IsImmutableLexicalVariableMode(lookupResult.Mode) && !lookupResult.IsReplMode))
                        {
                            // Given combination of indices can't be encoded, so use slow stub.
                            UpdateLookupStartObjectMap(global);
                            SetCache(name, LoadHandler.LoadSlow(_isolate));
                        }
                    }
                    return scriptContext.Slots[lookupResult.SlotIndex];
                }
            }
            var ic = new LoadIC(_isolate, _vector, _slot, _kind);
            return ic.Load(global, name);
        }
    }
}

/// <summary>V8's KeyedLoadIC (and KeyedHasIC).</summary>
public sealed class KeyedLoadIC : IC
{
    public KeyedLoadIC(Isolate isolate, FeedbackVector? vector, int slot, FeedbackSlotKind kind)
        : base(isolate, vector, slot, kind) { }

    // ---- Interpreter entry points ---------------------------------------------------------

    /// <summary>GetKeyedProperty: KeyedLoadIC with element fast paths.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static JSValue Load(Isolate isolate, FeedbackVector? vector, int slot, JSValue obj, JSValue key)
    {
        if (vector is not null && key.IsNumber && obj._obj is JSObject jsObject)
        {
            JSValue[] slots = vector.Slots;
            if (ReferenceEquals(slots[slot]._obj, jsObject.Map) && slots[slot + 1]._obj is LoadHandler handler &&
                handler.HandlerKind == LoadHandler.Kind.kElement &&
                ElementAccess.TryLoadFastElement(isolate, jsObject, key._num, handler, out JSValue result))
            {
                return result;
            }
        }
        return LoadSlow(isolate, vector, slot, obj, key);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static JSValue LoadSlow(Isolate isolate, FeedbackVector? vector, int slot, JSValue obj, JSValue key)
    {
        if (vector is not null && isolate.Flags.use_ic)
        {
            JSValue feedback = vector.Slots[slot];
            // A named key that matches the keyed IC's name: the handlers are named-load handlers.
            if (key.HeapObjectOrNull is Name name)
            {
                if (ReferenceEquals(feedback._obj, name) && ICMaps.MapOf(isolate, obj) is Map map &&
                    vector.Slots[slot + 1]._obj is FixedArray namedFeedback &&
                    LoadIC.FindPolymorphicHandler(namedFeedback, map) is LoadHandler namedHandler &&
                    LoadIC.TryHandleLoad(isolate, namedHandler, obj, name, out JSValue namedResult))
                {
                    return namedResult;
                }
                if (ReferenceEquals(feedback._obj, ReadOnlyRoots.megamorphic_symbol) && name.IsUniqueName &&
                    ICMaps.MapOf(isolate, obj) is Map megaMap &&
                    ICIsolateState.Get(isolate).LoadStubCache.Get(name, megaMap) is LoadHandler cached &&
                    LoadIC.TryHandleLoad(isolate, cached, obj, name, out JSValue cachedResult))
                {
                    return cachedResult;
                }
            }
            else if (obj._obj is JSObject jsObject && key.IsNumber && feedback._obj is FixedArray polymorphic &&
                     LoadIC.FindPolymorphicHandler(polymorphic, jsObject.Map) is LoadHandler { HandlerKind: LoadHandler.Kind.kElement } elementHandler &&
                     ElementAccess.TryLoadFastElement(isolate, jsObject, key._num, elementHandler, out JSValue elementResult))
            {
                return elementResult;
            }
            else if (obj.HeapObjectOrNull is JSString str && key.IsNumber && ReferenceEquals(feedback._obj, ICMaps.MapOf(isolate, obj)))
            {
                double index = key._num;
                if (index >= 0 && index < str.Length && (int)index == index)
                {
                    return isolate.Factory.LookupSingleCharacterStringFromCode(str.Get((int)index));
                }
            }
        }
        return Miss(isolate, vector, slot, obj, key, FeedbackSlotKind.kLoadKeyed);
    }

    /// <summary>
    /// GetEnumeratedKeyedProperty: a keyed load whose key comes from for-in
    /// (EnumeratedKeyedLoadIC); with the receiver's map still the enum cache
    /// type, the key is an own fast property.
    /// </summary>
    public static JSValue LoadEnumerated(Isolate isolate, FeedbackVector? vector, int slot, JSValue obj, JSValue key,
        JSValue enumIndex, JSValue cacheType)
    {
        if (obj.HeapObjectOrNull is JSObject jsObject && ReferenceEquals(jsObject.Map, cacheType.HeapObjectOrNull) &&
            jsObject.HasFastProperties)
        {
            // The enum cache of the map lists the keys in descriptor order; the
            // enum index is the descriptor index of the key.
            Map map = jsObject.Map;
            var descriptor = new InternalIndex((int)enumIndex.Number);
            DescriptorArray descriptors = map.InstanceDescriptors;
            if (descriptor.AsInt < map.NumberOfOwnDescriptors &&
                ReferenceEquals(descriptors.GetKey(descriptor), key.HeapObjectOrNull))
            {
                PropertyDetails details = descriptors.GetDetails(descriptor);
                if (details.Kind == PropertyKind.Data && details.Location == PropertyLocation.Field)
                {
                    FieldIndex index = FieldIndex.ForDetails(map, details);
                    JSValue value = jsObject._fields[index.PropertyIndex];
                    if (!ReferenceEquals(value.HeapObjectOrNull, Oddball.Uninitialized)) return value;
                }
            }
        }
        return Load(isolate, vector, slot, obj, key);
    }

    /// <summary>Runtime_KeyedLoadIC_Miss / Runtime_KeyedHasIC_Miss.</summary>
    public static JSValue Miss(Isolate isolate, FeedbackVector? vector, int slot, JSValue obj, JSValue key, FeedbackSlotKind kind)
    {
        var ic = new KeyedLoadIC(isolate, vector, slot, kind);
        ic.UpdateState(obj, key);
        return ic.Load(obj, key);
    }

    // ---- IC proper ----------------------------------------------------------------------------

    enum KeyType { kIntPtr, kName, kBailout }

    /// <summary>TryConvertKey (ic.cc).</summary>
    static KeyType TryConvertKey(JSValue key, Isolate isolate, out long indexOut, out Name? nameOut)
    {
        indexOut = 0;
        nameOut = null;
        if (key.IsUndefined)
        {
            nameOut = ReadOnlyRoots.undefined_string;
            return KeyType.kName;
        }
        if (key.IsNull)
        {
            nameOut = ReadOnlyRoots.null_string;
            return KeyType.kName;
        }
        if (key.IsTrue)
        {
            nameOut = ReadOnlyRoots.true_string;
            return KeyType.kName;
        }
        if (key.IsFalse)
        {
            nameOut = ReadOnlyRoots.false_string;
            return KeyType.kName;
        }
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
                // {key} is a string representation of an array index beyond the range
                // that the IC could handle. Don't try to take the named-property path.
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

    static bool CanCache(JSValue receiver, InlineCacheState state, Isolate isolate)
    {
        if (!isolate.Flags.use_ic || state == InlineCacheState.NO_FEEDBACK) return false;
        if (!receiver.IsJSReceiver && !receiver.IsString) return false;
        return receiver.HeapObjectOrNull is not JSPrimitiveWrapper;
    }

    JSValue RuntimeLoad(JSValue obj, JSValue key, out bool isFound)
    {
        if (IsKeyedLoadIC) return RuntimeObject.GetObjectProperty(_isolate, obj, key, obj, out isFound);
        isFound = false;
        return RuntimeObject.HasProperty(_isolate, obj, key);
    }

    /// <summary>KeyedLoadIC::Load.</summary>
    public JSValue Load(JSValue obj, JSValue key)
    {
        if (MigrateDeprecated(_isolate, obj)) return RuntimeLoad(obj, key, out _);

        KeyType keyType = TryConvertKey(key, _isolate, out long maybeIndex, out Name? maybeName);

        if (keyType == KeyType.kName) return LoadNamedKey(obj, key, maybeName!);

        JSValue result = RuntimeLoad(obj, key, out bool isFound);

        if (keyType == KeyType.kIntPtr && CanCache(obj, _state, _isolate) && maybeIndex >= 0 &&
            maybeIndex <= JSObject.kMaxElementIndex)
        {
            KeyedAccessLoadMode loadMode = GetNewKeyedLoadMode(obj, (ulong)maybeIndex, isFound);
            UpdateLoadElement(obj, loadMode);
        }

        if (!_vectorSet && _state != InlineCacheState.NO_FEEDBACK && _state != InlineCacheState.MEGAMORPHIC)
        {
            ConfigureVectorStateMegamorphic(key);
        }
        return result;
    }

    /// <summary>
    /// KeyedLoadIC::Load with a name key: LoadIC::Load on the keyed IC's nexus
    /// and state (KeyedLoadIC derives from LoadIC in V8).
    /// </summary>
    JSValue LoadNamedKey(JSValue obj, JSValue key, Name name)
    {
        var loadIc = new LoadIC(_isolate, _vector, _slot, _kind);
        loadIc.ShareState(this);
        JSValue result = loadIc.Load(obj, name);
        if (!loadIc._vectorSet && _state != InlineCacheState.NO_FEEDBACK)
        {
            ConfigureVectorStateMegamorphic(key);
        }
        return result;
    }

    // ---- Element handlers -----------------------------------------------------------------------

    static bool AllowConvertHoleElementToUndefined(Isolate isolate, Map receiverMap)
    {
        if (receiverMap.InstanceType == InstanceType.JSTypedArrayType) return true;
        // For other {receiver}s we need to check the "no elements" protector.
        if (Protectors.IsNoElementsIntact(isolate))
        {
            if (InstanceTypeChecks.IsString(receiverMap.InstanceType)) return true;
            if (InstanceTypeChecks.IsJSObject(receiverMap.InstanceType))
            {
                // For other JSObjects (including JSArrays) we can only continue if
                // the {receiver}s prototype is either the initial Object.prototype
                // or the initial Array.prototype, which are both guarded by the
                // "no elements" protector checked above.
                JSReceiver? prototype = receiverMap.Prototype;
                if (prototype is null) return false;
                NativeContext nc = isolate.NativeContext;
                if (ReferenceEquals(prototype, nc.InitialObjectPrototype) || ReferenceEquals(prototype, nc.InitialArrayPrototype))
                {
                    return true;
                }
            }
        }
        return false;
    }

    internal static bool IsOutOfBoundsAccess(JSValue receiver, ulong index)
    {
        ulong length;
        switch (receiver.HeapObjectOrNull)
        {
            case JSArray array:
                length = (ulong)array.Length.Number;
                break;
            case JSTypedArray typedArray:
                length = typedArray.GetLength();
                break;
            case JSObject obj:
                length = (ulong)obj.Elements.Length;
                break;
            case JSString s:
                length = (ulong)s.Length;
                break;
            default:
                return false;
        }
        return index >= length;
    }

    /// <summary>GetNewKeyedLoadMode (ic.cc).</summary>
    KeyedAccessLoadMode GetNewKeyedLoadMode(JSValue receiver, ulong index, bool isFound)
    {
        Map? receiverMap = ICMaps.MapOf(_isolate, receiver);
        if (receiverMap is null || !AllowConvertHoleElementToUndefined(_isolate, receiverMap)) return KeyedAccessLoadMode.kInBounds;

        // Always handle holes when the elements kind is HOLEY_ELEMENTS, since the
        // optimizer compilers can not benefit from this information to narrow the
        // type.
        ElementsKind elementsKind = receiverMap.ElementsKind;
        bool alwaysHandleHoles = elementsKind == ElementsKind.HOLEY_ELEMENTS;

        // In bound access and did not read a hole.
        if (isFound) return alwaysHandleHoles ? KeyedAccessLoadMode.kHandleHoles : KeyedAccessLoadMode.kInBounds;

        // OOB access.
        if (IsOutOfBoundsAccess(receiver, index))
        {
            return alwaysHandleHoles ? KeyedAccessLoadMode.kHandleOOBAndHoles : KeyedAccessLoadMode.kHandleOOB;
        }

        // Read a hole.
        bool handleHole = ElementsKinds.IsHoleyElementsKind(elementsKind);
        return handleHole ? KeyedAccessLoadMode.kHandleHoles : KeyedAccessLoadMode.kInBounds;
    }

    /// <summary>KeyedLoadIC::UpdateLoadElement.</summary>
    void UpdateLoadElement(JSValue receiver, KeyedAccessLoadMode newLoadMode)
    {
        Map receiverMap = ICMaps.MapOf(_isolate, receiver)!;
        _lookupStartObjectMap = receiverMap;

        var targetMapsAndHandlers = new List<(Map Map, JSValue Handler)>();
        _nexus.ExtractMapsAndHandlers(targetMapsAndHandlers);

        if (targetMapsAndHandlers.Count == 0)
        {
            HeapObject handler = LoadElementHandler(receiverMap, newLoadMode);
            ConfigureVectorState(null, receiverMap, handler);
            return;
        }

        foreach ((Map map, _) in targetMapsAndHandlers)
        {
            if (map.InstanceType is InstanceType.JSPrimitiveWrapperType or InstanceType.JSProxyType) return;
        }

        // The first time a receiver is seen that is a transitioned version of the
        // previous monomorphic receiver type, assume the new ElementsKind is the
        // monomorphic type.
        if (_state == InlineCacheState.MONOMORPHIC && receiver.HeapObjectOrNull is JSObject jsObject &&
            ElementsKinds.IsMoreGeneralElementsKindTransition(targetMapsAndHandlers[0].Map.ElementsKind,
                jsObject.GetElementsKind()))
        {
            HeapObject handler = LoadElementHandler(receiverMap, newLoadMode);
            ConfigureVectorState(null, receiverMap, handler);
            return;
        }

        // Determine the list of receiver maps that this call site has seen,
        // adding the map that was just encountered.
        KeyedAccessLoadMode oldLoadMode = KeyedAccessLoadMode.kInBounds;
        bool added = true;
        foreach ((Map map, _) in targetMapsAndHandlers)
        {
            if (ReferenceEquals(map, receiverMap)) added = false;
        }
        if (receiverMap.IsDeprecated) added = false;
        if (added)
        {
            targetMapsAndHandlers.Add((receiverMap, JSValue.Undefined));
        }
        else
        {
            oldLoadMode = GetKeyedAccessLoadModeFor(receiverMap);
            if (!AllowedHandlerChange(oldLoadMode, newLoadMode)) return;
        }

        // If the maximum number of receiver maps has been exceeded, use the generic
        // version of the IC.
        if (targetMapsAndHandlers.Count > _isolate.Flags.max_valid_polymorphic_map_count) return;

        KeyedAccessLoadMode loadMode = oldLoadMode | newLoadMode;
        var newMapsAndHandlers = new List<(Map Map, JSValue Handler)>(targetMapsAndHandlers.Count);
        foreach ((Map oldMap, JSValue oldHandler) in targetMapsAndHandlers)
        {
            // Filter out deprecated maps to ensure their instances get migrated.
            if (oldMap.IsDeprecated) continue;
            KeyedAccessLoadMode oldMode = oldHandler.HeapObjectOrNull is LoadHandler lh ? LoadModeOf(lh) : KeyedAccessLoadMode.kInBounds;
            newMapsAndHandlers.Add((oldMap, LoadElementHandler(oldMap, GetUpdatedLoadModeForMap(oldMap, oldMode, loadMode))));
        }
        if (newMapsAndHandlers.Count == 0)
        {
            ConfigureVectorState(null, receiverMap, LoadElementHandler(receiverMap, newLoadMode));
        }
        else if (newMapsAndHandlers.Count == 1)
        {
            ConfigureVectorState(null, newMapsAndHandlers[0].Map, newMapsAndHandlers[0].Handler);
        }
        else
        {
            ConfigureVectorState(null, newMapsAndHandlers);
        }
    }

    static KeyedAccessLoadMode LoadModeOf(LoadHandler handler) =>
        (handler.AllowOutOfBounds ? KeyedAccessLoadMode.kHandleOOB : 0) |
        (handler.AllowHandlingHole ? KeyedAccessLoadMode.kHandleHoles : 0);

    KeyedAccessLoadMode GetKeyedAccessLoadModeFor(Map map)
    {
        JSValue handler = _nexus.FindHandlerForMap(map);
        return handler.HeapObjectOrNull is LoadHandler lh ? LoadModeOf(lh) : KeyedAccessLoadMode.kInBounds;
    }

    static bool AllowedHandlerChange(KeyedAccessLoadMode oldMode, KeyedAccessLoadMode newMode) =>
        ((oldMode ^ (oldMode | newMode)) & KeyedAccessLoadMode.kHandleOOBAndHoles) != 0;

    KeyedAccessLoadMode GetUpdatedLoadModeForMap(Map map, KeyedAccessLoadMode oldLoadMode, KeyedAccessLoadMode loadMode)
    {
        if (!AllowConvertHoleElementToUndefined(_isolate, map)) return KeyedAccessLoadMode.kInBounds;
        bool allowReadingHoleElement = ElementsKinds.IsHoleyElementsKind(map.ElementsKind);
        bool allowOob = ((oldLoadMode | loadMode) & KeyedAccessLoadMode.kHandleOOB) != 0;
        bool allowHoles = allowReadingHoleElement && ((oldLoadMode | loadMode) & KeyedAccessLoadMode.kHandleHoles) != 0;
        return (allowOob ? KeyedAccessLoadMode.kHandleOOB : 0) | (allowHoles ? KeyedAccessLoadMode.kHandleHoles : 0);
    }

    /// <summary>KeyedLoadIC::LoadElementHandler.</summary>
    HeapObject LoadElementHandler(Map receiverMap, KeyedAccessLoadMode loadMode)
    {
        InstanceType instanceType = receiverMap.InstanceType;
        bool allowOob = (loadMode & KeyedAccessLoadMode.kHandleOOB) != 0;
        bool allowHoles = (loadMode & KeyedAccessLoadMode.kHandleHoles) != 0;
        if (InstanceTypeChecks.IsString(instanceType))
        {
            if (IsAnyHas) return LoadHandler.LoadSlow(_isolate);
            return LoadHandler.LoadIndexedString(_isolate, allowOob);
        }
        if (!InstanceTypeChecks.IsJSReceiver(instanceType)) return LoadHandler.LoadSlow(_isolate);
        if (instanceType == InstanceType.JSProxyType) return LoadHandler.LoadProxy(_isolate);

        ElementsKind elementsKind = receiverMap.ElementsKind;
        if (ElementsKinds.IsSloppyArgumentsElementsKind(elementsKind)) return LoadHandler.LoadSlow(_isolate);
        bool isJSArray = instanceType == InstanceType.JSArrayType;
        return LoadHandler.LoadElement(_isolate, elementsKind, isJSArray, allowOob, allowHoles);
    }
}

/// <summary>Package-private access to the LoadIC internals for the keyed IC's named path.</summary>
public static class KeyedHasIC
{
    public static JSValue Has(Isolate isolate, FeedbackVector? vector, int slot, JSValue obj, JSValue key)
    {
        if (!obj.IsJSReceiver)
        {
            return isolate.ThrowTypeError(MessageTemplate.InvalidInOperatorUse, key, obj);
        }
        if (vector is not null && isolate.Flags.use_ic)
        {
            var ic = new KeyedLoadIC(isolate, vector, slot, FeedbackSlotKind.kHasKeyed);
            return ic.Load(obj, key);
        }
        return RuntimeObject.HasProperty(isolate, obj, key);
    }
}
