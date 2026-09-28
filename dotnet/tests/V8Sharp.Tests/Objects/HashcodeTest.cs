// Port of test/unittests/objects/hashcode-unittest.cc.
//
// The tests that set up objects with RunJS build the same objects through the
// object API instead (`var x = {}; x.a = 1` is AddProperty on a fresh
// Object-function instance), since V8Sharp's interpreter is a separate port.
//
// Deviation: V8 stores the identity hash in properties_or_hash (a Smi, the
// PropertyArray's hash field or the dictionary's hash slot). V8Sharp keeps it
// in a field of the receiver, so the checks of *where* the hash lives are
// replaced by checks that Object::GetHash keeps returning it across every
// transition. Not ported: HalfSipHashQuality (halfsiphash is not used by
// V8Sharp's hash tables).
namespace V8Sharp.Tests.Objects;

public class HashcodeTest : TestWithContext
{
    int AddToSetAndGetHash(JSObject obj, bool hasFastProperties)
    {
        Assert.Equal(hasFastProperties, obj.HasFastProperties);
        Assert.True(ObjectOps.GetHash(obj).IsUndefined);
        OrderedHashSet set = OrderedHashSet.Allocate(OrderedHashTable.kInitialCapacity, i_isolate);
        OrderedHashSet.Add(i_isolate, set, obj);
        Assert.Equal(hasFastProperties, obj.HasFastProperties);
        return (int)ObjectOps.GetHash(obj).Number;
    }

    void CheckFastObject(JSObject obj, int hash)
    {
        Assert.True(obj.HasFastProperties);
        Assert.True(PropertyArrayLength(obj) > 0);
        Assert.Equal(hash, (int)ObjectOps.GetHash(obj).Number);
    }

    void CheckDictionaryObject(JSObject obj, int hash)
    {
        Assert.False(obj.HasFastProperties);
        Assert.IsType<NameDictionary>(obj.PropertyDictionary);
        Assert.Equal(hash, (int)ObjectOps.GetHash(obj).Number);
    }

    JSObject NewObject() => factory.NewJSObject(i_isolate.NativeContext.ObjectFunction);

    void AddProperties(JSObject obj, params string[] names)
    {
        int i = 1;
        foreach (string name in names)
        {
            ObjectOps.SetProperty(i_isolate, obj, factory.InternalizeString(name), JSValue.FromInt(i++));
        }
    }

    [Fact]
    public void AddHashCodeToFastObjectWithoutProperties()
    {
        JSObject obj = NewObject();
        Assert.True(obj.HasFastProperties);

        int hash = AddToSetAndGetHash(obj, true);
        Assert.Equal(hash, (int)ObjectOps.GetHash(obj).Number);
    }

    [Fact]
    public void AddHashCodeToFastObjectWithInObjectProperties()
    {
        // var x = { a: 1};
        JSObject obj = NewObject();
        AddProperties(obj, "a");
        Assert.Equal(0, PropertyArrayLength(obj));

        int hash = AddToSetAndGetHash(obj, true);
        Assert.Equal(hash, (int)ObjectOps.GetHash(obj).Number);
    }

    [Fact]
    public void AddHashCodeToFastObjectWithPropertiesArray()
    {
        // var x = {}; x.a = 1; x.b = 2; x.c = 3; x.d = 4; x.e = 5;
        JSObject obj = NewObject();
        AddProperties(obj, "a", "b", "c", "d", "e");
        Assert.True(obj.HasFastProperties);

        int hash = AddToSetAndGetHash(obj, true);
        CheckFastObject(obj, hash);
    }

    [Fact]
    public void AddHashCodeToSlowObject()
    {
        JSObject obj = NewObject();
        Assert.True(obj.HasFastProperties);
        JSObject.NormalizeProperties(i_isolate, obj, PropertyNormalizationMode.CLEAR_INOBJECT_PROPERTIES, 0,
            "cctest/test-hashcode");
        Assert.False(obj.HasFastProperties);

        int hash = AddToSetAndGetHash(obj, false);
        CheckDictionaryObject(obj, hash);
    }

    [Fact]
    public void TransitionFastWithInObjectToFastWithPropertyArray()
    {
        // var x = { }; x.a = 1; x.b = 2; x.c = 3; x.d = 4;
        JSObject obj = NewObject();
        AddProperties(obj, "a", "b", "c", "d");
        Assert.True(obj.HasFastProperties);

        int hash = AddToSetAndGetHash(obj, true);
        Assert.Equal(hash, (int)ObjectOps.GetHash(obj).Number);

        int length = PropertyArrayLength(obj);
        AddProperties(obj, "e");
        Assert.True(PropertyArrayLength(obj) > length);
        CheckFastObject(obj, hash);
    }

    [Fact]
    public void TransitionFastWithPropertyArray()
    {
        // var x = { }; x.a = 1; x.b = 2; x.c = 3; x.d = 4; x.e = 5;
        JSObject obj = NewObject();
        AddProperties(obj, "a", "b", "c", "d", "e");
        Assert.True(PropertyArrayLength(obj) > 0);

        int hash = AddToSetAndGetHash(obj, true);

        int length = PropertyArrayLength(obj);
        AddProperties(obj, "f", "g", "h");
        Assert.True(PropertyArrayLength(obj) > length);
        CheckFastObject(obj, hash);
    }

    [Fact]
    public void TransitionFastWithPropertyArrayToSlow()
    {
        JSObject obj = NewObject();
        AddProperties(obj, "a", "b", "c", "d", "e");
        Assert.True(PropertyArrayLength(obj) > 0);

        int hash = AddToSetAndGetHash(obj, true);
        Assert.True(PropertyArrayLength(obj) > 0);

        JSObject.NormalizeProperties(i_isolate, obj, PropertyNormalizationMode.KEEP_INOBJECT_PROPERTIES, 0,
            "cctest/test-hashcode");
        CheckDictionaryObject(obj, hash);
    }

    [Fact]
    public void TransitionSlowToSlow()
    {
        JSObject obj = NewObject();
        JSObject.NormalizeProperties(i_isolate, obj, PropertyNormalizationMode.CLEAR_INOBJECT_PROPERTIES, 0,
            "cctest/test-hashcode");
        Assert.False(obj.HasFastProperties);

        int hash = AddToSetAndGetHash(obj, false);

        int length = obj.PropertyDictionary.Capacity;
        // for(var i = 0; i < 10; i++) { x['f'+i] = i };
        for (int i = 0; i < 10; i++) AddProperties(obj, "f" + i);
        Assert.True(obj.PropertyDictionary.Capacity > length);
        CheckDictionaryObject(obj, hash);
    }

    [Fact]
    public void TransitionSlowToFastWithoutProperties()
    {
        JSObject obj = NewObject();
        JSObject.NormalizeProperties(i_isolate, obj, PropertyNormalizationMode.CLEAR_INOBJECT_PROPERTIES, 0,
            "cctest/test-hashcode");
        Assert.False(obj.HasFastProperties);

        int hash = AddToSetAndGetHash(obj, false);

        JSObject.MigrateSlowToFast(i_isolate, obj, 0, "cctest/test-hashcode");
        Assert.Equal(hash, (int)ObjectOps.GetHash(obj).Number);
    }

    [Fact]
    public void TransitionSlowToFastWithPropertyArray()
    {
        // var x = Object.create(null); for(var i = 0; i < 10; i++) { x['f'+i] = i };
        JSObject obj = factory.NewSlowJSObjectWithNullProto();
        for (int i = 0; i < 10; i++) AddProperties(obj, "f" + i);
        Assert.False(obj.HasFastProperties);

        int hash = AddToSetAndGetHash(obj, false);

        JSObject.MigrateSlowToFast(i_isolate, obj, 0, "cctest/test-hashcode");
        CheckFastObject(obj, hash);
    }

    static void TestIntegerHashQuality(int samplesLog2, int numBucketsLog2, ulong seed, double maxVar,
        Func<uint, ulong, uint> hashFunction)
    {
        int samples = 1 << samplesLog2;
        int numBuckets = 1 << numBucketsLog2;
        int mean = samples / numBuckets;
        int[] buckets = new int[numBuckets];

        for (int i = 0; i < samples; i++)
        {
            uint hash = hashFunction((uint)i, seed);
            buckets[hash % numBuckets]++;
        }

        int sumDeviation = 0;
        for (int i = 0; i < numBuckets; i++)
        {
            int deviation = Math.Abs(buckets[i] - mean);
            sumDeviation += deviation * deviation;
        }

        double variationCoefficient = Math.Sqrt(sumDeviation * 1.0 / numBuckets) / mean;
        Assert.True(variationCoefficient < maxVar,
            $"samples: 1 << {samplesLog2}, buckets: 1 << {numBucketsLog2}, var_coeff: {variationCoefficient:F3}");
    }

    static void TestIntegerHashQuality(Func<uint, ulong, uint> hashFunction)
    {
        TestIntegerHashQuality(17, 13, 0x123456789ABCDEFUL, 0.4, hashFunction);
        TestIntegerHashQuality(16, 12, 0x123456789ABCDEFUL, 0.4, hashFunction);
        TestIntegerHashQuality(15, 11, 0xFEDCBA987654321UL, 0.4, hashFunction);
        TestIntegerHashQuality(14, 10, 0xFEDCBA987654321UL, 0.4, hashFunction);
        TestIntegerHashQuality(13, 9, 1, 0.4, hashFunction);
        TestIntegerHashQuality(12, 8, 1, 0.4, hashFunction);

        TestIntegerHashQuality(17, 10, 0x123456789ABCDEFUL, 0.2, hashFunction);
        TestIntegerHashQuality(16, 9, 0x123456789ABCDEFUL, 0.2, hashFunction);
        TestIntegerHashQuality(15, 8, 0xFEDCBA987654321UL, 0.2, hashFunction);
        TestIntegerHashQuality(14, 7, 0xFEDCBA987654321UL, 0.2, hashFunction);
        TestIntegerHashQuality(13, 6, 1, 0.2, hashFunction);
        TestIntegerHashQuality(12, 5, 1, 0.2, hashFunction);
    }

    [Fact]
    public void SeededHashQuality() => TestIntegerHashQuality(Hashing.ComputeSeededHash);
}
