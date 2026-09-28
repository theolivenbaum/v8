// Tests of the Function builtins (Builtins.Function.cs).
namespace V8Sharp.Tests.Builtins;

public class FunctionBuiltinsTest : CoreBuiltinsTest
{
    JSFunction Recorder(out List<(JSValue This, JSValue[] Args)> calls, string name = "rec", int length = 3)
    {
        var list = new List<(JSValue, JSValue[])>();
        calls = list;
        return Fn((self, a) =>
        {
            list.Add((self, a));
            return Num(a.Length);
        }, name, length);
    }

    [Fact]
    public void PrototypeCall()
    {
        JSFunction f = Recorder(out var calls);
        JSObject self = Obj();
        Assert.Equal("2", S(CallOn("Function.prototype.call", f, self, Num(1), Num(2))));
        Assert.Same(self, calls[0].This.Object);
        Assert.Equal("0", S(CallOn("Function.prototype.call", f)));
        Assert.True(calls[1].This.IsUndefined);
        Assert.Equal("TypeError: Function.prototype.call was called on 1, which is a number and not a function",
            Throws(() => CallOn("Function.prototype.call", Num(1))));
        Assert.Equal("TypeError: Function.prototype.call was called on #<Object>, which is an object and not a function",
            Throws(() => CallOn("Function.prototype.call", Obj())));
        Assert.Equal("TypeError: Function.prototype.call was called on undefined, which is undefined and not a function",
            Throws(() => CallOn("Function.prototype.call", JSValue.Undefined)));
    }

    [Fact]
    public void PrototypeApply()
    {
        JSFunction f = Recorder(out var calls);
        JSObject self = Obj();
        Assert.Equal("3", S(CallOn("Function.prototype.apply", f, self, Arr(Num(1), Num(2), Num(3)))));
        Assert.Same(self, calls[0].This.Object);
        Assert.Equal("3", S(calls[0].Args[2]));
        Assert.Equal("0", S(CallOn("Function.prototype.apply", f, self, JSValue.Null)));
        Assert.Equal("0", S(CallOn("Function.prototype.apply", f)));
        // Array-likes.
        JSObject arrayLike = Obj(("length", Num(2)), ("0", Str("a")), ("1", Str("b")));
        Assert.Equal("2", S(CallOn("Function.prototype.apply", f, self, arrayLike)));
        Assert.Equal("b", S(calls[^1].Args[1]));
        // Holey and double arrays.
        JSArray holey = Arr(Num(1.5), Num(2.5));
        Set(holey, "length", Num(4));
        Assert.Equal("4", S(CallOn("Function.prototype.apply", f, self, holey)));
        Assert.True(calls[^1].Args[3].IsUndefined);
        Assert.Equal("2.5", S(calls[^1].Args[1]));
        Assert.Equal("TypeError: CreateListFromArrayLike called on non-object",
            Throws(() => CallOn("Function.prototype.apply", f, self, Num(1))));
        Assert.Equal("TypeError: Function.prototype.apply was called on 1, which is a number and not a function",
            Throws(() => CallOn("Function.prototype.apply", Num(1), self, Arr())));
        Assert.Equal("TypeError: Function.prototype.apply was called on null, which is null and not a function",
            Throws(() => CallOn("Function.prototype.apply", JSValue.Null)));
    }

    [Fact]
    public void PrototypeBind()
    {
        JSFunction f = Recorder(out var calls, "target", 3);
        JSObject self = Obj();
        JSValue bound = CallOn("Function.prototype.bind", f, self, Num(1));
        Assert.IsType<JSBoundFunction>(bound.Object);
        Assert.Equal("bound target", S(Get(bound, "name")));
        Assert.Equal("2", S(Get(bound, "length")));
        Assert.Equal("3", S(Execution.Call(i_isolate, bound, Obj(), [Num(2), Num(3)])));
        Assert.Same(self, calls[0].This.Object);
        Assert.Equal("1", S(calls[0].Args[0]));
        // Bound twice.
        JSValue twice = CallOn("Function.prototype.bind", bound, JSValue.Undefined, Num(2));
        Assert.Equal("bound bound target", S(Get(twice, "name")));
        Assert.Equal("1", S(Get(twice, "length")));
        Assert.Equal("TypeError: Bind must be called on a function", Throws(() => CallOn("Function.prototype.bind", Obj())));
        // Slow path: a changed name.
        JSFunction g = Fn((_, _) => JSValue.Undefined, "g", 5);
        Call("Object.defineProperty", g, Str("name"), Obj(("value", Num(7))));
        Call("Object.defineProperty", g, Str("length"), Obj(("value", Num(2.5))));
        JSValue boundG = CallOn("Function.prototype.bind", g, JSValue.Undefined, Num(1), Num(2), Num(3));
        Assert.Equal("bound ", S(Get(boundG, "name")));
        Assert.Equal("0", S(Get(boundG, "length")));
        // Constructing a bound builtin constructor.
        JSValue boundError = CallOn("Function.prototype.bind", G("Error"), JSValue.Undefined, Str("m"));
        JSValue err = Execution.New(i_isolate, boundError, []);
        Assert.Equal("Error: m", S(CallOn("Error.prototype.toString", err)));
        Assert.Same(NC.InitialErrorPrototype, ((JSObject)err.Object).Map.Prototype);
    }

    [Fact]
    public void ToStringOfNativeFunctions()
    {
        Assert.Equal("function keys() { [native code] }", S(CallOn("Function.prototype.toString", G("Object.keys"))));
        Assert.Equal("function Object() { [native code] }", S(CallOn("Function.prototype.toString", G("Object"))));
        JSValue bound = CallOn("Function.prototype.bind", G("Object"));
        Assert.Equal("function () { [native code] }", S(CallOn("Function.prototype.toString", bound)));
        JSValue proxy = New("Proxy", G("Object"), Obj());
        Assert.Equal("function () { [native code] }", S(CallOn("Function.prototype.toString", proxy)));
        Assert.Equal("TypeError: Function.prototype.toString requires that 'this' be a Function",
            Throws(() => CallOn("Function.prototype.toString", Obj())));
    }

    [Fact]
    public void HasInstance()
    {
        JSValue hasInstance = Get(G("Function.prototype"), ReadOnlyRoots.has_instance_symbol);
        Assert.True(Execution.Call(i_isolate, hasInstance, G("Object"), [Obj()]).IsTrue);
        Assert.True(Execution.Call(i_isolate, hasInstance, G("Array"), [Obj()]).IsFalse);
        Assert.True(Execution.Call(i_isolate, hasInstance, G("Object"), [Num(1)]).IsFalse);
        Assert.True(Execution.Call(i_isolate, hasInstance, Obj(), [Obj()]).IsFalse);
        JSValue bound = CallOn("Function.prototype.bind", G("Error"));
        Assert.True(Execution.Call(i_isolate, hasInstance, bound, [New("Error")]).IsTrue);
    }

    [Fact]
    public void LegacyArgumentsAndCaller()
    {
        JSValue argumentsDesc = Call("Object.getOwnPropertyDescriptor", G("Function.prototype"), Str("arguments"));
        JSValue getter = Get(argumentsDesc, "get");
        Assert.Equal("TypeError: 'caller', 'callee', and 'arguments' properties may not be accessed on strict mode functions or the arguments objects for calls to them",
            Throws(() => Execution.Call(i_isolate, getter, G("Object.keys"), [])));
    }

    [Fact]
    public void FunctionConstructorNeedsCompiler()
    {
        Assert.Throws<InvalidOperationException>(() => Call("Function", Str("return 1")));
    }

    [Fact]
    public void FunctionConstructorBuildsSource()
    {
        var compiler = new RecordingCompiler();
        i_isolate.DynamicFunctionCompiler = compiler;
        Assert.Throws<InvalidOperationException>(() => Call("Function", Str("a"), Str("b"), Str("return a + b")));
        Assert.Equal("(function anonymous(a,b\n) {\nreturn a + b\n})", compiler.Source);
        Assert.Equal("(function anonymous(a,b\n".Length, compiler.ParametersEndPos);
        Assert.Throws<InvalidOperationException>(() => Call("Function"));
        Assert.Equal("(function anonymous(\n) {\n\n})", compiler.Source);
    }

    sealed class RecordingCompiler : IDynamicFunctionCompiler
    {
        public string? Source;
        public int ParametersEndPos;

        public JSFunction GetFunctionFromString(Isolate isolate, NativeContext nativeContext, JSString source, int parametersEndPos,
            bool isCodeLike)
        {
            Source = source.ToString();
            ParametersEndPos = parametersEndPos;
            throw new InvalidOperationException("recorded");
        }

        public JSFunction GetFunctionFromValidatedString(Isolate isolate, NativeContext nativeContext, JSString source,
            ParseRestriction restriction, int parametersEndPos)
        {
            Source = source.ToString();
            throw new InvalidOperationException("recorded");
        }
    }

    [Fact]
    public void IndirectEvalReturnsNonStrings()
    {
        JSObject o = Obj();
        Assert.Same(o, Call("eval", o).Object);
        Assert.True(Call("eval").IsUndefined);
        var compiler = new RecordingCompiler();
        i_isolate.DynamicFunctionCompiler = compiler;
        Assert.Throws<InvalidOperationException>(() => Call("eval", Str("1+1")));
        Assert.Equal("1+1", compiler.Source);
        NC.AllowCodeGenFromStrings = JSValue.False;
        Assert.Equal("EvalError: Code generation from strings disallowed for this context", Throws(() => Call("eval", Str("1"))));
        Assert.Same(o, Call("eval", o).Object);
    }

    [Fact]
    public void FunctionPrototypeIsCallable()
    {
        Assert.True(Execution.Call(i_isolate, G("Function.prototype"), JSValue.Undefined, [Num(1)]).IsUndefined);
        Assert.Equal("", S(Get(G("Function.prototype"), "name")));
        Assert.Equal("0", S(Get(G("Function.prototype"), "length")));
        Assert.Equal("function () { [native code] }", S(CallOn("Function.prototype.toString", G("Function.prototype"))));
    }
}
