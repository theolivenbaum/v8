// Port of the IC base class of src/ic/ic.{h,cc,-inl.h}: the feedback state
// machine (UNINITIALIZED -> MONOMORPHIC -> POLYMORPHIC (4 maps) ->
// MEGAMORPHIC with the stub cache), handler installation (SetCache,
// UpdatePolymorphicIC, ConfigureVectorState) and the RECOMPUTE_HANDLER
// logic. The concrete ICs are in LoadIC.cs and StoreIC.cs.
//
// An IC object is created on a miss only; hits are served by the static fast
// paths of the concrete ICs (V8's AccessorAssembler) without allocating.
using V8Sharp.Interpreter;

namespace V8Sharp.IC;

public abstract class IC
{
    protected readonly Isolate _isolate;
    protected readonly FeedbackVector? _vector;
    protected readonly int _slot;
    protected readonly FeedbackNexus _nexus;
    protected readonly FeedbackSlotKind _kind;
    protected internal InlineCacheState _state;
    protected internal InlineCacheState _oldState;
    protected internal Map? _lookupStartObjectMap;
    protected internal bool _vectorSet;

    protected IC(Isolate isolate, FeedbackVector? vector, int slot, FeedbackSlotKind kind)
    {
        _isolate = isolate;
        _vector = vector;
        _slot = slot;
        _kind = vector is null ? kind : vector.GetKind(slot);
        _nexus = new FeedbackNexus(isolate, vector, new FeedbackSlot(slot));
        _state = vector is null ? InlineCacheState.NO_FEEDBACK : _nexus.IcState();
        _oldState = _state;
    }

    public Isolate Isolate => _isolate;

    /// <summary>Takes over the state of <paramref name="outer"/> (a derived IC sharing its nexus).</summary>
    internal void ShareState(IC outer)
    {
        _state = outer._state;
        _oldState = outer._oldState;
        _lookupStartObjectMap = outer._lookupStartObjectMap;
    }
    public InlineCacheState State => _state;
    public FeedbackSlotKind Kind => _kind;

    public bool IsGlobalIC => FeedbackMetadata.IsGlobalICKind(_kind);
    public bool IsLoadIC => FeedbackMetadata.IsLoadICKind(_kind);
    public bool IsLoadGlobalIC => FeedbackMetadata.IsLoadGlobalICKind(_kind);
    public bool IsKeyedLoadIC => FeedbackMetadata.IsKeyedLoadICKind(_kind);
    public bool IsStoreGlobalIC => FeedbackMetadata.IsStoreGlobalICKind(_kind);
    public bool IsSetNamedIC => FeedbackMetadata.IsSetNamedICKind(_kind);
    public bool IsDefineNamedOwnIC => FeedbackMetadata.IsDefineNamedOwnICKind(_kind);
    public bool IsStoreInArrayLiteralIC => FeedbackMetadata.IsStoreInArrayLiteralICKind(_kind);
    public bool IsKeyedStoreIC => FeedbackMetadata.IsKeyedStoreICKind(_kind);
    public bool IsKeyedHasIC => FeedbackMetadata.IsKeyedHasICKind(_kind);
    public bool IsDefineKeyedOwnIC => FeedbackMetadata.IsDefineKeyedOwnICKind(_kind);
    public bool IsAnyLoad => IsLoadIC || IsLoadGlobalIC || IsKeyedLoadIC;
    public bool IsAnyHas => IsKeyedHasIC;
    public bool IsAnyDefineOwn => IsDefineNamedOwnIC || IsDefineKeyedOwnIC;
    public bool IsAnyStore =>
        IsSetNamedIC || IsDefineNamedOwnIC || IsStoreGlobalIC || IsKeyedStoreIC || IsStoreInArrayLiteralIC || IsDefineKeyedOwnIC;
    public bool IsKeyed => IsKeyedLoadIC || IsKeyedStoreIC || IsStoreInArrayLiteralIC || IsKeyedHasIC || IsDefineKeyedOwnIC;

    protected Map LookupStartObjectMap => _lookupStartObjectMap!;

    /// <summary>IC::update_lookup_start_object_map.</summary>
    protected void UpdateLookupStartObjectMap(JSValue obj) => _lookupStartObjectMap = ICMaps.MapOf(_isolate, obj);

    /// <summary>The stub cache of this IC kind (IC::stub_cache).</summary>
    protected StubCache StubCache
    {
        get
        {
            ICIsolateState state = ICIsolateState.Get(_isolate);
            if (IsAnyLoad) return state.LoadStubCache;
            if (IsAnyDefineOwn) return state.DefineOwnStubCache;
            return state.StoreStubCache;
        }
    }

    /// <summary>IC::MarkRecomputeHandler.</summary>
    protected void MarkRecomputeHandler(JSValue name)
    {
        _oldState = _state;
        _state = InlineCacheState.RECOMPUTE_HANDLER;
    }

    /// <summary>IC::UpdateState.</summary>
    protected void UpdateState(JSValue lookupStartObject, JSValue name)
    {
        if (_state == InlineCacheState.NO_FEEDBACK) return;
        UpdateLookupStartObjectMap(lookupStartObject);
        if (!name.IsName) return;
        if (_state != InlineCacheState.MONOMORPHIC && _state != InlineCacheState.POLYMORPHIC) return;
        if (lookupStartObject.IsNullOrUndefined) return;

        // Remove the target from the code cache if it became invalid
        // because of changes in the prototype chain to avoid hitting it
        // again.
        if (ShouldRecomputeHandler(name.As<Name>())) MarkRecomputeHandler(name);
    }

    /// <summary>IC::ShouldRecomputeHandler.</summary>
    bool ShouldRecomputeHandler(Name name)
    {
        if (!RecomputeHandlerForName(name)) return false;

        // This is a contextual access, always just update the handler and stay
        // monomorphic.
        if (IsGlobalIC) return true;

        JSValue maybeHandler = _nexus.FindHandlerForMap(LookupStartObjectMap);

        // The current map wasn't handled yet. There's no reason to stay monomorphic,
        // *unless* we're moving from a deprecated map to its replacement, or
        // to a more general elements kind.
        if (maybeHandler.IsUndefined)
        {
            if (!InstanceTypeChecks.IsJSObject(LookupStartObjectMap.InstanceType)) return false;
            Map? firstMap = _nexus.GetFirstMap();
            if (firstMap is null) return false;
            if (firstMap.IsDeprecated) return true;
            return ElementsKinds.IsMoreGeneralElementsKindTransition(firstMap.ElementsKind, LookupStartObjectMap.ElementsKind);
        }
        return true;
    }

    /// <summary>IC::RecomputeHandlerForName.</summary>
    bool RecomputeHandlerForName(JSValue name)
    {
        if (IsKeyed)
        {
            // Determine whether the failure is due to a name failure.
            if (!name.IsName) return false;
            Name? stubName = _nexus.GetName();
            if (!ReferenceEquals(name.HeapObjectOrNull, stubName)) return false;
        }
        return true;
    }

    /// <summary>MigrateDeprecated: migrates an object with a deprecated map.</summary>
    protected static bool MigrateDeprecated(Isolate isolate, JSValue obj)
    {
        if (obj.HeapObjectOrNull is not JSObject receiver) return false;
        if (!receiver.Map.IsDeprecated) return false;
        JSObject.MigrateInstance(isolate, receiver);
        return true;
    }

    // ---- Vector state -------------------------------------------------------------------

    /// <summary>IC::ConfigureVectorState(MEGAMORPHIC, key).</summary>
    protected bool ConfigureVectorStateMegamorphic(JSValue key)
    {
        bool changed = _nexus.ConfigureMegamorphic(key.IsName ? IcCheckType.kProperty : IcCheckType.kElement);
        if (changed) OnFeedbackChanged();
        return changed;
    }

    /// <summary>IC::ConfigureVectorState(name, map, handler).</summary>
    protected void ConfigureVectorState(Name? name, Map map, JSValue handler)
    {
        if (IsGlobalIC)
        {
            _nexus.ConfigureHandlerMode(handler);
        }
        else
        {
            // Non-keyed ICs don't track the name explicitly.
            if (!IsKeyed) name = null;
            _nexus.ConfigureMonomorphic(name, map, handler);
        }
        OnFeedbackChanged();
    }

    /// <summary>IC::ConfigureVectorState(name, maps_and_handlers).</summary>
    protected void ConfigureVectorState(Name? name, List<(Map Map, JSValue Handler)> mapsAndHandlers)
    {
        // Non-keyed ICs don't track the name explicitly.
        if (!IsKeyed) name = null;
        _nexus.ConfigurePolymorphic(name, mapsAndHandlers);
        OnFeedbackChanged();
    }

    /// <summary>IC::OnFeedbackChanged (TieringManager::NotifyICChanged).</summary>
    protected void OnFeedbackChanged() => _vectorSet = true;

    // ---- Cache update -----------------------------------------------------------------------

    /// <summary>IC::SetCache.</summary>
    protected void SetCache(Name name, HeapObject handler)
    {
        switch (_state)
        {
            case InlineCacheState.NO_FEEDBACK:
                throw new UnreachableException();
            case InlineCacheState.UNINITIALIZED:
                UpdateMonomorphicIC(handler, name);
                break;
            case InlineCacheState.RECOMPUTE_HANDLER:
            case InlineCacheState.MONOMORPHIC:
                if (IsGlobalIC)
                {
                    UpdateMonomorphicIC(handler, name);
                    break;
                }
                goto case InlineCacheState.POLYMORPHIC;
            case InlineCacheState.POLYMORPHIC:
                if (UpdatePolymorphicIC(name, handler)) break;
                goto case InlineCacheState.HOMOMORPHIC;
            case InlineCacheState.HOMOMORPHIC:
                if (!IsKeyed || _state == InlineCacheState.RECOMPUTE_HANDLER) CopyICToMegamorphicCache(name);
                goto case InlineCacheState.MEGADOM;
            case InlineCacheState.MEGADOM:
                ConfigureVectorStateMegamorphic(name);
                goto case InlineCacheState.MEGAMORPHIC;
            case InlineCacheState.MEGAMORPHIC:
                UpdateMegamorphicCache(LookupStartObjectMap, name, handler);
                // Indicate that we've handled this case.
                _vectorSet = true;
                break;
            case InlineCacheState.GENERIC:
                throw new UnreachableException();
        }
    }

    /// <summary>IC::UpdateMonomorphicIC.</summary>
    void UpdateMonomorphicIC(HeapObject handler, Name name) => ConfigureVectorState(name, LookupStartObjectMap, handler);

    /// <summary>IC::UpdatePolymorphicIC.</summary>
    bool UpdatePolymorphicIC(Name name, HeapObject handler)
    {
        if (IsKeyed && _state != InlineCacheState.RECOMPUTE_HANDLER)
        {
            if (!ReferenceEquals(_nexus.GetName(), name)) return false;
        }
        Map map = LookupStartObjectMap;

        var mapsAndHandlers = new List<(Map Map, JSValue Handler)>(_isolate.Flags.max_valid_polymorphic_map_count);
        int deprecatedMaps = 0;
        int handlerToOverwrite = -1;
        int i = 0;
        for (var it = new FeedbackIterator(_nexus); !it.Done; it.Advance())
        {
            if (FeedbackVector.IsCleared(it.Handler)) continue;
            JSValue existingHandler = it.Handler;
            Map existingMap = it.Map!;
            mapsAndHandlers.Add((existingMap, existingHandler));

            if (existingMap.IsDeprecated)
            {
                // Filter out deprecated maps to ensure their instances get migrated.
                deprecatedMaps++;
            }
            else if (ReferenceEquals(map, existingMap))
            {
                // If both map and handler stayed the same (and the name is also the
                // same as checked above, for keyed accesses), we're not progressing
                // in the lattice and need to go MEGAMORPHIC instead. There's one
                // exception to this rule, which is when we're in RECOMPUTE_HANDLER
                // state, there we allow to migrate to a new handler.
                if (ReferenceEquals(handler, existingHandler.HeapObjectOrNull) &&
                    _state != InlineCacheState.RECOMPUTE_HANDLER)
                {
                    return false;
                }
                if (map.IsDictionaryMap)
                {
                    // If the receiver type is a dictionary map and the handler is
                    // different, it means the dictionary rehashed. Go MEGAMORPHIC to
                    // prevent deopt loops.
                    return false;
                }
                // If the receiver type is already in the polymorphic IC, this indicates
                // there was a prototoype chain failure. In that case, just overwrite
                // the handler.
                handlerToOverwrite = i;
            }
            else if (handlerToOverwrite == -1 && IsTransitionOfMonomorphicTarget(existingMap, map))
            {
                handlerToOverwrite = i;
            }
            i++;
        }

        int numberOfMaps = mapsAndHandlers.Count;
        int numberOfValidMaps = numberOfMaps - deprecatedMaps - (handlerToOverwrite != -1 ? 1 : 0);

        int maxMaps = _isolate.Flags.max_valid_polymorphic_map_count;
        if (numberOfValidMaps >= maxMaps) return false;
        if (deprecatedMaps >= maxMaps) return false;
        if (numberOfMaps == 0 && _state != InlineCacheState.MONOMORPHIC && _state != InlineCacheState.POLYMORPHIC) return false;

        numberOfValidMaps++;
        if (numberOfValidMaps == 1)
        {
            ConfigureVectorState(name, LookupStartObjectMap, handler);
        }
        else
        {
            if (IsKeyed && !ReferenceEquals(_nexus.GetName(), name)) return false;
            if (handlerToOverwrite >= 0)
            {
                mapsAndHandlers[handlerToOverwrite] = (map, handler);
            }
            else
            {
                mapsAndHandlers.Add((map, handler));
            }
            ConfigureVectorState(name, mapsAndHandlers);
        }
        return true;
    }

    /// <summary>IC::IsTransitionOfMonomorphicTarget.</summary>
    bool IsTransitionOfMonomorphicTarget(Map? sourceMap, Map? targetMap)
    {
        if (sourceMap is null) return true;
        if (targetMap is null) return false;
        if (sourceMap.IsAbandonedPrototypeMap) return false;
        ElementsKind targetElementsKind = targetMap.ElementsKind;
        bool moreGeneralTransition = ElementsKinds.IsMoreGeneralElementsKindTransition(sourceMap.ElementsKind, targetElementsKind);
        if (!moreGeneralTransition) return false;
        Map? transitionedMap = sourceMap.FindElementsKindTransitionedMap(_isolate, [targetMap]);
        return ReferenceEquals(transitionedMap, targetMap);
    }

    /// <summary>IC::CopyICToMegamorphicCache.</summary>
    void CopyICToMegamorphicCache(Name name)
    {
        var mapsAndHandlers = new List<(Map Map, JSValue Handler)>();
        _nexus.ExtractMapsAndHandlers(mapsAndHandlers);
        foreach ((Map map, JSValue handler) in mapsAndHandlers)
        {
            if (handler.HeapObjectOrNull is { } h) UpdateMegamorphicCache(map, name, h);
        }
    }

    /// <summary>IC::UpdateMegamorphicCache.</summary>
    protected void UpdateMegamorphicCache(Map map, Name name, HeapObject handler)
    {
        if (!IsAnyHas) StubCache.Set(name, map, handler);
    }
}

/// <summary>The per-isolate IC state: the stub caches (Isolate::load_stub_cache & co).</summary>
public sealed class ICIsolateState
{
    public readonly StubCache LoadStubCache = new();
    public readonly StubCache StoreStubCache = new();
    public readonly StubCache DefineOwnStubCache = new();

    public static ICIsolateState Get(Isolate isolate) => isolate.ICState ??= new ICIsolateState();
}

/// <summary>
/// The lookup start object maps of primitives. V8's primitives have maps
/// (string maps, heap_number_map, oddball maps ...) that IC feedback keys on;
/// V8Sharp's primitives do not, so each native context has a stand-in map per
/// primitive kind whose prototype is the wrapper prototype (the map is only an
/// IC key and the root of the prototype chain validity cell).
/// </summary>
public static class ICMaps
{
    public sealed class PrimitiveMaps(Map stringMap, Map numberMap, Map booleanMap, Map symbolMap, Map bigIntMap)
    {
        public readonly Map StringMap = stringMap;
        public readonly Map NumberMap = numberMap;
        public readonly Map BooleanMap = booleanMap;
        public readonly Map SymbolMap = symbolMap;
        public readonly Map BigIntMap = bigIntMap;
    }

    /// <summary>The map feedback keys on for <paramref name="value"/> (V8: the value's map).</summary>
    public static Map? MapOf(Isolate isolate, in JSValue value)
    {
        HeapObject? o = value.HeapObjectOrNull;
        if (o is JSReceiver receiver) return receiver.Map;
        if (value.IsNullOrUndefined) return null;
        PrimitiveMaps maps = GetPrimitiveMaps(isolate, isolate.NativeContext);
        if (value.IsNumber) return maps.NumberMap;
        return o switch
        {
            JSString => maps.StringMap,
            Symbol => maps.SymbolMap,
            BigInt => maps.BigIntMap,
            Oddball when value.IsBoolean => maps.BooleanMap,
            _ => null,
        };
    }

    /// <summary>The primitive stand-in maps of a native context.</summary>
    public static PrimitiveMaps GetPrimitiveMaps(Isolate isolate, NativeContext nativeContext)
    {
        if (nativeContext.ICPrimitiveMaps is PrimitiveMaps existing) return existing;
        var maps = new PrimitiveMaps(
            NewPrimitiveMap(isolate, InstanceType.SeqStringType, nativeContext.StringFunction),
            NewPrimitiveMap(isolate, InstanceType.HeapNumberType, nativeContext.NumberFunction),
            NewPrimitiveMap(isolate, InstanceType.OddballType, nativeContext.BooleanFunction),
            NewPrimitiveMap(isolate, InstanceType.SymbolType, nativeContext.SymbolFunction),
            NewPrimitiveMap(isolate, InstanceType.BigIntType, nativeContext.BigIntFunction));
        nativeContext.ICPrimitiveMaps = maps;
        return maps;
    }

    static Map NewPrimitiveMap(Isolate isolate, InstanceType type, JSFunction wrapperFunction)
    {
        Map map = isolate.Factory.NewMap(type, 0);
        map.Prototype = wrapperFunction.InitialMap.Prototype;
        return map;
    }

    /// <summary>Whether the map is one of the primitive stand-in maps (V8's IsPrimitiveMap).</summary>
    public static bool IsPrimitiveMap(Map map) => !InstanceTypeChecks.IsJSReceiver(map.InstanceType);
}
