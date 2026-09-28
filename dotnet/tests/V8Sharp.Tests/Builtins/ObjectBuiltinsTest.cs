// Tests of the Object builtins (Builtins.Object.cs).
namespace V8Sharp.Tests.Builtins;

public class ObjectBuiltinsTest : CoreBuiltinsTest
{
    [Fact]
    public void ObjectConstructor()
    {
        JSValue o = Call("Object");
        Assert.True(o.HeapObjectOrNull is JSObject);
        Assert.Same(NC.InitialObjectPrototype, ((JSObject)o.Object).Map.Prototype);
        JSValue wrapped = Call("Object", Num(1));
        Assert.IsType<JSPrimitiveWrapper>(wrapped.Object);
        JSObject existing = Obj();
        Assert.Same(existing, Call("Object", existing).Object);
        Assert.Same(existing, New("Object", existing).Object);
        Assert.True(New("Object").HeapObjectOrNull is JSObject);
    }

    [Fact]
    public void KeysValuesEntries()
    {
        JSObject o = Obj(("a", Num(1)), ("b", Str("x")), ("1", Num(3)));
        Assert.Equal("1,a,b", S(Call("Object.keys", o)));
        // Second call hits the enum cache fast path.
        Assert.Equal("1,a,b", S(Call("Object.keys", o)));
        Assert.Equal("3,1,x", S(Call("Object.values", o)));
        JSValue entries = Call("Object.entries", o);
        Assert.Equal("1,3,a,1,b,x", S(entries));
        Assert.Equal("0,1,2", S(Call("Object.keys", Str("abc"))));
        Assert.Equal("", S(Call("Object.keys", Num(5))));
        Assert.Equal("TypeError: Cannot convert undefined or null to object", Throws(() => Call("Object.keys")));
        Assert.Equal("TypeError: Cannot convert undefined or null to object", Throws(() => Call("Object.values", JSValue.Null)));
    }

    [Fact]
    public void KeysUsesEnumCache()
    {
        JSObject o = Obj(("x", Num(1)), ("y", Num(2)));
        Call("Object.keys", o);
        Assert.NotEqual(Map.kInvalidEnumCacheSentinel, o.Map.EnumLength);
        JSObject p = Obj(("x", Num(3)), ("y", Num(4)));
        Assert.Same(o.Map, p.Map);
        Assert.Equal("x,y", S(Call("Object.keys", p)));
    }

    [Fact]
    public void GetOwnPropertyNamesAndSymbols()
    {
        JSObject o = Obj(("a", Num(1)));
        Symbol sym = factory.NewSymbol(Str("s"));
        ObjectOps.SetProperty(i_isolate, o, sym, Num(2), StoreOrigin.MaybeKeyed, ShouldThrow.ThrowOnError);
        Call("Object.defineProperty", o, Str("hidden"), Obj(("value", Num(3))));
        Assert.Equal("a,hidden", S(Call("Object.getOwnPropertyNames", o)));
        Assert.Equal("Symbol(s)", S(Call("Object.getOwnPropertySymbols", o)));
        Assert.Equal("a,hidden,Symbol(s)", S(Call("Reflect.ownKeys", o)));
        Assert.Equal("0,1,length", S(Call("Object.getOwnPropertyNames", Str("ab"))));
        JSObject all = Obj(("p", Num(1)), ("q", Num(2)));
        Assert.Equal("p,q", S(Call("Object.getOwnPropertyNames", all)));
        Assert.Equal("p,q", S(Call("Object.getOwnPropertyNames", all)));
    }

    [Fact]
    public void AssignCopiesEnumerableOwnProperties()
    {
        JSObject target = Obj(("a", Num(1)));
        JSObject s1 = Obj(("b", Num(2)), ("a", Num(5)));
        JSObject s2 = Obj(("c", Num(3)));
        JSValue result = Call("Object.assign", target, s1, JSValue.Null, s2, Str("xy"));
        Assert.Same(target, result.Object);
        Assert.Equal("0,1,a,b,c", S(Call("Object.keys", target)));
        Assert.Equal("5", S(Get(target, "a")));
        Assert.Equal("y", S(Get(target, "1")));
        Assert.IsType<JSPrimitiveWrapper>(Call("Object.assign", Num(1)).Object);
        Assert.Equal("TypeError: Cannot convert undefined or null to object", Throws(() => Call("Object.assign")));
    }

    [Fact]
    public void CreateAndDefineProperties()
    {
        JSObject proto = Obj(("inherited", Num(1)));
        JSValue o = Call("Object.create", proto, Obj(("x", Obj(("value", Num(7)), ("enumerable", JSValue.True)))));
        Assert.Same(proto, ((JSObject)o.Object).Map.Prototype);
        Assert.Equal("7", S(Get(o, "x")));
        Assert.Equal("1", S(Get(o, "inherited")));
        JSValue n = Call("Object.create", JSValue.Null);
        Assert.True(Call("Object.getPrototypeOf", n).IsNull);
        Assert.Equal("TypeError: Object prototype may only be an Object or null: 1", Throws(() => Call("Object.create", Num(1))));
        Assert.Equal("TypeError: Object prototype may only be an Object or null: undefined", Throws(() => Call("Object.create")));
        Assert.Equal("TypeError: Object.defineProperty called on non-object",
            Throws(() => Call("Object.defineProperty", Num(1), Str("x"), Obj())));
        Assert.Equal("TypeError: Property description must be an object: 1",
            Throws(() => Call("Object.defineProperty", Obj(), Str("x"), Num(1))));
        Assert.Equal("TypeError: Object.defineProperties called on non-object",
            Throws(() => Call("Object.defineProperties", Num(1), Obj())));
    }

    [Fact]
    public void GetOwnPropertyDescriptor()
    {
        JSObject o = Obj(("a", Num(1)));
        JSValue d = Call("Object.getOwnPropertyDescriptor", o, Str("a"));
        Assert.Equal("value,writable,enumerable,configurable", S(Call("Object.keys", d)));
        Assert.Equal("1", S(Get(d, "value")));
        Assert.True(Get(d, "writable").IsTrue);
        Assert.True(Call("Object.getOwnPropertyDescriptor", o, Str("zz")).IsUndefined);
        JSValue proto = Call("Object.getOwnPropertyDescriptor", NC.InitialObjectPrototype, Str("__proto__"));
        Assert.Equal("get,set,enumerable,configurable", S(Call("Object.keys", proto)));
        JSValue all = Call("Object.getOwnPropertyDescriptors", o);
        Assert.Equal("a", S(Call("Object.keys", all)));
        Assert.Equal("1", S(Get(Get(all, "a"), "value")));
        // Primitive: length of a string.
        JSValue len = Call("Object.getOwnPropertyDescriptor", Str("abc"), Str("length"));
        Assert.Equal("3", S(Get(len, "value")));
        Assert.True(Get(len, "writable").IsFalse);
    }

    [Fact]
    public void IntegrityLevels()
    {
        JSObject o = Obj(("a", Num(1)));
        Assert.True(Call("Object.isExtensible", o).IsTrue);
        Assert.True(Call("Object.isFrozen", o).IsFalse);
        Assert.Same(o, Call("Object.freeze", o).Object);
        Assert.True(Call("Object.isFrozen", o).IsTrue);
        Assert.True(Call("Object.isSealed", o).IsTrue);
        Assert.True(Call("Object.isExtensible", o).IsFalse);
        JSObject s = Obj(("a", Num(1)));
        Call("Object.seal", s);
        Assert.True(Call("Object.isSealed", s).IsTrue);
        Assert.True(Call("Object.isFrozen", s).IsFalse);
        JSObject p = Obj();
        Call("Object.preventExtensions", p);
        Assert.True(Call("Object.isExtensible", p).IsFalse);
        Assert.True(Call("Object.isFrozen", p).IsTrue);
        Assert.True(Call("Object.isFrozen", Num(1)).IsTrue);
        Assert.True(Call("Object.isSealed", Num(1)).IsTrue);
        Assert.True(Call("Object.isExtensible", Num(1)).IsFalse);
        Assert.Equal("1", S(Call("Object.freeze", Num(1))));
        Assert.Equal("1", S(Call("Object.preventExtensions", Num(1))));
    }

    [Fact]
    public void Prototypes()
    {
        JSObject o = Obj();
        JSObject proto = Obj();
        Assert.Same(NC.InitialObjectPrototype, Call("Object.getPrototypeOf", o).Object);
        Assert.Same(o, Call("Object.setPrototypeOf", o, proto).Object);
        Assert.Same(proto, Call("Object.getPrototypeOf", o).Object);
        Assert.Same(NC.InitialStringPrototype, Call("Object.getPrototypeOf", Str("s")).Object);
        Assert.Equal("TypeError: Object.setPrototypeOf called on null or undefined",
            Throws(() => Call("Object.setPrototypeOf", JSValue.Undefined, JSValue.Null)));
        Assert.Equal("TypeError: Object prototype may only be an Object or null: 3",
            Throws(() => Call("Object.setPrototypeOf", o, Num(3))));
        Assert.Equal("1", S(Call("Object.setPrototypeOf", Num(1), JSValue.Null)));
        // Cycles.
        Assert.Equal("TypeError: Cyclic __proto__ value", Throws(() => Call("Object.setPrototypeOf", proto, o)));
        // __proto__ accessors.
        JSValue getter = Get(Call("Object.getOwnPropertyDescriptor", NC.InitialObjectPrototype, Str("__proto__")), "get");
        JSValue setter = Get(Call("Object.getOwnPropertyDescriptor", NC.InitialObjectPrototype, Str("__proto__")), "set");
        Assert.Same(proto, Execution.Call(i_isolate, getter, o, []).Object);
        JSObject other = Obj();
        Assert.True(Execution.Call(i_isolate, setter, o, [other]).IsUndefined);
        Assert.Same(other, Call("Object.getPrototypeOf", o).Object);
        Assert.True(Execution.Call(i_isolate, setter, o, [Num(1)]).IsUndefined);
        Assert.Same(other, Call("Object.getPrototypeOf", o).Object);
        Assert.Equal("TypeError: set Object.prototype.__proto__ called on null or undefined",
            Throws(() => Execution.Call(i_isolate, setter, JSValue.Undefined, [other])));
        Assert.True(Execution.Call(i_isolate, setter, Num(1), [other]).IsUndefined);
        Assert.Equal("TypeError: Cannot convert undefined or null to object",
            Throws(() => Execution.Call(i_isolate, getter, JSValue.Null, [])));
    }

    [Fact]
    public void HasOwnAndHasOwnProperty()
    {
        JSObject o = Obj(("a", Num(1)), ("2", Num(2)));
        Assert.True(CallOn("Object.prototype.hasOwnProperty", o, Str("a")).IsTrue);
        Assert.True(CallOn("Object.prototype.hasOwnProperty", o, Num(2)).IsTrue);
        Assert.True(CallOn("Object.prototype.hasOwnProperty", o, Str("toString")).IsFalse);
        Assert.True(CallOn("Object.prototype.hasOwnProperty", Str("abc"), Num(1)).IsTrue);
        Assert.True(CallOn("Object.prototype.hasOwnProperty", Str("abc"), Num(3)).IsFalse);
        Assert.True(CallOn("Object.prototype.hasOwnProperty", Str("abc"), Str("length")).IsTrue);
        Assert.True(CallOn("Object.prototype.hasOwnProperty", Num(1), Str("x")).IsFalse);
        Assert.Equal("TypeError: Cannot convert undefined or null to object",
            Throws(() => CallOn("Object.prototype.hasOwnProperty", JSValue.Undefined, Str("x"))));
        Assert.True(Call("Object.hasOwn", o, Str("a")).IsTrue);
        Assert.True(Call("Object.hasOwn", o, Str("b")).IsFalse);
        Assert.Equal("TypeError: Cannot convert undefined or null to object", Throws(() => Call("Object.hasOwn", JSValue.Null, Str("a"))));
    }

    [Fact]
    public void IsAndIsPrototypeOf()
    {
        Assert.True(Call("Object.is", Num(double.NaN), Num(double.NaN)).IsTrue);
        Assert.True(Call("Object.is", Num(0), Num(-0.0)).IsFalse);
        Assert.True(Call("Object.is", Str("a"), Str("a")).IsTrue);
        Assert.True(Call("Object.is").IsTrue);
        JSObject o = Obj();
        Assert.True(CallOn("Object.prototype.isPrototypeOf", NC.InitialObjectPrototype, o).IsTrue);
        Assert.True(CallOn("Object.prototype.isPrototypeOf", o, NC.InitialObjectPrototype).IsFalse);
        Assert.True(CallOn("Object.prototype.isPrototypeOf", JSValue.Undefined, Num(1)).IsFalse);
        Assert.True(CallOn("Object.prototype.isPrototypeOf", JSValue.Undefined, Str("s")).IsFalse);
        Assert.Equal("TypeError: Cannot convert undefined or null to object",
            Throws(() => CallOn("Object.prototype.isPrototypeOf", JSValue.Undefined, o)));
    }

    [Fact]
    public void PropertyIsEnumerable()
    {
        JSObject o = Obj(("a", Num(1)));
        Assert.True(CallOn("Object.prototype.propertyIsEnumerable", o, Str("a")).IsTrue);
        Assert.True(CallOn("Object.prototype.propertyIsEnumerable", o, Str("toString")).IsFalse);
        Assert.True(CallOn("Object.prototype.propertyIsEnumerable", Arr(Num(1)), Str("length")).IsFalse);
        Assert.True(CallOn("Object.prototype.propertyIsEnumerable", Arr(Num(1)), Num(0)).IsTrue);
    }

    [Fact]
    public void ToStringTags()
    {
        Assert.Equal("[object Undefined]", S(CallOn("Object.prototype.toString", JSValue.Undefined)));
        Assert.Equal("[object Null]", S(CallOn("Object.prototype.toString", JSValue.Null)));
        Assert.Equal("[object Number]", S(CallOn("Object.prototype.toString", Num(1))));
        Assert.Equal("[object String]", S(CallOn("Object.prototype.toString", Str("s"))));
        Assert.Equal("[object Boolean]", S(CallOn("Object.prototype.toString", JSValue.True)));
        Assert.Equal("[object Symbol]", S(CallOn("Object.prototype.toString", factory.NewSymbol())));
        Assert.Equal("[object Object]", S(CallOn("Object.prototype.toString", Obj())));
        Assert.Equal("[object Array]", S(CallOn("Object.prototype.toString", Arr())));
        Assert.Equal("[object Function]", S(CallOn("Object.prototype.toString", G("Object"))));
        Assert.Equal("[object Error]", S(CallOn("Object.prototype.toString", New("TypeError"))));
        Assert.Equal("[object Math]", S(CallOn("Object.prototype.toString", G("Math"))));
        Assert.Equal("[object JSON]", S(CallOn("Object.prototype.toString", G("JSON"))));
        Assert.Equal("[object Reflect]", S(CallOn("Object.prototype.toString", G("Reflect"))));
        Assert.Equal("[object Number]", S(CallOn("Object.prototype.toString", Call("Object", Num(1)))));
        Assert.Equal("[object Boolean]", S(CallOn("Object.prototype.toString", New("Boolean", JSValue.True))));
        JSObject tagged = Obj(("x", Num(1)));
        ObjectOps.SetProperty(i_isolate, tagged, ReadOnlyRoots.to_string_tag_symbol, Str("Custom"), StoreOrigin.MaybeKeyed,
            ShouldThrow.ThrowOnError);
        Assert.Equal("[object Custom]", S(CallOn("Object.prototype.toString", tagged)));
        // A tag on a prototype is found through the lookup start object.
        JSValue withProto = Call("Object.create", tagged);
        Assert.Equal("[object Custom]", S(CallOn("Object.prototype.toString", withProto)));
        // Proxies.
        JSValue proxy = New("Proxy", Arr(), Obj());
        Assert.Equal("[object Array]", S(CallOn("Object.prototype.toString", proxy)));
        JSValue fnProxy = New("Proxy", G("Object"), Obj());
        Assert.Equal("[object Function]", S(CallOn("Object.prototype.toString", fnProxy)));
        JSValue revocable = Call("Proxy.revocable", Obj(), Obj());
        Execution.Call(i_isolate, Get(revocable, "revoke"), JSValue.Undefined, []);
        Assert.Equal("TypeError: Cannot perform 'Object.prototype.toString' on a proxy that has been revoked",
            Throws(() => CallOn("Object.prototype.toString", Get(revocable, "proxy"))));
    }

    [Fact]
    public void ValueOfAndToLocaleString()
    {
        JSObject o = Obj();
        Assert.Same(o, CallOn("Object.prototype.valueOf", o).Object);
        Assert.IsType<JSPrimitiveWrapper>(CallOn("Object.prototype.valueOf", Num(2)).Object);
        Assert.Equal("TypeError: Cannot convert undefined or null to object",
            Throws(() => CallOn("Object.prototype.valueOf", JSValue.Undefined)));
        Assert.Equal("[object Object]", S(CallOn("Object.prototype.toLocaleString", o)));
        Assert.Equal("TypeError: Object.prototype.toLocaleString called on null or undefined",
            Throws(() => CallOn("Object.prototype.toLocaleString", JSValue.Null)));
    }

    [Fact]
    public void LegacyAccessors()
    {
        JSObject o = Obj();
        JSFunction getter = Fn((_, _) => Num(42), "g");
        JSFunction setter = Fn((_, _) => JSValue.Undefined, "s");
        CallOn("Object.prototype.__defineGetter__", o, Str("x"), getter);
        CallOn("Object.prototype.__defineSetter__", o, Str("x"), setter);
        Assert.Equal("42", S(Get(o, "x")));
        Assert.Same(getter, CallOn("Object.prototype.__lookupGetter__", o, Str("x")).Object);
        Assert.Same(setter, CallOn("Object.prototype.__lookupSetter__", o, Str("x")).Object);
        JSValue child = Call("Object.create", o);
        Assert.Same(getter, CallOn("Object.prototype.__lookupGetter__", child, Str("x")).Object);
        Assert.True(CallOn("Object.prototype.__lookupGetter__", child, Str("y")).IsUndefined);
        JSValue d = Call("Object.getOwnPropertyDescriptor", o, Str("x"));
        Assert.True(Get(d, "enumerable").IsTrue);
        Assert.Equal("TypeError: Object.prototype.__defineGetter__: Expecting function",
            Throws(() => CallOn("Object.prototype.__defineGetter__", o, Str("y"), Num(1))));
        Assert.Equal("TypeError: Object.prototype.__defineSetter__: Expecting function",
            Throws(() => CallOn("Object.prototype.__defineSetter__", o, Str("y"), Num(1))));
        // Through a proxy: the target's descriptor, then the proxy's prototype.
        JSValue proxy = New("Proxy", o, Obj());
        Assert.Same(getter, CallOn("Object.prototype.__lookupGetter__", proxy, Str("x")).Object);
        JSValue proxyChild = New("Proxy", child, Obj());
        Assert.Same(getter, CallOn("Object.prototype.__lookupGetter__", proxyChild, Str("x")).Object);
    }

    [Fact]
    public void FromEntries()
    {
        JSValue o = Call("Object.fromEntries", Arr(Arr(Str("a"), Num(1)), Arr(Num(2), Str("b")), Arr(JSValue.True, Num(3))));
        Assert.Equal("2,a,true", S(Call("Object.keys", o)));
        Assert.Equal("b", S(Get(o, "2")));
        // Slow path: a pair object that is not an array.
        JSObject pair = Obj(("0", Str("k")), ("1", Str("v")));
        JSValue o2 = Call("Object.fromEntries", Iterable(pair, Arr(Str("k2"), Num(2))));
        Assert.Equal("v", S(Get(o2, "k")));
        Assert.Equal("k,k2", S(Call("Object.keys", o2)));
        Assert.Equal("TypeError: Iterator value 1 is not an entry object", Throws(() => Call("Object.fromEntries", Iterable(Num(1)))));
        Assert.Equal("TypeError: undefined is not iterable", Throws(() => Call("Object.fromEntries")));
        Assert.Equal("TypeError: number 5 is not iterable (cannot read property Symbol(Symbol.iterator))",
            Throws(() => Call("Object.fromEntries", Num(5))));
    }

    [Fact]
    public void FromEntriesWithCustomIterator()
    {
        int i = 0;
        bool closed = false;
        JSObject iterator = Obj();
        Set(iterator, "next", Fn((_, _) =>
        {
            i++;
            JSObject result = Obj();
            Set(result, "done", JSValue.FromBoolean(i > 2));
            Set(result, "value", i == 1 ? Arr(Str("x"), Num(1)) : Num(7));
            return result;
        }));
        Set(iterator, "return", Fn((_, _) =>
        {
            closed = true;
            return Obj();
        }));
        JSObject iterable = Obj();
        ObjectOps.SetProperty(i_isolate, iterable, ReadOnlyRoots.iterator_symbol, Fn((_, _) => iterator), StoreOrigin.MaybeKeyed,
            ShouldThrow.ThrowOnError);
        Assert.Equal("TypeError: Iterator value 7 is not an entry object", Throws(() => Call("Object.fromEntries", iterable)));
        Assert.True(closed);
    }

    [Fact]
    public void GroupBy()
    {
        JSFunction callback = Fn((_, a) => a[0].Number % 2 == 0 ? Str("even") : Str("odd"));
        JSValue groups = Call("Object.groupBy", Arr(Num(1), Num(2), Num(3), Num(4), Num(5)), callback);
        Assert.True(Call("Object.getPrototypeOf", groups).IsNull);
        Assert.Equal("odd,even", S(Call("Reflect.ownKeys", groups)));
        Assert.Equal("1,3,5", S(Get(groups, "odd")));
        Assert.Equal("2,4", S(Get(groups, "even")));
        JSFunction indexCallback = Fn((_, a) => a[1]);
        JSValue byIndex = Call("Object.groupBy", Arr(Str("a"), Str("b")), indexCallback);
        Assert.Equal("0,1", S(Call("Reflect.ownKeys", byIndex)));
        JSValue generic = Call("Object.groupBy", Iterable(Num(1), Num(2), Num(3)), callback);
        Assert.Equal("odd,even", S(Call("Reflect.ownKeys", generic)));
        Assert.Equal("1,3", S(Get(generic, "odd")));
        Assert.Equal("TypeError: Object.groupBy called on null or undefined", Throws(() => Call("Object.groupBy")));
        Assert.Equal("TypeError: 1 is not a function", Throws(() => Call("Object.groupBy", Arr(), Num(1))));
    }
}
