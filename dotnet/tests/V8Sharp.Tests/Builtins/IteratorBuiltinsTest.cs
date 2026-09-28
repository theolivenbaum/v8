// Tests of the Iterator constructor, the iterator helpers, Iterator.from,
// Iterator.concat/zip/zipKeyed (Builtins.Iterator*.cs) and the
// %AsyncFromSyncIteratorPrototype% methods. Messages were recorded with the
// oracle where it ships the feature.
namespace V8Sharp.Tests.Builtins;

public class IteratorBuiltinsTest : IterationBuiltinsTest
{
    readonly List<string> _log = [];

    JSFunction Mapper(Func<double, JSValue> f) => Fn((_, a) => f(a[0].Number));

    /// <summary>An iterator over <paramref name="values"/> whose return() is logged.</summary>
    JSObject ClosableIter(params JSValue[] values)
    {
        int i = 0;
        JSObject iterator = Obj();
        Set(iterator, "next", Fn((_, _) =>
        {
            JSObject result = Obj();
            Set(result, "done", JSValue.FromBoolean(i >= values.Length));
            Set(result, "value", i < values.Length ? values[i] : JSValue.Undefined);
            i++;
            return result;
        }));
        Set(iterator, "return", Fn((_, _) =>
        {
            _log.Add("return");
            return Obj();
        }));
        // Make it an Iterator so the prototype methods are reachable.
        JSObject.SetPrototype(i_isolate, iterator, NC.InitialIteratorPrototype, false, ShouldThrow.ThrowOnError);
        return iterator;
    }

    [Fact]
    public void IteratorConstructor()
    {
        Assert.Equal("TypeError: Abstract class Iterator not directly constructable", Throws(() => New("Iterator")));
        Assert.Equal("TypeError: Constructor Iterator requires 'new'", Throws(() => Call("Iterator")));
        Assert.Equal("Iterator", S(Get(G("Iterator.prototype"), ReadOnlyRoots.to_string_tag_symbol)));
        Assert.Same(G("Iterator").Object, Get(G("Iterator.prototype"), "constructor").Object);
    }

    [Fact]
    public void LazyHelpers()
    {
        JSValue mapped = CallMethod(Iter(Num(1), Num(2), Num(3), Num(4)), "map", Mapper(v => Num(v * 10)));
        Assert.Equal("10|20|30|40", Drain(mapped));

        JSValue filtered = CallMethod(Iter(Num(1), Num(2), Num(3), Num(4)), "filter", Mapper(v => JSValue.FromBoolean(v % 2 == 0)));
        Assert.Equal("2|4", Drain(filtered));

        JSValue taken = CallMethod(ClosableIter(Num(1), Num(2), Num(3)), "take", Num(2));
        Assert.Equal("1|2", Drain(taken));
        // take closes the underlying iterator once the limit is reached.
        Assert.Equal("return", string.Join(",", _log));

        JSValue dropped = CallMethod(Iter(Num(1), Num(2), Num(3)), "drop", Num(2));
        Assert.Equal("3", Drain(dropped));

        JSValue flat = CallMethod(Iter(Num(1), Num(2)), "flatMap", Mapper(v => Iterable(Num(v), Num(v + 0.5))));
        Assert.Equal("1|1.5|2|2.5", Drain(flat));

        Assert.Equal("Iterator Helper", S(Get(mapped, ReadOnlyRoots.to_string_tag_symbol)));
    }

    [Fact]
    public void HelperReturnClosesUnderlying()
    {
        JSValue mapped = CallMethod(ClosableIter(Num(1), Num(2)), "map", Mapper(Num));
        JSValue result = CallMethod(mapped, "return");
        Assert.True(Get(result, "done").IsTrue);
        Assert.Equal("return", string.Join(",", _log));
        // A completed helper does not call return again.
        CallMethod(mapped, "return");
        Assert.Equal("return", string.Join(",", _log));
        Assert.Equal("", Drain(mapped));
    }

    [Fact]
    public void EagerHelpers()
    {
        Assert.Equal("10", S(CallMethod(Iter(Num(1), Num(2), Num(3), Num(4)), "reduce", Fn((_, a) => Num(a[0].Number + a[1].Number)))));
        Assert.Equal("15", S(CallMethod(Iter(Num(1), Num(2), Num(3), Num(4)), "reduce", Fn((_, a) => Num(a[0].Number + a[1].Number)), Num(5))));
        Assert.Equal("TypeError: Reduce of a done iterator with no initial value",
            Throws(() => CallMethod(Iter(), "reduce", Fn((_, a) => a[0]))));
        Assert.Equal("1,2,3", S(CallMethod(Iter(Num(1), Num(2), Num(3)), "toArray")));

        var seen = new List<string>();
        CallMethod(Iter(Num(1), Num(2)), "forEach", Fn((_, a) =>
        {
            seen.Add(S(a[0]) + "@" + S(a[1]));
            return default;
        }));
        Assert.Equal("1@0,2@1", string.Join(",", seen));

        Assert.True(CallMethod(ClosableIter(Num(1), Num(2)), "some", Mapper(v => JSValue.FromBoolean(v == 1))).IsTrue);
        Assert.Equal("return", string.Join(",", _log));
        Assert.False(CallMethod(Iter(Num(1), Num(2)), "every", Mapper(v => JSValue.FromBoolean(v == 1))).IsTrue);
        Assert.Equal("2", S(CallMethod(Iter(Num(1), Num(2), Num(3)), "find", Mapper(v => JSValue.FromBoolean(v > 1)))));
        Assert.Equal("undefined", S(CallMethod(Iter(Num(1)), "find", Mapper(v => JSValue.False))));
    }

    [Fact]
    public void JoinAndIncludes()
    {
        Assert.Equal("1-2-3", S(CallMethod(Iter(Num(1), Num(2), Num(3)), "join", Str("-"))));
        Assert.Equal("1,,2", S(CallMethod(Iter(Num(1), JSValue.Null, Num(2)), "join")));
        Assert.True(CallMethod(Iter(Num(1), Num(2)), "includes", Num(2)).IsTrue);
        Assert.True(CallMethod(Iter(Num(double.NaN)), "includes", Num(double.NaN)).IsTrue);
        Assert.False(CallMethod(Iter(Num(1)), "includes", Num(3)).IsTrue);
    }

    [Fact]
    public void ArgumentValidation()
    {
        Assert.Equal("TypeError: Iterator.prototype.map called on non-object", Throws(() => CallOn("Iterator.prototype.map", Num(1))));
        Assert.Equal("TypeError: Iterator.from called on non-object", Throws(() => Call("Iterator.from", Num(1))));
        Assert.Equal("RangeError: -1 must be positive",
            Throws(() => CallOn("Iterator.prototype.take", Obj(("next", Fn((_, _) => default))), Num(-1))));
        Assert.Equal("RangeError: NaN must be positive",
            Throws(() => CallOn("Iterator.prototype.take", Obj(("next", Fn((_, _) => default))), Num(double.NaN))));
        Assert.Equal("TypeError: Iterator.concat called on non-object", Throws(() => Call("Iterator.concat", Num(1))));
        Assert.StartsWith("TypeError: ", Throws(() => Call("Iterator.zip", Num(1))));
        Assert.StartsWith("TypeError: ", Throws(() => Call("Iterator.zip", Iterable(), Obj(("mode", Str("x"))))));
    }

    [Fact]
    public void IteratorFrom()
    {
        // An object with next() but not inheriting from Iterator.prototype is wrapped.
        int i = 0;
        JSObject plain = Obj(("next", Fn((_, _) =>
        {
            JSObject r = Obj();
            Set(r, "done", JSValue.FromBoolean(i >= 2));
            Set(r, "value", Num(i++));
            return r;
        })));
        JSValue wrapped = Call("Iterator.from", plain);
        Assert.NotSame(plain, wrapped.Object);
        Assert.Equal("0|1", Drain(wrapped));
        // An Iterator is returned as is.
        JSObject iter = ClosableIter(Num(1));
        Assert.Same(iter, Call("Iterator.from", iter).Object);
        // Iterables are unwrapped through @@iterator.
        Assert.Equal("7|8", Drain(Call("Iterator.from", Iterable(Num(7), Num(8)))));
    }

    [Fact]
    public void Concat()
    {
        JSValue concat = Call("Iterator.concat", Iterable(Num(1)), Iterable(Num(2), Num(3)));
        Assert.Equal("1|2|3", Drain(concat));
        Assert.Equal("", Drain(Call("Iterator.concat")));
    }

    [Fact]
    public void Zip()
    {
        JSValue shortest = Call("Iterator.zip", Iterable(Iterable(Num(1), Num(2)), Iterable(Num(3))));
        Assert.Equal("1,3", Drain(shortest));
        JSValue longest = Call("Iterator.zip", Iterable(Iterable(Num(1), Num(2)), Iterable(Num(3))),
            Obj(("mode", Str("longest")), ("padding", Iterable(Num(0), Num(9)))));
        Assert.Equal("1,3|2,9", Drain(longest));
        JSValue strict = Call("Iterator.zip", Iterable(Iterable(Num(1), Num(2)), Iterable(Num(3))), Obj(("mode", Str("strict"))));
        Assert.StartsWith("TypeError: ", Throws(() => Drain(strict)));

        JSValue keyed = Call("Iterator.zipKeyed", Obj(("a", Iterable(Num(1), Num(2))), ("b", Iterable(Num(3), Num(4)))));
        var rows = new List<string>();
        while (true)
        {
            JSValue result = CallMethod(keyed, "next");
            if (Get(result, "done").IsTrue) break;
            JSValue row = Get(result, "value");
            rows.Add(S(Get(row, "a")) + ":" + S(Get(row, "b")));
        }
        Assert.Equal("1:3,2:4", string.Join(",", rows));
    }

    [Fact]
    public void AsyncFromSyncIterator()
    {
        JSObject sync = ClosableIter(Num(1), Num(2));
        JSObject asyncIterator = AsyncFromSyncIteratorBuiltins.CreateAsyncFromSyncIterator(i_isolate, sync);
        JSValue p1 = CallMethod(asyncIterator, "next");
        Assert.IsType<JSPromise>(p1.Object);
        var results = new List<string>();
        JSFunction record = Fn((_, a) =>
        {
            results.Add(S(Get(a[0], "value")) + "/" + S(Get(a[0], "done")));
            return default;
        });
        CallMethod(p1, "then", record);
        CallMethod(CallMethod(asyncIterator, "return", Num(5)), "then", record);
        i_isolate.DefaultMicrotaskQueue.PerformCheckpoint(i_isolate);
        Assert.Equal("1/false,undefined/false", string.Join(",", results));
        Assert.Equal("return", string.Join(",", _log));

        // throw() without a throw method closes the sync iterator and rejects.
        JSObject noThrow = ClosableIter(Num(1));
        JSObject asyncNoThrow = AsyncFromSyncIteratorBuiltins.CreateAsyncFromSyncIterator(i_isolate, noThrow);
        var errors = new List<string>();
        CallMethod(CallMethod(asyncNoThrow, "throw", Num(1)), "then", JSValue.Undefined, Fn((_, a) =>
        {
            errors.Add(ErrorUtils.ToString(i_isolate, a[0]).ToString());
            return default;
        }));
        i_isolate.DefaultMicrotaskQueue.PerformCheckpoint(i_isolate);
        Assert.Equal("TypeError: The iterator does not provide a 'throw' method.", string.Join(",", errors));
        Assert.Equal("return,return", string.Join(",", _log));
    }
}
