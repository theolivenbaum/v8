// Tests of the Array builtins (builtins-array.cc, builtins-array-gen.cc and
// the array-*.tq files); results and error messages taken from V8 (the oracle).
using V8Sharp.Objects;
using V8Sharp.Roots;

namespace V8Sharp.Tests.Builtins;

public class ArrayBuiltinsTest : ArrayTestBase
{
    JSValue ArrayProto(string name) => Get(Get(Global("Array"), "prototype"), name);

    void SetKey(JSValue obj, Name key, JSValue value) =>
        ObjectOps.SetProperty(i_isolate, obj, key, value, StoreOrigin.MaybeKeyed, ShouldThrow.ThrowOnError);

    JSObject ArrayLike(double length)
    {
        JSObject o = Obj();
        Set(o, "length", N(length));
        return o;
    }

    [Fact]
    public void Constructor()
    {
        Assert.Equal(3, Num(Get(New("Array", N(3)), "length")));
        Assert.Equal("1,2", Elements(New("Array", N(1), N(2))));
        Assert.Equal("x", Elements(CallPath("Array", S("x"))));
        Assert.Equal("RangeError: Invalid array length", Throws(() => New("Array", N(-1))));
        Assert.Equal("RangeError: Invalid array length", Throws(() => New("Array", N(1.5))));
        Assert.True(CallPath("Array.isArray", Arr(1)).IsTrue);
        Assert.True(CallPath("Array.isArray", Obj()).IsFalse);
        Assert.Equal("7,8", Elements(CallPath("Array.of", N(7), N(8))));
    }

    [Fact]
    public void From()
    {
        JSObject arrayLike = ArrayLike(2);
        Set(arrayLike, 0, S("a"));
        Assert.Equal("a,undefined", Elements(CallPath("Array.from", arrayLike)));
        Assert.Equal("2,4", Elements(CallPath("Array.from", Arr(1, 2), Fn((_, a) => N(a[0].Number * 2)))));
        Assert.Equal("TypeError: number 3 is not a function", Throws(() => CallPath("Array.from", ArrayLike(1), N(3))));
        JSObject badIterable = Obj();
        SetKey(badIterable, ReadOnlyRoots.iterator_symbol, N(3));
        Assert.Equal(
            "TypeError: %Array%.from requires that the property of the first argument, items[Symbol.iterator], when exists, be a function",
            Throws(() => CallPath("Array.from", badIterable)));
    }

    [Fact]
    public void PushPopShiftUnshift()
    {
        JSArray a = Arr(1, 2);
        Assert.Equal(4, Num(Invoke(a, "push", N(3), S("x"))));
        Assert.Equal("1,2,3,x", Elements(a));
        Assert.Equal("x", Str(Invoke(a, "pop")));
        Assert.Equal(1, Num(Invoke(a, "shift")));
        Assert.Equal(4, Num(Invoke(a, "unshift", N(-1), N(0))));
        Assert.Equal("-1,0,2,3", Elements(a));
        Assert.True(Invoke(Arr(Array.Empty<JSValue>()), "pop").IsUndefined);

        Assert.Equal(
            "TypeError: Pushing 1 elements on an array-like of length 9007199254740991 is disallowed, as the total surpasses 2**53-1",
            Throws(() => Call(ArrayProto("push"), ArrayLike(9007199254740991), N(1))));
        Assert.Equal("TypeError: Invalid array length",
            Throws(() => Call(ArrayProto("unshift"), ArrayLike(9007199254740991), N(1))));

        JSArray frozen = Arr(1, 2);
        CallPath("Object.freeze", frozen);
        Assert.Equal("TypeError: Cannot delete property '1' of [object Array]", Throws(() => Invoke(frozen, "pop")));
        Assert.Equal("TypeError: Cannot add property 2, object is not extensible", Throws(() => Invoke(frozen, "push", N(3))));
    }

    [Fact]
    public void Concat()
    {
        Assert.Equal("1,2,3,4,5", Elements(Invoke(Arr(1, 2), "concat", Arr(3, 4), N(5))));
        JSObject spreadable = ArrayLike(2);
        Set(spreadable, 0, S("a"));
        SetKey(spreadable, ReadOnlyRoots.is_concat_spreadable_symbol, JSValue.True);
        Assert.Equal("1,a,undefined", Elements(Invoke(Arr(1), "concat", spreadable)));
        Assert.Equal("TypeError: Array.prototype.concat called on null or undefined",
            Throws(() => Call(ArrayProto("concat"), JSValue.Null)));
    }

    [Fact]
    public void SliceSplice()
    {
        JSArray a = Arr(1, 2, 3, 4, 5);
        Assert.Equal("2,3", Elements(Invoke(a, "slice", N(1), N(3))));
        Assert.Equal("4,5", Elements(Invoke(a, "slice", N(-2))));
        Assert.Equal("1,2,3,4,5", Elements(Invoke(a, "slice")));
        Assert.Equal("2,3", Elements(Invoke(a, "splice", N(1), N(2), S("a"), S("b"), S("c"))));
        Assert.Equal("1,a,b,c,4,5", Elements(a));
        Assert.Equal("4,5", Elements(Invoke(a, "splice", N(-2))));
        Assert.Equal("1,a,b,c", Elements(a));
        Assert.Equal("", Elements(Invoke(a, "splice", N(1), N(0), N(9.5))));
        Assert.Equal("1,9.5,a,b,c", Elements(a));

        JSObject o = ArrayLike(3);
        Set(o, 0, S("x"));
        Set(o, 2, S("z"));
        JSValue removed = Call(ArrayProto("splice"), o, N(0), N(1));
        Assert.Equal("x", Elements(removed));
        Assert.Equal(2, Num(Get(o, "length")));
        Assert.False(JSReceiver.HasElement(i_isolate, o, 0));
        Assert.Equal("z", Str(Get(o, 1)));
        Assert.Equal("TypeError: Invalid array length",
            Throws(() => Call(ArrayProto("splice"), ArrayLike(9007199254740991), N(0), N(0), N(1))));
    }

    [Fact]
    public void CallbackBuiltins()
    {
        JSArray a = Arr(1, 2, 3, 4);
        JSFunction isEven = Fn((_, args) => JSValue.FromBoolean(args[0].Number % 2 == 0));
        Assert.Equal("2,4", Elements(Invoke(a, "filter", isEven)));
        Assert.Equal("2,4,6,8", Elements(Invoke(a, "map", Fn((_, args) => N(args[0].Number * 2)))));
        Assert.True(Invoke(a, "some", isEven).IsTrue);
        Assert.True(Invoke(a, "every", isEven).IsFalse);
        Assert.Equal(2, Num(Invoke(a, "find", isEven)));
        Assert.Equal(1, Num(Invoke(a, "findIndex", isEven)));
        Assert.Equal(4, Num(Invoke(a, "findLast", isEven)));
        Assert.Equal(3, Num(Invoke(a, "findLastIndex", isEven)));
        Assert.Equal(10, Num(Invoke(a, "reduce", Fn((_, args) => N(args[0].Number + args[1].Number)))));
        Assert.Equal("4321", Str(Invoke(a, "reduceRight", Fn((_, args) => S(Str(args[0]) + Str(args[1]))), S(""))));
        double sum = 0;
        Invoke(a, "forEach", Fn((_, args) => { sum += args[0].Number; return Undefined; }));
        Assert.Equal(10, sum);

        Assert.Equal("TypeError: Reduce of empty array with no initial value", Throws(() => Invoke(Arr(Array.Empty<JSValue>()), "reduce", isEven)));
        Assert.Equal("TypeError: Reduce of empty array with no initial value", Throws(() => Invoke(Arr(Array.Empty<JSValue>()), "reduceRight", isEven)));
        foreach (string name in new[] { "forEach", "map", "filter", "every", "some", "reduce", "find", "reduceRight", "findLast" })
        {
            Assert.Equal("TypeError: number 3 is not a function", Throws(() => Invoke(Arr(1), name, N(3))));
            Assert.Equal($"TypeError: Array.prototype.{name} called on null or undefined",
                Throws(() => Call(ArrayProto(name), JSValue.Null, isEven)));
        }
    }

    [Fact]
    public void HolesAreSkipped()
    {
        JSArray a = Arr(1);
        Set(a, 2, N(3));
        int calls = 0;
        Invoke(a, "forEach", Fn((_, _) => { calls++; return Undefined; }));
        Assert.Equal(2, calls);
        JSValue mapped = Invoke(a, "map", Fn((_, args) => args[0]));
        Assert.False(JSReceiver.HasElement(i_isolate, mapped.As<JSReceiver>(), 1));
        calls = 0;
        Invoke(a, "find", Fn((_, _) => { calls++; return JSValue.False; }));
        Assert.Equal(3, calls);
    }

    [Fact]
    public void Searching()
    {
        JSArray a = Arr(N(1), N(2), N(double.NaN), N(2), S("s"));
        Assert.Equal(1, Num(Invoke(a, "indexOf", N(2))));
        Assert.Equal(3, Num(Invoke(a, "lastIndexOf", N(2))));
        Assert.Equal(-1, Num(Invoke(a, "indexOf", N(double.NaN))));
        Assert.True(Invoke(a, "includes", N(double.NaN)).IsTrue);
        Assert.True(Invoke(a, "includes", S("s")).IsTrue);
        Assert.True(Invoke(a, "includes", N(1), N(1)).IsFalse);
        Assert.Equal(3, Num(Invoke(a, "indexOf", N(2), N(-2))));

        JSArray doubles = Arr(1.5, 2.5, -0.0);
        Assert.Equal(2, Num(Invoke(doubles, "indexOf", N(0))));
        Assert.True(Invoke(doubles, "includes", N(2.5)).IsTrue);

        JSArray holey = Arr(1);
        Set(holey, "length", N(3));
        Assert.True(Invoke(holey, "includes", Undefined).IsTrue);
        Assert.Equal(-1, Num(Invoke(holey, "indexOf", Undefined)));

        Assert.Equal("TypeError: Array.prototype.indexOf called on null or undefined",
            Throws(() => Call(ArrayProto("indexOf"), Undefined)));
        Assert.Equal("TypeError: Cannot convert undefined or null to object",
            Throws(() => Call(ArrayProto("includes"), Undefined)));
    }

    [Fact]
    public void JoinAndToString()
    {
        Assert.Equal("1,2,3", Str(Invoke(Arr(1, 2, 3), "join")));
        Assert.Equal("1-2-3", Str(Invoke(Arr(1, 2, 3), "join", S("-"))));
        JSArray mixed = Arr(N(1), Undefined, JSValue.Null, S("x"), N(1.5), JSValue.True);
        Assert.Equal("1,,,x,1.5,true", Str(Invoke(mixed, "join")));
        Assert.Equal("1,,,x,1.5,true", Str(Invoke(mixed, "toString")));
        JSArray cyclic = Arr(1);
        Set(cyclic, 1, cyclic);
        Set(cyclic, 2, N(3));
        Assert.Equal("1,,3", Str(Invoke(cyclic, "join")));
        JSArray holey = New("Array", N(4)).As<JSArray>();
        Assert.Equal(":::", Str(Invoke(holey, "join", S(":"))));
        Assert.Equal("[object Object]", Str(Call(ArrayProto("toString"), Obj())));
        Assert.Equal("TypeError: Invalid array length", Throws(() => Call(ArrayProto("join"), ArrayLike(Math.Pow(2, 32)))));
        Assert.Equal("RangeError: Invalid string length",
            Throws(() => Invoke(New("Array", N(1 << 20)), "join", S(new string('x', 1024)))));
        JSObject localized = Obj();
        Set(localized, "toLocaleString", Fn((_, args) => S("L" + args.Length)));
        Assert.Equal("L2,,L2", Str(Invoke(Arr(localized, JSValue.Null, localized), "toLocaleString")));
    }

    [Fact]
    public void ReverseCopyWithinFill()
    {
        Assert.Equal("3,2,1", Elements(Invoke(Arr(1, 2, 3), "reverse")));
        Assert.Equal("4,5,3,4,5", Elements(Invoke(Arr(1, 2, 3, 4, 5), "copyWithin", N(0), N(3))));
        Assert.Equal("1,0,0,4", Elements(Invoke(Arr(1, 2, 3, 4), "fill", N(0), N(1), N(3))));
        JSObject o = ArrayLike(3);
        Set(o, 0, S("a"));
        Call(ArrayProto("reverse"), o);
        Assert.Equal("a", Str(Get(o, 2)));
        Assert.False(JSReceiver.HasElement(i_isolate, o, 0));
    }

    [Fact]
    public void Flat()
    {
        JSArray nested = Arr(N(1), Arr(N(2), Arr(N(3), Arr(4))));
        Assert.Equal(3, Num(Get(Invoke(nested, "flat"), "length")));
        Assert.Equal(4, Num(Get(Invoke(nested, "flat", N(double.PositiveInfinity)), "length")));
        Assert.Equal("1,1,2,2", Elements(Invoke(Arr(1, 2), "flatMap", Fn((_, args) => Arr(args[0], args[0])))));
        Assert.Equal("TypeError: undefined is not a function", Throws(() => Invoke(Arr(1), "flatMap")));
    }

    [Fact]
    public void ChangeArrayByCopy()
    {
        JSArray a = Arr(1, 2, 3);
        Assert.Equal("3,2,1", Elements(Invoke(a, "toReversed")));
        Assert.Equal("1,9,3", Elements(Invoke(a, "with", N(1), N(9))));
        Assert.Equal("1,2,9", Elements(Invoke(a, "with", N(-1), N(9))));
        Assert.Equal("1,x,y,3", Elements(Invoke(a, "toSpliced", N(1), N(1), S("x"), S("y"))));
        Assert.Equal("1,2,3", Elements(a));
        Assert.Equal("RangeError: Invalid index : 5", Throws(() => Invoke(Arr(1), "with", N(5), N(1))));
        Assert.Equal("RangeError: Invalid array length", Throws(() => Call(ArrayProto("toReversed"), ArrayLike(Math.Pow(2, 32)))));
        Assert.Equal("TypeError: Invalid array length",
            Throws(() => Call(ArrayProto("toSpliced"), ArrayLike(9007199254740991), N(0), N(0), N(1))));

        JSArray holey = Arr(1);
        Set(holey, 2, N(3));
        JSValue reversed = Invoke(holey, "toReversed");
        Assert.True(JSReceiver.HasElement(i_isolate, reversed.As<JSReceiver>(), 1));
    }

    [Fact]
    public void AtAndIterators()
    {
        JSArray a = Arr(1, 2, 3);
        Assert.Equal(3, Num(Invoke(a, "at", N(-1))));
        Assert.True(Invoke(a, "at", N(3)).IsUndefined);

        JSValue it = Invoke(a, "entries");
        JSValue first = Invoke(it, "next");
        Assert.Equal("0,1", Elements(Get(first, "value")));
        Assert.True(Get(first, "done").IsFalse);
        Invoke(it, "next");
        Invoke(it, "next");
        JSValue done = Invoke(it, "next");
        Assert.True(Get(done, "done").IsTrue);
        Assert.True(Get(done, "value").IsUndefined);
        Set(a, 3, N(4));
        Assert.True(Get(Invoke(it, "next"), "done").IsTrue);

        Assert.Equal("Method Array Iterator.prototype.next called on incompatible receiver #<Object>",
            Throws(() => Call(Get(it, "next"), Obj()))[11..]);
    }
}
