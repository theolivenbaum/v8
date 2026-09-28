// Tests of the Map, Set, WeakMap and WeakSet builtins (Builtins.Collections.cs,
// Builtins.Set.cs) and of OrderedHashTable iteration across mutation. Expected
// messages were recorded with the oracle from the equivalent scripts.
namespace V8Sharp.Tests.Builtins;

/// <summary>Helpers shared by the collection, iterator and disposable stack tests.</summary>
public abstract class IterationBuiltinsTest : CoreBuiltinsTest
{
    /// <summary>Steps <paramref name="iterator"/> to the end, joining the values with "|".</summary>
    protected string Drain(JSValue iterator, int limit = int.MaxValue)
    {
        var parts = new List<string>();
        while (parts.Count < limit)
        {
            JSValue result = CallMethod(iterator, "next");
            if (ObjectOps.BooleanValue(Get(result, "done"))) break;
            parts.Add(S(Get(result, "value")));
        }
        return string.Join("|", parts);
    }

    protected JSValue Pair(JSValue a, JSValue b) => Arr(a, b);

    /// <summary>An iterator over <paramref name="values"/> (from <see cref="CoreBuiltinsTest.Iterable"/>).</summary>
    protected JSValue Iter(params JSValue[] values)
    {
        JSObject iterable = Iterable(values);
        JSValue iterator = Execution.Call(i_isolate, Get(iterable, ReadOnlyRoots.iterator_symbol), iterable, []);
        // Inherit from %Iterator.prototype% so that the helpers are reachable.
        JSObject.SetPrototype(i_isolate, iterator.As<JSReceiver>(), NC.InitialIteratorPrototype, false, ShouldThrow.ThrowOnError);
        return iterator;
    }

}

public class CollectionsBuiltinsTest : IterationBuiltinsTest
{
    [Fact]
    public void MapBasics()
    {
        JSValue m = New("Map");
        CallMethod(m, "set", Num(1), Str("a"));
        CallMethod(m, "set", Str("1"), Str("b"));
        CallMethod(m, "set", Num(-0.0), Str("z"));
        CallMethod(m, "set", Num(double.NaN), Str("nan"));
        Assert.Equal("4", S(Get(m, "size")));
        Assert.Equal("a", S(CallMethod(m, "get", Num(1))));
        Assert.Equal("b", S(CallMethod(m, "get", Str("1"))));
        Assert.Equal("z", S(CallMethod(m, "get", Num(0))));
        Assert.Equal("nan", S(CallMethod(m, "get", Num(double.NaN))));
        Assert.Equal("undefined", S(CallMethod(m, "get", Num(2))));
        Assert.True(CallMethod(m, "has", Num(0)).IsTrue);
        // -0 is normalized to +0 when stored.
        Assert.Equal("1|1|0|NaN", Drain(CallMethod(m, "keys")));
        Assert.True(CallMethod(m, "delete", Num(1)).IsTrue);
        Assert.False(CallMethod(m, "delete", Num(1)).IsTrue);
        Assert.Equal("1,b|0,z|NaN,nan", Drain(CallMethod(m, "entries")));
        CallMethod(m, "clear");
        Assert.Equal("0", S(Get(m, "size")));
    }

    [Fact]
    public void MapConstructor()
    {
        JSValue m = New("Map", Arr(Pair(Num(1), Str("a")), Pair(Num(2), Str("b"))));
        Assert.Equal("1,a|2,b", Drain(CallMethod(m, "entries")));
        JSValue m2 = New("Map", Iterable(Pair(Num(3), Str("c"))));
        Assert.Equal("c", S(CallMethod(m2, "get", Num(3))));
        JSValue copy = New("Map", m);
        Assert.Equal("1,a|2,b", Drain(CallMethod(copy, "entries")));

        Assert.Equal("TypeError: Constructor Map requires 'new'", Throws(() => Call("Map")));
        Assert.Equal("TypeError: number 1 is not iterable (cannot read property Symbol(Symbol.iterator))",
            Throws(() => New("Map", Num(1))));
        Assert.Equal("TypeError: Iterator value 1 is not an entry object", Throws(() => New("Map", Iterable(Num(1)))));
        Assert.Equal("TypeError: Method Map.prototype.get called on incompatible receiver #<Object>",
            Throws(() => CallOn("Map.prototype.get", Obj(), Num(1))));
    }

    [Fact]
    public void SetBasics()
    {
        JSValue s = New("Set", Arr(Num(1), Num(2), Num(2), Num(-0.0)));
        Assert.Equal("3", S(Get(s, "size")));
        Assert.Equal("1|2|0", Drain(CallMethod(s, "values")));
        Assert.Equal("1,1|2,2|0,0", Drain(CallMethod(s, "entries")));
        Assert.True(CallMethod(s, "has", Num(0)).IsTrue);
        Assert.True(CallMethod(s, "delete", Num(2)).IsTrue);
        Assert.Equal("1|0", Drain(CallMethod(s, "keys")));
        // Set.prototype.keys === Set.prototype.values.
        Assert.Same(G("Set.prototype.keys").Object, G("Set.prototype.values").Object);
    }

    [Fact]
    public void IteratorsSurviveDeletionAndAddition()
    {
        JSValue m = New("Map", Arr(Pair(Num(1), Str("a")), Pair(Num(2), Str("b")), Pair(Num(3), Str("c"))));
        JSValue it = CallMethod(m, "keys");
        Assert.Equal("1", Drain(it, 1));
        CallMethod(m, "delete", Num(2));
        CallMethod(m, "set", Num(4), Str("d"));
        Assert.Equal("3|4", Drain(it));
        // An exhausted iterator stays exhausted.
        CallMethod(m, "set", Num(5), Str("e"));
        Assert.Equal("", Drain(it));
    }

    [Fact]
    public void IteratorsSurviveClear()
    {
        JSValue m = New("Map", Arr(Pair(Num(1), Str("a")), Pair(Num(2), Str("b"))));
        JSValue it = CallMethod(m, "entries");
        Assert.Equal("1,a", Drain(it, 1));
        CallMethod(m, "clear");
        CallMethod(m, "set", Num(5), Str("e"));
        Assert.Equal("5,e", Drain(it));
    }

    [Fact]
    public void IteratorsSurviveRehash()
    {
        // Adding during iteration grows (rehashes) the table several times;
        // every element added is still visited.
        JSValue s = New("Set", Arr(Num(0)));
        JSValue it = CallMethod(s, "values");
        int count = 0;
        while (true)
        {
            JSValue result = CallMethod(it, "next");
            if (ObjectOps.BooleanValue(Get(result, "done"))) break;
            double v = Get(result, "value").Number;
            if (v < 100) CallMethod(s, "add", Num(v + 1));
            // Deleting behind the iterator shrinks the table too.
            if (v > 10) CallMethod(s, "delete", Num(v - 10));
            count++;
        }
        Assert.Equal(101, count);
        Assert.Equal("11", S(Get(s, "size")));
    }

    [Fact]
    public void ForEachSeesMutation()
    {
        JSValue m = New("Map", Arr(Pair(Num(1), Str("a")), Pair(Num(2), Str("b")), Pair(Num(3), Str("c"))));
        var log = new List<string>();
        CallMethod(m, "forEach", Fn((_, a) =>
        {
            log.Add(S(a[1]) + "=" + S(a[0]));
            if (a[1].Number == 1)
            {
                CallMethod(m, "delete", Num(2));
                CallMethod(m, "set", Num(4), Str("d"));
            }
            return default;
        }));
        Assert.Equal("1=a,3=c,4=d", string.Join(",", log));
        Assert.Equal("TypeError: undefined is not a function", Throws(() => CallMethod(m, "forEach")));
    }

    [Fact]
    public void SetMethods()
    {
        JSValue S1() => New("Set", Arr(Num(1), Num(2), Num(3)));
        JSValue s2 = New("Set", Arr(Num(2), Num(3), Num(4)));
        Assert.Equal("1|2|3|4", Drain(CallMethod(CallMethod(S1(), "union", s2), "values")));
        Assert.Equal("2|3", Drain(CallMethod(CallMethod(S1(), "intersection", s2), "values")));
        Assert.Equal("1", Drain(CallMethod(CallMethod(S1(), "difference", s2), "values")));
        Assert.Equal("1|4", Drain(CallMethod(CallMethod(S1(), "symmetricDifference", s2), "values")));
        Assert.False(CallMethod(S1(), "isSubsetOf", s2).IsTrue);
        Assert.True(CallMethod(New("Set", Arr(Num(2))), "isSubsetOf", s2).IsTrue);
        Assert.True(CallMethod(S1(), "isSupersetOf", New("Set", Arr(Num(3), Num(1)))).IsTrue);
        Assert.True(CallMethod(S1(), "isDisjointFrom", New("Set", Arr(Num(7)))).IsTrue);
        Assert.False(CallMethod(S1(), "isDisjointFrom", s2).IsTrue);
        // A Map is set-like through its keys.
        JSValue map = New("Map", Arr(Pair(Num(3), Str("x"))));
        Assert.Equal("3", Drain(CallMethod(CallMethod(S1(), "intersection", map), "values")));
    }

    [Fact]
    public void SetMethodsOnSetLikeObjects()
    {
        var log = new List<string>();
        JSObject setLike = Obj(
            ("size", Num(2)),
            ("has", Fn((_, a) =>
            {
                log.Add("has(" + S(a[0]) + ")");
                return JSValue.FromBoolean(a[0].Number is 2 or 5);
            })),
            ("keys", Fn((_, _) =>
            {
                log.Add("keys");
                return Iter(Num(2), Num(5));
            })));
        JSValue s1 = New("Set", Arr(Num(1), Num(2), Num(3)));
        Assert.Equal("2", Drain(CallMethod(CallMethod(s1, "intersection", setLike), "values")));
        // this.size (3) > other.size (2): iterate other's keys.
        Assert.Equal("keys", string.Join(",", log));
        log.Clear();
        Assert.Equal("1|3", Drain(CallMethod(CallMethod(s1, "difference", setLike), "values")));
        Assert.Equal("keys", string.Join(",", log));
        log.Clear();
        JSValue small = New("Set", Arr(Num(5)));
        Assert.True(CallMethod(small, "isSubsetOf", setLike).IsTrue);
        Assert.Equal("has(5)", string.Join(",", log));

        Assert.Equal("TypeError: Set.prototype.union argument must be an object", Throws(() => CallMethod(s1, "union", Num(1))));
        Assert.Equal("TypeError: The .size property is NaN", Throws(() => CallMethod(s1, "union", Obj(("size", Num(double.NaN))))));
        Assert.Equal("TypeError: string \"has\" is not a function",
            Throws(() => CallMethod(s1, "union", Obj(("size", Num(1)), ("has", Num(1))))));
        Assert.Equal("RangeError: '-1' is an invalid size",
            Throws(() => CallMethod(s1, "union", Obj(("size", Num(-1)), ("has", Fn((_, _) => default)), ("keys", Fn((_, _) => default))))));
    }

    [Fact]
    public void MapGroupBy()
    {
        JSValue groups = Call("Map.groupBy", Iterable(Num(1), Num(2), Num(3), Num(4), Num(5)),
            Fn((_, a) => Str(a[0].Number % 2 == 0 ? "even" : "odd")));
        Assert.Equal("odd|even", Drain(CallMethod(groups, "keys")));
        Assert.Equal("1,3,5", S(CallMethod(groups, "get", Str("odd"))));
        Assert.Equal("2,4", S(CallMethod(groups, "get", Str("even"))));
        // Keys are compared with SameValueZero, so -0 groups with 0.
        JSValue zeros = Call("Map.groupBy", Arr(Num(1), Num(2)), Fn((_, a) => Num(a[0].Number == 1 ? -0.0 : 0)));
        Assert.Equal("1", S(Get(zeros, "size")));
    }

    [Fact]
    public void WeakMapAndWeakSet()
    {
        JSValue wm = New("WeakMap");
        JSObject key = Obj();
        CallMethod(wm, "set", key, Num(1));
        Assert.True(CallMethod(wm, "has", key).IsTrue);
        Assert.Equal("1", S(CallMethod(wm, "get", key)));
        Assert.False(CallMethod(wm, "has", Obj()).IsTrue);
        Assert.False(CallMethod(wm, "has", Num(1)).IsTrue);
        Assert.True(CallMethod(wm, "delete", key).IsTrue);
        Assert.False(CallMethod(wm, "has", key).IsTrue);

        JSValue symbol = Call("Symbol", Str("x"));
        CallMethod(wm, "set", symbol, Num(2));
        Assert.Equal("2", S(CallMethod(wm, "get", symbol)));
        Assert.Equal("TypeError: Invalid value used as weak map key", Throws(() => CallMethod(wm, "set", Num(1), Num(1))));
        Assert.Equal("TypeError: Invalid value used as weak map key",
            Throws(() => CallMethod(wm, "set", Call("Symbol.for", Str("x")), Num(1))));

        JSValue ws = New("WeakSet", Arr(key));
        Assert.True(CallMethod(ws, "has", key).IsTrue);
        Assert.True(CallMethod(ws, "delete", key).IsTrue);
        Assert.False(CallMethod(ws, "has", key).IsTrue);
        Assert.Equal("TypeError: Invalid value used in weak set", Throws(() => CallMethod(ws, "add", Num(1))));
    }

    [Fact]
    public void GetOrInsert()
    {
        JSValue m = New("Map");
        Assert.Equal("1", S(CallMethod(m, "getOrInsert", Str("k"), Num(1))));
        Assert.Equal("1", S(CallMethod(m, "getOrInsert", Str("k"), Num(2))));
        int calls = 0;
        JSFunction compute = Fn((_, a) =>
        {
            calls++;
            return Str("v:" + S(a[0]));
        });
        Assert.Equal("v:j", S(CallMethod(m, "getOrInsertComputed", Str("j"), compute)));
        Assert.Equal("v:j", S(CallMethod(m, "getOrInsertComputed", Str("j"), compute)));
        Assert.Equal(1, calls);

        JSValue wm = New("WeakMap");
        JSObject key = Obj();
        Assert.Equal("7", S(CallMethod(wm, "getOrInsert", key, Num(7))));
        Assert.Equal("7", S(CallMethod(wm, "getOrInsertComputed", key, compute)));
    }
}
