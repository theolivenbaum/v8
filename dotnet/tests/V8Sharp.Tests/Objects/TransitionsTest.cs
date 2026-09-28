// Port of test/cctest/test-transitions.cc (and the TestTransitionsAccessor of
// test-transitions.h).
//
// Not ported: TransitionArray_InsertToBinarySearchSizeAfterRehashing and
// TransitionArray_RehashNoDuplicatesWithinLinearSearchSize. Both build a
// snapshot and deserialize it with another hash seed; V8Sharp has no
// snapshot and no hash seed.
namespace V8Sharp.Tests.Objects;

public class TransitionsTest : TestWithContext
{
    Map CopyWithField(Map map, Name name, PropertyAttributes attributes) =>
        Map.CopyWithField(i_isolate, map, name, FieldType.Any, attributes, PropertyConstness.Mutable,
            Representation.Tagged, TransitionFlag.OMIT_TRANSITION)!;

    static PropertyAttributes PropertyAttributesFromInt(int value) => (PropertyAttributes)value;

    [Fact]
    public void TransitionArray_SimpleFieldTransitions()
    {
        JSString name1 = factory.InternalizeString("foo");
        JSString name2 = factory.InternalizeString("bar");
        const PropertyAttributes attributes = PropertyAttributes.NONE;

        Map map0 = Map.Create(i_isolate, 0);
        Map map1 = CopyWithField(map0, name1, attributes);
        Map map2 = CopyWithField(map0, name2, attributes);

        Assert.Null(map0.RawTransitions);

        TransitionsAccessor.Insert(i_isolate, map0, name1, map1, TransitionKindFlag.SIMPLE_PROPERTY_TRANSITION);
        {
            var transitions = new TransitionsAccessor(i_isolate, map0);
            Assert.Equal(TransitionsAccessor.Encoding.WeakRef, transitions.encoding);
            Assert.Same(map1, transitions.SearchTransition(name1, PropertyKind.Data, attributes));
            Assert.Equal(1, transitions.NumberOfTransitions());
            Assert.Same(name1, transitions.GetKey(0));
            Assert.Same(map1, transitions.GetTarget(0));
        }

        TransitionsAccessor.Insert(i_isolate, map0, name2, map2, TransitionKindFlag.SIMPLE_PROPERTY_TRANSITION);
        {
            var transitions = new TransitionsAccessor(i_isolate, map0);
            Assert.Equal(TransitionsAccessor.Encoding.FullTransitionArray, transitions.encoding);

            Assert.Same(map1, transitions.SearchTransition(name1, PropertyKind.Data, attributes));
            Assert.Same(map2, transitions.SearchTransition(name2, PropertyKind.Data, attributes));
            Assert.Equal(2, transitions.NumberOfTransitions());
            for (int i = 0; i < 2; i++)
            {
                Name key = transitions.GetKey(i);
                Map target = transitions.GetTarget(i);
                Assert.True((ReferenceEquals(key, name1) && ReferenceEquals(target, map1)) ||
                            (ReferenceEquals(key, name2) && ReferenceEquals(target, map2)));
            }

            Assert.True(transitions.TransitionArrayForTesting.IsSortedNoDuplicates());
        }
    }

    [Fact]
    public void TransitionArray_FullFieldTransitions()
    {
        JSString name1 = factory.InternalizeString("foo");
        JSString name2 = factory.InternalizeString("bar");
        const PropertyAttributes attributes = PropertyAttributes.NONE;

        Map map0 = Map.Create(i_isolate, 0);
        Map map1 = CopyWithField(map0, name1, attributes);
        Map map2 = CopyWithField(map0, name2, attributes);

        Assert.Null(map0.RawTransitions);

        TransitionsAccessor.Insert(i_isolate, map0, name1, map1, TransitionKindFlag.PROPERTY_TRANSITION);
        {
            var transitions = new TransitionsAccessor(i_isolate, map0);
            Assert.Equal(TransitionsAccessor.Encoding.FullTransitionArray, transitions.encoding);
            Assert.Same(map1, transitions.SearchTransition(name1, PropertyKind.Data, attributes));
            Assert.Equal(1, transitions.NumberOfTransitions());
            Assert.Same(name1, transitions.GetKey(0));
            Assert.Same(map1, transitions.GetTarget(0));
        }

        TransitionsAccessor.Insert(i_isolate, map0, name2, map2, TransitionKindFlag.PROPERTY_TRANSITION);
        {
            var transitions = new TransitionsAccessor(i_isolate, map0);
            Assert.Equal(TransitionsAccessor.Encoding.FullTransitionArray, transitions.encoding);

            Assert.Same(map1, transitions.SearchTransition(name1, PropertyKind.Data, attributes));
            Assert.Same(map2, transitions.SearchTransition(name2, PropertyKind.Data, attributes));
            Assert.Equal(2, transitions.NumberOfTransitions());
            for (int i = 0; i < 2; i++)
            {
                Name key = transitions.GetKey(i);
                Map target = transitions.GetTarget(i);
                Assert.True((ReferenceEquals(key, name1) && ReferenceEquals(target, map1)) ||
                            (ReferenceEquals(key, name2) && ReferenceEquals(target, map2)));
            }

            Assert.True(transitions.TransitionArrayForTesting.IsSortedNoDuplicates());
        }
    }

    [Fact]
    public void TransitionArray_DifferentFieldNames()
    {
        const int PROPS_COUNT = 10;
        var names = new JSString[PROPS_COUNT];
        var maps = new Map[PROPS_COUNT];
        const PropertyAttributes attributes = PropertyAttributes.NONE;

        Map map0 = Map.Create(i_isolate, 0);
        Assert.Null(map0.RawTransitions);

        for (int i = 0; i < PROPS_COUNT; i++)
        {
            JSString name = factory.InternalizeString("prop" + i);
            Map map = CopyWithField(map0, name, attributes);
            names[i] = name;
            maps[i] = map;

            TransitionsAccessor.Insert(i_isolate, map0, name, map, TransitionKindFlag.PROPERTY_TRANSITION);
        }

        var transitions = new TransitionsAccessor(i_isolate, map0);
        for (int i = 0; i < PROPS_COUNT; i++)
        {
            Assert.Same(maps[i], transitions.SearchTransition(names[i], PropertyKind.Data, attributes));
        }
        for (int i = 0; i < PROPS_COUNT; i++)
        {
            Name key = transitions.GetKey(i);
            Map target = transitions.GetTarget(i);
            for (int j = 0; j < PROPS_COUNT; j++)
            {
                if (ReferenceEquals(names[i], key))
                {
                    Assert.Same(maps[i], target);
                    break;
                }
            }
        }

        Assert.True(transitions.TransitionArrayForTesting.IsSortedNoDuplicates());
    }

    [Fact]
    public void TransitionArray_SameFieldNamesDifferentAttributesSimple()
    {
        Map map0 = Map.Create(i_isolate, 0);
        Assert.Null(map0.RawTransitions);

        const int ATTRS_COUNT = (int)(PropertyAttributes.READ_ONLY | PropertyAttributes.DONT_ENUM | PropertyAttributes.DONT_DELETE) + 1;
        Assert.Equal(8, ATTRS_COUNT);
        var attrMaps = new Map[ATTRS_COUNT];
        JSString name = factory.InternalizeString("foo");

        // Add transitions for same field name but different attributes.
        for (int i = 0; i < ATTRS_COUNT; i++)
        {
            PropertyAttributes attributes = PropertyAttributesFromInt(i);
            Map map = CopyWithField(map0, name, attributes);
            attrMaps[i] = map;

            TransitionsAccessor.Insert(i_isolate, map0, name, map, TransitionKindFlag.PROPERTY_TRANSITION);
        }

        // Ensure that transitions for |name| field are valid.
        var transitions = new TransitionsAccessor(i_isolate, map0);
        for (int i = 0; i < ATTRS_COUNT; i++)
        {
            PropertyAttributes attributes = PropertyAttributesFromInt(i);
            Assert.Same(attrMaps[i], transitions.SearchTransition(name, PropertyKind.Data, attributes));
            // All transitions use the same key, so this check doesn't need to
            // care about ordering.
            Assert.Same(name, transitions.GetKey(i));
        }

        Assert.True(transitions.TransitionArrayForTesting.IsSortedNoDuplicates());
    }

    [Fact]
    public void TransitionArray_SameFieldNamesDifferentAttributes()
    {
        const int PROPS_COUNT = 10;
        var names = new JSString[PROPS_COUNT];
        var maps = new Map[PROPS_COUNT];

        Map map0 = Map.Create(i_isolate, 0);
        Assert.Null(map0.RawTransitions);

        // Some number of fields.
        for (int i = 0; i < PROPS_COUNT; i++)
        {
            JSString propName = factory.InternalizeString("prop" + i);
            Map map = CopyWithField(map0, propName, PropertyAttributes.NONE);
            names[i] = propName;
            maps[i] = map;

            TransitionsAccessor.Insert(i_isolate, map0, propName, map, TransitionKindFlag.PROPERTY_TRANSITION);
        }

        const int ATTRS_COUNT = (int)(PropertyAttributes.READ_ONLY | PropertyAttributes.DONT_ENUM | PropertyAttributes.DONT_DELETE) + 1;
        var attrMaps = new Map[ATTRS_COUNT];
        JSString name = factory.InternalizeString("foo");

        // Add transitions for same field name but different attributes.
        for (int i = 0; i < ATTRS_COUNT; i++)
        {
            PropertyAttributes attributes = PropertyAttributesFromInt(i);
            Map map = CopyWithField(map0, name, attributes);
            attrMaps[i] = map;

            TransitionsAccessor.Insert(i_isolate, map0, name, map, TransitionKindFlag.PROPERTY_TRANSITION);
        }

        // Ensure that transitions for |name| field are valid.
        var transitions = new TransitionsAccessor(i_isolate, map0);
        for (int i = 0; i < ATTRS_COUNT; i++)
        {
            PropertyAttributes attr = PropertyAttributesFromInt(i);
            Assert.Same(attrMaps[i], transitions.SearchTransition(name, PropertyKind.Data, attr));
        }

        // Ensure that info about the other fields still valid.
        Assert.Equal(PROPS_COUNT + ATTRS_COUNT, transitions.NumberOfTransitions());
        for (int i = 0; i < PROPS_COUNT + ATTRS_COUNT; i++)
        {
            Name key = transitions.GetKey(i);
            Map target = transitions.GetTarget(i);
            if (ReferenceEquals(key, name))
            {
                // Attributes transition.
                PropertyAttributes attributes = target.GetLastDescriptorDetails().Attributes;
                Assert.Same(attrMaps[(int)attributes], target);
            }
            else
            {
                for (int j = 0; j < PROPS_COUNT; j++)
                {
                    if (ReferenceEquals(names[j], key))
                    {
                        Assert.Same(maps[j], target);
                        break;
                    }
                }
            }
        }

        Assert.True(transitions.TransitionArrayForTesting.IsSortedNoDuplicates());
    }

    // LinearSearchName should work correctly even when the underlying
    // TransitionArray is not in hash-sorted order.
    [Fact]
    public void TransitionArray_LinearSearchHandlesUnsortedArray()
    {
        // Keep the number of transitions small to ensure linear search is used.
        const int kCount = TransitionArray.kMaxElementsForLinearSearch / 2;
        Map map0 = Map.Create(i_isolate, 0);

        var names = new JSString[kCount];
        for (int i = 0; i < kCount; i++)
        {
            JSString name = factory.InternalizeString("prop" + i);
            Map next = CopyWithField(map0, name, PropertyAttributes.NONE);
            TransitionsAccessor.Insert(i_isolate, map0, name, next, TransitionKindFlag.PROPERTY_TRANSITION);
            names[i] = name;
        }

        // Sort by hash, then reverse so the array is no longer in increasing order.
        {
            var transitions = new TransitionsAccessor(i_isolate, map0);
            Assert.Equal(kCount, transitions.NumberOfTransitions());
            TransitionArray array = transitions.TransitionArrayForTesting;
            array.Sort(force: true);

            for (int i = 0, j = kCount - 1; i < j; i++, j--)
            {
                Name ki = array.GetKey(i);
                Name kj = array.GetKey(j);
                Map? ti = array.GetRawTarget(i);
                Map? tj = array.GetRawTarget(j);
                array.SetKey(i, kj);
                array.SetRawTarget(i, tj);
                array.SetKey(j, ki);
                array.SetRawTarget(j, ti);
            }

            // Verify that the array is no longer in increasing hash order.
            // Use >= to allow for hash collisions.
            for (int i = 0; i + 1 < kCount; i++)
            {
                Assert.True(array.GetKey(i).EnsureHash() >= array.GetKey(i + 1).EnsureHash());
            }
        }

        // Every transition must still be findable.
        {
            var transitions = new TransitionsAccessor(i_isolate, map0);
            TransitionArray array = transitions.TransitionArrayForTesting;
            for (int i = 0; i < kCount; i++)
            {
                int idx = array.SearchNameForTesting(names[i]);
                Assert.NotEqual(TransitionArray.kNotFound, idx);
                Assert.Same(names[i], array.GetKey(idx));
            }
        }

        // Re-inserting any existing key with a fresh target must overwrite in place
        // rather than create a duplicate.
        for (int i = 0; i < kCount; i++)
        {
            Map fresh = CopyWithField(map0, names[i], PropertyAttributes.NONE);
            TransitionsAccessor.Insert(i_isolate, map0, names[i], fresh, TransitionKindFlag.PROPERTY_TRANSITION);

            var transitions = new TransitionsAccessor(i_isolate, map0);
            Assert.Equal(kCount, transitions.NumberOfTransitions());
        }
    }
}
