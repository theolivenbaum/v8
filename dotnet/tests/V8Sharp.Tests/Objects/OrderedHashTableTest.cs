// Port of test/cctest/test-orderedhashtable.cc: the OrderedHashMap and
// OrderedHashSet tests.
//
// Not ported: the SmallOrderedHashSet/Map, OrderedNameDictionary and
// SmallOrderedNameDictionary tests and the *Handler* tests. V8 uses those
// small tables from CSA code and for swiss-dictionary-free builds of the
// property dictionary; V8Sharp's Map and Set always use OrderedHashMap and
// OrderedHashSet. Verify() (heap verification) has no counterpart.
namespace V8Sharp.Tests.Objects;

public class OrderedHashTableTest : TestWithContext
{
    static void CopyHashCode(JSReceiver from, JSReceiver to)
    {
        int hash = (int)ObjectOps.GetHash(from).Number;
        to.SetIdentityHash(hash);
    }

    // The Add/HasKey/Delete helpers abstract over OrderedHashMap and OrderedHashSet.
    static OrderedHashTable Add(Isolate isolate, OrderedHashTable table, JSString key, JSString value) =>
        table is OrderedHashMap map
            ? OrderedHashMap.Add(isolate, map, key, value)
            : OrderedHashSet.Add(isolate, (OrderedHashSet)table, key);

    static bool HasKey(Isolate isolate, OrderedHashTable table, JSValue key) =>
        OrderedHashTable.HasKey(isolate, table, key);

    static OrderedHashTable Delete(Isolate isolate, OrderedHashTable table, JSValue key)
    {
        OrderedHashTable.Delete(isolate, table, key);
        return table;
    }

    static void TestEmptyOrderedHashTable(Isolate isolate, Factory factory, OrderedHashTable table)
    {
        Assert.Equal(0, table.NumberOfElements);

        JSString key1 = factory.InternalizeString("key1");
        JSString value1 = factory.InternalizeString("value1");
        table = Add(isolate, table, key1, value1);
        Assert.Equal(1, table.NumberOfElements);
        Assert.True(HasKey(isolate, table, key1));

        JSString key2 = factory.InternalizeString("key2");
        JSString value2 = factory.InternalizeString("value2");
        Assert.False(HasKey(isolate, table, key2));
        table = Add(isolate, table, key2, value2);
        Assert.Equal(2, table.NumberOfElements);
        Assert.True(HasKey(isolate, table, key1));
        Assert.True(HasKey(isolate, table, key2));

        JSString key3 = factory.InternalizeString("key3");
        JSString value3 = factory.InternalizeString("value3");
        Assert.False(HasKey(isolate, table, key3));
        table = Add(isolate, table, key3, value3);
        Assert.Equal(3, table.NumberOfElements);
        Assert.True(HasKey(isolate, table, key1));
        Assert.True(HasKey(isolate, table, key2));
        Assert.True(HasKey(isolate, table, key3));

        JSString key4 = factory.InternalizeString("key4");
        JSString value4 = factory.InternalizeString("value4");
        Assert.False(HasKey(isolate, table, key4));
        table = Delete(isolate, table, key4);
        Assert.Equal(3, table.NumberOfElements);
        Assert.Equal(0, table.NumberOfDeletedElements);
        Assert.False(HasKey(isolate, table, key4));

        table = Add(isolate, table, key4, value4);
        Assert.Equal(4, table.NumberOfElements);
        Assert.Equal(0, table.NumberOfDeletedElements);
        Assert.True(HasKey(isolate, table, key4));

        Assert.True(HasKey(isolate, table, key4));
        table = Delete(isolate, table, key4);
        Assert.Equal(3, table.NumberOfElements);
        Assert.Equal(1, table.NumberOfDeletedElements);
        Assert.False(HasKey(isolate, table, key4));
    }

    [Fact]
    public void OrderedHashTableInsertion()
    {
        OrderedHashMap map = factory.NewOrderedHashMap();
        Assert.Equal(2, map.NumberOfBuckets);
        Assert.Equal(0, map.NumberOfElements);

        // Add a new key.
        JSValue key1 = JSValue.FromInt(1);
        JSValue value1 = JSValue.FromInt(1);
        Assert.False(OrderedHashMap.HasKey(i_isolate, map, key1));
        map = OrderedHashMap.Add(i_isolate, map, key1, value1);
        Assert.Equal(2, map.NumberOfBuckets);
        Assert.Equal(1, map.NumberOfElements);
        Assert.True(OrderedHashMap.HasKey(i_isolate, map, key1));

        // Add existing key.
        map = OrderedHashMap.Add(i_isolate, map, key1, value1);
        Assert.Equal(2, map.NumberOfBuckets);
        Assert.Equal(1, map.NumberOfElements);
        Assert.True(OrderedHashMap.HasKey(i_isolate, map, key1));

        JSString key2 = factory.NewStringFromAsciiChecked("foo");
        JSString value = factory.NewStringFromAsciiChecked("bar");
        Assert.False(OrderedHashMap.HasKey(i_isolate, map, key2));
        map = OrderedHashMap.Add(i_isolate, map, key2, value);
        Assert.Equal(2, map.NumberOfBuckets);
        Assert.Equal(2, map.NumberOfElements);
        Assert.True(OrderedHashMap.HasKey(i_isolate, map, key1));
        Assert.True(OrderedHashMap.HasKey(i_isolate, map, key2));

        map = OrderedHashMap.Add(i_isolate, map, key2, value);
        Assert.Equal(2, map.NumberOfBuckets);
        Assert.Equal(2, map.NumberOfElements);
        Assert.True(OrderedHashMap.HasKey(i_isolate, map, key1));
        Assert.True(OrderedHashMap.HasKey(i_isolate, map, key2));

        Symbol key3 = factory.NewSymbol();
        Assert.False(OrderedHashMap.HasKey(i_isolate, map, key3));
        map = OrderedHashMap.Add(i_isolate, map, key3, value);
        Assert.Equal(2, map.NumberOfBuckets);
        Assert.Equal(3, map.NumberOfElements);
        Assert.True(OrderedHashMap.HasKey(i_isolate, map, key1));
        Assert.True(OrderedHashMap.HasKey(i_isolate, map, key2));
        Assert.True(OrderedHashMap.HasKey(i_isolate, map, key3));

        map = OrderedHashMap.Add(i_isolate, map, key3, value);
        Assert.Equal(2, map.NumberOfBuckets);
        Assert.Equal(3, map.NumberOfElements);
        Assert.True(OrderedHashMap.HasKey(i_isolate, map, key1));
        Assert.True(OrderedHashMap.HasKey(i_isolate, map, key2));
        Assert.True(OrderedHashMap.HasKey(i_isolate, map, key3));

        JSValue key4 = factory.NewNumber(42.0);
        Assert.False(OrderedHashMap.HasKey(i_isolate, map, key4));
        map = OrderedHashMap.Add(i_isolate, map, key4, value);
        Assert.Equal(2, map.NumberOfBuckets);
        Assert.Equal(4, map.NumberOfElements);
        Assert.True(OrderedHashMap.HasKey(i_isolate, map, key1));
        Assert.True(OrderedHashMap.HasKey(i_isolate, map, key2));
        Assert.True(OrderedHashMap.HasKey(i_isolate, map, key3));
        Assert.True(OrderedHashMap.HasKey(i_isolate, map, key4));

        map = OrderedHashMap.Add(i_isolate, map, key4, value);
        Assert.Equal(2, map.NumberOfBuckets);
        Assert.Equal(4, map.NumberOfElements);
        Assert.True(OrderedHashMap.HasKey(i_isolate, map, key1));
        Assert.True(OrderedHashMap.HasKey(i_isolate, map, key2));
        Assert.True(OrderedHashMap.HasKey(i_isolate, map, key3));
        Assert.True(OrderedHashMap.HasKey(i_isolate, map, key4));
    }

    [Fact]
    public void OrderedHashMapDuplicateHashCode()
    {
        OrderedHashMap map = factory.NewOrderedHashMap();
        JSObject key1 = factory.NewJSObjectWithNullProto();
        JSObject value = factory.NewJSObjectWithNullProto();
        map = OrderedHashMap.Add(i_isolate, map, key1, value);
        Assert.Equal(2, map.NumberOfBuckets);
        Assert.Equal(1, map.NumberOfElements);
        Assert.True(OrderedHashMap.HasKey(i_isolate, map, key1));

        JSObject key2 = factory.NewJSObjectWithNullProto();
        CopyHashCode(key1, key2);

        map = OrderedHashMap.Add(i_isolate, map, key2, value);
        Assert.Equal(2, map.NumberOfBuckets);
        Assert.Equal(2, map.NumberOfElements);
        Assert.True(OrderedHashMap.HasKey(i_isolate, map, key1));
        Assert.True(OrderedHashMap.HasKey(i_isolate, map, key2));
    }

    [Fact]
    public void OrderedHashMapDeletion()
    {
        JSValue value1 = JSValue.FromInt(1);
        JSString value = factory.NewStringFromAsciiChecked("bar");

        OrderedHashMap map = factory.NewOrderedHashMap();
        Assert.Equal(2, map.NumberOfBuckets);
        Assert.Equal(0, map.NumberOfElements);
        Assert.Equal(0, map.NumberOfDeletedElements);

        // Delete from an empty hash table
        JSValue key1 = JSValue.FromInt(1);
        Assert.False(OrderedHashMap.Delete(i_isolate, map, key1));
        Assert.Equal(2, map.NumberOfBuckets);
        Assert.Equal(0, map.NumberOfElements);
        Assert.Equal(0, map.NumberOfDeletedElements);
        Assert.False(OrderedHashMap.HasKey(i_isolate, map, key1));

        map = OrderedHashMap.Add(i_isolate, map, key1, value1);
        Assert.Equal(2, map.NumberOfBuckets);
        Assert.Equal(1, map.NumberOfElements);
        Assert.Equal(0, map.NumberOfDeletedElements);
        Assert.True(OrderedHashMap.HasKey(i_isolate, map, key1));

        // Delete single existing key
        Assert.True(OrderedHashMap.Delete(i_isolate, map, key1));
        Assert.Equal(2, map.NumberOfBuckets);
        Assert.Equal(0, map.NumberOfElements);
        Assert.Equal(1, map.NumberOfDeletedElements);
        Assert.False(OrderedHashMap.HasKey(i_isolate, map, key1));

        map = OrderedHashMap.Add(i_isolate, map, key1, value1);
        Assert.Equal(2, map.NumberOfBuckets);
        Assert.Equal(1, map.NumberOfElements);
        Assert.Equal(1, map.NumberOfDeletedElements);
        Assert.True(OrderedHashMap.HasKey(i_isolate, map, key1));

        JSString key2 = factory.NewStringFromAsciiChecked("foo");
        Assert.False(OrderedHashMap.HasKey(i_isolate, map, key2));
        map = OrderedHashMap.Add(i_isolate, map, key2, value);
        Assert.Equal(2, map.NumberOfBuckets);
        Assert.Equal(2, map.NumberOfElements);
        Assert.Equal(1, map.NumberOfDeletedElements);
        Assert.True(OrderedHashMap.HasKey(i_isolate, map, key2));

        Symbol key3 = factory.NewSymbol();
        Assert.False(OrderedHashMap.HasKey(i_isolate, map, key3));
        map = OrderedHashMap.Add(i_isolate, map, key3, value);
        Assert.Equal(2, map.NumberOfBuckets);
        Assert.Equal(3, map.NumberOfElements);
        Assert.Equal(1, map.NumberOfDeletedElements);
        Assert.True(OrderedHashMap.HasKey(i_isolate, map, key1));
        Assert.True(OrderedHashMap.HasKey(i_isolate, map, key2));
        Assert.True(OrderedHashMap.HasKey(i_isolate, map, key3));

        // Delete multiple existing keys
        Assert.True(OrderedHashMap.Delete(i_isolate, map, key1));
        Assert.Equal(2, map.NumberOfBuckets);
        Assert.Equal(2, map.NumberOfElements);
        Assert.Equal(2, map.NumberOfDeletedElements);
        Assert.False(OrderedHashMap.HasKey(i_isolate, map, key1));
        Assert.True(OrderedHashMap.HasKey(i_isolate, map, key2));
        Assert.True(OrderedHashMap.HasKey(i_isolate, map, key3));

        Assert.True(OrderedHashMap.Delete(i_isolate, map, key2));
        Assert.Equal(2, map.NumberOfBuckets);
        Assert.Equal(1, map.NumberOfElements);
        Assert.Equal(3, map.NumberOfDeletedElements);
        Assert.False(OrderedHashMap.HasKey(i_isolate, map, key1));
        Assert.False(OrderedHashMap.HasKey(i_isolate, map, key2));
        Assert.True(OrderedHashMap.HasKey(i_isolate, map, key3));

        Assert.True(OrderedHashMap.Delete(i_isolate, map, key3));
        Assert.Equal(2, map.NumberOfBuckets);
        Assert.Equal(0, map.NumberOfElements);
        Assert.Equal(4, map.NumberOfDeletedElements);
        Assert.False(OrderedHashMap.HasKey(i_isolate, map, key1));
        Assert.False(OrderedHashMap.HasKey(i_isolate, map, key2));
        Assert.False(OrderedHashMap.HasKey(i_isolate, map, key3));

        // Delete non existent key from non new hash table
        Assert.False(OrderedHashMap.Delete(i_isolate, map, key3));
        Assert.Equal(2, map.NumberOfBuckets);
        Assert.Equal(0, map.NumberOfElements);
        Assert.Equal(4, map.NumberOfDeletedElements);
        Assert.False(OrderedHashMap.HasKey(i_isolate, map, key1));
        Assert.False(OrderedHashMap.HasKey(i_isolate, map, key2));
        Assert.False(OrderedHashMap.HasKey(i_isolate, map, key3));

        // Delete non existent key from non empty hash table
        map = OrderedHashMap.Shrink(i_isolate, map);
        map = OrderedHashMap.Add(i_isolate, map, key1, value);
        Assert.Equal(2, map.NumberOfBuckets);
        Assert.Equal(1, map.NumberOfElements);
        Assert.Equal(0, map.NumberOfDeletedElements);
        Assert.True(OrderedHashMap.HasKey(i_isolate, map, key1));
        Assert.False(OrderedHashMap.HasKey(i_isolate, map, key2));
        Assert.False(OrderedHashMap.HasKey(i_isolate, map, key3));
        Assert.False(OrderedHashMap.Delete(i_isolate, map, key2));
        Assert.Equal(2, map.NumberOfBuckets);
        Assert.Equal(1, map.NumberOfElements);
        Assert.Equal(0, map.NumberOfDeletedElements);
        Assert.True(OrderedHashMap.HasKey(i_isolate, map, key1));
        Assert.False(OrderedHashMap.HasKey(i_isolate, map, key2));
        Assert.False(OrderedHashMap.HasKey(i_isolate, map, key3));
    }

    [Fact]
    public void OrderedHashMapDuplicateHashCodeDeletion()
    {
        OrderedHashMap map = factory.NewOrderedHashMap();
        JSObject key1 = factory.NewJSObjectWithNullProto();
        JSObject value = factory.NewJSObjectWithNullProto();
        map = OrderedHashMap.Add(i_isolate, map, key1, value);
        Assert.Equal(2, map.NumberOfBuckets);
        Assert.Equal(1, map.NumberOfElements);
        Assert.Equal(0, map.NumberOfDeletedElements);
        Assert.True(OrderedHashMap.HasKey(i_isolate, map, key1));

        JSObject key2 = factory.NewJSObjectWithNullProto();
        CopyHashCode(key1, key2);

        // We shouldn't be able to delete the key!
        Assert.False(OrderedHashMap.Delete(i_isolate, map, key2));
        Assert.Equal(2, map.NumberOfBuckets);
        Assert.Equal(1, map.NumberOfElements);
        Assert.Equal(0, map.NumberOfDeletedElements);
        Assert.True(OrderedHashMap.HasKey(i_isolate, map, key1));
        Assert.False(OrderedHashMap.HasKey(i_isolate, map, key2));
    }

    [Fact]
    public void OrderedHashSetDeletion()
    {
        OrderedHashSet set = factory.NewOrderedHashSet();
        Assert.Equal(2, set.NumberOfBuckets);
        Assert.Equal(0, set.NumberOfElements);
        Assert.Equal(0, set.NumberOfDeletedElements);

        // Delete from an empty hash table
        JSValue key1 = JSValue.FromInt(1);
        Assert.False(OrderedHashSet.Delete(i_isolate, set, key1));
        Assert.Equal(2, set.NumberOfBuckets);
        Assert.Equal(0, set.NumberOfElements);
        Assert.Equal(0, set.NumberOfDeletedElements);
        Assert.False(OrderedHashSet.HasKey(i_isolate, set, key1));

        set = OrderedHashSet.Add(i_isolate, set, key1);
        Assert.Equal(2, set.NumberOfBuckets);
        Assert.Equal(1, set.NumberOfElements);
        Assert.Equal(0, set.NumberOfDeletedElements);
        Assert.True(OrderedHashSet.HasKey(i_isolate, set, key1));

        // Delete single existing key
        Assert.True(OrderedHashSet.Delete(i_isolate, set, key1));
        Assert.Equal(2, set.NumberOfBuckets);
        Assert.Equal(0, set.NumberOfElements);
        Assert.Equal(1, set.NumberOfDeletedElements);
        Assert.False(OrderedHashSet.HasKey(i_isolate, set, key1));

        set = OrderedHashSet.Add(i_isolate, set, key1);
        Assert.Equal(2, set.NumberOfBuckets);
        Assert.Equal(1, set.NumberOfElements);
        Assert.Equal(1, set.NumberOfDeletedElements);
        Assert.True(OrderedHashSet.HasKey(i_isolate, set, key1));

        JSString key2 = factory.NewStringFromAsciiChecked("foo");
        Assert.False(OrderedHashSet.HasKey(i_isolate, set, key2));
        set = OrderedHashSet.Add(i_isolate, set, key2);
        Assert.Equal(2, set.NumberOfBuckets);
        Assert.Equal(2, set.NumberOfElements);
        Assert.Equal(1, set.NumberOfDeletedElements);
        Assert.True(OrderedHashSet.HasKey(i_isolate, set, key2));

        Symbol key3 = factory.NewSymbol();
        Assert.False(OrderedHashSet.HasKey(i_isolate, set, key3));
        set = OrderedHashSet.Add(i_isolate, set, key3);
        Assert.Equal(2, set.NumberOfBuckets);
        Assert.Equal(3, set.NumberOfElements);
        Assert.Equal(1, set.NumberOfDeletedElements);
        Assert.True(OrderedHashSet.HasKey(i_isolate, set, key1));
        Assert.True(OrderedHashSet.HasKey(i_isolate, set, key2));
        Assert.True(OrderedHashSet.HasKey(i_isolate, set, key3));

        // Delete multiple existing keys
        Assert.True(OrderedHashSet.Delete(i_isolate, set, key1));
        Assert.Equal(2, set.NumberOfBuckets);
        Assert.Equal(2, set.NumberOfElements);
        Assert.Equal(2, set.NumberOfDeletedElements);
        Assert.False(OrderedHashSet.HasKey(i_isolate, set, key1));
        Assert.True(OrderedHashSet.HasKey(i_isolate, set, key2));
        Assert.True(OrderedHashSet.HasKey(i_isolate, set, key3));

        Assert.True(OrderedHashSet.Delete(i_isolate, set, key2));
        Assert.Equal(2, set.NumberOfBuckets);
        Assert.Equal(1, set.NumberOfElements);
        Assert.Equal(3, set.NumberOfDeletedElements);
        Assert.False(OrderedHashSet.HasKey(i_isolate, set, key1));
        Assert.False(OrderedHashSet.HasKey(i_isolate, set, key2));
        Assert.True(OrderedHashSet.HasKey(i_isolate, set, key3));

        Assert.True(OrderedHashSet.Delete(i_isolate, set, key3));
        Assert.Equal(2, set.NumberOfBuckets);
        Assert.Equal(0, set.NumberOfElements);
        Assert.Equal(4, set.NumberOfDeletedElements);
        Assert.False(OrderedHashSet.HasKey(i_isolate, set, key1));
        Assert.False(OrderedHashSet.HasKey(i_isolate, set, key2));
        Assert.False(OrderedHashSet.HasKey(i_isolate, set, key3));

        // Delete non existent key from non new hash table
        Assert.False(OrderedHashSet.Delete(i_isolate, set, key3));
        Assert.Equal(2, set.NumberOfBuckets);
        Assert.Equal(0, set.NumberOfElements);
        Assert.Equal(4, set.NumberOfDeletedElements);
        Assert.False(OrderedHashSet.HasKey(i_isolate, set, key1));
        Assert.False(OrderedHashSet.HasKey(i_isolate, set, key2));
        Assert.False(OrderedHashSet.HasKey(i_isolate, set, key3));

        // Delete non existent key from non empty hash table
        set = OrderedHashSet.Shrink(i_isolate, set);
        set = OrderedHashSet.Add(i_isolate, set, key1);
        Assert.Equal(2, set.NumberOfBuckets);
        Assert.Equal(1, set.NumberOfElements);
        Assert.Equal(0, set.NumberOfDeletedElements);
        Assert.True(OrderedHashSet.HasKey(i_isolate, set, key1));
        Assert.False(OrderedHashSet.HasKey(i_isolate, set, key2));
        Assert.False(OrderedHashSet.HasKey(i_isolate, set, key3));
        Assert.False(OrderedHashSet.Delete(i_isolate, set, key2));
        Assert.Equal(2, set.NumberOfBuckets);
        Assert.Equal(1, set.NumberOfElements);
        Assert.Equal(0, set.NumberOfDeletedElements);
        Assert.True(OrderedHashSet.HasKey(i_isolate, set, key1));
        Assert.False(OrderedHashSet.HasKey(i_isolate, set, key2));
        Assert.False(OrderedHashSet.HasKey(i_isolate, set, key3));
    }

    [Fact]
    public void OrderedHashSetDuplicateHashCodeDeletion()
    {
        OrderedHashSet set = factory.NewOrderedHashSet();
        JSObject key1 = factory.NewJSObjectWithNullProto();
        set = OrderedHashSet.Add(i_isolate, set, key1);
        Assert.Equal(2, set.NumberOfBuckets);
        Assert.Equal(1, set.NumberOfElements);
        Assert.Equal(0, set.NumberOfDeletedElements);
        Assert.True(OrderedHashSet.HasKey(i_isolate, set, key1));

        JSObject key2 = factory.NewJSObjectWithNullProto();
        CopyHashCode(key1, key2);

        // We shouldn't be able to delete the key!
        Assert.False(OrderedHashSet.Delete(i_isolate, set, key2));
        Assert.Equal(2, set.NumberOfBuckets);
        Assert.Equal(1, set.NumberOfElements);
        Assert.Equal(0, set.NumberOfDeletedElements);
        Assert.True(OrderedHashSet.HasKey(i_isolate, set, key1));
        Assert.False(OrderedHashSet.HasKey(i_isolate, set, key2));
    }

    [Fact]
    public void ZeroSizeOrderedHashMap()
    {
        JSValue key1 = JSValue.FromInt(1);
        JSValue value1 = JSValue.FromInt(1);

        OrderedHashMap empty = ReadOnlyRoots.empty_ordered_hash_map;
        {
            OrderedHashMap map = empty;

            Assert.Equal(0, map.NumberOfBuckets);
            Assert.Equal(0, map.NumberOfElements);
            Assert.False(OrderedHashMap.HasKey(i_isolate, map, key1));

            TestEmptyOrderedHashTable(i_isolate, factory, map);
        }
        {
            OrderedHashMap map = empty;

            map = OrderedHashMap.EnsureCapacityForAdding(i_isolate, map);

            Assert.True(map.NumberOfBuckets > 0);
            Assert.Equal(0, map.NumberOfElements);
        }
        {
            OrderedHashMap map = empty;

            Assert.True(map.FindEntry(i_isolate, key1).IsNotFound);

            TestEmptyOrderedHashTable(i_isolate, factory, map);
        }
        {
            OrderedHashMap map = empty;

            map = OrderedHashMap.Add(i_isolate, map, key1, value1);

            Assert.Equal(1, map.NumberOfElements);
            Assert.True(OrderedHashMap.HasKey(i_isolate, map, key1));
        }
        {
            OrderedHashMap map = empty;

            map = OrderedHashMap.Clear(i_isolate, map);

            TestEmptyOrderedHashTable(i_isolate, factory, map);
        }
        {
            OrderedHashMap map = empty;

            map = OrderedHashMap.Rehash(i_isolate, map);

            TestEmptyOrderedHashTable(i_isolate, factory, map);
        }
        {
            OrderedHashMap map = empty;

            map = OrderedHashMap.Shrink(i_isolate, map);

            TestEmptyOrderedHashTable(i_isolate, factory, map);
        }
        {
            OrderedHashMap map = empty;

            OrderedHashMap.Delete(i_isolate, map, key1);

            TestEmptyOrderedHashTable(i_isolate, factory, map);
        }
    }

    [Fact]
    public void ZeroSizeOrderedHashSet()
    {
        JSValue key1 = JSValue.FromInt(1);

        OrderedHashSet empty = ReadOnlyRoots.empty_ordered_hash_set;

        {
            OrderedHashSet set = empty;

            Assert.Equal(0, set.NumberOfBuckets);
            Assert.Equal(0, set.NumberOfElements);
            Assert.False(OrderedHashSet.HasKey(i_isolate, set, key1));

            TestEmptyOrderedHashTable(i_isolate, factory, set);
        }
        {
            OrderedHashSet set = empty;

            set = OrderedHashSet.EnsureCapacityForAdding(i_isolate, set);

            Assert.True(set.NumberOfBuckets > 0);
            Assert.Equal(0, set.NumberOfElements);
        }
        {
            OrderedHashSet set = empty;

            Assert.True(set.FindEntry(i_isolate, key1).IsNotFound);

            TestEmptyOrderedHashTable(i_isolate, factory, set);
        }
        {
            OrderedHashSet set = empty;

            set = OrderedHashSet.Add(i_isolate, set, key1);

            Assert.Equal(1, set.NumberOfElements);
            Assert.True(OrderedHashSet.HasKey(i_isolate, set, key1));
        }
        {
            OrderedHashSet set = empty;

            set = OrderedHashSet.Clear(i_isolate, set);

            TestEmptyOrderedHashTable(i_isolate, factory, set);
        }
        {
            OrderedHashSet set = empty;

            set = OrderedHashSet.Rehash(i_isolate, set);

            TestEmptyOrderedHashTable(i_isolate, factory, set);
        }
        {
            OrderedHashSet set = empty;

            set = OrderedHashSet.Shrink(i_isolate, set);

            TestEmptyOrderedHashTable(i_isolate, factory, set);
        }
        {
            OrderedHashSet set = empty;

            OrderedHashSet.Delete(i_isolate, set, key1);

            TestEmptyOrderedHashTable(i_isolate, factory, set);
        }
    }
}
