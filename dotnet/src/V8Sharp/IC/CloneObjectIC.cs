// Port of the CloneObjectIC used by the CloneObject bytecode (object spread
// `{...source}`): AccessorAssembler::GenerateCloneObjectIC
// (src/ic/accessor-assembler.cc), CodeStubAssembler::FastCloneJSObject
// (src/codegen/code-stub-assembler-inl.h), and from src/ic/ic.cc
// Runtime_CloneObjectIC_Miss, Runtime_CloneObjectIC_Slow, CloneObjectSlowPath,
// GetCloneModeForMap(PreCheck), CanCacheCloneTargetMapTransition,
// CanFastCloneObjectToObjectLiteral and the kCloneObject side-step
// transitions (GetCloneTargetMap, SetCloneTargetMap,
// SetCloneTargetMapUnsupported).
//
// The feedback is V8's: the source map with a handler that is the result map
// (monomorphic, or pairs in a polymorphic array), Smi 0 for "the empty object
// literal", or megamorphic. Deviation: V8Sharp's null and undefined have no
// map to key feedback on (ICMaps.MapOf), so cloning them builds the empty
// object without recording feedback; Smis take the slow path without feedback
// as in V8.
using System.Runtime.CompilerServices;
using V8Sharp.Interpreter;

namespace V8Sharp.IC;

public static class CloneObjectIC
{
    const int kHasNullPrototype = 1 << 4;  // ObjectLiteral::kHasNullPrototype

    /// <summary>FastCloneObjectMode (ic.cc).</summary>
    enum FastCloneObjectMode
    {
        // The clone has the same map as the input.
        kIdenticalMap,
        // The clone is the empty object literal.
        kEmptyObject,
        // The clone has an empty object literal map.
        kDifferentMap,
        // The source map is to complicated to handle.
        kNotSupported,
        // Returned by PreCheck
        kMaybeSupported,
    }

    /// <summary>CloneObject: AccessorAssembler::GenerateCloneObjectIC.</summary>
    public static JSValue Clone(Isolate isolate, FeedbackVector? vector, int slot, JSValue source, int flags)
    {
        if (source.HeapObjectOrNull is JSObject sourceObject && vector is not null)
        {
            Map sourceMap = sourceObject.Map;
            if (!sourceMap.IsDeprecated)
            {
                // Decide if monomorphic or polymorphic, then dispatch based on the handler.
                JSValue feedback = vector.Slots[slot];
                JSValue handler;
                if (ReferenceEquals(feedback._obj, sourceMap))
                {
                    handler = vector.Slots[slot + 1];
                    if (TryHandler(isolate, sourceObject, handler, out JSObject? result)) return result!;
                }
                else if (feedback._obj is FixedArray polymorphic)
                {
                    for (int i = 0; i < polymorphic.Length; i += FeedbackNexus.kCloneObjectPolymorphicEntrySize)
                    {
                        if (!ReferenceEquals(polymorphic[i]._obj, sourceMap)) continue;
                        handler = polymorphic[i + 1];
                        if (TryHandler(isolate, sourceObject, handler, out JSObject? result)) return result!;
                        break;
                    }
                }
                else if (ReferenceEquals(feedback._obj, ReadOnlyRoots.megamorphic_symbol))
                {
                    // CloneObjectIC_Slow.
                    return CloneObjectSlowPath(isolate, source, flags);
                }
            }
        }
        return Miss(isolate, vector, slot, source, flags);
    }

    /// <summary>The if_handler part of GenerateCloneObjectIC: false for a stale handler (miss).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static bool TryHandler(Isolate isolate, JSObject source, JSValue handler, out JSObject? result)
    {
        // When the result of cloning the object is an empty object literal we store
        // a Smi into the feedback.
        if (handler.IsSmi)
        {
            result = isolate.Factory.NewJSObjectFromMap(isolate.NativeContext.ObjectFunction.InitialMap);
            return true;
        }
        // Handlers for the CloneObjectIC stub are references to the Map of a
        // result object.
        if (handler._obj is Map resultMap && !resultMap.IsDeprecated)
        {
            result = FastCloneJSObject(source, resultMap);
            return true;
        }
        result = null;
        return false;
    }

    /// <summary>
    /// CodeStubAssembler::FastCloneJSObject: a new object of
    /// <paramref name="targetMap"/> with the source's property fields and a copy
    /// of its elements (copy-on-write elements are shared).
    /// </summary>
    static JSObject FastCloneJSObject(JSObject source, Map targetMap)
    {
        JSObject target = JSObject.AllocateForMap(targetMap);

        // Copy the property backing store: the in-object properties and the
        // PropertyArray (the IC only caches maps whose field layouts are
        // identical, JS_OBJECT_TYPE on both sides; JSObject.CopyFastFieldsFrom
        // also handles differing in-object counts).
        target.CopyFastFieldsFrom(source);

        // Clone the elements.
        FixedArrayBase sourceElements = source.Elements;
        if (sourceElements.Length > 0)
        {
            if (sourceElements.IsCowArray)
            {
                target.Elements = sourceElements;
            }
            else
            {
                var fixedArray = (FixedArray)sourceElements;
                target.Elements = fixedArray.CopyAndResize(fixedArray.Length, false);
            }
        }
        return target;
    }

    /// <summary>Runtime_CloneObjectIC_Miss.</summary>
    static JSValue Miss(Isolate isolate, FeedbackVector? vector, int slot, JSValue source, int flags)
    {
        if (!MigrateDeprecated(isolate, source))
        {
            FeedbackNexus? nexus = vector is not null ? new FeedbackNexus(isolate, vector, new FeedbackSlot(slot)) : null;
            if (!source.IsSmi && (nexus is null || !nexus.Value.IsMegamorphic))
            {
                bool nullProtoLiteral = (flags & kHasNullPrototype) != 0;
                Map? sourceMap = ICMaps.MapOf(isolate, source);
                if (sourceMap is null)
                {
                    // null and undefined (see the header comment): the empty object
                    // literal, or the null-prototype one.
                    return CloneObjectSlowPath(isolate, source, flags);
                }

                // In case we are still slack tracking let's defer a decision. The fast
                // case does not support it.
                if (!sourceMap.IsInobjectSlackTrackingInProgress())
                {
                    bool unsupported = false;
                    if (!nullProtoLiteral)
                    {
                        HeapObject maybeTarget = GetCloneTargetMap(isolate, sourceMap);
                        if (ReferenceEquals(maybeTarget, SideStepTransition.Unreachable))
                        {
                            unsupported = true;
                        }
                        else if (!ReferenceEquals(maybeTarget, SideStepTransition.Empty))
                        {
                            var target = (Map)maybeTarget;
                            nexus?.ConfigureCloneObject(sourceMap, target);
                            // When returning a map the IC miss handler re-starts from the top.
                            return FastCloneJSObject((JSObject)source.Object, target);
                        }
                    }

                    FastCloneObjectMode cloneMode = unsupported
                        ? FastCloneObjectMode.kNotSupported
                        : GetCloneModeForMap(isolate, sourceMap, nullProtoLiteral);
                    switch (cloneMode)
                    {
                        case FastCloneObjectMode.kIdenticalMap:
                        {
                            UpdateState(isolate, nexus, sourceMap, sourceMap, nullProtoLiteral);
                            // When returning a map the IC miss handler re-starts from the top.
                            return FastCloneJSObject((JSObject)source.Object, sourceMap);
                        }
                        case FastCloneObjectMode.kEmptyObject:
                        {
                            nexus?.ConfigureCloneObject(sourceMap, JSValue.Zero);
                            return CloneObjectSlowPath(isolate, source, flags);
                        }
                        case FastCloneObjectMode.kDifferentMap:
                        {
                            JSObject res = CloneObjectSlowPath(isolate, source, flags);
                            Map resultMap = res.Map;
                            if (resultMap.IsInobjectSlackTrackingInProgress()) return res;
                            if (CanFastCloneObjectToObjectLiteral(isolate, sourceMap, resultMap, nullProtoLiteral))
                            {
                                Debug.Assert(resultMap.OnlyHasSimpleProperties());
                                UpdateState(isolate, nexus, sourceMap, resultMap, nullProtoLiteral);
                            }
                            else
                            {
                                if (CanCacheCloneTargetMapTransition(isolate, sourceMap, null, nullProtoLiteral))
                                {
                                    SetCloneTargetMapUnsupported(isolate, sourceMap);
                                }
                                nexus?.ConfigureMegamorphic();
                            }
                            return res;
                        }
                        case FastCloneObjectMode.kNotSupported:
                            break;
                        default:
                            throw new UnreachableException();
                    }
                    Debug.Assert(cloneMode == FastCloneObjectMode.kNotSupported);
                    nexus?.ConfigureMegamorphic();
                }
            }
        }

        return CloneObjectSlowPath(isolate, source, flags);
    }

    static void UpdateState(Isolate isolate, FeedbackNexus? nexus, Map sourceMap, Map targetMap, bool nullProtoLiteral)
    {
        nexus?.ConfigureCloneObject(sourceMap, targetMap);
        if (CanCacheCloneTargetMapTransition(isolate, sourceMap, targetMap, nullProtoLiteral))
        {
            SetCloneTargetMap(isolate, sourceMap, targetMap);
        }
    }

    /// <summary>MigrateDeprecated (ic.cc).</summary>
    static bool MigrateDeprecated(Isolate isolate, JSValue obj)
    {
        if (obj.HeapObjectOrNull is not JSObject receiver) return false;
        if (!receiver.Map.IsDeprecated) return false;
        JSObject.MigrateInstance(isolate, receiver);
        return true;
    }

    /// <summary>Map::BelongsToSameNativeContextAs(isolate->context()).</summary>
    static bool BelongsToCurrentNativeContext(Isolate isolate, Map map) =>
        ReferenceEquals(map.NativeContext, isolate.NativeContext);

    /// <summary>GetCloneModeForMapPreCheck.</summary>
    static FastCloneObjectMode GetCloneModeForMapPreCheck(Isolate isolate, Map map, bool nullProtoLiteral)
    {
        if (!Map.IsJSObjectMap(map))
        {
            // Everything that produces the empty object literal can be supported since
            // we have a special case for that.
            if (nullProtoLiteral) return FastCloneObjectMode.kNotSupported;
            // (null and undefined have no map in V8Sharp and do not get here.)
            return map.InstanceType is InstanceType.OddballType or InstanceType.HeapNumberType
                ? FastCloneObjectMode.kEmptyObject
                : FastCloneObjectMode.kNotSupported;
        }
        ElementsKind elementsKind = map.ElementsKind;
        if (!ElementsKinds.IsSmiOrObjectElementsKind(elementsKind) && !ElementsKinds.IsAnyNonextensibleElementsKind(elementsKind))
        {
            return FastCloneObjectMode.kNotSupported;
        }
        if (!map.OnlyHasSimpleProperties()) return FastCloneObjectMode.kNotSupported;

        // TODO(olivf): Think about cases where cross-context copies are safe.
        if (!BelongsToCurrentNativeContext(isolate, map)) return FastCloneObjectMode.kNotSupported;

        return FastCloneObjectMode.kMaybeSupported;
    }

    /// <summary>GetCloneModeForMap.</summary>
    static FastCloneObjectMode GetCloneModeForMap(Isolate isolate, Map map, bool nullProtoLiteral)
    {
        FastCloneObjectMode preCheck = GetCloneModeForMapPreCheck(isolate, map, nullProtoLiteral);
        if (preCheck != FastCloneObjectMode.kMaybeSupported) return preCheck;

        // The clone must always start from an object literal map, it must be an
        // instance of the object function, have the default prototype and not be a
        // prototype itself. Only if the source map fits that criterion we can
        // directly use it as the target map.
        NativeContext nativeContext = isolate.NativeContext;
        FastCloneObjectMode mode =
            map.InstanceType == InstanceType.JSObjectType && !ElementsKinds.IsAnyNonextensibleElementsKind(map.ElementsKind) &&
            ReferenceEquals(map.GetConstructor(), nativeContext.ObjectFunction) &&
            ReferenceEquals(map.Prototype, nativeContext.ObjectFunctionPrototype) && !map.IsPrototypeMap
                ? FastCloneObjectMode.kIdenticalMap
                : FastCloneObjectMode.kDifferentMap;

        if (nullProtoLiteral || map.Prototype is null) mode = FastCloneObjectMode.kDifferentMap;

        DescriptorArray descriptors = map.InstanceDescriptors;
        int n = map.NumberOfOwnDescriptors;
        for (int i = 0; i < n; i++)
        {
            PropertyDetails details = descriptors.GetDetails(i);
            Name key = descriptors.GetKey(i);
            if (details.Kind != PropertyKind.Data || details.IsDontEnum || key.IsAnyPrivateName)
            {
                return FastCloneObjectMode.kNotSupported;
            }
            if (!details.IsConfigurable || details.IsReadOnly) mode = FastCloneObjectMode.kDifferentMap;
        }

        Debug.Assert(mode != FastCloneObjectMode.kIdenticalMap || !map.IsPrototypeMap);
        return mode;
    }

    /// <summary>CanCacheCloneTargetMapTransition.</summary>
    static bool CanCacheCloneTargetMapTransition(Isolate isolate, Map sourceMap, Map? targetMap, bool nullProtoLiteral)
    {
        if (!isolate.Flags.clone_object_sidestep_transitions || nullProtoLiteral) return false;
        if (sourceMap.IsDeprecated || sourceMap.IsPrototypeMap) return false;
        if (targetMap is null) return true;
        return !targetMap.IsDeprecated;
    }

    /// <summary>
    /// CanFastCloneObjectToObjectLiteral: whether an object with
    /// <paramref name="sourceMap"/> can be cloned by FastCloneJSObject into a
    /// fresh object of <paramref name="targetMap"/>.
    /// </summary>
    static bool CanFastCloneObjectToObjectLiteral(Isolate isolate, Map sourceMap, Map targetMap, bool nullProtoLiteral)
    {
        Debug.Assert(!targetMap.IsDeprecated);
        Debug.Assert(sourceMap.OnlyHasSimpleProperties());

        // Ensure source and target have identical binary representation of properties
        // and elements as the IC relies on copying the raw bytes. This also excludes
        // cases with non-enumerable properties or accessors on the source object.
        if (sourceMap.InstanceType != InstanceType.JSObjectType || targetMap.InstanceType != InstanceType.JSObjectType ||
            !targetMap.OnlyHasSimpleProperties() || !targetMap.HasFastElements)
        {
            return false;
        }
        // There are no transitions between prototype maps.
        if (sourceMap.IsPrototypeMap || targetMap.IsPrototypeMap) return false;
        // Exclude edge-cases like not copying a __proto__ property.
        if (sourceMap.NumberOfOwnDescriptors != targetMap.NumberOfOwnDescriptors) return false;
        // Check that the source inobject properties fit into the target.
        int sourceUsedInobjProperties = sourceMap.GetInObjectProperties() - sourceMap.UnusedInObjectProperties();
        int targetUsedInobjProperties = targetMap.GetInObjectProperties() - targetMap.UnusedInObjectProperties();
        if (sourceUsedInobjProperties != targetUsedInobjProperties) return false;
        // The properties backing store must be of the same size as the clone ic again
        // blindly copies it.
        if (sourceMap.HasOutOfObjectProperties() != targetMap.HasOutOfObjectProperties() ||
            (targetMap.HasOutOfObjectProperties() && sourceMap.UnusedPropertyFields() != targetMap.UnusedPropertyFields()))
        {
            return false;
        }
        DescriptorArray descriptors = sourceMap.InstanceDescriptors;
        DescriptorArray targetDescriptors = targetMap.InstanceDescriptors;
        int n = targetMap.NumberOfOwnDescriptors;
        for (int i = 0; i < n; i++)
        {
            if (!ReferenceEquals(descriptors.GetKey(i), targetDescriptors.GetKey(i))) return false;
            PropertyDetails details = descriptors.GetDetails(i);
            PropertyDetails targetDetails = targetDescriptors.GetDetails(i);
            HeapObject type = descriptors.GetFieldType(new InternalIndex(i));
            HeapObject targetType = targetDescriptors.GetFieldType(new InternalIndex(i));
            // Field updates don't generalize across prototype transitions, because the
            // transitions happen on root maps (i.e., before any field is added). In
            // other words we cannot rely on changes in the source map propagating to
            // the target map when there is a SetPrototype involved.
            bool prototypeTransitionIsShortcutted = !ReferenceEquals(sourceMap.Prototype, targetMap.Prototype);
            if (!prototypeTransitionIsShortcutted &&
                CanCacheCloneTargetMapTransition(isolate, sourceMap, targetMap, nullProtoLiteral))
            {
                if (!details.Representation.FitsInto(targetDetails.Representation) ||
                    (targetDetails.Representation.IsDouble && details.Representation.IsSmi))
                {
                    return false;
                }
                if (!FieldType.NowIs(type, targetType)) return false;
            }
            else
            {
                // In the case we cannot connect the maps in the transition tree (e.g.,
                // the clone also involves a proto transition) we cannot keep track of
                // representation dependencies. We can only allow the most generic target
                // representation. The same goes for field types.
                if (!details.Representation.MostGenericInPlaceChange().Equals(targetDetails.Representation) ||
                    !FieldType.IsAny(targetType))
                {
                    return false;
                }
            }
        }
        return true;
    }

    /// <summary>GetCloneTargetMap&lt;kCloneObject&gt;: the cached side-step transition, Empty or Unreachable.</summary>
    static HeapObject GetCloneTargetMap(Isolate isolate, Map sourceMap)
    {
        if (!isolate.Flags.clone_object_sidestep_transitions) return SideStepTransition.Empty;

        // Ensure we can follow the sidestep transition NativeContext-wise.
        if (!BelongsToCurrentNativeContext(isolate, sourceMap)) return SideStepTransition.Empty;
        var transitions = new TransitionsAccessor(isolate, sourceMap);
        if (!transitions.HasSideStepTransitions()) return SideStepTransition.Empty;
        HeapObject result = transitions.GetSideStepTransition(SideStepTransition.Kind.CloneObject);
        // Exclude deprecated maps.
        if (result is Map map && map.IsDeprecated) return SideStepTransition.Empty;
        return result;
    }

    /// <summary>SetCloneTargetMap&lt;kCloneObject&gt;.</summary>
    static void SetCloneTargetMap(Isolate isolate, Map sourceMap, Map newTargetMap)
    {
        if (!isolate.Flags.clone_object_sidestep_transitions) return;
        Debug.Assert(!newTargetMap.IsDeprecated);
        // Adding this transition also ensures that when the source map field
        // generalizes, we also generalize the target map.
        TransitionsAccessor.EnsureHasSideStepTransitions(isolate, sourceMap);
        new TransitionsAccessor(isolate, sourceMap).SetSideStepTransition(SideStepTransition.Kind.CloneObject, newTargetMap);
    }

    /// <summary>SetCloneTargetMapUnsupported&lt;kCloneObject&gt;.</summary>
    static void SetCloneTargetMapUnsupported(Isolate isolate, Map sourceMap)
    {
        if (!isolate.Flags.clone_object_sidestep_transitions) return;
        TransitionsAccessor.EnsureHasSideStepTransitions(isolate, sourceMap);
        new TransitionsAccessor(isolate, sourceMap).SetSideStepTransition(SideStepTransition.Kind.CloneObject,
            SideStepTransition.Unreachable);
    }

    /// <summary>CloneObjectSlowPath (ic.cc).</summary>
    public static JSObject CloneObjectSlowPath(Isolate isolate, JSValue source, int flags)
    {
        JSObject newObject;
        if ((flags & kHasNullPrototype) != 0)
        {
            newObject = isolate.Factory.NewJSObjectWithNullProto();
        }
        else if (source.HeapObjectOrNull is JSObject sourceObject && sourceObject.Map.OnlyHasSimpleProperties())
        {
            Map sourceMap = sourceObject.Map;
            // TODO(olivf, chrome:1204540) It might be interesting to pick a map with
            // more properties, depending how many properties are added by the
            // surrounding literal.
            int properties = sourceMap.GetInObjectProperties() - sourceMap.UnusedInObjectProperties();
            Map map = isolate.Factory.ObjectLiteralMapFromCache(isolate.NativeContext, properties);
            newObject = map.IsDictionaryMap
                ? isolate.Factory.NewSlowJSObjectFromMap(map)
                : isolate.Factory.NewJSObjectFromMap(map);
        }
        else
        {
            newObject = isolate.Factory.NewJSObject(isolate.NativeContext.ObjectFunction);
        }

        if (source.IsNullOrUndefined) return newObject;

        JSReceiver.SetOrCopyDataProperties(isolate, newObject, source, JSReceiver.PropertiesEnumerationMode.PropertyAdditionOrder,
            default, false);
        return newObject;
    }
}
