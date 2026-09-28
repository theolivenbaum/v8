// Port of test/cctest/test-field-type-tracking.cc.
//
// V8Sharp has no optimized code yet, so the dependency checks
// (CreateDummyOptimizedCode, DependentCode::InstallDependency and
// CheckCodeObjectForDeopt) are not ported; everything the tests assert about
// maps, field types, representations and deprecation is.
//
// Not ported: ReconfigureAccessorToNonExistingDataFieldHeavy,
// the StoreToConstantField_* tests and HoleyHeapNumber (they compile and run
// JavaScript).
namespace V8Sharp.Tests.Objects;

public partial class FieldTypeTrackingTest : TestWithContext
{
    // TODO(ishell): fix this once TransitionToAccessorProperty is able to always
    // keep map in fast mode.
    static readonly bool IS_ACCESSOR_FIELD_SUPPORTED = false;

    // Number of properties used in the tests.
    const int kPropCount = 7;

    enum ChangeAlertMechanism { kDeprecation, kFieldOwnerDependency, kNoAlert }

    static readonly HeapObject any_type = FieldType.Any;
    static readonly HeapObject none_type = FieldType.None;

    //
    // Helper functions.
    //

    AccessorPair CreateAccessorPair(bool withGetter, bool withSetter)
    {
        AccessorPair pair = factory.NewAccessorPair();
        if (withGetter) pair.Getter = factory.NewFunctionForTesting(ReadOnlyRoots.empty_string);
        if (withSetter) pair.Setter = factory.NewFunctionForTesting(ReadOnlyRoots.empty_string);
        return pair;
    }

    // Check cached migration target map after Map::Update() and Map::TryUpdate()
    static void CheckMigrationTarget(Isolate isolate, Map oldMap, Map newMap)
    {
        Map? target = new TransitionsAccessor(isolate, oldMap).GetMigrationTarget();
        if (target is null) return;
        Assert.Same(newMap, target);
        Assert.Same(target, MapUpdater.TryUpdateNoLock(isolate, oldMap));
    }

    sealed class Expectations
    {
        const int MAX_PROPERTIES = 10;
        readonly Isolate _isolate;
        ElementsKind _elementsKind;
        readonly PropertyKind[] _kinds = new PropertyKind[MAX_PROPERTIES];
        readonly PropertyLocation[] _locations = new PropertyLocation[MAX_PROPERTIES];
        readonly PropertyConstness[] _constnesses = new PropertyConstness[MAX_PROPERTIES];
        readonly PropertyAttributes[] _attributes = new PropertyAttributes[MAX_PROPERTIES];
        readonly Representation[] _representations = new Representation[MAX_PROPERTIES];
        // FieldType for kField, value for DATA_CONSTANT and getter for
        // ACCESSOR_CONSTANT.
        readonly JSValue[] _values = new JSValue[MAX_PROPERTIES];
        // Setter for ACCESSOR_CONSTANT.
        readonly JSValue[] _setterValues = new JSValue[MAX_PROPERTIES];
        int _numberOfProperties;

        public Expectations(Isolate isolate, ElementsKind elementsKind)
        {
            _isolate = isolate;
            _elementsKind = elementsKind;
        }

        public Expectations(Isolate isolate)
            : this(isolate, isolate.NativeContext.ObjectFunction.InitialMap.ElementsKind)
        {
        }

        /// <summary>The copy constructor C++ gets for free.</summary>
        public Expectations Clone()
        {
            var copy = new Expectations(_isolate, _elementsKind) { _numberOfProperties = _numberOfProperties };
            Array.Copy(_kinds, copy._kinds, MAX_PROPERTIES);
            Array.Copy(_locations, copy._locations, MAX_PROPERTIES);
            Array.Copy(_constnesses, copy._constnesses, MAX_PROPERTIES);
            Array.Copy(_attributes, copy._attributes, MAX_PROPERTIES);
            Array.Copy(_representations, copy._representations, MAX_PROPERTIES);
            Array.Copy(_values, copy._values, MAX_PROPERTIES);
            Array.Copy(_setterValues, copy._setterValues, MAX_PROPERTIES);
            return copy;
        }

        void Init(int index, PropertyKind kind, PropertyAttributes attributes, PropertyConstness constness,
            PropertyLocation location, Representation representation, JSValue value)
        {
            Assert.True(index < MAX_PROPERTIES);
            _kinds[index] = kind;
            _locations[index] = location;
            if (kind == PropertyKind.Data && location == PropertyLocation.Field &&
                ElementsKinds.IsTransitionableFastElementsKind(_elementsKind))
            {
                // Maps with transitionable elements kinds must have the most general
                // field type.
                value = FieldType.Any;
                representation = Representation.Tagged;
            }
            _constnesses[index] = constness;
            _attributes[index] = attributes;
            _representations[index] = representation;
            _values[index] = value;
        }

        public void SetElementsKind(ElementsKind elementsKind) => _elementsKind = elementsKind;

        public HeapObject GetFieldType(int index)
        {
            Assert.True(index < MAX_PROPERTIES);
            Assert.Equal(PropertyLocation.Field, _locations[index]);
            return _values[index].Object;
        }

        public void SetDataField(int index, PropertyAttributes attrs, PropertyConstness constness,
            Representation representation, HeapObject fieldType) =>
            Init(index, PropertyKind.Data, attrs, constness, PropertyLocation.Field, representation, fieldType);

        public void SetDataField(int index, PropertyConstness constness, Representation representation,
            HeapObject fieldType) =>
            SetDataField(index, _attributes[index], constness, representation, fieldType);

        public void SetAccessorField(int index, PropertyAttributes attrs) =>
            Init(index, PropertyKind.Accessor, attrs, PropertyConstness.Const, PropertyLocation.Descriptor,
                Representation.Tagged, FieldType.Any);

        public void SetAccessorField(int index) => SetAccessorField(index, _attributes[index]);

        public void SetDataConstant(int index, PropertyAttributes attrs, JSFunction value)
        {
            HeapObject fieldType = FieldType.Class(value.Map);
            Init(index, PropertyKind.Data, attrs, PropertyConstness.Const, PropertyLocation.Field,
                Representation.HeapObject, fieldType);
        }

        public void SetDataConstant(int index, JSFunction value) => SetDataConstant(index, _attributes[index], value);

        public void SetAccessorConstant(int index, PropertyAttributes attrs, JSValue getter, JSValue setter)
        {
            Init(index, PropertyKind.Accessor, attrs, PropertyConstness.Const, PropertyLocation.Descriptor,
                Representation.Tagged, getter);
            _setterValues[index] = setter;
        }

        public void SetAccessorConstantComponent(int index, PropertyAttributes attrs, AccessorComponent component,
            JSValue accessor)
        {
            Assert.Equal(PropertyKind.Accessor, _kinds[index]);
            Assert.Equal(PropertyLocation.Descriptor, _locations[index]);
            Assert.True(index < _numberOfProperties);
            if (component == AccessorComponent.ACCESSOR_GETTER)
            {
                _values[index] = accessor;
            }
            else
            {
                _setterValues[index] = accessor;
            }
        }

        public void SetAccessorConstant(int index, PropertyAttributes attrs, AccessorPair pair) =>
            SetAccessorConstant(index, attrs, pair.Getter, pair.Setter);

        public void SetAccessorConstant(int index, JSValue getter, JSValue setter) =>
            SetAccessorConstant(index, _attributes[index], getter, setter);

        public void SetAccessorConstant(int index, AccessorPair pair) =>
            SetAccessorConstant(index, pair.Getter, pair.Setter);

        public void GeneralizeField(int index)
        {
            Assert.True(index < _numberOfProperties);
            _representations[index] = Representation.Tagged;
            if (_locations[index] == PropertyLocation.Field)
            {
                _values[index] = FieldType.Any;
            }
        }

        bool Check(DescriptorArray descriptors, InternalIndex descriptor)
        {
            PropertyDetails details = descriptors.GetDetails(descriptor);
            int i = descriptor.AsInt;

            if (details.Kind != _kinds[i]) return false;
            if (details.Location != _locations[i]) return false;
            if (details.Constness != _constnesses[i]) return false;

            PropertyAttributes expectedAttributes = _attributes[i];
            if (details.Attributes != expectedAttributes) return false;

            Representation expectedRepresentation = _representations[i];
            if (!details.Representation.Equals(expectedRepresentation)) return false;

            JSValue expectedValue = _values[i];
            if (details.Location == PropertyLocation.Field)
            {
                if (details.Kind == PropertyKind.Data)
                {
                    HeapObject type = descriptors.GetFieldType(descriptor);
                    return ReferenceEquals(expectedValue.HeapObjectOrNull, type);
                }
                // kAccessor
                throw new InvalidOperationException("unreachable");
            }

            Assert.Equal(PropertyKind.Accessor, details.Kind);
            JSValue value = descriptors.GetStrongValue(descriptor);
            if (value.IsIdenticalTo(expectedValue)) return true;
            if (value.HeapObjectOrNull is not AccessorPair pair) return false;
            return pair.Equals(expectedValue, _setterValues[i]);
        }

        public bool Check(Map map, int expectedNof)
        {
            Assert.Equal(_elementsKind, map.ElementsKind);
            Assert.True(_numberOfProperties <= MAX_PROPERTIES);
            Assert.Equal(expectedNof, map.NumberOfOwnDescriptors);
            Assert.False(map.IsDictionaryMap);

            DescriptorArray descriptors = map.InstanceDescriptors;
            Assert.True(expectedNof <= _numberOfProperties);
            for (int i = 0; i < expectedNof; i++)
            {
                if (!Check(descriptors, new InternalIndex(i))) return false;
            }
            return true;
        }

        public bool Check(Map map) => Check(map, _numberOfProperties);

        public bool CheckNormalized(Map map)
        {
            Assert.True(map.IsDictionaryMap);
            Assert.Equal(_elementsKind, map.ElementsKind);
            // TODO(leszeks): Iterate over the key/value pairs of the map and compare
            // them against the expected fields.
            return true;
        }

        //
        // Helper methods for initializing expectations and adding properties to
        // given |map|.
        //

        JSString MakeName(string prefix, int index) =>
            _isolate.Factory.InternalizeString(prefix + index.ToString(System.Globalization.CultureInfo.InvariantCulture));

        public Map AsElementsKind(Map map, ElementsKind elementsKind)
        {
            _elementsKind = elementsKind;
            map = Map.AsElementsKind(_isolate, map, elementsKind);
            Assert.Equal(_elementsKind, map.ElementsKind);
            return map;
        }

        public void ChangeAttributesForAllProperties(PropertyAttributes attributes)
        {
            for (int i = 0; i < _numberOfProperties; i++)
            {
                _attributes[i] = attributes;
            }
        }

        public Map AddDataField(Map map, PropertyAttributes attributes, PropertyConstness constness,
            Representation representation, HeapObject fieldType)
        {
            Assert.Equal(_numberOfProperties, map.NumberOfOwnDescriptors);
            int propertyIndex = _numberOfProperties++;
            SetDataField(propertyIndex, attributes, constness, representation, fieldType);

            JSString name = MakeName("prop", propertyIndex);
            return Map.CopyWithField(_isolate, map, name, fieldType, attributes, constness, representation,
                TransitionFlag.INSERT_TRANSITION)!;
        }

        public Map AddDataConstant(Map map, PropertyAttributes attributes, JSFunction value)
        {
            Assert.Equal(_numberOfProperties, map.NumberOfOwnDescriptors);
            int propertyIndex = _numberOfProperties++;
            SetDataConstant(propertyIndex, attributes, value);

            JSString name = MakeName("prop", propertyIndex);
            return Map.CopyWithConstant(_isolate, map, name, value, attributes, TransitionFlag.INSERT_TRANSITION)!;
        }

        public Map TransitionToDataField(Map map, PropertyAttributes attributes, PropertyConstness constness,
            Representation representation, HeapObject heapType, JSValue value)
        {
            Assert.Equal(_numberOfProperties, map.NumberOfOwnDescriptors);
            int propertyIndex = _numberOfProperties++;
            SetDataField(propertyIndex, attributes, constness, representation, heapType);

            JSString name = MakeName("prop", propertyIndex);
            return Map.TransitionToDataProperty(_isolate, map, name, value, attributes, constness, StoreOrigin.Named);
        }

        public Map TransitionToDataConstant(Map map, PropertyAttributes attributes, JSFunction value)
        {
            Assert.Equal(_numberOfProperties, map.NumberOfOwnDescriptors);
            int propertyIndex = _numberOfProperties++;
            SetDataConstant(propertyIndex, attributes, value);

            JSString name = MakeName("prop", propertyIndex);
            return Map.TransitionToDataProperty(_isolate, map, name, value, attributes, PropertyConstness.Const,
                StoreOrigin.Named);
        }

        public Map FollowDataTransition(Map map, PropertyAttributes attributes, PropertyConstness constness,
            Representation representation, HeapObject heapType)
        {
            Assert.Equal(_numberOfProperties, map.NumberOfOwnDescriptors);
            int propertyIndex = _numberOfProperties++;
            SetDataField(propertyIndex, attributes, constness, representation, heapType);

            JSString name = MakeName("prop", propertyIndex);
            Map? target = TransitionsAccessor.SearchTransition(_isolate, map, name, PropertyKind.Data, attributes);
            Assert.NotNull(target);
            return target!;
        }

        public Map AddAccessorConstant(Map map, PropertyAttributes attributes, AccessorPair pair)
        {
            Assert.Equal(_numberOfProperties, map.NumberOfOwnDescriptors);
            int propertyIndex = _numberOfProperties++;
            SetAccessorConstant(propertyIndex, attributes, pair);

            JSString name = MakeName("prop", propertyIndex);

            Descriptor d = Descriptor.AccessorConstant(name, pair, attributes);
            return Map.CopyInsertDescriptor(_isolate, map, d, TransitionFlag.INSERT_TRANSITION);
        }

        public Map AddAccessorConstant(Map map, PropertyAttributes attributes, JSValue getter, JSValue setter)
        {
            Assert.Equal(_numberOfProperties, map.NumberOfOwnDescriptors);
            int propertyIndex = _numberOfProperties++;
            SetAccessorConstant(propertyIndex, attributes, getter, setter);

            JSString name = MakeName("prop", propertyIndex);

            Assert.True(!getter.IsNull || !setter.IsNull);
            Factory factory = _isolate.Factory;

            if (!getter.IsNull)
            {
                AccessorPair pair = factory.NewAccessorPair();
                pair.SetComponents(getter, JSValue.Null);
                Descriptor d = Descriptor.AccessorConstant(name, pair, attributes);
                map = Map.CopyInsertDescriptor(_isolate, map, d, TransitionFlag.INSERT_TRANSITION);
            }
            if (!setter.IsNull)
            {
                AccessorPair pair = factory.NewAccessorPair();
                pair.SetComponents(getter, setter);
                Descriptor d = Descriptor.AccessorConstant(name, pair, attributes);
                map = Map.CopyInsertDescriptor(_isolate, map, d, TransitionFlag.INSERT_TRANSITION);
            }
            return map;
        }

        public Map TransitionToAccessorConstant(Map map, PropertyAttributes attributes, AccessorPair pair)
        {
            Assert.Equal(_numberOfProperties, map.NumberOfOwnDescriptors);
            int propertyIndex = _numberOfProperties++;
            SetAccessorConstant(propertyIndex, attributes, pair);

            JSString name = MakeName("prop", propertyIndex);

            JSValue getter = pair.Getter;
            JSValue setter = pair.Setter;

            InternalIndex descriptor = map.InstanceDescriptors.Search(name, map.NumberOfOwnDescriptors);
            map = Map.TransitionToAccessorProperty(_isolate, map, name, descriptor, getter, setter, attributes);
            Assert.False(map.IsDeprecated);
            Assert.False(map.IsDictionaryMap);
            return map;
        }
    }

    ////////////////////////////////////////////////////////////////////////////////
    // A set of tests for property reconfiguration that makes new transition tree
    // branch.
    //

    static Map ReconfigureProperty(Isolate isolate, Map map, InternalIndex modifyIndex, PropertyKind newKind,
        PropertyAttributes newAttributes, Representation newRepresentation, HeapObject newFieldType)
    {
        Assert.Equal(PropertyKind.Data, newKind);  // Only kData case is supported.
        var mu = new MapUpdater(isolate, map);
        return mu.ReconfigureToDataField(modifyIndex, newAttributes, PropertyConstness.Const, newRepresentation,
            newFieldType);
    }

    [Fact]
    public void ReconfigureAccessorToNonExistingDataField()
    {
        Isolate isolate = i_isolate;
        AccessorPair pair = CreateAccessorPair(true, true);

        var expectations = new Expectations(isolate);

        // Create a map, add required properties to it and initialize expectations.
        Map initialMap = Map.Create(isolate, 0);
        Map map = initialMap;
        map = expectations.AddAccessorConstant(map, PropertyAttributes.NONE, pair);

        Assert.False(map.IsDeprecated);
        Assert.True(map.IsStable);
        Assert.True(expectations.Check(map));

        var first = new InternalIndex(0);
        Map newMap = ReconfigureProperty(isolate, map, first, PropertyKind.Data, PropertyAttributes.NONE,
            Representation.None, none_type);
        // |map| did not change except marked unstable.
        Assert.False(map.IsDeprecated);
        Assert.False(map.IsStable);
        Assert.True(expectations.Check(map));

        // Property kind reconfiguration always makes the field mutable.
        expectations.SetDataField(0, PropertyAttributes.NONE, PropertyConstness.Mutable, Representation.None,
            none_type);

        Assert.False(newMap.IsDeprecated);
        Assert.True(newMap.IsStable);
        Assert.True(expectations.Check(newMap));

        Map newMap2 = ReconfigureProperty(isolate, map, first, PropertyKind.Data, PropertyAttributes.NONE,
            Representation.None, none_type);
        Assert.Same(newMap, newMap2);

        JSValue value = JSValue.Zero;
        Map preparedMap = Map.PrepareForDataProperty(isolate, newMap, first, PropertyConstness.Const, value);
        // None to Smi generalization is trivial, map does not change.
        Assert.Same(newMap, preparedMap);

        expectations.SetDataField(0, PropertyAttributes.NONE, PropertyConstness.Mutable, Representation.Smi, any_type);
        Assert.True(preparedMap.IsStable);
        Assert.True(expectations.Check(preparedMap));

        // Now create an object with |map|, migrate it to |prepared_map| and ensure
        // that the data property is uninitialized.
        JSObject obj = factory.NewJSObjectFromMap(map);
        JSObject.MigrateToMap(isolate, obj, preparedMap);
        FieldIndex index = FieldIndex.ForDescriptor(preparedMap, first);
        Assert.Same(Oddball.Uninitialized, obj.RawFastPropertyAt(index).HeapObjectOrNull);
    }

    ////////////////////////////////////////////////////////////////////////////////
    // A set of tests for field generalization case.
    //

    // <Constness, Representation, FieldType> data.
    readonly record struct CRFTData(PropertyConstness constness, Representation representation, HeapObject type);

    // This test ensures that field generalization at |property_index| is done
    // correctly independently of the fact that the |map| is detached from
    // transition tree or not.
    //
    //  {} - p0 - p1 - p2: |detach_point_map|
    //                  |
    //                  X - detached at |detach_property_at_index|
    //                  |
    //                  + - p3 - p4: |map|
    //
    // Detaching does not happen if |detach_property_at_index| is -1.
    //
    void TestGeneralizeField(int detachPropertyAtIndex, int propertyIndex, CRFTData from, CRFTData to,
        CRFTData expected, ChangeAlertMechanism expectedAlert)
    {
        Isolate isolate = i_isolate;

        Assert.True(detachPropertyAtIndex >= -1 && detachPropertyAtIndex < kPropCount);
        Assert.True(propertyIndex < kPropCount);
        Assert.NotEqual(detachPropertyAtIndex, propertyIndex);

        bool isDetachedMap = detachPropertyAtIndex >= 0;

        var expectations = new Expectations(isolate);

        // Create a map, add required properties to it and initialize expectations.
        Map initialMap = Map.Create(isolate, 0);
        Map map = initialMap;
        Map? detachPointMap = null;
        for (int i = 0; i < kPropCount; i++)
        {
            if (i == propertyIndex)
            {
                map = expectations.AddDataField(map, PropertyAttributes.NONE, from.constness, from.representation,
                    from.type);
            }
            else
            {
                map = expectations.AddDataField(map, PropertyAttributes.NONE, PropertyConstness.Const,
                    Representation.Smi, any_type);
                if (i == detachPropertyAtIndex) detachPointMap = map;
            }
        }
        Assert.False(map.IsDeprecated);
        Assert.True(map.IsStable);
        Assert.True(expectations.Check(map));

        if (isDetachedMap)
        {
            detachPointMap = ReconfigureProperty(isolate, detachPointMap!, new InternalIndex(detachPropertyAtIndex),
                PropertyKind.Data, PropertyAttributes.NONE, Representation.Double, any_type);
            expectations.SetDataField(detachPropertyAtIndex, PropertyConstness.Const, Representation.Double, any_type);
            Assert.True(map.IsDeprecated);
            Assert.True(expectations.Check(detachPointMap, detachPointMap.NumberOfOwnDescriptors));
        }

        Map fieldOwner = map.FindFieldOwner(new InternalIndex(propertyIndex));

        // Create new maps by generalizing representation of propX field.
        Map newMap = ReconfigureProperty(isolate, map, new InternalIndex(propertyIndex), PropertyKind.Data,
            PropertyAttributes.NONE, to.representation, to.type);

        expectations.SetDataField(propertyIndex, expected.constness, expected.representation, expected.type);

        Assert.False(newMap.IsDeprecated);
        Assert.True(expectations.Check(newMap));

        if (isDetachedMap)
        {
            Assert.False(map.IsStable);
            Assert.True(map.IsDeprecated);
            Assert.NotSame(map, newMap);
        }
        else if (expectedAlert == ChangeAlertMechanism.kDeprecation)
        {
            Assert.False(map.IsStable);
            Assert.True(map.IsDeprecated);
            Assert.True(fieldOwner.IsDeprecated);
        }
        else
        {
            Assert.False(fieldOwner.IsDeprecated);
            Assert.True(map.IsStable);  // Map did not change, must be left stable.
            Assert.Same(map, newMap);
        }

        {
            // Check that all previous maps are not stable.
            Map tmp = newMap;
            while (true)
            {
                HeapObject? back = tmp.GetBackPointer();
                if (back is null) break;
                tmp = (Map)back;
                Assert.False(tmp.IsStable);
            }
        }

        // Update all deprecated maps and check that they are now the same.
        Map updatedMap = Map.Update(isolate, map);
        Assert.Same(newMap, updatedMap);
        CheckMigrationTarget(isolate, map, updatedMap);
    }

    void TestGeneralizeField(CRFTData from, CRFTData to, CRFTData expected, ChangeAlertMechanism expectedAlert)
    {
        // Check the cases when the map being reconfigured is a part of the
        // transition tree.
        int[] indices = [0, 2, kPropCount - 1];
        foreach (int index in indices)
        {
            TestGeneralizeField(-1, index, from, to, expected, expectedAlert);
        }

        if (!from.representation.IsNone)
        {
            // Check the cases when the map being reconfigured is NOT a part of the
            // transition tree. "None -> anything" representation changes make sense
            // only for "attached" maps.
            int[] indices2 = [0, kPropCount - 1];
            foreach (int index in indices2)
            {
                TestGeneralizeField(index, 2, from, to, expected, expectedAlert);
            }

            // Check that reconfiguration to the very same field works correctly.
            CRFTData data = from;
            TestGeneralizeField(-1, 2, data, data, data, ChangeAlertMechanism.kNoAlert);
        }
    }

    [Fact]
    public void GeneralizeSmiFieldToDouble()
    {
        TestGeneralizeField(
            new(PropertyConstness.Mutable, Representation.Smi, any_type),
            new(PropertyConstness.Mutable, Representation.Double, any_type),
            new(PropertyConstness.Mutable, Representation.Double, any_type),
            ChangeAlertMechanism.kDeprecation);
    }

    [Fact]
    public void GeneralizeSmiFieldToTagged()
    {
        HeapObject valueType = FieldType.Class(Map.Create(i_isolate, 0));

        TestGeneralizeField(
            new(PropertyConstness.Mutable, Representation.Smi, any_type),
            new(PropertyConstness.Mutable, Representation.HeapObject, valueType),
            new(PropertyConstness.Mutable, Representation.Tagged, any_type),
            ChangeAlertMechanism.kFieldOwnerDependency);
    }

    [Fact]
    public void GeneralizeDoubleFieldToTagged()
    {
        HeapObject valueType = FieldType.Class(Map.Create(i_isolate, 0));

        TestGeneralizeField(
            new(PropertyConstness.Mutable, Representation.Double, any_type),
            new(PropertyConstness.Mutable, Representation.HeapObject, valueType),
            new(PropertyConstness.Mutable, Representation.Tagged, any_type),
            ChangeAlertMechanism.kFieldOwnerDependency);
    }

    [Fact]
    public void GeneralizeHeapObjectFieldToTagged()
    {
        HeapObject valueType = FieldType.Class(Map.Create(i_isolate, 0));

        TestGeneralizeField(
            new(PropertyConstness.Mutable, Representation.HeapObject, valueType),
            new(PropertyConstness.Mutable, Representation.Smi, any_type),
            new(PropertyConstness.Mutable, Representation.Tagged, any_type),
            ChangeAlertMechanism.kFieldOwnerDependency);
    }

    [Fact]
    public void GeneralizeHeapObjectFieldToHeapObject()
    {
        HeapObject currentType = FieldType.Class(Map.Create(i_isolate, 0));
        HeapObject newType = FieldType.Class(Map.Create(i_isolate, 0));
        HeapObject expectedType = any_type;

        TestGeneralizeField(
            new(PropertyConstness.Mutable, Representation.HeapObject, currentType),
            new(PropertyConstness.Mutable, Representation.HeapObject, newType),
            new(PropertyConstness.Mutable, Representation.HeapObject, expectedType),
            ChangeAlertMechanism.kFieldOwnerDependency);

        newType = FieldType.Class(Map.Create(i_isolate, 0));

        TestGeneralizeField(
            new(PropertyConstness.Mutable, Representation.HeapObject, any_type),
            new(PropertyConstness.Mutable, Representation.HeapObject, newType),
            new(PropertyConstness.Mutable, Representation.HeapObject, any_type),
            ChangeAlertMechanism.kNoAlert);
    }

    [Fact]
    public void GeneralizeNoneFieldToSmi()
    {
        // None -> Smi representation change is trivial.
        TestGeneralizeField(
            new(PropertyConstness.Mutable, Representation.None, none_type),
            new(PropertyConstness.Mutable, Representation.Smi, any_type),
            new(PropertyConstness.Mutable, Representation.Smi, any_type),
            ChangeAlertMechanism.kFieldOwnerDependency);
    }

    [Fact]
    public void GeneralizeNoneFieldToDouble()
    {
        // None -> Double representation change is NOT trivial.
        TestGeneralizeField(
            new(PropertyConstness.Mutable, Representation.None, none_type),
            new(PropertyConstness.Mutable, Representation.Double, any_type),
            new(PropertyConstness.Mutable, Representation.Double, any_type),
            ChangeAlertMechanism.kDeprecation);
    }

    [Fact]
    public void GeneralizeNoneFieldToHeapObject()
    {
        HeapObject valueType = FieldType.Class(Map.Create(i_isolate, 0));

        // None -> HeapObject representation change is trivial.
        TestGeneralizeField(
            new(PropertyConstness.Mutable, Representation.None, none_type),
            new(PropertyConstness.Mutable, Representation.HeapObject, valueType),
            new(PropertyConstness.Mutable, Representation.HeapObject, valueType),
            ChangeAlertMechanism.kFieldOwnerDependency);
    }

    [Fact]
    public void GeneralizeNoneFieldToTagged()
    {
        // None -> HeapObject representation change is trivial.
        TestGeneralizeField(
            new(PropertyConstness.Mutable, Representation.None, none_type),
            new(PropertyConstness.Mutable, Representation.Tagged, any_type),
            new(PropertyConstness.Mutable, Representation.Tagged, any_type),
            ChangeAlertMechanism.kFieldOwnerDependency);
    }

    ////////////////////////////////////////////////////////////////////////////////
    // A set of tests for field generalization case with kAccessor properties.
    //

    [Fact]
    public void GeneralizeFieldWithAccessorProperties()
    {
        Isolate isolate = i_isolate;
        AccessorPair pair = CreateAccessorPair(true, true);

        const int kAccessorProp = kPropCount / 2;
        var expectations = new Expectations(isolate);

        // Create a map, add required properties to it and initialize expectations.
        Map initialMap = Map.Create(isolate, 0);
        Map map = initialMap;
        for (int i = 0; i < kPropCount; i++)
        {
            if (i == kAccessorProp)
            {
                map = expectations.AddAccessorConstant(map, PropertyAttributes.NONE, pair);
            }
            else
            {
                map = expectations.AddDataField(map, PropertyAttributes.NONE, PropertyConstness.Mutable,
                    Representation.Smi, any_type);
            }
        }
        Assert.False(map.IsDeprecated);
        Assert.True(map.IsStable);
        Assert.True(expectations.Check(map));

        // Create new maps by generalizing representation of propX field.
        var maps = new Map[kPropCount];
        for (int i = 0; i < kPropCount; i++)
        {
            if (i == kAccessorProp)
            {
                // Skip accessor property reconfiguration.
                maps[i] = maps[i - 1];
                continue;
            }
            Map newMap = ReconfigureProperty(isolate, map, new InternalIndex(i), PropertyKind.Data,
                PropertyAttributes.NONE, Representation.Double, any_type);
            maps[i] = newMap;

            expectations.SetDataField(i, PropertyConstness.Mutable, Representation.Double, any_type);

            Assert.False(map.IsStable);
            Assert.True(map.IsDeprecated);
            Assert.NotSame(map, newMap);
            Assert.True(i == 0 || maps[i - 1].IsDeprecated);

            Assert.False(newMap.IsDeprecated);
            Assert.True(expectations.Check(newMap));
        }

        Map activeMap = maps[kPropCount - 1];
        Assert.False(activeMap.IsDeprecated);

        // Update all deprecated maps and check that they are now the same.
        Map updatedMap = Map.Update(isolate, map);
        Assert.Same(activeMap, updatedMap);
        CheckMigrationTarget(isolate, map, updatedMap);
        for (int i = 0; i < kPropCount; i++)
        {
            updatedMap = Map.Update(isolate, maps[i]);
            Assert.Same(activeMap, updatedMap);
            CheckMigrationTarget(isolate, maps[i], updatedMap);
        }
    }

    ////////////////////////////////////////////////////////////////////////////////
    // A set of tests for attribute reconfiguration case.
    //

    // This test ensures that field generalization is correctly propagated from one
    // branch of transition tree (|map2|) to another (|map|).
    //
    //             + - p2B - p3 - p4: |map2|
    //             |
    //  {} - p0 - p1 - p2A - p3 - p4: |map|
    //
    // where "p2A" and "p2B" differ only in the attributes.
    //
    void TestReconfigureDataFieldAttribute_GeneralizeField(CRFTData from, CRFTData to, CRFTData expected,
        ChangeAlertMechanism expectedAlert)
    {
        Isolate isolate = i_isolate;

        var expectations = new Expectations(isolate);

        // Create a map, add required properties to it and initialize expectations.
        Map initialMap = Map.Create(isolate, 0);
        Map map = initialMap;
        for (int i = 0; i < kPropCount; i++)
        {
            map = expectations.AddDataField(map, PropertyAttributes.NONE, from.constness, from.representation, from.type);
        }
        Assert.False(map.IsDeprecated);
        Assert.True(map.IsStable);
        Assert.True(expectations.Check(map));

        // Create another branch in transition tree (property at index |kSplitProp|
        // has different attributes), initialize expectations.
        const int kSplitProp = kPropCount / 2;
        var expectations2 = new Expectations(isolate);

        Map map2 = initialMap;
        for (int i = 0; i < kSplitProp; i++)
        {
            map2 = expectations2.FollowDataTransition(map2, PropertyAttributes.NONE, from.constness,
                from.representation, from.type);
        }
        map2 = expectations2.AddDataField(map2, PropertyAttributes.READ_ONLY, to.constness, to.representation, to.type);

        for (int i = kSplitProp + 1; i < kPropCount; i++)
        {
            map2 = expectations2.AddDataField(map2, PropertyAttributes.NONE, to.constness, to.representation, to.type);
        }
        Assert.False(map2.IsDeprecated);
        Assert.True(map2.IsStable);
        Assert.True(expectations2.Check(map2));

        // Reconfigure attributes of property |kSplitProp| of |map2| to NONE, which
        // should generalize representations in |map1|.
        Map newMap = MapUpdater.ReconfigureExistingProperty(isolate, map2, new InternalIndex(kSplitProp),
            PropertyKind.Data, PropertyAttributes.NONE, PropertyConstness.Const);

        // |map2| should be mosly left unchanged but marked unstable and if the
        // source property was constant it should also be transitioned to kMutable.
        Assert.False(map2.IsStable);
        Assert.False(map2.IsDeprecated);
        Assert.NotSame(map2, newMap);
        Assert.True(expectations2.Check(map2));

        for (int i = kSplitProp; i < kPropCount; i++)
        {
            expectations.SetDataField(i, expected.constness, expected.representation, expected.type);
        }

        if (expectedAlert == ChangeAlertMechanism.kDeprecation)
        {
            // |map| should be deprecated and |new_map| should match new expectations.
            Assert.True(map.IsDeprecated);
            Assert.NotSame(map, newMap);

            Assert.False(newMap.IsDeprecated);
            Assert.True(expectations.Check(newMap));

            // Update deprecated |map|, it should become |new_map|.
            Map updatedMap = Map.Update(isolate, map);
            Assert.Same(newMap, updatedMap);
            CheckMigrationTarget(isolate, map, updatedMap);
        }
        else
        {
            Assert.True(expectedAlert is ChangeAlertMechanism.kFieldOwnerDependency or ChangeAlertMechanism.kNoAlert);
            // In case of in-place generalization |map| should be returned as a result
            // of the property reconfiguration, respective field types should be
            // generalized and respective code dependencies should be invalidated.
            // |map| should be NOT deprecated and it should match new expectations.
            Assert.False(map.IsDeprecated);
            Assert.Same(map, newMap);

            Assert.False(newMap.IsDeprecated);
            Assert.True(expectations.Check(newMap));

            Map updatedMap = Map.Update(isolate, map);
            Assert.Same(newMap, updatedMap);
        }
    }

    [Fact]
    public void ReconfigureDataFieldAttribute_GeneralizeSmiFieldToDouble()
    {
        TestReconfigureDataFieldAttribute_GeneralizeField(
            new(PropertyConstness.Const, Representation.Smi, any_type),
            new(PropertyConstness.Const, Representation.Double, any_type),
            new(PropertyConstness.Const, Representation.Double, any_type),
            ChangeAlertMechanism.kDeprecation);

        TestReconfigureDataFieldAttribute_GeneralizeField(
            new(PropertyConstness.Const, Representation.Smi, any_type),
            new(PropertyConstness.Mutable, Representation.Double, any_type),
            new(PropertyConstness.Mutable, Representation.Double, any_type),
            ChangeAlertMechanism.kDeprecation);

        TestReconfigureDataFieldAttribute_GeneralizeField(
            new(PropertyConstness.Mutable, Representation.Smi, any_type),
            new(PropertyConstness.Const, Representation.Double, any_type),
            new(PropertyConstness.Mutable, Representation.Double, any_type),
            ChangeAlertMechanism.kDeprecation);

        TestReconfigureDataFieldAttribute_GeneralizeField(
            new(PropertyConstness.Mutable, Representation.Smi, any_type),
            new(PropertyConstness.Mutable, Representation.Double, any_type),
            new(PropertyConstness.Mutable, Representation.Double, any_type),
            ChangeAlertMechanism.kDeprecation);
    }

    [Fact]
    public void ReconfigureDataFieldAttribute_GeneralizeSmiFieldToTagged()
    {
        HeapObject valueType = FieldType.Class(Map.Create(i_isolate, 0));

        TestReconfigureDataFieldAttribute_GeneralizeField(
            new(PropertyConstness.Const, Representation.Smi, any_type),
            new(PropertyConstness.Const, Representation.HeapObject, valueType),
            new(PropertyConstness.Const, Representation.Tagged, any_type),
            ChangeAlertMechanism.kFieldOwnerDependency);

        TestReconfigureDataFieldAttribute_GeneralizeField(
            new(PropertyConstness.Const, Representation.Smi, any_type),
            new(PropertyConstness.Mutable, Representation.HeapObject, valueType),
            new(PropertyConstness.Mutable, Representation.Tagged, any_type),
            ChangeAlertMechanism.kFieldOwnerDependency);

        TestReconfigureDataFieldAttribute_GeneralizeField(
            new(PropertyConstness.Mutable, Representation.Smi, any_type),
            new(PropertyConstness.Const, Representation.HeapObject, valueType),
            new(PropertyConstness.Mutable, Representation.Tagged, any_type),
            ChangeAlertMechanism.kFieldOwnerDependency);

        TestReconfigureDataFieldAttribute_GeneralizeField(
            new(PropertyConstness.Mutable, Representation.Smi, any_type),
            new(PropertyConstness.Mutable, Representation.HeapObject, valueType),
            new(PropertyConstness.Mutable, Representation.Tagged, any_type),
            ChangeAlertMechanism.kFieldOwnerDependency);
    }

    [Fact]
    public void ReconfigureDataFieldAttribute_GeneralizeDoubleFieldToTagged()
    {
        HeapObject valueType = FieldType.Class(Map.Create(i_isolate, 0));

        TestReconfigureDataFieldAttribute_GeneralizeField(
            new(PropertyConstness.Const, Representation.Double, any_type),
            new(PropertyConstness.Const, Representation.HeapObject, valueType),
            new(PropertyConstness.Const, Representation.Tagged, any_type),
            ChangeAlertMechanism.kFieldOwnerDependency);

        TestReconfigureDataFieldAttribute_GeneralizeField(
            new(PropertyConstness.Const, Representation.Double, any_type),
            new(PropertyConstness.Mutable, Representation.HeapObject, valueType),
            new(PropertyConstness.Mutable, Representation.Tagged, any_type),
            ChangeAlertMechanism.kFieldOwnerDependency);

        TestReconfigureDataFieldAttribute_GeneralizeField(
            new(PropertyConstness.Mutable, Representation.Double, any_type),
            new(PropertyConstness.Const, Representation.HeapObject, valueType),
            new(PropertyConstness.Mutable, Representation.Tagged, any_type),
            ChangeAlertMechanism.kFieldOwnerDependency);

        TestReconfigureDataFieldAttribute_GeneralizeField(
            new(PropertyConstness.Mutable, Representation.Double, any_type),
            new(PropertyConstness.Mutable, Representation.HeapObject, valueType),
            new(PropertyConstness.Mutable, Representation.Tagged, any_type),
            ChangeAlertMechanism.kFieldOwnerDependency);
    }

    [Fact]
    public void ReconfigureDataFieldAttribute_GeneralizeHeapObjFieldToHeapObj()
    {
        HeapObject currentType = FieldType.Class(Map.Create(i_isolate, 0));
        HeapObject newType = FieldType.Class(Map.Create(i_isolate, 0));
        HeapObject expectedType = any_type;

        // Check generalizations that trigger deopts.
        TestReconfigureDataFieldAttribute_GeneralizeField(
            new(PropertyConstness.Const, Representation.HeapObject, currentType),
            new(PropertyConstness.Const, Representation.HeapObject, newType),
            new(PropertyConstness.Const, Representation.HeapObject, expectedType),
            ChangeAlertMechanism.kFieldOwnerDependency);

        // PropertyConstness::kConst to PropertyConstness::kMutable migration does
        // not create a new map, therefore trivial generalization.
        TestReconfigureDataFieldAttribute_GeneralizeField(
            new(PropertyConstness.Const, Representation.HeapObject, currentType),
            new(PropertyConstness.Mutable, Representation.HeapObject, newType),
            new(PropertyConstness.Mutable, Representation.HeapObject, expectedType),
            ChangeAlertMechanism.kFieldOwnerDependency);

        TestReconfigureDataFieldAttribute_GeneralizeField(
            new(PropertyConstness.Mutable, Representation.HeapObject, currentType),
            new(PropertyConstness.Const, Representation.HeapObject, newType),
            new(PropertyConstness.Mutable, Representation.HeapObject, expectedType),
            ChangeAlertMechanism.kFieldOwnerDependency);

        TestReconfigureDataFieldAttribute_GeneralizeField(
            new(PropertyConstness.Mutable, Representation.HeapObject, currentType),
            new(PropertyConstness.Mutable, Representation.HeapObject, newType),
            new(PropertyConstness.Mutable, Representation.HeapObject, expectedType),
            ChangeAlertMechanism.kFieldOwnerDependency);

        // Check generalizations that do not trigger deopts.
        newType = FieldType.Class(Map.Create(i_isolate, 0));

        TestReconfigureDataFieldAttribute_GeneralizeField(
            new(PropertyConstness.Const, Representation.HeapObject, any_type),
            new(PropertyConstness.Const, Representation.HeapObject, newType),
            new(PropertyConstness.Const, Representation.HeapObject, any_type),
            ChangeAlertMechanism.kNoAlert);

        // PropertyConstness::kConst to PropertyConstness::kMutable migration does
        // not create a new map, therefore trivial generalization.
        TestReconfigureDataFieldAttribute_GeneralizeField(
            new(PropertyConstness.Const, Representation.HeapObject, any_type),
            new(PropertyConstness.Mutable, Representation.HeapObject, newType),
            new(PropertyConstness.Mutable, Representation.HeapObject, any_type),
            ChangeAlertMechanism.kFieldOwnerDependency);

        TestReconfigureDataFieldAttribute_GeneralizeField(
            new(PropertyConstness.Mutable, Representation.HeapObject, any_type),
            new(PropertyConstness.Const, Representation.HeapObject, newType),
            new(PropertyConstness.Mutable, Representation.HeapObject, any_type),
            ChangeAlertMechanism.kNoAlert);

        TestReconfigureDataFieldAttribute_GeneralizeField(
            new(PropertyConstness.Mutable, Representation.HeapObject, any_type),
            new(PropertyConstness.Mutable, Representation.HeapObject, newType),
            new(PropertyConstness.Mutable, Representation.HeapObject, any_type),
            ChangeAlertMechanism.kNoAlert);
    }

    [Fact]
    public void ReconfigureDataFieldAttribute_GeneralizeHeapObjectFieldToTagged()
    {
        HeapObject valueType = FieldType.Class(Map.Create(i_isolate, 0));

        TestReconfigureDataFieldAttribute_GeneralizeField(
            new(PropertyConstness.Mutable, Representation.HeapObject, valueType),
            new(PropertyConstness.Mutable, Representation.Smi, any_type),
            new(PropertyConstness.Mutable, Representation.Tagged, any_type),
            ChangeAlertMechanism.kFieldOwnerDependency);
    }

    // Checks that given |map| is deprecated and that it updates to given |new_map|
    // which in turn should match expectations.
    static void CheckDeprecated(Isolate isolate, Map map, Map newMap, Expectations expectations)
    {
        Assert.True(map.IsDeprecated);
        Assert.NotSame(map, newMap);

        Assert.False(newMap.IsDeprecated);
        Assert.True(expectations.Check(newMap));

        // Update deprecated |map|, it should become |new_map|.
        Map updatedMap = Map.Update(isolate, map);
        Assert.Same(newMap, updatedMap);
        CheckMigrationTarget(isolate, map, updatedMap);
    }

    // Checks that given |map| is NOT deprecated, equals to given |new_map| and
    // matches expectations.
    static void CheckSameMap(Isolate isolate, Map map, Map newMap, Expectations expectations)
    {
        // |map| was not reconfigured, therefore it should stay stable.
        Assert.True(map.IsStable);
        Assert.False(map.IsDeprecated);
        Assert.Same(map, newMap);

        Assert.False(newMap.IsDeprecated);
        Assert.True(expectations.Check(newMap));

        // Update deprecated |map|, it should become |new_map|.
        Map updatedMap = Map.Update(isolate, map);
        Assert.Same(newMap, updatedMap);
    }

    // Checks that given |map| is NOT deprecated and matches expectations.
    // |new_map| is unrelated to |map|.
    static void CheckUnrelated(Isolate isolate, Map map, Map newMap, Expectations expectations)
    {
        Assert.False(map.IsDeprecated);
        Assert.NotSame(map, newMap);
        Assert.True(expectations.Check(map));

        Assert.True(newMap.IsStable);
        Assert.False(newMap.IsDeprecated);
    }

    // Checks that given |map| is NOT deprecated, and |new_map| is a result of going
    // dictionary mode.
    static void CheckNormalize(Isolate isolate, Map map, Map newMap, Expectations expectations)
    {
        Assert.False(map.IsDeprecated);
        Assert.NotSame(map, newMap);

        Assert.Null(newMap.GetBackPointer());
        Assert.False(newMap.IsDeprecated);
        Assert.True(expectations.CheckNormalized(newMap));
    }

    delegate void Checker(Isolate isolate, Map map, Map newMap, Expectations expectations);

    interface ICustomPropertyConfig
    {
        Map AddPropertyAtBranch(int branchId, Expectations expectations, Map map);
        void UpdateExpectations(int propertyIndex, Expectations expectations);
    }

    // This test ensures that field generalization is correctly propagated from one
    // branch of transition tree (|map2|) to another (|map1|).
    //
    //             + - p2B - p3 - p4: |map2|
    //             |
    //  {} - p0 - p1: |map|
    //             |
    //             + - p2A - p3 - p4: |map1|
    //                        |
    //                        + - the property customized by the TestConfig provided
    //
    // where "p2A" and "p2B" differ only in the attributes.
    //
    void TestReconfigureProperty_CustomPropertyAfterTargetMap(ICustomPropertyConfig config, Checker checker)
    {
        Isolate isolate = i_isolate;

        const int kCustomPropIndex = kPropCount - 2;
        var expectations = new Expectations(isolate);

        const int kSplitProp = 2;
        Assert.True(kSplitProp < kCustomPropIndex);

        const PropertyConstness constness = PropertyConstness.Mutable;
        Representation representation = Representation.Smi;

        // Create common part of transition tree.
        Map initialMap = Map.Create(isolate, 0);
        Map map = initialMap;
        for (int i = 0; i < kSplitProp; i++)
        {
            map = expectations.AddDataField(map, PropertyAttributes.NONE, constness, representation, any_type);
        }
        Assert.False(map.IsDeprecated);
        Assert.True(map.IsStable);
        Assert.True(expectations.Check(map));

        // Create branch to |map1|.
        Map map1 = map;
        Expectations expectations1 = expectations.Clone();
        for (int i = kSplitProp; i < kCustomPropIndex; i++)
        {
            map1 = expectations1.AddDataField(map1, PropertyAttributes.NONE, constness, representation, any_type);
        }
        map1 = config.AddPropertyAtBranch(1, expectations1, map1);
        for (int i = kCustomPropIndex + 1; i < kPropCount; i++)
        {
            map1 = expectations1.AddDataField(map1, PropertyAttributes.NONE, constness, representation, any_type);
        }
        Assert.False(map1.IsDeprecated);
        Assert.True(map1.IsStable);
        Assert.True(expectations1.Check(map1));

        // Create another branch in transition tree (property at index |kSplitProp|
        // has different attributes), initialize expectations.
        Map map2 = map;
        Expectations expectations2 = expectations.Clone();
        map2 = expectations2.AddDataField(map2, PropertyAttributes.READ_ONLY, constness, representation, any_type);
        for (int i = kSplitProp + 1; i < kCustomPropIndex; i++)
        {
            map2 = expectations2.AddDataField(map2, PropertyAttributes.NONE, constness, representation, any_type);
        }
        map2 = config.AddPropertyAtBranch(2, expectations2, map2);
        for (int i = kCustomPropIndex + 1; i < kPropCount; i++)
        {
            map2 = expectations2.AddDataField(map2, PropertyAttributes.NONE, constness, representation, any_type);
        }
        Assert.False(map2.IsDeprecated);
        Assert.True(map2.IsStable);
        Assert.True(expectations2.Check(map2));

        // Reconfigure attributes of property |kSplitProp| of |map2| to NONE, which
        // should generalize representations in |map1|.
        Map newMap = MapUpdater.ReconfigureExistingProperty(isolate, map2, new InternalIndex(kSplitProp),
            PropertyKind.Data, PropertyAttributes.NONE, PropertyConstness.Const);

        // |map2| should be left unchanged but marked unstable.
        Assert.False(map2.IsStable);
        Assert.False(map2.IsDeprecated);
        Assert.NotSame(map2, newMap);
        Assert.True(expectations2.Check(map2));

        config.UpdateExpectations(kCustomPropIndex, expectations1);
        checker(isolate, map1, newMap, expectations1);
    }

    sealed class SameDataConstantConfig(JSFunction jsFunc) : ICustomPropertyConfig
    {
        public Map AddPropertyAtBranch(int branchId, Expectations expectations, Map map)
        {
            Assert.True(branchId is 1 or 2);
            // Add the same data constant property at both transition tree branches.
            return expectations.AddDataConstant(map, PropertyAttributes.NONE, jsFunc);
        }

        public void UpdateExpectations(int propertyIndex, Expectations expectations)
        {
            // Expectations stay the same.
        }
    }

    [Fact]
    public void ReconfigureDataFieldAttribute_SameDataConstantAfterTargetMap()
    {
        var config = new SameDataConstantConfig(factory.NewFunctionForTesting(ReadOnlyRoots.empty_string));
        // Two branches are "compatible" so the |map1| should NOT be deprecated.
        TestReconfigureProperty_CustomPropertyAfterTargetMap(config, CheckSameMap);
    }

    sealed class DataConstantToDataFieldConfig : ICustomPropertyConfig
    {
        readonly JSFunction _jsFunc1;
        readonly JSFunction _jsFunc2;
        readonly HeapObject _functionType;

        public DataConstantToDataFieldConfig(Isolate isolate)
        {
            Factory factory = isolate.Factory;
            JSString name = ReadOnlyRoots.empty_string;
            Map sloppyMap = Map.CopyInitialMap(isolate, isolate.NativeContext.SloppyFunctionMap);
            SharedFunctionInfo info = factory.NewSharedFunctionInfoForBuiltin(name, Builtin.Illegal, 0, false);
            _functionType = FieldType.Class(sloppyMap);
            Assert.True(sloppyMap.IsStable);

            _jsFunc1 = factory.NewFunction(info, isolate.NativeContext, sloppyMap);
            _jsFunc2 = factory.NewFunction(info, isolate.NativeContext, sloppyMap);
        }

        public Map AddPropertyAtBranch(int branchId, Expectations expectations, Map map)
        {
            Assert.True(branchId is 1 or 2);
            JSFunction jsFunc = branchId == 1 ? _jsFunc1 : _jsFunc2;
            return expectations.AddDataConstant(map, PropertyAttributes.NONE, jsFunc);
        }

        public void UpdateExpectations(int propertyIndex, Expectations expectations) =>
            expectations.SetDataField(propertyIndex, PropertyConstness.Const, Representation.HeapObject, _functionType);
    }

    [Fact]
    public void ReconfigureDataFieldAttribute_DataConstantToDataFieldAfterTargetMap()
    {
        var config = new DataConstantToDataFieldConfig(i_isolate);
        TestReconfigureProperty_CustomPropertyAfterTargetMap(config, CheckSameMap);
    }

    sealed class DataConstantToAccConstantConfig(JSFunction jsFunc, AccessorPair pair) : ICustomPropertyConfig
    {
        public Map AddPropertyAtBranch(int branchId, Expectations expectations, Map map)
        {
            Assert.True(branchId is 1 or 2);
            return branchId == 1
                ? expectations.AddDataConstant(map, PropertyAttributes.NONE, jsFunc)
                : expectations.AddAccessorConstant(map, PropertyAttributes.NONE, pair);
        }

        public void UpdateExpectations(int propertyIndex, Expectations expectations) { }
    }

    [Fact]
    public void ReconfigureDataFieldAttribute_DataConstantToAccConstantAfterTargetMap()
    {
        var config = new DataConstantToAccConstantConfig(factory.NewFunctionForTesting(ReadOnlyRoots.empty_string),
            CreateAccessorPair(true, true));
        // These are completely separate branches in transition tree.
        TestReconfigureProperty_CustomPropertyAfterTargetMap(config, CheckUnrelated);
    }

    sealed class SameAccessorConstantConfig(AccessorPair pair) : ICustomPropertyConfig
    {
        public Map AddPropertyAtBranch(int branchId, Expectations expectations, Map map)
        {
            Assert.True(branchId is 1 or 2);
            // Add the same accessor constant property at both transition tree
            // branches.
            return expectations.AddAccessorConstant(map, PropertyAttributes.NONE, pair);
        }

        public void UpdateExpectations(int propertyIndex, Expectations expectations)
        {
            // Two branches are "compatible" so the |map1| should NOT be deprecated.
        }
    }

    [Fact]
    public void ReconfigureDataFieldAttribute_SameAccessorConstantAfterTargetMap()
    {
        var config = new SameAccessorConstantConfig(CreateAccessorPair(true, true));
        TestReconfigureProperty_CustomPropertyAfterTargetMap(config, CheckSameMap);
    }

    sealed class AccConstantToAccFieldConfig(AccessorPair pair1, AccessorPair pair2) : ICustomPropertyConfig
    {
        public Map AddPropertyAtBranch(int branchId, Expectations expectations, Map map)
        {
            Assert.True(branchId is 1 or 2);
            AccessorPair pair = branchId == 1 ? pair1 : pair2;
            return expectations.AddAccessorConstant(map, PropertyAttributes.NONE, pair);
        }

        public void UpdateExpectations(int propertyIndex, Expectations expectations)
        {
            if (IS_ACCESSOR_FIELD_SUPPORTED)
            {
                expectations.SetAccessorField(propertyIndex);
            }
            else
            {
                // Currently we have a normalize case and ACCESSOR property becomes
                // ACCESSOR_CONSTANT.
                expectations.SetAccessorConstant(propertyIndex, pair2);
            }
        }
    }

    [Fact]
    public void ReconfigureDataFieldAttribute_AccConstantToAccFieldAfterTargetMap()
    {
        var config = new AccConstantToAccFieldConfig(CreateAccessorPair(true, true), CreateAccessorPair(true, true));
        // Currently we have a normalize case (IS_ACCESSOR_FIELD_SUPPORTED is false).
        TestReconfigureProperty_CustomPropertyAfterTargetMap(config, CheckNormalize);
    }

    sealed class AccConstantToDataFieldConfig(AccessorPair pair) : ICustomPropertyConfig
    {
        public Map AddPropertyAtBranch(int branchId, Expectations expectations, Map map)
        {
            Assert.True(branchId is 1 or 2);
            if (branchId == 1)
            {
                return expectations.AddAccessorConstant(map, PropertyAttributes.NONE, pair);
            }
            return expectations.AddDataField(map, PropertyAttributes.NONE, PropertyConstness.Const,
                Representation.Smi, FieldType.Any);
        }

        public void UpdateExpectations(int propertyIndex, Expectations expectations) { }
    }

    [Fact]
    public void ReconfigureDataFieldAttribute_AccConstantToDataFieldAfterTargetMap()
    {
        var config = new AccConstantToDataFieldConfig(CreateAccessorPair(true, true));
        // These are completely separate branches in transition tree.
        TestReconfigureProperty_CustomPropertyAfterTargetMap(config, CheckUnrelated);
    }
}
