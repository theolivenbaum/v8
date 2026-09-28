// Tests of the Error, Boolean and Symbol builtins (Builtins.Error.cs,
// Builtins.Error.CallSite.cs, Builtins.Boolean.cs, Builtins.Symbol.cs).
namespace V8Sharp.Tests.Builtins;

public class ErrorBuiltinsTest : CoreBuiltinsTest
{
    [Fact]
    public void ErrorConstructor()
    {
        JSValue e = Call("Error", Str("boom"));
        Assert.Equal("Error: boom", S(CallOn("Error.prototype.toString", e)));
        Assert.Equal("stack,message", S(Call("Object.getOwnPropertyNames", e)));
        JSValue withCause = New("TypeError", Str("m"), Obj(("cause", Num(7))));
        Assert.Equal("7", S(Get(withCause, "cause")));
        Assert.True(Get(Call("Object.getOwnPropertyDescriptor", withCause, Str("cause")), "enumerable").IsFalse);
        JSValue noMessage = New("RangeError");
        Assert.True(Call("Object.hasOwn", noMessage, Str("message")).IsFalse);
        Assert.Equal("RangeError", S(CallOn("Error.prototype.toString", noMessage)));
        Assert.Same(G("RangeError.prototype").Object, ((JSObject)noMessage.Object).Map.Prototype);
        Assert.Equal("[object Error]", S(CallOn("Object.prototype.toString", noMessage)));
    }

    [Fact]
    public void ErrorToString()
    {
        Assert.Equal("Error", S(CallOn("Error.prototype.toString", Obj())));
        Assert.Equal("x: y", S(CallOn("Error.prototype.toString", Obj(("name", Str("x")), ("message", Str("y"))))));
        Assert.Equal("y", S(CallOn("Error.prototype.toString", Obj(("name", Str("")), ("message", Str("y"))))));
        Assert.Equal("TypeError: Method Error.prototype.toString called on incompatible receiver 1",
            Throws(() => CallOn("Error.prototype.toString", Num(1))));
    }

    [Fact]
    public void IsError()
    {
        Assert.True(Call("Error.isError", New("Error")).IsTrue);
        Assert.True(Call("Error.isError", Call("AggregateError", Arr())).IsTrue);
        Assert.True(Call("Error.isError", Obj()).IsFalse);
        Assert.True(Call("Error.isError", Call("Object.create", G("Error.prototype"))).IsFalse);
        Assert.True(Call("Error.isError", Num(1)).IsFalse);
    }

    [Fact]
    public void CaptureStackTrace()
    {
        JSObject o = Obj();
        Assert.True(Call("Error.captureStackTrace", o).IsUndefined);
        JSValue desc = Call("Object.getOwnPropertyDescriptor", o, Str("stack"));
        Assert.Equal("get,set,enumerable,configurable", S(Call("Object.keys", desc)));
        Assert.Equal("Error", S(Get(o, "stack")));
        Assert.Equal("TypeError: invalid_argument", Throws(() => Call("Error.captureStackTrace", Num(1))));
        JSObject frozen = Obj();
        Call("Object.freeze", frozen);
        Assert.Equal("TypeError: Cannot define property stack, object is not extensible",
            Throws(() => Call("Error.captureStackTrace", frozen)));
    }

    [Fact]
    public void ErrorStack()
    {
        JSValue e = New("Error", Str("m"));
        Assert.Equal("Error: m", S(Get(e, "stack")));
        Set(e, "stack", Str("custom"));
        Assert.Equal("custom", S(Get(e, "stack")));
    }

    [Fact]
    public void AggregateError()
    {
        JSValue e = New("AggregateError", Arr(Num(1), Num(2)), Str("msg"));
        Assert.Equal("AggregateError: msg", S(CallOn("Error.prototype.toString", e)));
        Assert.Equal("1,2", S(Get(e, "errors")));
        Assert.Equal("stack,message,errors", S(Call("Object.getOwnPropertyNames", e)));
        JSValue fromIterable = Call("AggregateError", Iterable(Str("a")));
        Assert.Equal("a", S(Get(fromIterable, "errors")));
        Assert.Equal("TypeError: undefined is not iterable (cannot read property Symbol(Symbol.iterator))",
            Throws(() => Call("AggregateError")));
    }

    [Fact]
    public void SuppressedError()
    {
        JSValue e = New("SuppressedError", Num(1), Num(2), Str("msg"));
        Assert.Equal("SuppressedError: msg", S(CallOn("Error.prototype.toString", e)));
        Assert.Equal("1", S(Get(e, "error")));
        Assert.Equal("2", S(Get(e, "suppressed")));
        Assert.Equal("stack,message,error,suppressed", S(Call("Object.getOwnPropertyNames", e)));
    }

    [Fact]
    public void CallSiteMethodsCheckTheReceiver()
    {
        JSValue getLineNumber = Get(NC.CallSiteFunction.InstancePrototype, "getLineNumber");
        Assert.Equal("TypeError: CallSite method getLineNumber expects CallSite as receiver",
            Throws(() => Execution.Call(i_isolate, getLineNumber, Obj(), [])));
        Assert.Equal("TypeError: Method getLineNumber called on incompatible receiver 1",
            Throws(() => Execution.Call(i_isolate, getLineNumber, Num(1), [])));
    }

    [Fact]
    public void Boolean()
    {
        Assert.True(Call("Boolean", Num(1)).IsTrue);
        Assert.True(Call("Boolean", Str("")).IsFalse);
        Assert.True(Call("Boolean").IsFalse);
        JSValue wrapper = New("Boolean", Num(0));
        Assert.IsType<JSPrimitiveWrapper>(wrapper.Object);
        Assert.True(CallOn("Boolean.prototype.valueOf", wrapper).IsFalse);
        Assert.Equal("false", S(CallOn("Boolean.prototype.toString", wrapper)));
        Assert.Equal("true", S(CallOn("Boolean.prototype.toString", JSValue.True)));
        Assert.Equal("TypeError: Boolean.prototype.toString requires that 'this' be a Boolean",
            Throws(() => CallOn("Boolean.prototype.toString", Num(1))));
        Assert.Equal("TypeError: Boolean.prototype.valueOf requires that 'this' be a Boolean",
            Throws(() => CallOn("Boolean.prototype.valueOf", Obj())));
    }

    [Fact]
    public void Symbols()
    {
        JSValue s = Call("Symbol", Str("desc"));
        Assert.True(s.IsSymbol);
        Assert.Equal("desc", S(Get(s, "description")));
        Assert.Equal("Symbol(desc)", S(CallOn("Symbol.prototype.toString", s)));
        Assert.Equal("Symbol()", S(CallOn("Symbol.prototype.toString", Call("Symbol"))));
        Assert.True(Get(Call("Symbol"), "description").IsUndefined);
        Assert.Equal("", S(Get(Call("Symbol", Str("")), "description")));
        Assert.Equal("TypeError: Symbol is not a constructor", Throws(() => New("Symbol")));
        Assert.Equal("TypeError: Symbol.prototype.toString requires that 'this' be a Symbol",
            Throws(() => CallOn("Symbol.prototype.toString", Num(1))));
        JSValue wrapped = Call("Object", s);
        Assert.Same(s.Object, CallOn("Symbol.prototype.valueOf", wrapped).Object);
        JSValue toPrimitive = Get(G("Symbol.prototype"), ReadOnlyRoots.to_primitive_symbol);
        Assert.Same(s.Object, Execution.Call(i_isolate, toPrimitive, wrapped, [Str("default")]).Object);
    }

    [Fact]
    public void SymbolRegistry()
    {
        JSValue a = Call("Symbol.for", Str("app"));
        JSValue b = Call("Symbol.for", Str("app"));
        Assert.Same(a.Object, b.Object);
        Assert.Equal("app", S(Call("Symbol.keyFor", a)));
        Assert.True(Call("Symbol.keyFor", Call("Symbol", Str("app"))).IsUndefined);
        Assert.True(Call("Symbol.keyFor", G("Symbol.iterator")).IsUndefined);
        Assert.Equal("TypeError: 1 is not a symbol", Throws(() => Call("Symbol.keyFor", Num(1))));
        Assert.Equal("undefined", S(Get(Call("Symbol.for"), "description")));
    }

    [Fact]
    public void GetScriptHash()
    {
        Script script = factory.NewScript(Str("print(1)"));
        var shared = factory.NewSharedFunctionInfoForBuiltin(factory.InternalizeString("f"), Builtin.Illegal, 0, false);
        shared.Script = script;
        JSFunction f = factory.NewFunction(shared, NC);
        var info = new CallSiteInfo(JSValue.Undefined, f, 0, 0);
        Assert.Equal("5d85b9fc4e30af2a74fa5b5a1ce5ad7e5f63ebd2dc2e48c93a1c7a33ad0d2d95".Length,
            S(BuiltinsCallSite.GetScriptHash(i_isolate, info)).Length);
    }
}
