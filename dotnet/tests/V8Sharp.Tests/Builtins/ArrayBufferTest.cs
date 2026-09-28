// Tests of the ArrayBuffer and SharedArrayBuffer builtins
// (src/builtins/builtins-arraybuffer.cc, arraybuffer.tq); expectations taken
// from V8 (the oracle).
using V8Sharp.Objects;

namespace V8Sharp.Tests.Builtins;

public class ArrayBufferTest : ArrayTestBase
{
    JSObject Options(double maxByteLength)
    {
        JSObject o = Obj();
        Set(o, "maxByteLength", N(maxByteLength));
        return o;
    }

    [Fact]
    public void ConstructAndGetters()
    {
        JSValue ab = New("ArrayBuffer", N(8));
        Assert.Equal(8, Num(Get(ab, "byteLength")));
        Assert.Equal(8, Num(Get(ab, "maxByteLength")));
        Assert.True(Get(ab, "resizable").IsFalse);
        Assert.True(Get(ab, "detached").IsFalse);
        Assert.Equal("[object ArrayBuffer]", Str(Call(Get(Global("Object"), "prototype").HeapObjectOrNull is { } p
            ? Get(p, "toString") : Undefined, ab)));
    }

    [Fact]
    public void ConstructErrors()
    {
        Assert.Equal("TypeError: Constructor ArrayBuffer requires 'new'", Throws(() => CallPath("ArrayBuffer", N(1))));
        Assert.Equal("RangeError: Invalid array buffer length", Throws(() => New("ArrayBuffer", N(-1))));
        Assert.Equal("RangeError: Invalid array buffer length", Throws(() => New("ArrayBuffer", N(Math.Pow(2, 53)))));
        Assert.Equal("RangeError: Invalid array buffer max length", Throws(() => New("ArrayBuffer", N(8), Options(4))));
        Assert.Equal("RangeError: Array buffer allocation failed", Throws(() => New("ArrayBuffer", N(Math.Pow(2, 34)))));
    }

    [Fact]
    public void Resizable()
    {
        JSValue ab = New("ArrayBuffer", N(4), Options(16));
        Assert.True(Get(ab, "resizable").IsTrue);
        Assert.Equal(16, Num(Get(ab, "maxByteLength")));
        Invoke(ab, "resize", N(12));
        Assert.Equal(12, Num(Get(ab, "byteLength")));
        Invoke(ab, "resize", N(2));
        Assert.Equal(2, Num(Get(ab, "byteLength")));
        Assert.Equal("RangeError: ArrayBuffer.prototype.resize: Invalid length parameter",
            Throws(() => Invoke(ab, "resize", N(17))));
        JSValue fixedAb = New("ArrayBuffer", N(4));
        Assert.Equal("TypeError: Method ArrayBuffer.prototype.resize called on incompatible receiver #<ArrayBuffer>",
            Throws(() => Invoke(fixedAb, "resize", N(1))));
    }

    [Fact]
    public void SliceCopiesBytes()
    {
        JSValue ab = New("ArrayBuffer", N(8));
        var buffer = ab.As<JSArrayBuffer>();
        for (int i = 0; i < 8; i++) buffer.BackingStoreBuffer[i] = (byte)(i + 1);
        JSValue slice = Invoke(ab, "slice", N(2), N(-2));
        var sliced = slice.As<JSArrayBuffer>();
        Assert.Equal(4ul, sliced.ByteLength);
        Assert.Equal(new byte[] { 3, 4, 5, 6 }, sliced.BackingStoreBuffer);
        Assert.Equal(0, Num(Get(Invoke(ab, "slice", N(6), N(2)), "byteLength")));
    }

    [Fact]
    public void TransferDetaches()
    {
        JSValue ab = New("ArrayBuffer", N(4));
        ab.As<JSArrayBuffer>().BackingStoreBuffer[1] = 7;
        JSValue moved = Invoke(ab, "transfer", N(6));
        Assert.True(Get(ab, "detached").IsTrue);
        Assert.Equal(0, Num(Get(ab, "byteLength")));
        Assert.Equal(6, Num(Get(moved, "byteLength")));
        Assert.Equal(7, moved.As<JSArrayBuffer>().BackingStoreBuffer[1]);
        Assert.Equal("TypeError: Cannot perform ArrayBuffer.prototype.transfer on a detached ArrayBuffer",
            Throws(() => Invoke(ab, "transfer")));
        Assert.Equal("TypeError: Cannot perform ArrayBuffer.prototype.slice on a detached ArrayBuffer",
            Throws(() => Invoke(ab, "slice")));

        JSValue rab = New("ArrayBuffer", N(4), Options(8));
        JSValue same = Invoke(rab, "transfer");
        Assert.True(Get(same, "resizable").IsTrue);
        JSValue fixedLength = Invoke(same, "transferToFixedLength");
        Assert.True(Get(fixedLength, "resizable").IsFalse);
        Assert.Equal(4, Num(Get(fixedLength, "byteLength")));
    }

    [Fact]
    public void IsView()
    {
        Assert.True(CallPath("ArrayBuffer.isView", New("Uint8Array", N(2))).IsTrue);
        Assert.True(CallPath("ArrayBuffer.isView", New("DataView", New("ArrayBuffer", N(2)))).IsTrue);
        Assert.True(CallPath("ArrayBuffer.isView", New("ArrayBuffer", N(2))).IsFalse);
        Assert.True(CallPath("ArrayBuffer.isView").IsFalse);
    }

    [Fact]
    public void SharedArrayBuffer()
    {
        JSValue sab = New("SharedArrayBuffer", N(4), Options(8));
        Assert.True(Get(sab, "growable").IsTrue);
        Assert.Equal(4, Num(Get(sab, "byteLength")));
        Invoke(sab, "grow", N(6));
        Assert.Equal(6, Num(Get(sab, "byteLength")));
        Assert.Equal("RangeError: SharedArrayBuffer.prototype.grow: Invalid length parameter",
            Throws(() => Invoke(sab, "grow", N(5))));
        Assert.Equal("[object SharedArrayBuffer]", Str(Call(Get(Get(Global("Object"), "prototype"), "toString"), sab)));
        JSValue slice = Invoke(sab, "slice", N(1));
        Assert.Equal(5, Num(Get(slice, "byteLength")));
        Assert.Equal("TypeError: Method get ArrayBuffer.prototype.byteLength called on incompatible receiver #<SharedArrayBuffer>",
            Throws(() => Call(ByteLengthGetter("ArrayBuffer"), sab)));
    }

    JSValue ByteLengthGetter(string ctor)
    {
        JSValue proto = Get(Global(ctor), "prototype");
        var desc = new PropertyDescriptor();
        JSReceiver.GetOwnPropertyDescriptor(i_isolate, proto.As<JSReceiver>(), factory.InternalizeString("byteLength"), ref desc);
        return desc.Get;
    }
}
