// Port of test/unittests/objects/dictionary-unittest.cc.
//
// Not ported: the GC checks (InvokeMinorGC, ObjectHashTableCausesGC: objects
// never move in V8Sharp) and MaximumClonedShallowObjectProperties (there is
// no large-object space to stay out of).
namespace V8Sharp.Tests.Objects;

public class DictionaryTest : TestWithContext
{
    JSArray NewJSArray(int capacity) =>
        factory.NewJSArray(ElementsKind.PACKED_ELEMENTS, 0, capacity);

    [Fact]
    public void HashMap()
    {
        ObjectHashTable table = ObjectHashTable.New(23);

        JSObject a = NewJSArray(7);
        JSObject b = NewJSArray(11);
        table = ObjectHashTable.Put(i_isolate, table, a, b);
        Assert.Equal(1, table.NumberOfElements);
        Assert.Same(b, table.Lookup(i_isolate, a).Object);
        // When the key does not exist in the map, Lookup returns the hole.
        Assert.True(table.Lookup(i_isolate, b).IsTheHole);

        // Keys that are overwritten should not change number of elements.
        table = ObjectHashTable.Put(i_isolate, table, a, NewJSArray(13));
        Assert.Equal(1, table.NumberOfElements);
        Assert.NotSame(b, table.Lookup(i_isolate, a).Object);

        // Keys that have been removed are mapped to the hole.
        table = ObjectHashTable.Remove(i_isolate, table, a, out bool wasPresent);
        Assert.True(wasPresent);
        Assert.Equal(0, table.NumberOfElements);
        Assert.True(table.Lookup(i_isolate, a).IsTheHole);

        // Keys should map back to their respective values and also should get
        // an identity hash code generated.
        for (int i = 0; i < 100; i++)
        {
            JSReceiver key = NewJSArray(7);
            JSObject value = NewJSArray(11);
            table = ObjectHashTable.Put(i_isolate, table, key, value);
            Assert.Equal(i + 1, table.NumberOfElements);
            Assert.True(table.FindEntry(i_isolate, key).IsFound);
            Assert.Same(value, table.Lookup(i_isolate, key).Object);
            Assert.True(key.GetIdentityHash().IsSmi);
        }

        // Keys never added to the map which already have an identity hash
        // code should not be found.
        for (int i = 0; i < 100; i++)
        {
            JSReceiver key = NewJSArray(7);
            key.GetOrCreateIdentityHash(i_isolate);
            Assert.True(key.GetIdentityHash().IsSmi);
            Assert.True(table.FindEntry(i_isolate, key).IsNotFound);
            Assert.True(table.Lookup(i_isolate, key).IsTheHole);
            Assert.True(key.GetIdentityHash().IsSmi);
        }

        // Keys that don't have an identity hash should not be found and also
        // should not get an identity hash code generated.
        for (int i = 0; i < 100; i++)
        {
            JSReceiver key = NewJSArray(7);
            Assert.True(table.Lookup(i_isolate, key).IsTheHole);
            Assert.True(key.GetIdentityHash().IsUndefined);
        }
    }

    [Fact]
    public void HashSet()
    {
        ObjectHashSet table = ObjectHashSet.New(23);

        JSObject a = NewJSArray(7);
        JSObject b = NewJSArray(11);
        table = ObjectHashSet.Add(i_isolate, table, a);
        Assert.Equal(1, table.NumberOfElements);
        Assert.True(table.Has(i_isolate, a));
        Assert.False(table.Has(i_isolate, b));

        // Keys that are overwritten should not change number of elements.
        table = ObjectHashSet.Add(i_isolate, table, a);
        Assert.Equal(1, table.NumberOfElements);
        Assert.True(table.Has(i_isolate, a));
        Assert.False(table.Has(i_isolate, b));

        // Keys should map back to their respective values and also should get
        // an identity hash code generated.
        for (int i = 0; i < 100; i++)
        {
            JSReceiver key = NewJSArray(7);
            table = ObjectHashSet.Add(i_isolate, table, key);
            Assert.Equal(i + 2, table.NumberOfElements);
            Assert.True(table.Has(i_isolate, key));
            Assert.True(key.GetIdentityHash().IsSmi);
        }

        // Keys never added to the map which already have an identity hash
        // code should not be found.
        for (int i = 0; i < 100; i++)
        {
            JSReceiver key = NewJSArray(7);
            key.GetOrCreateIdentityHash(i_isolate);
            Assert.True(key.GetIdentityHash().IsSmi);
            Assert.False(table.Has(i_isolate, key));
            Assert.True(key.GetIdentityHash().IsSmi);
        }

        // Keys that don't have an identity hash should not be found and also
        // should not get an identity hash code generated.
        for (int i = 0; i < 100; i++)
        {
            JSReceiver key = NewJSArray(7);
            Assert.False(table.Has(i_isolate, key));
            Assert.True(key.GetIdentityHash().IsUndefined);
        }
    }

    [Fact]
    public void HashTableRehash()
    {
        // Test almost filled table.
        {
            ObjectHashTable table = ObjectHashTable.New(100);
            int capacity = table.Capacity;
            for (int i = 0; i < capacity - 1; i++)
            {
                table.SetEntry(new InternalIndex(i), JSValue.FromInt(i * i), JSValue.FromInt(i));
            }
            table.Rehash();
            for (int i = 0; i < capacity - 1; i++)
            {
                Assert.Equal(i, table.Lookup(i_isolate, JSValue.FromInt(i * i)).Number);
            }
        }
        // Test half-filled table.
        {
            ObjectHashTable table = ObjectHashTable.New(100);
            int capacity = table.Capacity;
            for (int i = 0; i < capacity / 2; i++)
            {
                table.SetEntry(new InternalIndex(i), JSValue.FromInt(i * i), JSValue.FromInt(i));
            }
            table.Rehash();
            for (int i = 0; i < capacity / 2; i++)
            {
                Assert.Equal(i, table.Lookup(i_isolate, JSValue.FromInt(i * i)).Number);
            }
        }
    }
}
