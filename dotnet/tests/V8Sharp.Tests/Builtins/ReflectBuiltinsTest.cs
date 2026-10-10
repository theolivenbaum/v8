// Tests of the Reflect and Proxy builtins (Builtins.Reflect.cs, Builtins.Proxy.cs).
namespace V8Sharp.Tests.Builtins;

public class ReflectBuiltinsTest : CoreBuiltinsTest
{
    [Fact]
    public void NonObjectTargets()
    {
        foreach (string name in (string[])["defineProperty", "deleteProperty", "get", "getOwnPropertyDescriptor", "getPrototypeOf",
                     "has", "isExtensible", "ownKeys", "preventExtensions", "set", "setPrototypeOf"])
        {
            Assert.Equal($"TypeError: Reflect.{name} called on non-object", Throws(() => Call("Reflect." + name, Num(1))));
        }
    }

    [Fact]
    public void Basics()
    {
        JSObject o = Obj(("a", Num(1)));
        Assert.True(Call("Reflect.defineProperty", o, Str("b"), Obj(("value", Num(2)))).IsTrue);
        Call("Object.freeze", o);
        Assert.True(Call("Reflect.defineProperty", o, Str("c"), Obj(("value", Num(2)))).IsFalse);
        Assert.True(Call("Reflect.deleteProperty", o, Str("a")).IsFalse);
        JSObject d = Obj(("a", Num(1)), ("0", Num(0)));
        Assert.True(Call("Reflect.deleteProperty", d, Str("a")).IsTrue);
        Assert.True(Call("Reflect.deleteProperty", d, Num(0)).IsTrue);
        Assert.Equal("", S(Call("Reflect.ownKeys", d)));
        Assert.Equal("1", S(Call("Reflect.get", o, Str("a"))));
        Assert.True(Call("Reflect.has", o, Str("toString")).IsTrue);
        Assert.True(Call("Reflect.has", o, Str("zz")).IsFalse);
        Assert.True(Call("Reflect.isExtensible", o).IsFalse);
        JSObject p = Obj();
        Assert.True(Call("Reflect.preventExtensions", p).IsTrue);
        Assert.True(Call("Reflect.isExtensible", p).IsFalse);
        Assert.True(Call("Reflect.setPrototypeOf", p, JSValue.Null).IsFalse);
        JSObject q = Obj();
        Assert.True(Call("Reflect.setPrototypeOf", q, JSValue.Null).IsTrue);
        Assert.True(Call("Reflect.getPrototypeOf", q).IsNull);
        Assert.Equal("TypeError: Object prototype may only be an Object or null: undefined",
            Throws(() => Call("Reflect.setPrototypeOf", q)));
        Assert.True(Call("Reflect.getOwnPropertyDescriptor", o, Str("nope")).IsUndefined);
        Assert.Equal("1", S(Get(Call("Reflect.getOwnPropertyDescriptor", o, Str("a")), "value")));
    }

    [Fact]
    public void GetAndSetWithReceiver()
    {
        JSObject target = Obj();
        JSObject receiver = Obj();
        JSFunction getter = Fn((self, _) => self, "g");
        JSFunction setter = Fn((self, a) =>
        {
            Set(self, "written", a[0]);
            return JSValue.Undefined;
        }, "s");
        CallOn("Object.prototype.__defineGetter__", target, Str("x"), getter);
        CallOn("Object.prototype.__defineSetter__", target, Str("x"), setter);
        Assert.Same(receiver, Call("Reflect.get", target, Str("x"), receiver).Object);
        Assert.Same(target, Call("Reflect.get", target, Str("x")).Object);
        Assert.True(Call("Reflect.set", target, Str("x"), Num(5), receiver).IsTrue);
        Assert.Equal("5", S(Get(receiver, "written")));
        JSObject plain = Obj();
        Assert.True(Call("Reflect.set", plain, Str("y"), Num(1), receiver).IsTrue);
        Assert.Equal("1", S(Get(receiver, "y")));
        Assert.True(Get(plain, "y").IsUndefined);
        Assert.True(Call("Reflect.set", plain, Num(3), Num(4)).IsTrue);
        Assert.Equal("4", S(Get(plain, "3")));
        Call("Object.freeze", plain);
        Assert.True(Call("Reflect.set", plain, Str("z"), Num(1)).IsFalse);
    }

    [Fact]
    public void ApplyAndConstruct()
    {
        JSFunction f = Fn((self, a) => Num(a.Length), "f");
        Assert.Equal("2", S(Call("Reflect.apply", f, JSValue.Undefined, Arr(Num(1), Num(2)))));
        Assert.Equal("TypeError: CreateListFromArrayLike called on non-object", Throws(() => Call("Reflect.apply", f)));
        Assert.Equal("TypeError: Function.prototype.apply was called on #<Object>, which is an object and not a function",
            Throws(() => Call("Reflect.apply", Obj(), JSValue.Undefined, Arr())));
        JSValue err = Call("Reflect.construct", G("Error"), Arr(Str("msg")));
        Assert.Equal("Error: msg", S(CallOn("Error.prototype.toString", err)));
        JSValue typeErr = Call("Reflect.construct", G("Error"), Arr(Str("m")), G("TypeError"));
        Assert.Same(G("TypeError.prototype").Object, ((JSObject)typeErr.Object).Map.Prototype);
        Assert.Equal("TypeError: 1 is not a constructor", Throws(() => Call("Reflect.construct", Num(1), Arr())));
        Assert.Equal("TypeError: #<Object> is not a constructor", Throws(() => Call("Reflect.construct", G("Error"), Arr(), Obj())));
        Assert.Equal("TypeError: CreateListFromArrayLike called on non-object", Throws(() => Call("Reflect.construct", G("Error"))));
    }

    [Fact]
    public void ProxyConstructor()
    {
        Assert.Equal("TypeError: Constructor Proxy requires 'new'", Throws(() => Call("Proxy", Obj(), Obj())));
        Assert.Equal("TypeError: Cannot create proxy with a non-object as target or handler", Throws(() => New("Proxy", Num(1), Obj())));
        Assert.Equal("TypeError: Cannot create proxy with a non-object as target or handler", Throws(() => New("Proxy", Obj())));
        JSObject target = Obj(("a", Num(1)));
        JSValue proxy = New("Proxy", target, Obj());
        Assert.IsType<JSProxy>(proxy.Object);
        Assert.Equal("1", S(Get(proxy, "a")));
        Assert.Equal("a", S(Call("Object.keys", proxy)));
    }

    [Fact]
    public void ProxyTraps()
    {
        JSObject target = Obj(("a", Num(1)));
        var log = new List<string>();
        JSObject handler = Obj();
        Set(handler, "get", Fn((_, a) =>
        {
            log.Add("get " + S(a[1]));
            return Num(42);
        }));
        Set(handler, "has", Fn((_, a) =>
        {
            log.Add("has " + S(a[1]));
            return JSValue.True;
        }));
        Set(handler, "ownKeys", Fn((_, _) =>
        {
            log.Add("ownKeys");
            return Arr(Str("a"), Str("z"));
        }));
        JSValue proxy = New("Proxy", target, handler);
        Assert.Equal("42", S(Call("Reflect.get", proxy, Str("q"))));
        Assert.True(Call("Reflect.has", proxy, Str("q")).IsTrue);
        Assert.Equal("a,z", S(Call("Reflect.ownKeys", proxy)));
        Assert.Equal("get q|has q|ownKeys", string.Join("|", log));
    }

    [Fact]
    public void Revocable()
    {
        JSObject target = Obj(("a", Num(1)));
        JSValue result = Call("Proxy.revocable", target, Obj());
        Assert.Equal("proxy,revoke", S(Call("Object.keys", result)));
        JSValue proxy = Get(result, "proxy");
        JSValue revoke = Get(result, "revoke");
        Assert.Equal("", S(Get(revoke, "name")));
        Assert.Equal("0", S(Get(revoke, "length")));
        Assert.Equal("1", S(Get(proxy, "a")));
        Assert.True(Execution.Call(i_isolate, revoke, JSValue.Undefined, []).IsUndefined);
        Assert.True(Execution.Call(i_isolate, revoke, JSValue.Undefined, []).IsUndefined);
        Assert.Equal("TypeError: Cannot perform 'get' on a proxy that has been revoked", Throws(() => Get(proxy, "a")));
        Assert.Equal("TypeError: Cannot create proxy with a non-object as target or handler",
            Throws(() => Call("Proxy.revocable", Num(1), Obj())));
        // Two revocable proxies share the revoke SharedFunctionInfo.
        JSValue other = Call("Proxy.revocable", target, Obj());
        Assert.Same(((JSFunction)revoke.Object).Shared, ((JSFunction)Get(other, "revoke").Object).Shared);
    }
}
