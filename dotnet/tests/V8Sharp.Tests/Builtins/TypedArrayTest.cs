// Tests of the TypedArray builtins (builtins-typed-array.cc, typed-array*.tq,
// the TypedElementsAccessor of elements.cc); expectations taken from V8 (the
// oracle).
using V8Sharp.Objects;
using V8Sharp.Roots;

namespace V8Sharp.Tests.Builtins;

public class TypedArrayTest : BuiltinsTestBase
{
    JSValue TA(string ctor, params double[] values) => New(ctor, Arr(values));

    string Join(JSValue v) => Str(Invoke(v, "join"));

    JSObject Options(double maxByteLength)
    {
        JSObject o = Obj();
        Set(o, "maxByteLength", N(maxByteLength));
        return o;
    }

    [Fact]
    public void ElementConversions()
    {
        Assert.Equal("1,2,44", Join(TA("Uint8Array", 1, 2, 300)));
        Assert.Equal("127,-128,127", Join(TA("Int8Array", 127, 128, -129)));
        Assert.Equal("2,2,0,255,0", Join(TA("Uint8ClampedArray", 1.5, 2.5, -1, 300, double.NaN)));
        Assert.Equal("1.100000023841858,0.5", Join(TA("Float32Array", 1.1, 0.5)));
        Assert.Equal("1.099609375,65504,Infinity,0,5.960464477539063e-8",
            Join(TA("Float16Array", 1.1, 65504, 65520, 1e-8, 5.960464477539063e-8)));
        Assert.Equal("1.5,0,NaN", Join(TA("Float64Array", 1.5, -0.0, double.NaN)));
        Assert.Equal("1,-1,-9223372036854775808", Join(New("BigInt64Array",
            Arr(BigInt.FromInt64(i_isolate, 1), BigInt.FromInt64(i_isolate, -1), BigInt.FromUint64(i_isolate, 1UL << 63)))));
        Assert.Equal("18446744073709551615", Join(New("BigUint64Array", Arr(BigInt.FromInt64(i_isolate, -1)))));
        Assert.Equal("1,2,3", Join(New("Uint16Array", TA("Uint8Array", 1, 2, 3))));
        JSObject arrayLike = Obj();
        Set(arrayLike, "length", N(2));
        Set(arrayLike, 0, N(5));
        Set(arrayLike, 1, S("6"));
        Assert.Equal("5,6", Join(New("Int8Array", arrayLike)));
    }

    [Fact]
    public void Float16RoundTrip()
    {
        // Every half-precision value converts back to itself.
        for (int bits = 0; bits < 0x10000; bits++)
        {
            double d = V8Sharp.Objects.TypedArrayScalars.Float16ToDouble((ushort)bits);
            if (double.IsNaN(d)) continue;
            Assert.Equal((ushort)bits, V8Sharp.Objects.TypedArrayScalars.DoubleToFloat16(d));
        }
        // Ties round to even; values past the largest half overflow to infinity.
        Assert.Equal(0x3C00, V8Sharp.Objects.TypedArrayScalars.DoubleToFloat16(1 + Math.Pow(2, -11)));
        Assert.Equal(0x3C02, V8Sharp.Objects.TypedArrayScalars.DoubleToFloat16(1 + 3 * Math.Pow(2, -11)));
        Assert.Equal(0x7BFF, V8Sharp.Objects.TypedArrayScalars.DoubleToFloat16(65519.99));
        Assert.Equal(0x7C00, V8Sharp.Objects.TypedArrayScalars.DoubleToFloat16(65520));
    }

    [Fact]
    public void ConstructorErrors()
    {
        JSValue ab8 = New("ArrayBuffer", N(8));
        Assert.Equal(1, Num(Get(New("Int32Array", ab8, N(4)), "length")));
        Assert.Equal("RangeError: start offset of Int32Array should be a multiple of 4", Throws(() => New("Int32Array", ab8, N(3))));
        Assert.Equal("RangeError: byte length of Int32Array should be a multiple of 4",
            Throws(() => New("Int32Array", New("ArrayBuffer", N(7)))));
        Assert.Equal("RangeError: Invalid typed array length: 2", Throws(() => New("Int32Array", ab8, N(4), N(2))));
        Assert.Equal("RangeError: Invalid typed array length: -1", Throws(() => New("Int32Array", N(-1))));
        Assert.Equal("TypeError: Constructor Int32Array requires 'new'", Throws(() => CallPath("Int32Array", N(1))));
        Assert.Equal(4, Num(Get(New("Uint8Array", New("SharedArrayBuffer", N(4))), "length")));
    }

    [Fact]
    public void Sort()
    {
        Assert.Equal("-Infinity,0,0,1,2,3,NaN", Join(Invoke(TA("Float64Array", 3, 1, double.NaN, -0.0, 0, double.NegativeInfinity, 2), "sort")));
        JSValue f = Invoke(TA("Float64Array", 0, -0.0), "sort");
        Assert.True(double.IsNegative(Num(Get(f, 0u))));
        Assert.Equal("-3,0,5,10", Join(Invoke(TA("Int16Array", 5, -3, 10, 0), "sort")));
        Assert.Equal("10,5,3", Join(Invoke(TA("Uint8Array", 5, 3, 10), "sort", Fn((_, a) => N(a[1].Number - a[0].Number)))));
        Assert.Equal("-5,1,3", Join(Invoke(New("BigInt64Array",
            Arr(BigInt.FromInt64(i_isolate, 3), BigInt.FromInt64(i_isolate, -5), BigInt.FromInt64(i_isolate, 1))), "sort")));
        Assert.Equal("1,2,3", Join(Invoke(TA("Uint8Array", 3, 1, 2), "toSorted")));
    }

    [Fact]
    public void Methods()
    {
        Assert.Equal("2,3,4", Join(Invoke(TA("Uint8Array", 1, 2, 3, 4, 5), "subarray", N(1), N(-1))));
        Assert.Equal("4,5", Join(Invoke(TA("Uint8Array", 1, 2, 3, 4, 5), "slice", N(-2))));

        JSValue a = New("Uint8Array", N(5));
        Invoke(a, "set", Arr(1, 2), N(3));
        Assert.Equal("0,0,0,1,2", Join(a));
        Assert.Equal("RangeError: offset is out of bounds", Throws(() => Invoke(a, "set", Arr(1, 2), N(4))));

        JSValue overlap = TA("Uint8Array", 1, 2, 3, 4, 5);
        Invoke(overlap, "set", Invoke(overlap, "subarray", N(0), N(3)), N(2));
        Assert.Equal("1,2,1,2,3", Join(overlap));

        Assert.Equal("4,5,3,4,5", Join(Invoke(TA("Uint8Array", 1, 2, 3, 4, 5), "copyWithin", N(0), N(3))));
        Assert.Equal("1,9,9", Join(Invoke(TA("Uint8Array", 1, 2, 3), "fill", N(9), N(1))));
        Assert.Equal("100,-56,44", Join(Invoke(TA("Int8Array", 1, 2, 3), "map", Fn((_, x) => N(x[0].Number * 100)))));
        Assert.Equal("1,3", Join(Invoke(TA("Int8Array", 1, 2, 3), "filter", Fn((_, x) => JSValue.FromBoolean(((int)x[0].Number & 1) != 0)))));
        Assert.Equal(6, Num(Invoke(TA("Uint8Array", 1, 2, 3), "reduce", Fn((_, x) => N(x[0].Number + x[1].Number)))));
        Assert.Equal(1, Num(Invoke(TA("Uint8Array", 1, 2, 3), "indexOf", N(2))));
        Assert.True(Invoke(TA("Float64Array", double.NaN), "includes", N(double.NaN)).IsTrue);
        Assert.Equal(-1, Num(Invoke(TA("Float64Array", double.NaN), "indexOf", N(double.NaN))));
        Assert.Equal(-1, Num(Invoke(TA("Int8Array", 1, 2, 3), "lastIndexOf", N(3), N(-2))));
        Assert.Equal("3,2,1", Join(Invoke(TA("Uint8Array", 1, 2, 3), "toReversed")));
        Assert.Equal("1,2,7", Join(Invoke(TA("Uint8Array", 1, 2, 3), "with", N(-1), N(7))));
        Assert.Equal("RangeError: Invalid typed array index", Throws(() => Invoke(TA("Uint8Array", 1), "with", N(5), N(1))));
        Assert.Equal("4,3,2,1", Join(Invoke(TA("Int32Array", 1, 2, 3, 4), "reverse")));
        Assert.Equal(20, Num(Invoke(TA("Uint8Array", 10, 20), "at", N(-1))));
        Assert.Equal("0,1", Elements(Get(Invoke(Invoke(TA("Uint8Array", 1, 2, 3), "entries"), "next"), "value")));
        Assert.Equal("2,3", Join(CallPath("Uint8Array.from", Arr(1, 2), Fn((_, x) => N(x[0].Number + 1)))));
        Assert.Equal("7,8", Join(CallPath("Uint8Array.of", N(7), N(8))));
    }

    [Fact]
    public void Accessors()
    {
        JSValue a = New("Uint8Array", N(1));
        Assert.Equal("Uint8Array", Str(Get(a, (JSValue)ReadOnlyRoots.to_string_tag_symbol)));
        Assert.Equal("1,8", Str(Get(Global("Uint8Array"), "BYTES_PER_ELEMENT")) + "," +
                            Str(Get(Get(Global("Float64Array"), "prototype"), "BYTES_PER_ELEMENT")));
        JSValue typedArrayCtor = Get(Global("Int8Array"), "__proto__");
        Assert.Equal("TypedArray", Str(Get(typedArrayCtor, "name")));

        JSValue b = New("Uint8Array", N(2));
        Set(b, 0, N(257));
        Set(b, 2, N(5));
        Assert.Equal("1,0", Join(b));
        Assert.True(Get(b, 2u).IsUndefined);
        Assert.Equal("TypeError: Method %TypedArray%.prototype.join called on incompatible receiver [object Array]",
            Throws(() => Call(Get(Get(Global("Uint8Array"), "prototype"), "join"), Arr(1))));
    }

    [Fact]
    public void Detached()
    {
        JSValue ab = New("ArrayBuffer", N(8));
        JSValue a = New("Uint8Array", ab);
        JSArrayBuffer.Detach(i_isolate, ab.As<JSArrayBuffer>());
        Assert.Equal(0, Num(Get(a, "length")));
        Assert.Equal(0, Num(Get(a, "byteLength")));
        Assert.Equal(0, Num(Get(a, "byteOffset")));
        Assert.True(Get(a, 0u).IsUndefined);
        Assert.Equal("TypeError: Cannot perform %TypedArray%.prototype.join on a detached or out-of-bounds ArrayBuffer",
            Throws(() => Invoke(a, "join")));
        Assert.Equal("TypeError: Cannot perform %TypedArray%.prototype.fill on a detached or out-of-bounds ArrayBuffer",
            Throws(() => Invoke(a, "fill", N(1))));
    }

    [Fact]
    public void ResizableBuffers()
    {
        JSValue rab = New("ArrayBuffer", N(4), Options(8));
        JSValue tracking = New("Uint8Array", rab);
        Invoke(rab, "resize", N(8));
        Assert.Equal(8, Num(Get(tracking, "length")));
        JSValue fixedView = New("Uint8Array", rab, N(2), N(2));
        Invoke(rab, "resize", N(3));
        Assert.Equal(3, Num(Get(tracking, "length")));
        Assert.Equal(0, Num(Get(fixedView, "length")));
        Assert.Equal(0, Num(Get(fixedView, "byteOffset")));
        Assert.Equal("TypeError: Cannot perform %TypedArray%.prototype.join on a detached or out-of-bounds ArrayBuffer",
            Throws(() => Invoke(fixedView, "join")));
        Invoke(rab, "resize", N(6));
        Assert.Equal(2, Num(Get(fixedView, "length")));
    }
}
