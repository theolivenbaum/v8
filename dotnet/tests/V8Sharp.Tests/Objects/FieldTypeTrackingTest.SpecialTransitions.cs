// Port of test/cctest/test-field-type-tracking.cc, second half: elements kind
// reconfiguration, split map overflow, special (elements kind and prototype)
// transitions, the TransitionTo* tests and the representation predicates.
namespace V8Sharp.Tests.Objects;

public partial class FieldTypeTrackingTest
{
    // This test ensures that in-place field generalization is correctly propagated
    // from one branch of transition tree (|map2|) to another (|map|).
    //
    //   + - p0 - p1 - p2A - p3 - p4: |map|
    //   |
    //  ek
    //   |
    //  {} - p0 - p1 - p2B - p3 - p4: |map2|
    //
    // where "p2A" and "p2B" differ only in the representation/field type.
    void TestReconfigureElementsKind_GeneralizeFieldInPlace(CRFTData from, CRFTData to, CRFTData expected)
    {
        Isolate isolate = i_isolate;

        var expectations = new Expectations(isolate, ElementsKind.PACKED_SMI_ELEMENTS);

        // Create a map, add required properties to it and initialize expectations.
        Map initialMap = factory.NewContextfulMapForCurrentContext(InstanceType.JSArrayType,
            JSObject.GetHeaderSize(InstanceType.JSArrayType), ElementsKind.PACKED_SMI_ELEMENTS);
        initialMap.SetConstructor(isolate.NativeContext.ObjectFunction);

        Map map = initialMap;
        map = expectations.AsElementsKind(map, ElementsKind.PACKED_ELEMENTS);
        for (int i = 0; i < kPropCount; i++)
        {
            map = expectations.AddDataField(map, PropertyAttributes.NONE, from.constness, from.representation,
                from.type);
        }
        Assert.False(map.IsDeprecated);
        Assert.True(map.IsStable);
        Assert.True(expectations.Check(map));

        // Create another branch in transition tree (property at index |kDiffProp|
        // has different attributes), initialize expectations.
        const int kDiffProp = kPropCount / 2;
        var expectations2 = new Expectations(isolate, ElementsKind.PACKED_SMI_ELEMENTS);

        Map map2 = initialMap;
        for (int i = 0; i < kPropCount; i++)
        {
            map2 = i == kDiffProp
                ? expectations2.AddDataField(map2, PropertyAttributes.NONE, to.constness, to.representation, to.type)
                : expectations2.AddDataField(map2, PropertyAttributes.NONE, from.constness, from.representation,
                    from.type);
        }
        Assert.False(map2.IsDeprecated);
        Assert.True(map2.IsStable);
        Assert.True(expectations2.Check(map2));

        // Reconfigure elements kinds of |map2|, which should generalize
        // representations in |map|.
        Map newMap = new MapUpdater(isolate, map2).ReconfigureElementsKind(ElementsKind.PACKED_ELEMENTS);

        // |map2| should be left unchanged but marked unstable.
        Assert.False(map2.IsStable);
        Assert.False(map2.IsDeprecated);
        Assert.NotSame(map2, newMap);
        Assert.True(expectations2.Check(map2));

        // In case of in-place generalization |map| should be returned as a result of
        // the elements kind reconfiguration, respective field types should be
        // generalized. |map| should be NOT deprecated and it should match new
        // expectations.
        expectations.SetDataField(kDiffProp, expected.constness, expected.representation, expected.type);
        Assert.False(map.IsDeprecated);
        Assert.Same(map, newMap);

        Assert.False(newMap.IsDeprecated);
        Assert.True(expectations.Check(newMap));

        Map updatedMap = Map.Update(isolate, map);
        Assert.Same(newMap, updatedMap);

        // Ensure Map::FindElementsKindTransitionedMap() is able to find the
        // transitioned map.
        Map transitionedMap = map2.FindElementsKindTransitionedMap(isolate, [updatedMap])!;
        Assert.Same(updatedMap, transitionedMap);
    }

    static CRFTData D(PropertyConstness c, Representation r, HeapObject t) => new(c, r, t);

    const PropertyConstness kConst = PropertyConstness.Const;
    const PropertyConstness kMutable = PropertyConstness.Mutable;

    [Fact]
    public void ReconfigureElementsKind_GeneralizeSmiFieldToDouble()
    {
        TestReconfigureElementsKind_GeneralizeFieldInPlace(D(kConst, Representation.Smi, any_type),
            D(kConst, Representation.Double, any_type), D(kConst, Representation.Double, any_type));
        TestReconfigureElementsKind_GeneralizeFieldInPlace(D(kConst, Representation.Smi, any_type),
            D(kMutable, Representation.Double, any_type), D(kMutable, Representation.Double, any_type));
        TestReconfigureElementsKind_GeneralizeFieldInPlace(D(kMutable, Representation.Smi, any_type),
            D(kConst, Representation.Double, any_type), D(kMutable, Representation.Double, any_type));
        TestReconfigureElementsKind_GeneralizeFieldInPlace(D(kMutable, Representation.Smi, any_type),
            D(kMutable, Representation.Double, any_type), D(kMutable, Representation.Double, any_type));
    }

    [Fact]
    public void ReconfigureElementsKind_GeneralizeSmiFieldToTagged()
    {
        HeapObject valueType = FieldType.Class(Map.Create(i_isolate, 0));

        TestReconfigureElementsKind_GeneralizeFieldInPlace(D(kConst, Representation.Smi, any_type),
            D(kConst, Representation.HeapObject, valueType), D(kConst, Representation.Tagged, any_type));
        TestReconfigureElementsKind_GeneralizeFieldInPlace(D(kConst, Representation.Smi, any_type),
            D(kMutable, Representation.HeapObject, valueType), D(kMutable, Representation.Tagged, any_type));
        TestReconfigureElementsKind_GeneralizeFieldInPlace(D(kMutable, Representation.Smi, any_type),
            D(kConst, Representation.HeapObject, valueType), D(kMutable, Representation.Tagged, any_type));
        TestReconfigureElementsKind_GeneralizeFieldInPlace(D(kMutable, Representation.Smi, any_type),
            D(kMutable, Representation.HeapObject, valueType), D(kMutable, Representation.Tagged, any_type));
    }

    [Fact]
    public void ReconfigureElementsKind_GeneralizeDoubleFieldToTagged()
    {
        HeapObject valueType = FieldType.Class(Map.Create(i_isolate, 0));

        TestReconfigureElementsKind_GeneralizeFieldInPlace(D(kConst, Representation.Double, any_type),
            D(kConst, Representation.HeapObject, valueType), D(kConst, Representation.Tagged, any_type));
        TestReconfigureElementsKind_GeneralizeFieldInPlace(D(kConst, Representation.Double, any_type),
            D(kMutable, Representation.HeapObject, valueType), D(kMutable, Representation.Tagged, any_type));
        TestReconfigureElementsKind_GeneralizeFieldInPlace(D(kMutable, Representation.Double, any_type),
            D(kConst, Representation.HeapObject, valueType), D(kMutable, Representation.Tagged, any_type));
        TestReconfigureElementsKind_GeneralizeFieldInPlace(D(kMutable, Representation.Double, any_type),
            D(kMutable, Representation.HeapObject, valueType), D(kMutable, Representation.Tagged, any_type));
    }

    [Fact]
    public void ReconfigureElementsKind_GeneralizeHeapObjFieldToHeapObj()
    {
        HeapObject currentType = FieldType.Class(Map.Create(i_isolate, 0));
        HeapObject newType = FieldType.Class(Map.Create(i_isolate, 0));
        HeapObject expectedType = any_type;

        // Check generalizations that trigger deopts.
        TestReconfigureElementsKind_GeneralizeFieldInPlace(D(kConst, Representation.HeapObject, currentType),
            D(kConst, Representation.HeapObject, newType), D(kConst, Representation.HeapObject, expectedType));
        // PropertyConstness::kConst to PropertyConstness::kMutable migration does
        // not create a new map, therefore trivial generalization.
        TestReconfigureElementsKind_GeneralizeFieldInPlace(D(kConst, Representation.HeapObject, currentType),
            D(kMutable, Representation.HeapObject, newType), D(kMutable, Representation.HeapObject, expectedType));
        TestReconfigureElementsKind_GeneralizeFieldInPlace(D(kMutable, Representation.HeapObject, currentType),
            D(kConst, Representation.HeapObject, newType), D(kMutable, Representation.HeapObject, expectedType));
        TestReconfigureElementsKind_GeneralizeFieldInPlace(D(kMutable, Representation.HeapObject, currentType),
            D(kMutable, Representation.HeapObject, newType), D(kMutable, Representation.HeapObject, expectedType));

        // Check generalizations that do not trigger deopts.
        newType = FieldType.Class(Map.Create(i_isolate, 0));

        TestReconfigureElementsKind_GeneralizeFieldInPlace(D(kConst, Representation.HeapObject, any_type),
            D(kConst, Representation.HeapObject, newType), D(kConst, Representation.HeapObject, any_type));
        TestReconfigureElementsKind_GeneralizeFieldInPlace(D(kConst, Representation.HeapObject, any_type),
            D(kMutable, Representation.HeapObject, newType), D(kMutable, Representation.HeapObject, any_type));
        TestReconfigureElementsKind_GeneralizeFieldInPlace(D(kMutable, Representation.HeapObject, any_type),
            D(kConst, Representation.HeapObject, newType), D(kMutable, Representation.HeapObject, any_type));
        TestReconfigureElementsKind_GeneralizeFieldInPlace(D(kMutable, Representation.HeapObject, any_type),
            D(kMutable, Representation.HeapObject, newType), D(kMutable, Representation.HeapObject, any_type));
    }

    [Fact]
    public void ReconfigureElementsKind_GeneralizeHeapObjectFieldToTagged()
    {
        HeapObject valueType = FieldType.Class(Map.Create(i_isolate, 0));

        TestReconfigureElementsKind_GeneralizeFieldInPlace(D(kConst, Representation.HeapObject, valueType),
            D(kConst, Representation.Smi, any_type), D(kConst, Representation.Tagged, any_type));
        TestReconfigureElementsKind_GeneralizeFieldInPlace(D(kConst, Representation.HeapObject, valueType),
            D(kMutable, Representation.Smi, any_type), D(kMutable, Representation.Tagged, any_type));
        TestReconfigureElementsKind_GeneralizeFieldInPlace(D(kMutable, Representation.HeapObject, valueType),
            D(kConst, Representation.Smi, any_type), D(kMutable, Representation.Tagged, any_type));
        TestReconfigureElementsKind_GeneralizeFieldInPlace(D(kMutable, Representation.HeapObject, valueType),
            D(kMutable, Representation.Smi, any_type), D(kMutable, Representation.Tagged, any_type));
    }

    ////////////////////////////////////////////////////////////////////////////////
    // A set of tests checking split map deprecation.
    //

    [Fact]
    public void ReconfigurePropertySplitMapTransitionsOverflow()
    {
        Isolate isolate = i_isolate;
        var expectations = new Expectations(isolate);

        // Create a map, add required properties to it and initialize expectations.
        Map initialMap = Map.Create(isolate, 0);
        Map map = initialMap;
        for (int i = 0; i < kPropCount; i++)
        {
            map = expectations.AddDataField(map, PropertyAttributes.NONE, kMutable, Representation.Smi, any_type);
        }
        Assert.False(map.IsDeprecated);
        Assert.True(map.IsStable);

        // Generalize representation of property at index |kSplitProp|.
        const int kSplitProp = kPropCount / 2;
        Map? splitMap = null;
        Map map2 = initialMap;
        for (int i = 0; i < kSplitProp + 1; i++)
        {
            if (i == kSplitProp) splitMap = map2;

            JSString name = MakeName("prop", i);
            Map? target = TransitionsAccessor.SearchTransition(isolate, map2, name, PropertyKind.Data,
                PropertyAttributes.NONE);
            Assert.NotNull(target);
            map2 = target!;
        }

        map2 = ReconfigureProperty(isolate, map2, new InternalIndex(kSplitProp), PropertyKind.Data,
            PropertyAttributes.NONE, Representation.Double, any_type);
        expectations.SetDataField(kSplitProp, kMutable, Representation.Double, any_type);

        Assert.True(expectations.Check(splitMap!, kSplitProp));
        Assert.True(expectations.Check(map2, kSplitProp + 1));

        // At this point |map| should be deprecated and disconnected from the
        // transition tree.
        Assert.True(map.IsDeprecated);
        Assert.False(splitMap!.IsDeprecated);
        Assert.True(map2.IsStable);
        Assert.False(map2.IsDeprecated);

        // Fill in transition tree of |map2| so that it can't have more transitions.
        for (int i = 0; i < TransitionsAccessor.kMaxNumberOfTransitions; i++)
        {
            Assert.True(TransitionsAccessor.CanHaveMoreTransitions(isolate, map2));
            JSString name = MakeName("foo", i);
            Assert.NotNull(Map.CopyWithField(isolate, map2, name, any_type, PropertyAttributes.NONE, kMutable,
                Representation.Smi, TransitionFlag.INSERT_TRANSITION));
        }
        Assert.False(TransitionsAccessor.CanHaveMoreTransitions(isolate, map2));

        // Try to update |map|, since there is no place for propX transition at |map2|
        // |map| should become normalized.
        Map updatedMap = Map.Update(isolate, map);

        CheckNormalize(isolate, map2, updatedMap, expectations);
    }

    ////////////////////////////////////////////////////////////////////////////////
    // A set of tests involving special transitions (such as elements kind
    // transition, observed transition or prototype transition).
    //
    // This test ensures that field generalization is correctly propagated from one
    // branch of transition tree (|map2|) to another (|map|).
    //
    //                            p4B: |map_b|
    //                             ^
    //                             |
    //                             * - special transition
    //                             |
    //  {} - p0 - p1 - p2A - p3 - p4A: |map_a|
    //
    // where "p4A" and "p4B" are exactly the same properties.
    //
    // UpdateDirectionCheck::kFwd checks if updates to map_a propagate to map_b,
    // whereas UpdateDirectionCheck::kBwd checks if updates to map_b propagate back
    // to map_a.
    //
    enum UpdateDirectionCheck { kFwd, kBwd }

    interface ISpecialTransitionConfig
    {
        Map Transition(Map map, Expectations expectations);
    }

    void TestGeneralizeFieldWithSpecialTransition(ISpecialTransitionConfig config, CRFTData from, CRFTData to,
        CRFTData expected, ChangeAlertMechanism expectedAlert,
        UpdateDirectionCheck direction = UpdateDirectionCheck.kFwd)
    {
        Isolate isolate = i_isolate;

        var expectationsA = new Expectations(isolate);

        // Create a map, add required properties to it and initialize expectations.
        Map mapA = Map.Create(isolate, 0);
        for (int i = 0; i < kPropCount; i++)
        {
            mapA = expectationsA.AddDataField(mapA, PropertyAttributes.NONE, from.constness, from.representation,
                from.type);
        }
        Assert.False(mapA.IsDeprecated);
        Assert.True(mapA.IsStable);
        Assert.True(expectationsA.Check(mapA));

        Expectations expectationsB = expectationsA.Clone();

        // Apply some special transition to |map|.
        Assert.True(mapA.OwnsDescriptors);
        Map mapB = config.Transition(mapA, expectationsB);

        // |map| should still match expectations.
        Assert.False(mapA.IsDeprecated);
        Assert.True(expectationsA.Check(mapA));

        Assert.False(mapB.IsDeprecated);
        Assert.True(mapB.IsStable);
        Assert.True(expectationsB.Check(mapB));

        // Create new maps by generalizing representation of propX field.
        var updatedMaps = new Map[kPropCount];
        for (int i = 0; i < kPropCount; i++)
        {
            Map newMapA = mapA;
            Map newMapB = mapB;
            Map mapToChange = direction == UpdateDirectionCheck.kFwd ? mapA : mapB;
            Map changedMap = ReconfigureProperty(isolate, mapToChange, new InternalIndex(i), PropertyKind.Data,
                PropertyAttributes.NONE, to.representation, to.type);
            updatedMaps[i] = changedMap;

            expectationsA.SetDataField(i, expected.constness, expected.representation, expected.type);
            expectationsB.SetDataField(i, expected.constness, expected.representation, expected.type);

            if (direction == UpdateDirectionCheck.kFwd)
            {
                newMapA = changedMap;
                Assert.True(expectationsA.Check(newMapA));
            }
            else
            {
                newMapB = changedMap;
                Assert.True(expectationsB.Check(newMapB));
            }

            // Prototype transitions are always moved to the front. Thus both
            // branches are independent since we have two independent property
            // owners in each branch. However on UpdatePrototype we do propagate
            // field types between the branches. Thus we need to call the MapUpdater
            // once more for the changes to propagate.
            if (!ReferenceEquals(newMapA.Prototype, newMapB.Prototype))
            {
                Expectations tmp = expectationsA.Clone();
                config.Transition(newMapA, tmp);
                // TODO(olivf) Prototype transitions do not propagate any changes back to
                // their "true" root map.
                Assert.Equal(UpdateDirectionCheck.kFwd, direction);
            }

            switch (expectedAlert)
            {
                case ChangeAlertMechanism.kDeprecation:
                {
                    Assert.True(mapToChange.IsDeprecated);

                    Assert.NotSame(mapToChange, changedMap);
                    Assert.True(i == 0 || updatedMaps[i - 1].IsDeprecated);

                    Map changedMap2 = Map.Update(isolate, mapToChange);
                    Assert.Same(changedMap, changedMap2);

                    newMapA = Map.Update(isolate, newMapA);
                    newMapB = Map.Update(isolate, newMapB);

                    Assert.False(newMapA.IsDeprecated);
                    Assert.False(newMapA.IsDictionaryMap);
                    Assert.False(newMapB.IsDeprecated);
                    Assert.False(newMapB.IsDictionaryMap);

                    // If Map::TryUpdate() manages to succeed the result must match the
                    // result of Map::Update().
                    Assert.Same(newMapA, Map.TryUpdate(isolate, mapA));
                    Assert.Same(newMapB, Map.TryUpdate(isolate, mapB));

                    Assert.True(expectationsA.Check(newMapA));
                    Assert.True(expectationsB.Check(newMapB));
                    Assert.NotNull(newMapB.GetBackPointer());
                    break;
                }
                case ChangeAlertMechanism.kFieldOwnerDependency:
                    Assert.False(mapA.IsDeprecated);
                    Assert.Same(mapA, newMapA);
                    Assert.NotSame(mapA, newMapB);

                    Assert.False(mapB.IsDeprecated);
                    Assert.Same(mapB, newMapB);
                    Assert.NotSame(mapB, newMapA);

                    Assert.True(expectationsB.Check(newMapB));
                    Assert.True(expectationsA.Check(newMapA));
                    break;
                default:
                    throw new InvalidOperationException("unreachable");
            }
        }

        Map activeMap = updatedMaps[kPropCount - 1];
        Map oldMap = direction == UpdateDirectionCheck.kFwd ? mapA : mapB;
        Assert.False(activeMap.IsDeprecated);
        // Update all deprecated maps and check that they are now the same.
        Map updatedMap = Map.Update(isolate, oldMap);
        Assert.Same(activeMap, updatedMap);
        CheckMigrationTarget(isolate, mapA, updatedMap);
        for (int i = 0; i < kPropCount; i++)
        {
            updatedMap = Map.Update(isolate, updatedMaps[i]);
            Assert.Same(activeMap, updatedMap);
            CheckMigrationTarget(isolate, updatedMaps[i], updatedMap);
        }
    }

    void TestMultipleElementsKindTransitions(ISpecialTransitionConfig config, UpdateDirectionCheck direction)
    {
        HeapObject valueType = FieldType.Class(Map.Create(i_isolate, 0));

        TestGeneralizeFieldWithSpecialTransition(config, D(kMutable, Representation.Smi, any_type),
            D(kMutable, Representation.HeapObject, valueType), D(kMutable, Representation.Tagged, any_type),
            ChangeAlertMechanism.kFieldOwnerDependency, direction);

        TestGeneralizeFieldWithSpecialTransition(config, D(kMutable, Representation.Double, any_type),
            D(kMutable, Representation.HeapObject, valueType), D(kMutable, Representation.Tagged, any_type),
            ChangeAlertMechanism.kFieldOwnerDependency, direction);

        TestGeneralizeFieldWithSpecialTransition(config, D(kMutable, Representation.Smi, any_type),
            D(kMutable, Representation.Double, valueType), D(kMutable, Representation.Double, any_type),
            ChangeAlertMechanism.kDeprecation, direction);
    }

    sealed class PreventExtensionsConfig(Isolate isolate, PropertyAttributes attributes, Symbol symbol,
        ElementsKind elementsKind, bool ownsDescriptors) : ISpecialTransitionConfig
    {
        public Map Transition(Map map, Expectations expectations)
        {
            if (!ownsDescriptors)
            {
                // Add one more transition to |map| in order to prevent descriptors
                // ownership.
                Assert.True(map.OwnsDescriptors);
                Map.CopyWithField(isolate, map, isolate.Factory.InternalizeString("foo"), FieldType.Any,
                    PropertyAttributes.NONE, PropertyConstness.Mutable, Representation.Smi,
                    TransitionFlag.INSERT_TRANSITION);
                Assert.False(map.OwnsDescriptors);
            }

            expectations.SetElementsKind(elementsKind);
            expectations.ChangeAttributesForAllProperties(attributes);
            return Map.CopyForPreventExtensions(isolate, map, attributes, symbol, "CopyForPreventExtensions");
        }
    }

    void TestElementsKindTransitionFromMap(bool ownsDescriptors)
    {
        PreventExtensionsConfig[] configs =
        [
            new(i_isolate, PropertyAttributes.FROZEN, ReadOnlyRoots.frozen_symbol,
                ElementsKind.HOLEY_FROZEN_ELEMENTS, ownsDescriptors),
            new(i_isolate, PropertyAttributes.SEALED, ReadOnlyRoots.sealed_symbol,
                ElementsKind.HOLEY_SEALED_ELEMENTS, ownsDescriptors),
            new(i_isolate, PropertyAttributes.NONE, ReadOnlyRoots.nonextensible_symbol,
                ElementsKind.HOLEY_NONEXTENSIBLE_ELEMENTS, ownsDescriptors),
        ];

        foreach (UpdateDirectionCheck direction in (UpdateDirectionCheck[])[UpdateDirectionCheck.kFwd,
                     UpdateDirectionCheck.kBwd])
        {
            foreach (PreventExtensionsConfig config in configs)
            {
                TestMultipleElementsKindTransitions(config, direction);
            }
        }
    }

    [Fact]
    public void ElementsKindTransitionFromMapOwningDescriptor() => TestElementsKindTransitionFromMap(true);

    [Fact]
    public void ElementsKindTransitionFromMapNotOwningDescriptor() => TestElementsKindTransitionFromMap(false);

    ////////////////////////////////////////////////////////////////////////////////
    // A set of tests for the prototype transition case.
    //
    // This test ensures that field generalization is correctly propagated across an
    // UpdatePrototype transition.
    //
    // In the case of prototype transitions the transition tree is actually
    // reshaped as:
    //
    //  {} - p0B - p1B - p2B - p3B - p4B: |map_b|
    //  ^
    //  |
    //  * - prototype transition
    //  |
    //  {} - p0A - p1A - p2A - p3A - p4A: |map_a|
    //
    //  And the updates go via the MapUpdater. Thus generalizations from map_a to
    //  map_b happen during UpdatePrototype, (i.e., on the transition of the next
    //  object).
    //
    // By design updates currently only happen in forward direction, i.e., changes
    // to map_a are propagated to map_b, but not the inverse.

    void TestMultiplePrototypeTransitions(ISpecialTransitionConfig config)
    {
        HeapObject valueType = FieldType.Class(Map.Create(i_isolate, 0));
        const ChangeAlertMechanism kFieldOwnerDependency = ChangeAlertMechanism.kFieldOwnerDependency;

        // Smi + HeapObject -> Tagged

        TestGeneralizeFieldWithSpecialTransition(config, D(kConst, Representation.Smi, any_type),
            D(kConst, Representation.HeapObject, valueType), D(kConst, Representation.Tagged, any_type),
            kFieldOwnerDependency);
        TestGeneralizeFieldWithSpecialTransition(config, D(kMutable, Representation.Smi, any_type),
            D(kConst, Representation.HeapObject, valueType), D(kMutable, Representation.Tagged, any_type),
            kFieldOwnerDependency);
        TestGeneralizeFieldWithSpecialTransition(config, D(kMutable, Representation.Smi, any_type),
            D(kMutable, Representation.HeapObject, valueType), D(kMutable, Representation.Tagged, any_type),
            kFieldOwnerDependency);

        TestGeneralizeFieldWithSpecialTransition(config, D(kConst, Representation.Smi, any_type),
            D(kConst, Representation.HeapObject, any_type), D(kConst, Representation.Tagged, any_type),
            kFieldOwnerDependency);

        // HeapObject + HeapObject -> Tagged

        TestGeneralizeFieldWithSpecialTransition(config, D(kConst, Representation.HeapObject, valueType),
            D(kConst, Representation.HeapObject, valueType), D(kConst, Representation.HeapObject, valueType),
            kFieldOwnerDependency);
        TestGeneralizeFieldWithSpecialTransition(config, D(kMutable, Representation.HeapObject, valueType),
            D(kConst, Representation.HeapObject, valueType), D(kMutable, Representation.HeapObject, valueType),
            kFieldOwnerDependency);
        TestGeneralizeFieldWithSpecialTransition(config, D(kMutable, Representation.HeapObject, valueType),
            D(kMutable, Representation.HeapObject, valueType), D(kMutable, Representation.HeapObject, valueType),
            kFieldOwnerDependency);

        TestGeneralizeFieldWithSpecialTransition(config, D(kConst, Representation.HeapObject, any_type),
            D(kConst, Representation.HeapObject, valueType), D(kConst, Representation.HeapObject, any_type),
            kFieldOwnerDependency);
        TestGeneralizeFieldWithSpecialTransition(config, D(kMutable, Representation.HeapObject, any_type),
            D(kConst, Representation.HeapObject, valueType), D(kMutable, Representation.HeapObject, any_type),
            kFieldOwnerDependency);
        TestGeneralizeFieldWithSpecialTransition(config, D(kMutable, Representation.HeapObject, any_type),
            D(kMutable, Representation.HeapObject, valueType), D(kMutable, Representation.HeapObject, any_type),
            kFieldOwnerDependency);

        // Double + HeapObject -> Tagged

        TestGeneralizeFieldWithSpecialTransition(config, D(kConst, Representation.Double, any_type),
            D(kConst, Representation.HeapObject, any_type), D(kConst, Representation.Tagged, any_type),
            kFieldOwnerDependency);

        TestGeneralizeFieldWithSpecialTransition(config, D(kConst, Representation.Double, any_type),
            D(kConst, Representation.HeapObject, valueType), D(kConst, Representation.Tagged, any_type),
            kFieldOwnerDependency);
        TestGeneralizeFieldWithSpecialTransition(config, D(kMutable, Representation.Double, any_type),
            D(kConst, Representation.HeapObject, valueType), D(kMutable, Representation.Tagged, any_type),
            kFieldOwnerDependency);
        TestGeneralizeFieldWithSpecialTransition(config, D(kMutable, Representation.Double, any_type),
            D(kMutable, Representation.HeapObject, valueType), D(kMutable, Representation.Tagged, any_type),
            kFieldOwnerDependency);

        // Smi + Double -> Double

        TestGeneralizeFieldWithSpecialTransition(config, D(kConst, Representation.Smi, any_type),
            D(kConst, Representation.Double, any_type), D(kConst, Representation.Double, any_type),
            ChangeAlertMechanism.kDeprecation);
        TestGeneralizeFieldWithSpecialTransition(config, D(kMutable, Representation.Smi, any_type),
            D(kConst, Representation.Double, any_type), D(kMutable, Representation.Double, any_type),
            ChangeAlertMechanism.kDeprecation);
        TestGeneralizeFieldWithSpecialTransition(config, D(kMutable, Representation.Smi, any_type),
            D(kMutable, Representation.Double, any_type), D(kMutable, Representation.Double, any_type),
            ChangeAlertMechanism.kDeprecation);
    }

    sealed class PrototypeTransitionConfig(Isolate isolate, bool ownsDescriptors) : ISpecialTransitionConfig
    {
        readonly JSObject _prototype = isolate.Factory.NewJSObjectFromMap(Map.Create(isolate, 0));

        public Map Transition(Map map, Expectations expectations)
        {
            if (!ownsDescriptors)
            {
                // Add one more transition to |map| in order to prevent descriptors
                // ownership.
                if (map.OwnsDescriptors)
                {
                    Map.CopyWithField(isolate, map, isolate.Factory.InternalizeString("foo"), FieldType.Any,
                        PropertyAttributes.NONE, PropertyConstness.Mutable, Representation.Smi,
                        TransitionFlag.INSERT_TRANSITION);
                }
                Assert.False(map.OwnsDescriptors);
            }

            return new MapUpdater(isolate, map).ApplyPrototypeTransition(_prototype);
        }
    }

    [Fact]
    public void PrototypeTransitionFromMapOwningDescriptor() =>
        TestMultiplePrototypeTransitions(new PrototypeTransitionConfig(i_isolate, true));

    [Fact]
    public void PrototypeTransitionFromMapNotOwningDescriptor() =>
        TestMultiplePrototypeTransitions(new PrototypeTransitionConfig(i_isolate, false));

    ////////////////////////////////////////////////////////////////////////////////
    // A set of tests for higher level transitioning mechanics.
    //

    interface ITransitionOperator
    {
        Map DoTransition(Expectations expectations, Map map);
    }

    sealed class TransitionToDataFieldOperator(PropertyConstness constness, Representation representation,
        HeapObject heapType, JSValue value, PropertyAttributes attributes = PropertyAttributes.NONE)
        : ITransitionOperator
    {
        public Map DoTransition(Expectations expectations, Map map) =>
            expectations.TransitionToDataField(map, attributes, constness, representation, heapType, value);
    }

    sealed class TransitionToDataConstantOperator(JSFunction value,
        PropertyAttributes attributes = PropertyAttributes.NONE) : ITransitionOperator
    {
        public Map DoTransition(Expectations expectations, Map map) =>
            expectations.TransitionToDataConstant(map, attributes, value);
    }

    sealed class TransitionToAccessorConstantOperator(AccessorPair pair,
        PropertyAttributes attributes = PropertyAttributes.NONE) : ITransitionOperator
    {
        public Map DoTransition(Expectations expectations, Map map) =>
            expectations.TransitionToAccessorConstant(map, attributes, pair);
    }

    interface ITransitionChecker
    {
        void Check(Isolate isolate, Expectations expectations, Map map1, Map map2);
    }

    // Checks that field generalization happened.
    sealed class FieldGeneralizationChecker(int descriptor, PropertyConstness constness,
        Representation representation, HeapObject heapType, PropertyAttributes attributes = PropertyAttributes.NONE)
        : ITransitionChecker
    {
        public void Check(Isolate isolate, Expectations expectations, Map map1, Map map2)
        {
            Assert.False(map2.IsDeprecated);

            Assert.True(map1.IsDeprecated);
            Assert.NotSame(map1, map2);
            Map updatedMap = Map.Update(isolate, map1);
            Assert.Same(map2, updatedMap);
            CheckMigrationTarget(isolate, map1, updatedMap);

            expectations.SetDataField(descriptor, attributes, constness, representation, heapType);
            Assert.True(expectations.Check(map2));
        }
    }

    // Checks that existing transition was taken as is.
    sealed class SameMapChecker : ITransitionChecker
    {
        public void Check(Isolate isolate, Expectations expectations, Map map1, Map map2)
        {
            Assert.False(map2.IsDeprecated);
            Assert.Same(map1, map2);
            Assert.True(expectations.Check(map2));
        }
    }

    // This test transitions to various property types under different
    // circumstances.
    // Plan:
    // 1) create a |map| with p0..p3 properties.
    // 2) create |map1| by adding "p4" to |map0|.
    // 3) create |map2| by transition to "p4" from |map0|.
    //
    //                       + - p4B: |map2|
    //                       |
    //  {} - p0 - p1 - pA - p3: |map|
    //                       |
    //                       + - p4A: |map1|
    //
    // where "p4A" and "p4B" differ only in the attributes.
    //
    void TestTransitionTo(ITransitionOperator transitionOp1, ITransitionOperator transitionOp2,
        ITransitionChecker checker)
    {
        Isolate isolate = i_isolate;
        var expectations = new Expectations(isolate);

        // Create a map, add required properties to it and initialize expectations.
        Map initialMap = Map.Create(isolate, 0);
        Map map = initialMap;
        for (int i = 0; i < kPropCount - 1; i++)
        {
            map = expectations.AddDataField(map, PropertyAttributes.NONE, kMutable, Representation.Smi, any_type);
        }
        Assert.True(expectations.Check(map));

        Expectations expectations1 = expectations.Clone();
        Map map1 = transitionOp1.DoTransition(expectations1, map);
        Assert.True(expectations1.Check(map1));

        Expectations expectations2 = expectations.Clone();
        Map map2 = transitionOp2.DoTransition(expectations2, map);

        // Let the test customization do the check.
        checker.Check(isolate, expectations2, map1, map2);
    }

    // V8 uses factory->NewHeapNumber(0) to get a Double-represented value. With
    // unboxed JSValue numbers 0 is always a Smi, so the port uses 0.5, which is the
    // closest equivalent: a number that only fits the Double representation.
    static readonly JSValue kHeapNumberValue = JSValue.FromNumber(0.5);

    [Fact]
    public void TransitionDataFieldToDataField()
    {
        var transitionOp1 = new TransitionToDataFieldOperator(kMutable, Representation.Smi, any_type,
            JSValue.FromInt(0));
        var transitionOp2 = new TransitionToDataFieldOperator(kMutable, Representation.Double, any_type,
            kHeapNumberValue);

        var checker = new FieldGeneralizationChecker(kPropCount - 1, kMutable, Representation.Double, any_type);
        TestTransitionTo(transitionOp1, transitionOp2, checker);
    }

    [Fact]
    public void TransitionDataConstantToSameDataConstant()
    {
        JSFunction jsFunc = factory.NewFunctionForTesting(ReadOnlyRoots.empty_string);
        var transitionOp = new TransitionToDataConstantOperator(jsFunc);

        TestTransitionTo(transitionOp, transitionOp, new SameMapChecker());
    }

    [Fact]
    public void TransitionDataConstantToAnotherDataConstant()
    {
        Isolate isolate = i_isolate;
        Map sloppyMap = Map.CopyInitialMap(isolate, isolate.NativeContext.SloppyFunctionMap);
        SharedFunctionInfo info = factory.NewSharedFunctionInfoForBuiltin(ReadOnlyRoots.empty_string,
            Builtin.Illegal, 0, false);
        Assert.True(sloppyMap.IsStable);

        JSFunction jsFunc1 = factory.NewFunction(info, isolate.NativeContext, sloppyMap);
        var transitionOp1 = new TransitionToDataConstantOperator(jsFunc1);

        JSFunction jsFunc2 = factory.NewFunction(info, isolate.NativeContext, sloppyMap);
        var transitionOp2 = new TransitionToDataConstantOperator(jsFunc2);

        TestTransitionTo(transitionOp1, transitionOp2, new SameMapChecker());
    }

    [Fact]
    public void TransitionDataConstantToDataField()
    {
        JSFunction jsFunc1 = factory.NewFunctionForTesting(ReadOnlyRoots.empty_string);
        var transitionOp1 = new TransitionToDataConstantOperator(jsFunc1);

        var transitionOp2 = new TransitionToDataFieldOperator(kMutable, Representation.Tagged, any_type,
            kHeapNumberValue);

        TestTransitionTo(transitionOp1, transitionOp2, new SameMapChecker());
    }

    [Fact]
    public void TransitionAccessorConstantToSameAccessorConstant()
    {
        AccessorPair pair = CreateAccessorPair(true, true);
        var transitionOp = new TransitionToAccessorConstantOperator(pair);

        TestTransitionTo(transitionOp, transitionOp, new SameMapChecker());
    }

    // TODO(ishell): add this test once IS_ACCESSOR_FIELD_SUPPORTED is supported.
    // TEST(TransitionAccessorConstantToAnotherAccessorConstant)

    [Fact]
    public void NormalizeToMigrationTarget()
    {
        Isolate isolate = i_isolate;

        Assert.NotNull(isolate.NativeContext.NormalizedMapCache);

        Map baseMap = Map.Create(isolate, 4);

        Map existingNormalizedMap = Map.Normalize(isolate, baseMap,
            PropertyNormalizationMode.CLEAR_INOBJECT_PROPERTIES, "Test_NormalizeToMigrationTarget_ExistingMap");
        existingNormalizedMap.IsMigrationTarget = true;

        // Normalizing a second map should hit the normalized map cache, including it
        // being OK for the new map to be a migration target.
        Assert.False(baseMap.IsMigrationTarget);
        Map newNormalizedMap = Map.Normalize(isolate, baseMap,
            PropertyNormalizationMode.CLEAR_INOBJECT_PROPERTIES, "Test_NormalizeToMigrationTarget_NewMap");
        Assert.Same(existingNormalizedMap, newNormalizedMap);
        Assert.True(newNormalizedMap.IsMigrationTarget);
    }

    [Fact]
    public void RepresentationPredicatesAreInSync()
    {
        Representation[] reps =
        [
            Representation.None, Representation.Smi, Representation.Double, Representation.HeapObject,
            Representation.Tagged, Representation.WasmValue,
        ];

        foreach (Representation from in reps)
        {
            Representation mostGenericRep = from.MostGenericInPlaceChange();
            Assert.True(from.CanBeInPlaceChangedTo(mostGenericRep));

            bool mightBeDeprecated = false;

            foreach (Representation to in reps)
            {
                // Skip representation narrowing cases.
                if (!from.FitsInto(to)) continue;

                if (!from.CanBeInPlaceChangedTo(to)) mightBeDeprecated = true;
            }
            Assert.Equal(from.MightCauseMapDeprecation(), mightBeDeprecated);
        }
    }

    static void CHECK_SAME(in JSValue obj, Representation rep, bool expected)
    {
        Assert.Equal(ObjectOps.FitsRepresentation(obj, rep, true), ObjectOps.FitsRepresentation(obj, rep, false));
        Assert.Equal(expected, ObjectOps.FitsRepresentation(obj, rep, true));
    }

    [Fact]
    public void CheckFitsRepresentationPredicate()
    {
        // factory->last_script_id(): any Smi serves.
        JSValue smiValue = JSValue.FromInt(0);
        JSValue doubleValue = JSValue.FromNumber(double.NaN);
        JSValue heapobjectValue = ReadOnlyRoots.empty_ordered_hash_map;

        Representation repSmi = Representation.Smi;
        Representation repDouble = Representation.Double;
        Representation repHeapobject = Representation.HeapObject;
        Representation repTagged = Representation.Tagged;

        // Verify the behavior of Object::FitsRepresentation() with and
        // without coercion. A Smi can be "coerced" into a Double
        // representation by converting it to a HeapNumber. If coercion is
        // disallowed, that query should fail.
        CHECK_SAME(smiValue, repSmi, true);
        Assert.True(ObjectOps.FitsRepresentation(smiValue, repDouble, true));
        Assert.False(ObjectOps.FitsRepresentation(smiValue, repDouble, false));
        CHECK_SAME(smiValue, repHeapobject, false);
        CHECK_SAME(smiValue, repTagged, true);

        CHECK_SAME(doubleValue, repSmi, false);
        CHECK_SAME(doubleValue, repDouble, true);
        // Deviation: V8 expects true here, because its NaN is a HeapNumber and so a
        // HeapObject. JSValue numbers are unboxed and never fit the HeapObject
        // representation (see ObjectOps.FitsRepresentation).
        CHECK_SAME(doubleValue, repHeapobject, false);
        CHECK_SAME(doubleValue, repTagged, true);

        CHECK_SAME(heapobjectValue, repSmi, false);
        CHECK_SAME(heapobjectValue, repDouble, false);
        CHECK_SAME(heapobjectValue, repHeapobject, true);
        CHECK_SAME(heapobjectValue, repTagged, true);
    }
}
