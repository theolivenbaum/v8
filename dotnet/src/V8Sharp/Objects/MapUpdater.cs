// Port of src/objects/map-updater.{h,cc}: map generalization, reconfiguration
// and deprecation-driven updates.
using V8Sharp.Common;

namespace V8Sharp.Objects;

/// <summary>
/// V8's MapUpdater: reconfigures or generalizes a descriptor of a map (or its
/// elements kind or prototype) and produces the most up-to-date map of the
/// resulting transition tree, deprecating the old branch when needed.
/// </summary>
public sealed class MapUpdater
{
    enum State
    {
        Initialized,
        AtRootMap,
        AtTargetMap,
        AtIntegrityLevelSource,
        End,
    }

    public enum InstanceTypeChange
    {
        TypedArrayDetaching,
    }

    readonly Isolate _isolate;
    readonly Map _oldMap;
    DescriptorArray _oldDescriptors;
    Map? _rootMap;
    Map? _targetMap;
    Map? _resultMap;
    readonly int _oldNof;
    bool _hasIntegrityLevelTransition;
    PropertyAttributes _integrityLevel = PropertyAttributes.NONE;
    Symbol? _integrityLevelSymbol;
    Map? _integritySourceMap;
    State _state = State.Initialized;
    ElementsKind _newElementsKind;
    InstanceType _newInstanceType;
    bool _isTransitionableFastElementsKind;
    JSReceiver? _newPrototype;
    bool _hasNewPrototype;
    InternalIndex _modifiedDescriptor = InternalIndex.NotFound;
    PropertyKind _newKind = PropertyKind.Data;
    PropertyAttributes _newAttributes = PropertyAttributes.NONE;
    PropertyConstness _newConstness = PropertyConstness.Mutable;
    PropertyLocation _newLocation = PropertyLocation.Field;
    Representation _newRepresentation = Representation.None;
    HeapObject _newFieldType = FieldType.None;

    public MapUpdater(Isolate isolate, Map oldMap)
    {
        _isolate = isolate;
        _oldMap = oldMap;
        _oldDescriptors = oldMap.InstanceDescriptors;
        _oldNof = oldMap.NumberOfOwnDescriptors;
        _newElementsKind = oldMap.ElementsKind;
        _newInstanceType = oldMap.InstanceType;
        _isTransitionableFastElementsKind = ElementsKinds.IsTransitionableFastElementsKind(_newElementsKind);
    }

    static bool EqualImmutableValues(in JSValue obj1, in JSValue obj2) => obj1.IsIdenticalTo(obj2);

    static HeapObject GeneralizeFieldType(Representation rep1, HeapObject type1, Representation rep2, HeapObject type2)
    {
        // Cleared field types need special treatment. They represent lost knowledge,
        // so we must be conservative, so their generalization with any other type
        // is "Any".
        if (FieldType.NowIs(type1, type2)) return type2;
        if (FieldType.NowIs(type2, type1)) return type1;
        return FieldType.Any;
    }

    Name GetKey(InternalIndex descriptor) => _oldDescriptors.GetKey(descriptor);

    PropertyDetails GetDetails(InternalIndex descriptor)
    {
        if (descriptor == _modifiedDescriptor)
        {
            PropertyAttributes attributes = _newAttributes;
            // If the original map was sealed or frozen, let's use the old
            // attributes so that we follow the same transition path as before.
            if ((_integrityLevel == PropertyAttributes.SEALED || _integrityLevel == PropertyAttributes.FROZEN) &&
                (_newAttributes & PropertyAttributes.READ_ONLY) == 0)
            {
                attributes = _oldDescriptors.GetDetails(descriptor).Attributes;
            }
            return new PropertyDetails(_newKind, attributes, _newLocation, _newConstness, _newRepresentation);
        }
        return _oldDescriptors.GetDetails(descriptor);
    }

    JSValue GetValue(InternalIndex descriptor)
    {
        if (descriptor == _modifiedDescriptor) return default;
        return _oldDescriptors.GetStrongValue(descriptor);
    }

    HeapObject GetFieldType(InternalIndex descriptor)
    {
        if (descriptor == _modifiedDescriptor) return _newFieldType;
        return _oldDescriptors.GetFieldType(descriptor);
    }

    HeapObject GetOrComputeFieldType(InternalIndex descriptor, PropertyLocation location, Representation representation)
    {
        if (location == PropertyLocation.Field) return GetFieldType(descriptor);
        return ObjectOps.OptimalType(GetValue(descriptor), _isolate, representation);
    }

    HeapObject GetOrComputeFieldType(DescriptorArray descriptors, InternalIndex descriptor, PropertyLocation location,
        Representation representation)
    {
        if (location == PropertyLocation.Field) return descriptors.GetFieldType(descriptor);
        return ObjectOps.OptimalType(descriptors.GetStrongValue(descriptor), _isolate, representation);
    }

    /// <summary>MapUpdater::ReconfigureToDataField.</summary>
    public Map ReconfigureToDataField(InternalIndex descriptor, PropertyAttributes attributes, PropertyConstness constness,
        Representation representation, HeapObject fieldType)
    {
        Debug.Assert(_state == State.Initialized);
        Debug.Assert(descriptor.IsFound);
        Debug.Assert(!_oldMap.IsDictionaryMap);

        _modifiedDescriptor = descriptor;
        _newKind = PropertyKind.Data;
        _newAttributes = attributes;
        _newLocation = PropertyLocation.Field;

        PropertyDetails oldDetails = _oldDescriptors.GetDetails(_modifiedDescriptor);

        // If property kind is not reconfigured merge the result with
        // representation/field type from the old descriptor.
        if (oldDetails.Kind == _newKind)
        {
            _newConstness = PropertyDetailsHelpers.GeneralizeConstness(constness, oldDetails.Constness);
            Representation oldRepresentation = oldDetails.Representation;
            _newRepresentation = representation.Generalize(oldRepresentation);
            HeapObject oldFieldType = GetOrComputeFieldType(_oldDescriptors, _modifiedDescriptor, oldDetails.Location, _newRepresentation);
            _newFieldType = GeneralizeFieldType(oldRepresentation, oldFieldType, _newRepresentation, fieldType);
        }
        else
        {
            // We don't know if this is a first property kind reconfiguration
            // and we don't know which value was in this property previously
            // therefore we can't treat such a property as constant.
            _newConstness = PropertyConstness.Mutable;
            _newRepresentation = representation;
            _newFieldType = fieldType;
        }

        Map.GeneralizeIfCanHaveTransitionableFastElementsKind(_oldMap.InstanceType, ref _newRepresentation, ref _newFieldType);

        if (TryReconfigureToDataFieldInplace() == State.End) return _resultMap!;
        if (FindRootMap() == State.End) return _resultMap!;
        if (FindTargetMap() == State.End) return _resultMap!;
        if (ConstructNewMap() == State.AtIntegrityLevelSource) ConstructNewMapWithIntegrityLevelTransition();
        if (_state != State.End) throw new InvalidOperationException("MapUpdater did not finish");
        return _resultMap!;
    }

    /// <summary>MapUpdater::ReconfigureElementsKind.</summary>
    public Map ReconfigureElementsKind(ElementsKind elementsKind)
    {
        Debug.Assert(_state == State.Initialized);
        _newElementsKind = elementsKind;
        _isTransitionableFastElementsKind = ElementsKinds.IsTransitionableFastElementsKind(_newElementsKind);
        return Update();
    }

    public Map ChangeInstanceType(InstanceTypeChange instanceTypeChange)
    {
        Debug.Assert(_state == State.Initialized);
        switch (instanceTypeChange)
        {
            case InstanceTypeChange.TypedArrayDetaching:
                if (_oldMap.InstanceType != InstanceType.JSTypedArrayType) throw new InvalidOperationException();
                // V8Sharp has no separate JS_DETACHED_TYPED_ARRAY_TYPE; the detached
                // state lives on the buffer.
                break;
        }
        return Update();
    }

    /// <summary>MapUpdater::ApplyPrototypeTransition.</summary>
    public Map ApplyPrototypeTransition(JSReceiver? prototype)
    {
        Debug.Assert(_state == State.Initialized);
        Debug.Assert(!ReferenceEquals(_oldMap.Prototype, prototype));
        // Prototype maps are replaced by deprecation when their prototype changes. No
        // need to add a transition.
        if (_oldMap.IsPrototypeMap) return Map.CopyForPrototypeTransition(_isolate, _oldMap, prototype);
        _newPrototype = prototype;
        _hasNewPrototype = true;
        return Update();
    }

    public Map Update() => UpdateImpl();

    Map UpdateImpl()
    {
        Debug.Assert(_state == State.Initialized);
        if (FindRootMap() == State.End) return _resultMap!;
        if (FindTargetMap() == State.End) return _resultMap!;
        if (ConstructNewMap() == State.AtIntegrityLevelSource) ConstructNewMapWithIntegrityLevelTransition();
        if (_state != State.End) throw new InvalidOperationException("MapUpdater did not finish");
        if (_isolate.Flags.fast_map_update && _oldMap.IsDeprecated)
        {
            TransitionsAccessor.SetMigrationTarget(_isolate, _oldMap, _resultMap!);
        }
        return _resultMap!;
    }

    struct IntegrityLevelTransitionInfo(Map map)
    {
        public bool HasIntegrityLevelTransition;
        public PropertyAttributes IntegrityLevel;
        public Map IntegrityLevelSourceMap = map;
        public Symbol? IntegrityLevelSymbol;
    }

    static IntegrityLevelTransitionInfo DetectIntegrityLevelTransitions(Map map, Isolate isolate)
    {
        var info = new IntegrityLevelTransitionInfo(map);
        // Figure out the most restrictive integrity level transition (it should
        // be the last one in the transition tree).
        Debug.Assert(!map.IsExtensible);
        Map previous = (Map)map.GetBackPointer()!;
        var lastTransitions = new TransitionsAccessor(isolate, previous);
        if (!lastTransitions.HasIntegrityLevelTransitionTo(map, out info.IntegrityLevelSymbol, out info.IntegrityLevel))
        {
            // The last transition was not integrity level transition - just bail out.
            return info;
        }
        Map sourceMap = previous;
        // Now walk up the back pointer chain and skip all integrity level
        // transitions. If we encounter any non-integrity level transition interleaved
        // with integrity level transitions, just bail out.
        while (!sourceMap.IsExtensible)
        {
            previous = (Map)sourceMap.GetBackPointer()!;
            var transitions = new TransitionsAccessor(isolate, previous);
            if (!transitions.HasIntegrityLevelTransitionTo(sourceMap, out _, out _)) return info;
            sourceMap = previous;
        }
        // Integrity-level transitions never change number of descriptors.
        if (map.NumberOfOwnDescriptors != sourceMap.NumberOfOwnDescriptors) throw new InvalidOperationException();
        info.HasIntegrityLevelTransition = true;
        info.IntegrityLevelSourceMap = sourceMap;
        return info;
    }

    /// <summary>MapUpdater::TryUpdateNoLock: find the updated map without creating any.</summary>
    public static Map? TryUpdateNoLock(Isolate isolate, Map oldMap)
    {
        // Check the state of the root map.
        Map rootMap = oldMap.FindRootMap();
        if (rootMap.IsDeprecated)
        {
            JSFunction constructor = (JSFunction)rootMap.GetConstructor()!;
            Debug.Assert(constructor.HasInitialMap);
            Debug.Assert(constructor.InitialMap.IsDictionaryMap);
            if (constructor.InitialMap.ElementsKind != oldMap.ElementsKind) return null;
            return constructor.InitialMap;
        }

        if (!ReferenceEquals(rootMap.Prototype, oldMap.Prototype))
        {
            Map? maybeTransition = TransitionsAccessor.GetPrototypeTransition(isolate, rootMap, oldMap.Prototype);
            if (maybeTransition is null) return null;
            rootMap = maybeTransition;
        }

        if (!oldMap.EquivalentToForTransition(rootMap)) return null;

        ElementsKind fromKind = rootMap.ElementsKind;
        ElementsKind toKind = oldMap.ElementsKind;

        var info = new IntegrityLevelTransitionInfo(oldMap);
        if (rootMap.IsExtensible != oldMap.IsExtensible)
        {
            info = DetectIntegrityLevelTransitions(oldMap, isolate);
            // Bail out if there were some private symbol transitions mixed up
            // with the integrity level transitions.
            if (!info.HasIntegrityLevelTransition) return null;
            // Make sure to replay the original elements kind transitions, before
            // the integrity level transition sets the elements to dictionary mode.
            toKind = info.IntegrityLevelSourceMap.ElementsKind;
        }
        if (fromKind != toKind)
        {
            // Try to follow existing elements kind transitions.
            Map? m = rootMap.LookupElementsTransitionMap(isolate, toKind);
            if (m is null) return null;
            rootMap = m;
        }

        // Replay the transitions as they were before the integrity level transition.
        Map? result = rootMap.TryReplayPropertyTransitions(isolate, info.IntegrityLevelSourceMap);
        if (result is null) return null;
        if (info.HasIntegrityLevelTransition)
        {
            // Now replay the integrity level transition.
            result = new TransitionsAccessor(isolate, result).SearchSpecial(info.IntegrityLevelSymbol!);
        }
        if (result is null) return null;
        if (oldMap.ElementsKind != result.ElementsKind) throw new InvalidOperationException("elements kind mismatch after map update");
        if (oldMap.InstanceType != result.InstanceType) throw new InvalidOperationException("instance type mismatch after map update");
        return result;
    }

    void GeneralizeField(Map map, InternalIndex modifyIndex, PropertyConstness newConstness, Representation newRepresentation,
        HeapObject newFieldType) =>
        GeneralizeField(_isolate, map, modifyIndex, newConstness, newRepresentation, newFieldType);

    State Normalize(string reason)
    {
        _resultMap = Map.Normalize(_isolate, _oldMap, _newInstanceType, _newElementsKind, _newPrototype, _hasNewPrototype,
            PropertyNormalizationMode.CLEAR_INOBJECT_PROPERTIES, true, reason);
        _state = State.End;
        return _state;
    }

    /// <summary>MapUpdater::CompleteInobjectSlackTracking.</summary>
    public static void CompleteInobjectSlackTracking(Isolate isolate, Map initialMap)
    {
        // Has to be an initial map.
        if (initialMap.GetBackPointer() is not null) throw new InvalidOperationException("not an initial map");
        int slack = initialMap.ComputeMinObjectSlack(isolate);
        var transitions = new TransitionsAccessor(isolate, initialMap);
        if (slack != 0)
        {
            // Resize the initial map and all maps in its transition tree.
            transitions.TraverseTransitionTree(map =>
            {
                map.SetInstanceSize(map.InstanceSizeFromSlack(slack));
                map.ConstructionCounter = Map.kNoSlackTracking;
            });
        }
        else
        {
            // Stop slack tracking for this map.
            transitions.TraverseTransitionTree(map => map.ConstructionCounter = Map.kNoSlackTracking);
        }
    }

    State TryReconfigureToDataFieldInplace()
    {
        // Updating deprecated maps in-place doesn't make sense.
        if (_oldMap.IsDeprecated) return _state;
        if (_newRepresentation.IsNone) return _state;  // Not done yet.

        PropertyDetails oldDetails = _oldDescriptors.GetDetails(_modifiedDescriptor);
        if (oldDetails.Attributes != _newAttributes || oldDetails.Kind != _newKind || oldDetails.Location != _newLocation)
        {
            // These changes can't be done in-place.
            return _state;  // Not done yet.
        }

        Representation oldRepresentation = oldDetails.Representation;
        if (!oldRepresentation.CanBeInPlaceChangedTo(_newRepresentation)) return _state;  // Not done yet.

        GeneralizeField(_oldMap, _modifiedDescriptor, _newConstness, _newRepresentation, _newFieldType);
        _resultMap = _oldMap;
        _state = State.End;
        return _state;  // Done.
    }

    bool TrySaveIntegrityLevelTransitions()
    {
        // Figure out the most restrictive integrity level transition (it should
        // be the last one in the transition tree).
        Map previous = (Map)_oldMap.GetBackPointer()!;
        var lastTransitions = new TransitionsAccessor(_isolate, previous);
        if (!lastTransitions.HasIntegrityLevelTransitionTo(_oldMap, out Symbol? integrityLevelSymbol, out _integrityLevel))
        {
            return false;
        }
        _integrityLevelSymbol = integrityLevelSymbol;
        _integritySourceMap = previous;

        // Now walk up the back pointer chain and skip all integrity level
        // transitions. If we encounter any non-integrity level transition interleaved
        // with integrity level transitions, just bail out.
        while (!_integritySourceMap.IsExtensible)
        {
            previous = (Map)_integritySourceMap.GetBackPointer()!;
            var transitions = new TransitionsAccessor(_isolate, previous);
            if (!transitions.HasIntegrityLevelTransitionTo(_integritySourceMap, out _, out _)) return false;
            _integritySourceMap = previous;
        }

        // Integrity-level transitions never change number of descriptors.
        if (_oldMap.NumberOfOwnDescriptors != _integritySourceMap.NumberOfOwnDescriptors) throw new InvalidOperationException();
        _hasIntegrityLevelTransition = true;
        _oldDescriptors = _integritySourceMap.InstanceDescriptors;
        return true;
    }

    State FindRootMap()
    {
        Debug.Assert(_state == State.Initialized);
        if (!_hasNewPrototype)
        {
            _newPrototype = _oldMap.Prototype;
            _hasNewPrototype = true;
        }

        // Check the state of the root map.
        _rootMap = _oldMap.FindRootMap();
        ElementsKind fromKind = _rootMap.ElementsKind;
        ElementsKind toKind = _newElementsKind;

        // Root maps shall not be deprecated.
        if (_rootMap.IsDeprecated) throw new InvalidOperationException("deprecated root map");

        // In this first check allow the root map to have the wrong prototype and
        // instance type, as we will deal with these transitions later.
        if (!_oldMap.EquivalentToForTransition(_rootMap, true, _rootMap.Prototype, _rootMap.InstanceType))
        {
            return Normalize("Normalize_NotEquivalent");
        }
        if (_oldMap.IsExtensible != _rootMap.IsExtensible)
        {
            // We have an integrity level transition in the tree, let us make a note
            // of that transition to be able to replay it later.
            if (!TrySaveIntegrityLevelTransitions())
            {
                return Normalize("Normalize_PrivateSymbolsOnNonExtensible");
            }
            // We want to build transitions to the original element kind (before
            // the seal transitions), so change {to_kind} accordingly.
            toKind = _integritySourceMap!.ElementsKind;
        }

        if (fromKind != toKind && toKind != ElementsKind.DICTIONARY_ELEMENTS &&
            toKind != ElementsKind.SLOW_STRING_WRAPPER_ELEMENTS &&
            toKind != ElementsKind.SLOW_SLOPPY_ARGUMENTS_ELEMENTS &&
            !(ElementsKinds.IsTransitionableFastElementsKind(fromKind) &&
              ElementsKinds.IsMoreGeneralElementsKindTransition(fromKind, toKind)))
        {
            return Normalize("Normalize_InvalidElementsTransition");
        }

        int rootNof = _rootMap.NumberOfOwnDescriptors;
        if (_modifiedDescriptor.IsFound && _modifiedDescriptor.AsInt < rootNof)
        {
            PropertyDetails oldDetails = _oldDescriptors.GetDetails(_modifiedDescriptor);
            if (oldDetails.Kind != _newKind || oldDetails.Attributes != _newAttributes)
            {
                return Normalize("Normalize_RootModification1");
            }
            if (oldDetails.Location != PropertyLocation.Field)
            {
                return Normalize("Normalize_RootModification2");
            }
            if (!_newRepresentation.FitsInto(oldDetails.Representation))
            {
                return Normalize("Normalize_RootModification4");
            }
            // Modify root map in-place. The GeneralizeField method is a no-op
            // if the {old_map_} is already general enough to hold the requested
            // {new_constness_} and {new_field_type_}.
            GeneralizeField(_oldMap, _modifiedDescriptor, _newConstness, oldDetails.Representation, _newFieldType);
        }

        // From here on, use the map with correct elements kind and prototype as root
        // map.
        if (!ReferenceEquals(_rootMap.Prototype, _newPrototype))
        {
            _rootMap = Map.TransitionToUpdatePrototype(_isolate, _rootMap, _newPrototype);
            // Still allow the instance type to be off as we will update that next.
            if (!_oldMap.EquivalentToForTransition(_rootMap, true, _newPrototype, _rootMap.InstanceType))
            {
                return Normalize("Normalize_NotEquivalent");
            }
        }
        _rootMap = Map.AsElementsKind(_isolate, _rootMap, toKind);

        if (!_oldMap.EquivalentToForTransition(_rootMap, true, _newPrototype, _newInstanceType))
        {
            throw new InvalidOperationException("root map not equivalent");
        }
        _state = State.AtRootMap;
        return _state;  // Not done yet.
    }

    State FindTargetMap()
    {
        Debug.Assert(_state == State.AtRootMap);
        _targetMap = _rootMap!;

        int rootNof = _rootMap!.NumberOfOwnDescriptors;
        for (int ii = rootNof; ii < _oldNof; ii++)
        {
            var i = new InternalIndex(ii);
            PropertyDetails oldDetails = GetDetails(i);
            Map? tmpMap = TransitionsAccessor.SearchTransition(_isolate, _targetMap, GetKey(i), oldDetails.Kind, oldDetails.Attributes);
            if (tmpMap is null) break;
            DescriptorArray tmpDescriptors = tmpMap.InstanceDescriptors;

            // Check if target map is incompatible.
            PropertyDetails tmpDetails = tmpDescriptors.GetDetails(i);
            if (oldDetails.Kind == PropertyKind.Accessor && !EqualImmutableValues(GetValue(i), tmpDescriptors.GetStrongValue(i)))
            {
                return Normalize("Normalize_Incompatible");
            }
            if (!PropertyDetailsHelpers.IsGeneralizableTo(oldDetails.Location, tmpDetails.Location)) break;
            Representation tmpRepresentation = tmpDetails.Representation;
            if (!oldDetails.Representation.FitsInto(tmpRepresentation))
            {
                // Try updating the field in-place to a generalized type.
                Representation generalized = tmpRepresentation.Generalize(oldDetails.Representation);
                if (!tmpRepresentation.CanBeInPlaceChangedTo(generalized)) break;
                tmpRepresentation = generalized;
            }

            if (tmpDetails.Location == PropertyLocation.Field)
            {
                HeapObject oldFieldType = GetOrComputeFieldType(i, oldDetails.Location, tmpRepresentation);
                GeneralizeField(tmpMap, i, oldDetails.Constness, tmpRepresentation, oldFieldType);
            }
            else
            {
                // kDescriptor: Check that the value matches.
                if (!EqualImmutableValues(GetValue(i), tmpDescriptors.GetStrongValue(i))) break;
            }
            _targetMap = tmpMap;
        }

        // Directly change the map if the target map is more general.
        int targetNof = _targetMap.NumberOfOwnDescriptors;
        if (targetNof == _oldNof)
        {
            if (!ReferenceEquals(_targetMap, _oldMap)) _oldMap.NotifyLeafMapLayoutChange(_isolate);
            if (!_hasIntegrityLevelTransition)
            {
                _resultMap = _targetMap;
                _state = State.End;
                return _state;  // Done.
            }

            // We try to replay the integrity level transition here.
            Map? maybeTransition = TransitionsAccessor.SearchSpecial(_isolate, _targetMap, _integrityLevelSymbol!);
            if (maybeTransition is not null)
            {
                _resultMap = maybeTransition;
                _state = State.End;
                return _state;  // Done.
            }
        }

        // Find the last compatible target map in the transition tree.
        for (int ii = targetNof; ii < _oldNof; ii++)
        {
            var i = new InternalIndex(ii);
            PropertyDetails oldDetails = GetDetails(i);
            Map? tmpMap = TransitionsAccessor.SearchTransition(_isolate, _targetMap, GetKey(i), oldDetails.Kind, oldDetails.Attributes);
            if (tmpMap is null) break;
            DescriptorArray tmpDescriptors = tmpMap.InstanceDescriptors;
            if (oldDetails.Kind == PropertyKind.Accessor && !EqualImmutableValues(GetValue(i), tmpDescriptors.GetStrongValue(i)))
            {
                return Normalize("Normalize_Incompatible");
            }
            _targetMap = tmpMap;
        }

        _state = State.AtTargetMap;
        return _state;  // Not done yet.
    }

    DescriptorArray BuildDescriptorArray()
    {
        InstanceType instanceType = _oldMap.InstanceType;
        int targetNof = _targetMap!.NumberOfOwnDescriptors;
        DescriptorArray targetDescriptors = _targetMap.InstanceDescriptors;

        // Allocate a new descriptor array large enough to hold the required
        // descriptors, with minimally the exact same size as the old descriptor
        // array.
        int newSlack = Math.Max(_oldNof, _oldDescriptors.NumberOfDescriptors) - _oldNof;
        DescriptorArray newDescriptors = DescriptorArray.Allocate(_oldNof, newSlack);

        int rootNof = _rootMap!.NumberOfOwnDescriptors;

        // Given that we passed root modification check in FindRootMap() so
        // the root descriptors are either not modified at all or already more
        // general than we requested. Take |root_nof| entries as is.
        int currentFieldIndex = 0;
        for (int ii = 0; ii < rootNof; ii++)
        {
            var i = new InternalIndex(ii);
            PropertyDetails oldDetails = _oldDescriptors.GetDetails(i);
            if (oldDetails.Location == PropertyLocation.Field) currentFieldIndex++;
            newDescriptors.Set(i, GetKey(i), _oldDescriptors.GetValue(i), oldDetails);
        }

        // Merge "updated" old_descriptor entries with target_descriptor entries.
        // |root_nof| -> |target_nof|
        for (int ii = rootNof; ii < targetNof; ii++)
        {
            var i = new InternalIndex(ii);
            Name key = GetKey(i);
            PropertyDetails oldDetails = GetDetails(i);
            PropertyDetails targetDetails = targetDescriptors.GetDetails(i);

            PropertyKind nextKind = oldDetails.Kind;
            PropertyAttributes nextAttributes = oldDetails.Attributes;
            PropertyConstness nextConstness = PropertyDetailsHelpers.GeneralizeConstness(oldDetails.Constness, targetDetails.Constness);

            // Note: failed values equality check does not invalidate per-object
            // property constness.
            PropertyLocation nextLocation =
                oldDetails.Location == PropertyLocation.Field ||
                targetDetails.Location == PropertyLocation.Field ||
                !EqualImmutableValues(targetDescriptors.GetStrongValue(i), GetValue(i))
                    ? PropertyLocation.Field
                    : PropertyLocation.Descriptor;

            Representation nextRepresentation = oldDetails.Representation.Generalize(targetDetails.Representation);

            if (nextLocation == PropertyLocation.Field)
            {
                HeapObject oldFieldType = GetOrComputeFieldType(i, oldDetails.Location, nextRepresentation);
                HeapObject targetFieldType = GetOrComputeFieldType(targetDescriptors, i, targetDetails.Location, nextRepresentation);
                HeapObject nextFieldType = GeneralizeFieldType(oldDetails.Representation, oldFieldType, nextRepresentation, targetFieldType);
                Map.GeneralizeIfCanHaveTransitionableFastElementsKind(instanceType, ref nextRepresentation, ref nextFieldType);
                if (nextKind != PropertyKind.Data) throw new NotImplementedException("mutable accessors are not implemented yet");
                Descriptor d = Descriptor.DataField(key, currentFieldIndex, nextAttributes, nextConstness, nextRepresentation, nextFieldType);
                currentFieldIndex++;
                newDescriptors.Set(i, d);
            }
            else
            {
                JSValue value = GetValue(i);
                Descriptor d = Descriptor.AccessorConstant(key, value.Object, nextAttributes);
                newDescriptors.Set(i, d);
            }
        }

        // Take "updated" old_descriptor entries.
        // |target_nof| -> |old_nof|
        for (int ii = targetNof; ii < _oldNof; ii++)
        {
            var i = new InternalIndex(ii);
            PropertyDetails oldDetails = GetDetails(i);
            Name key = GetKey(i);

            PropertyKind nextKind = oldDetails.Kind;
            PropertyAttributes nextAttributes = oldDetails.Attributes;
            PropertyConstness nextConstness = oldDetails.Constness;
            PropertyLocation nextLocation = oldDetails.Location;
            Representation nextRepresentation = oldDetails.Representation;

            if (nextLocation == PropertyLocation.Field)
            {
                HeapObject nextFieldType = GetOrComputeFieldType(i, oldDetails.Location, nextRepresentation);
                // If the |new_elements_kind_| is still transitionable then the old map's
                // elements kind is also transitionable and therefore the old descriptors
                // array must already have generalized field type.
                if (_isTransitionableFastElementsKind && !Map.IsMostGeneralFieldType(nextRepresentation, nextFieldType))
                {
                    throw new InvalidOperationException("field type not generalized");
                }
                if (nextKind != PropertyKind.Data) throw new NotImplementedException("mutable accessors are not implemented yet");
                Descriptor d = Descriptor.DataField(key, currentFieldIndex, nextAttributes, nextConstness, nextRepresentation, nextFieldType);
                currentFieldIndex++;
                newDescriptors.Set(i, d);
            }
            else
            {
                JSValue value = GetValue(i);
                Descriptor d = nextKind == PropertyKind.Data
                    ? Descriptor.DataConstant(key, value, nextAttributes)
                    : Descriptor.AccessorConstant(key, value.Object, nextAttributes);
                newDescriptors.Set(i, d);
            }
        }

        newDescriptors.Sort();
        return newDescriptors;
    }

    Map FindSplitMap(DescriptorArray descriptors)
    {
        int rootNof = _rootMap!.NumberOfOwnDescriptors;
        Map current = _rootMap;
        for (int ii = rootNof; ii < _oldNof; ii++)
        {
            var i = new InternalIndex(ii);
            Name name = descriptors.GetKey(i);
            PropertyDetails details = descriptors.GetDetails(i);
            Map? next = new TransitionsAccessor(_isolate, current).SearchTransition(name, details.Kind, details.Attributes);
            if (next is null) break;
            DescriptorArray nextDescriptors = next.InstanceDescriptors;
            PropertyDetails nextDetails = nextDescriptors.GetDetails(i);
            if (details.Constness != nextDetails.Constness) break;
            if (details.Location != nextDetails.Location) break;
            if (!details.Representation.Equals(nextDetails.Representation)) break;
            if (nextDetails.Location == PropertyLocation.Field)
            {
                HeapObject nextFieldType = nextDescriptors.GetFieldType(i);
                if (!FieldType.NowIs(descriptors.GetFieldType(i), nextFieldType)) break;
            }
            else if (!EqualImmutableValues(descriptors.GetStrongValue(i), nextDescriptors.GetStrongValue(i)))
            {
                break;
            }
            current = next;
        }
        return current;
    }

    State ConstructNewMap()
    {
        DescriptorArray newDescriptors = BuildDescriptorArray();
        Map splitMap = FindSplitMap(newDescriptors);
        int splitNof = splitMap.NumberOfOwnDescriptors;
        if (_oldNof == splitNof)
        {
            if (!_hasIntegrityLevelTransition) throw new InvalidOperationException();
            _state = State.AtIntegrityLevelSource;
            return _state;
        }
        var splitIndex = new InternalIndex(splitNof);
        PropertyDetails splitDetails = GetDetails(splitIndex);

        // Invalidate a transition target at |key|.
        Map? maybeTransition = TransitionsAccessor.SearchTransition(_isolate, splitMap, GetKey(splitIndex),
            splitDetails.Kind, splitDetails.Attributes);
        maybeTransition?.DeprecateTransitionTree(_isolate);

        // If |maybe_transition| is not nullptr then the transition array already
        // contains entry for given descriptor. This means that the transition
        // could be inserted regardless of whether transitions array is full or not.
        if (maybeTransition is null && !TransitionsAccessor.CanHaveMoreTransitions(_isolate, splitMap))
        {
            return Normalize("Normalize_CantHaveMoreTransitions");
        }

        _oldMap.NotifyLeafMapLayoutChange(_isolate);

        Map newMap = Map.AddMissingTransitions(_isolate, splitMap, newDescriptors);

        bool hadAnyEnumCache = splitMap.InstanceDescriptors.EnumCache.Keys.Length > 0 ||
                               _oldDescriptors.EnumCache.Keys.Length > 0;

        // Deprecated part of the transition tree is no longer reachable, so replace
        // current instance descriptors in the "survived" part of the tree with
        // the new descriptors to maintain descriptors sharing invariant.
        ReplaceDescriptorsOf(splitMap, newDescriptors);

        // If the old descriptors had an enum cache (or if {split_map}'s descriptors
        // had one), make sure the new ones do too.
        if (hadAnyEnumCache && newMap.NumberOfEnumerableProperties() > 0)
        {
            FastKeyAccumulator.InitializeFastPropertyEnumCache(_isolate, newMap, newMap.NumberOfEnumerableProperties());
        }

        if (_hasIntegrityLevelTransition)
        {
            _targetMap = newMap;
            _state = State.AtIntegrityLevelSource;
        }
        else
        {
            _resultMap = newMap;
            _state = State.End;
        }
        return _state;  // Done.
    }

    void ReplaceDescriptorsOf(Map splitMap, DescriptorArray newDescriptors) =>
        splitMap.ReplaceDescriptors(_isolate, newDescriptors);

    State ConstructNewMapWithIntegrityLevelTransition()
    {
        Debug.Assert(_state == State.AtIntegrityLevelSource);
        if (!TransitionsAccessor.CanHaveMoreTransitions(_isolate, _targetMap!))
        {
            return Normalize("Normalize_CantHaveMoreTransitions");
        }
        _resultMap = Map.CopyForPreventExtensions(_isolate, _targetMap!, _integrityLevel, _integrityLevelSymbol!,
            "CopyForPreventExtensions", _oldMap.ElementsKind == ElementsKind.DICTIONARY_ELEMENTS);
        _state = State.End;
        return _state;
    }

    /// <summary>MapUpdater::ReconfigureExistingProperty.</summary>
    public static Map ReconfigureExistingProperty(Isolate isolate, Map map, InternalIndex descriptor, PropertyKind kind,
        PropertyAttributes attributes, PropertyConstness constness)
    {
        // Dictionaries have to be reconfigured in-place.
        Debug.Assert(!map.IsDictionaryMap);
        Debug.Assert(kind == PropertyKind.Data);  // Only kData case is supported so far.
        if (map.GetBackPointer() is not Map)
        {
            // There is no benefit from reconstructing transition tree for maps without
            // back pointers, normalize and try to hit the map cache instead.
            return Map.Normalize(isolate, map, PropertyNormalizationMode.CLEAR_INOBJECT_PROPERTIES, "Normalize_AttributesMismatchProtoMap");
        }
        return new MapUpdater(isolate, map).ReconfigureToDataField(descriptor, attributes, constness, Representation.None, FieldType.None);
    }

    static void UpdateFieldType(Isolate isolate, Map map, InternalIndex descriptor, Name name, PropertyConstness newConstness,
        Representation newRepresentation, HeapObject newType)
    {
        PropertyDetails details = map.InstanceDescriptors.GetDetails(descriptor);
        if (details.Location != PropertyLocation.Field) return;
        if (details.Kind != PropertyKind.Data) throw new InvalidOperationException();

        if (newConstness != details.Constness && map.IsPrototypeMap)
        {
            JSObject.InvalidatePrototypeChains(map);
        }

        var backlog = new Queue<Map>();
        backlog.Enqueue(map);
        var sidestepTransition = new List<Map>();

        while (backlog.Count > 0)
        {
            Map current = backlog.Dequeue();
            var transitions = new TransitionsAccessor(isolate, current);
            transitions.ForEachTransition(backlog.Enqueue);
            if (transitions.HasSideStepTransitions())
            {
                for (int k = 0; k < SideStepTransition.kSize; k++)
                {
                    HeapObject target = transitions.GetSideStepTransition((SideStepTransition.Kind)k);
                    if (target is Map m && !m.IsDeprecated) sidestepTransition.Add(m);
                }
            }

            DescriptorArray descriptors = current.InstanceDescriptors;
            details = descriptors.GetDetails(descriptor);

            // It is allowed to change representation here only from None
            // to something or from Smi or HeapObject to Tagged.
            if (!(details.Representation.Equals(newRepresentation) || details.Representation.CanBeInPlaceChangedTo(newRepresentation)))
            {
                throw new InvalidOperationException("invalid in-place representation change");
            }

            // Skip if we already updated the shared descriptor or the target was more
            // general in the first place.
            if (newConstness == details.Constness && newRepresentation.Equals(details.Representation) &&
                FieldType.Equals(descriptors.GetFieldType(descriptor), newType))
            {
                continue;
            }

            Descriptor d = Descriptor.DataField(name, details.FieldIndex, details.Attributes, newConstness, newRepresentation, newType);
            descriptors.Replace(descriptor, d);
        }

        foreach (Map current in sidestepTransition)
        {
            DescriptorArray descriptors = current.InstanceDescriptors;
            details = descriptors.GetDetails(descriptor);
            // Through side-steps we can reach transition trees which are already more
            // generalized. Ensure we don't re-concretize them.
            PropertyConstness curNewConstness = PropertyDetailsHelpers.GeneralizeConstness(newConstness, details.Constness);
            Representation curNewRepresentation = newRepresentation.Generalize(details.Representation);
            HeapObject curNewType = GeneralizeFieldType(details.Representation, descriptors.GetFieldType(descriptor), curNewRepresentation, newType);
            if (curNewConstness != details.Constness || !curNewRepresentation.Equals(details.Representation) ||
                !FieldType.Equals(descriptors.GetFieldType(descriptor), curNewType))
            {
                GeneralizeField(isolate, current, descriptor, curNewConstness, curNewRepresentation, curNewType);
            }
        }
    }

    /// <summary>MapUpdater::GeneralizeField.</summary>
    public static void GeneralizeField(Isolate isolate, Map map, InternalIndex modifyIndex, PropertyConstness newConstness,
        Representation newRepresentation, HeapObject newFieldType)
    {
        if (map.IsDeprecated) throw new InvalidOperationException("deprecated map");

        // Check if we actually need to generalize the field type at all.
        DescriptorArray oldDescriptors = map.InstanceDescriptors;
        PropertyDetails oldDetails = oldDescriptors.GetDetails(modifyIndex);
        PropertyConstness oldConstness = oldDetails.Constness;
        Representation oldRepresentation = oldDetails.Representation;
        HeapObject oldFieldType = oldDescriptors.GetFieldType(modifyIndex);

        // Return if the current map is general enough to hold requested constness and
        // representation/field type.
        if (PropertyDetailsHelpers.IsGeneralizableTo(newConstness, oldConstness) &&
            oldRepresentation.Equals(newRepresentation) &&
            FieldType.NowIs(newFieldType, oldFieldType))
        {
            return;
        }

        // Determine the field owner.
        Map fieldOwner = map.FindFieldOwner(modifyIndex);
        DescriptorArray descriptors = fieldOwner.InstanceDescriptors;

        newFieldType = GeneralizeFieldType(oldRepresentation, oldFieldType, newRepresentation, newFieldType);
        newConstness = PropertyDetailsHelpers.GeneralizeConstness(oldConstness, newConstness);

        Name name = descriptors.GetKey(modifyIndex);
        UpdateFieldType(isolate, fieldOwner, modifyIndex, name, newConstness, newRepresentation, newFieldType);

        DependentCode.DependencyGroups depGroups = DependentCode.DependencyGroups.None;
        if (newConstness != oldConstness) depGroups |= DependentCode.DependencyGroups.FieldConst;
        if (!FieldType.Equals(newFieldType, oldFieldType)) depGroups |= DependentCode.DependencyGroups.FieldType;
        if (!newRepresentation.Equals(oldRepresentation)) depGroups |= DependentCode.DependencyGroups.FieldRepresentation;
        DependentCode.DeoptimizeDependencyGroups(isolate, fieldOwner, depGroups);
    }
}
