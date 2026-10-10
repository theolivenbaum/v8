// Tests of the DataView builtins (builtins-dataview.cc, data-view.tq) and the
// Atomics builtins (builtins-sharedarraybuffer*.cc); expectations taken from
// V8 (the oracle).
using V8Sharp.Objects;

namespace V8Sharp.Tests.Builtins;

public class DataViewAtomicsTest : ArrayTestBase
{
    string Bytes(JSValue buffer)
    {
        byte[] data = buffer.As<JSArrayBuffer>().BackingStoreBuffer;
        var parts = new string[(int)buffer.As<JSArrayBuffer>().ByteLength];
        for (int i = 0; i < parts.Length; i++) parts[i] = data[i].ToString("x", System.Globalization.CultureInfo.InvariantCulture);
        return string.Join(",", parts);
    }

    [Fact]
    public void GetAndSet()
    {
        JSValue ab = New("ArrayBuffer", N(8));
        JSValue dv = New("DataView", ab);
        Invoke(dv, "setUint16", N(0), N(0x1234));
        Assert.Equal("12,34,0,0,0,0,0,0", Bytes(ab));
        Invoke(dv, "setUint16", N(2), N(0x1234), JSValue.True);
        Assert.Equal("12,34,34,12,0,0,0,0", Bytes(ab));
        Assert.Equal(0x3412, Num(Invoke(dv, "getUint16", N(0), JSValue.True)));
        Assert.Equal(0x12343412, Num(Invoke(dv, "getInt32", N(0))));
        Invoke(dv, "setInt8", N(0), N(-1));
        Assert.Equal(255, Num(Invoke(dv, "getUint8", N(0))));
        Assert.Equal(-1, Num(Invoke(dv, "getInt8", N(0))));
        Invoke(dv, "setFloat64", N(0), N(1.5));
        Assert.Equal("3f,f8,0,0,0,0,0,0", Bytes(ab));
        Assert.Equal(1.5, Num(Invoke(dv, "getFloat64", N(0))));
        // V8's NaN constant is the positive quiet NaN (V8Sharp's JSValue.NaN has the sign bit set).
        Invoke(dv, "setFloat32", N(0), N(BitConverter.Int64BitsToDouble(0x7FF8000000000000)));
        Assert.Equal("7f,c0,0,0,0,0,0,0", Bytes(ab));
        Invoke(dv, "setFloat16", N(0), N(1.1), JSValue.True);
        Assert.Equal(1.099609375, Num(Invoke(dv, "getFloat16", N(0), JSValue.True)));
        Invoke(dv, "setBigInt64", N(0), BigInt.FromInt64(i_isolate, -2));
        Assert.Equal("ff,ff,ff,ff,ff,ff,ff,fe", Bytes(ab));
        Assert.Equal("18446744073709551614", Str(Invoke(dv, "getBigUint64", N(0))));
        Assert.Equal("-2", Str(Invoke(dv, "getBigInt64", N(0))));
        Assert.Equal(4294967294, Num(Invoke(dv, "getUint32", N(4))));
    }

    [Fact]
    public void Errors()
    {
        JSValue dv = New("DataView", New("ArrayBuffer", N(8)));
        Assert.Equal("RangeError: Offset is outside the bounds of the DataView", Throws(() => Invoke(dv, "getInt8", N(-1))));
        Assert.Equal("RangeError: Offset is outside the bounds of the DataView", Throws(() => Invoke(dv, "getInt8", N(8))));
        Assert.Equal("RangeError: Start offset 2 is outside the bounds of the buffer",
            Throws(() => New("DataView", New("ArrayBuffer", N(1)), N(2))));
        Assert.Equal("RangeError: Invalid DataView length 2", Throws(() => New("DataView", New("ArrayBuffer", N(1)), N(0), N(2))));
        Assert.Equal("RangeError: Start offset -1 is outside the bounds of the buffer",
            Throws(() => New("DataView", New("ArrayBuffer", N(1)), N(-1))));
        Assert.Equal("TypeError: First argument to DataView constructor must be an ArrayBuffer", Throws(() => New("DataView", Obj())));
        Assert.Equal("TypeError: Constructor DataView requires 'new'", Throws(() => CallPath("DataView")));
        Assert.Equal("TypeError: Method DataView.prototype.getInt8 called on incompatible receiver #<Object>",
            Throws(() => Call(Get(Get(Global("DataView"), "prototype"), "getInt8"), Obj(), N(0))));

        JSValue ab = New("ArrayBuffer", N(4));
        JSValue detachedView = New("DataView", ab);
        JSArrayBuffer.Detach(i_isolate, ab.As<JSArrayBuffer>());
        Assert.Equal("TypeError: Cannot perform DataView.prototype.getInt8 on a detached or out-of-bounds ArrayBuffer",
            Throws(() => Invoke(detachedView, "getInt8", N(0))));
        Assert.Equal("TypeError: Cannot perform get DataView.prototype.byteLength on a detached or out-of-bounds ArrayBuffer",
            Throws(() => Get(detachedView, "byteLength")));
        Assert.Equal("TypeError: Cannot perform get DataView.prototype.byteOffset on a detached or out-of-bounds ArrayBuffer",
            Throws(() => Get(detachedView, "byteOffset")));
    }

    [Fact]
    public void ResizableDataView()
    {
        JSObject options = Obj();
        Set(options, "maxByteLength", N(16));
        JSValue rab = New("ArrayBuffer", N(8), options);
        JSValue tracking = New("DataView", rab, N(2));
        Assert.Equal(6, Num(Get(tracking, "byteLength")));
        Invoke(rab, "resize", N(16));
        Assert.Equal(14, Num(Get(tracking, "byteLength")));
        JSValue fixedView = New("DataView", rab, N(8), N(4));
        Invoke(rab, "resize", N(10));
        Assert.Equal("TypeError: Cannot perform get DataView.prototype.byteLength on a detached or out-of-bounds ArrayBuffer",
            Throws(() => Get(fixedView, "byteLength")));
        Assert.Equal(8, Num(Get(tracking, "byteLength")));
    }

    [Fact]
    public void AtomicsOperations()
    {
        JSValue atomics = Global("Atomics");
        JSValue u32 = New("Uint32Array", Arr(4294967295));
        Assert.Equal(4294967295, Num(Invoke(atomics, "add", u32, N(0), N(1))));
        Assert.Equal(0, Num(Get(u32, 0u)));
        Assert.Equal(0, Num(Invoke(atomics, "sub", New("Uint8Array", Arr(0)), N(0), N(1))));
        JSValue i16 = New("Int16Array", N(1));
        Assert.Equal(0, Num(Invoke(atomics, "compareExchange", i16, N(0), N(0), N(70000))));
        Assert.Equal(4464, Num(Get(i16, 0u)));
        Assert.Equal(4464, Num(Invoke(atomics, "compareExchange", i16, N(0), N(70000), N(5))));
        Assert.Equal(5, Num(Get(i16, 0u)));
        JSValue i32 = New("Int32Array", Arr(6));
        Assert.Equal(6, Num(Invoke(atomics, "and", i32, N(0), N(3))));
        Assert.Equal(2, Num(Invoke(atomics, "or", i32, N(0), N(8))));
        Assert.Equal(10, Num(Invoke(atomics, "xor", i32, N(0), N(15))));
        Assert.Equal(5, Num(Invoke(atomics, "exchange", i32, N(0), N(-1))));
        Assert.Equal(-1, Num(Invoke(atomics, "load", i32, N(0))));
        Assert.Equal(3, Num(Invoke(atomics, "store", i32, N(0), N(3.7))));
        Assert.Equal(-3, Num(Invoke(atomics, "store", i32, N(0), N(-3.7))));
        Assert.False(double.IsNegative(Num(Invoke(atomics, "store", i32, N(0), N(-0.0)))));
        Assert.Equal("0", Str(Invoke(atomics, "exchange", New("BigInt64Array", N(1)), N(0), BigInt.FromInt64(i_isolate, -1))));
        Assert.True(Invoke(atomics, "isLockFree", N(8)).IsTrue);
        Assert.True(Invoke(atomics, "isLockFree", N(3)).IsFalse);
    }

    [Fact]
    public void AtomicsErrors()
    {
        JSValue atomics = Global("Atomics");
        JSValue ab = New("ArrayBuffer", N(8));
        JSValue detached = New("Int32Array", ab);
        JSArrayBuffer.Detach(i_isolate, ab.As<JSArrayBuffer>());
        Assert.Equal("TypeError: Cannot perform Atomics.load on a detached or out-of-bounds ArrayBuffer",
            Throws(() => Invoke(atomics, "load", detached, N(0))));
        Assert.Equal("TypeError: Cannot perform Atomics.store on a detached or out-of-bounds ArrayBuffer",
            Throws(() => Invoke(atomics, "compareExchange", detached, N(0), N(1), N(1))));
        Assert.Equal("TypeError: Cannot perform Atomics.add on a detached or out-of-bounds ArrayBuffer",
            Throws(() => Invoke(atomics, "add", detached, N(-1), N(1))));
        JSValue i32 = New("Int32Array", N(4));
        Assert.Equal("RangeError: Invalid atomic access index", Throws(() => Invoke(atomics, "add", i32, N(4), N(1))));
        Assert.Equal("RangeError: Invalid atomic access index", Throws(() => Invoke(atomics, "add", i32, N(-1), N(1))));
        Assert.Equal("TypeError: [object Float64Array] is not an integer typed array.",
            Throws(() => Invoke(atomics, "add", New("Float64Array", N(4)), N(0), N(1))));
        Assert.Equal("TypeError: 1 is not an integer typed array.", Throws(() => Invoke(atomics, "load", N(1), N(0))));
        Assert.Equal("TypeError: [object Int32Array] is not a shared typed array.",
            Throws(() => Invoke(atomics, "wait", i32, N(0), N(0))));
        Assert.Equal("TypeError: [object Int8Array] is not an int32 or BigInt64 typed array.",
            Throws(() => Invoke(atomics, "notify", New("Int8Array", N(4)), N(0))));
        Assert.Equal("TypeError: Atomics.pause argument must be undefined or an integer",
            Throws(() => Invoke(atomics, "pause", N(1.5))));
        Assert.True(Invoke(atomics, "pause", N(2)).IsUndefined);
    }

    [Fact]
    public void WaitAndNotify()
    {
        JSValue atomics = Global("Atomics");
        JSValue shared = New("Int32Array", New("SharedArrayBuffer", N(16)));
        Assert.Equal(0, Num(Invoke(atomics, "notify", New("Int32Array", N(4)), N(0))));
        Assert.Equal("not-equal", Str(Invoke(atomics, "wait", shared, N(0), N(1))));
        Assert.Equal("timed-out", Str(Invoke(atomics, "wait", shared, N(0), N(0), N(0))));
        Assert.Equal("timed-out", Str(Invoke(atomics, "wait", shared, N(0), N(0), N(5))));
        JSValue asyncResult = Invoke(atomics, "waitAsync", shared, N(0), N(1));
        Assert.True(Get(asyncResult, "async").IsFalse);
        Assert.Equal("not-equal", Str(Get(asyncResult, "value")));
        Assert.Equal(0, Num(Invoke(atomics, "notify", shared, N(0))));

        // A waiter on another thread is woken by notify.
        var store = shared.As<JSTypedArray>().Buffer.GetBackingStore()!;
        string? result = null;
        var waiter = new Thread(() => result = WaitOnOtherThread(store));
        waiter.Start();
        int woken = 0;
        for (int i = 0; i < 500 && woken == 0; i++)
        {
            Thread.Sleep(10);
            woken = (int)Num(Invoke(atomics, "notify", shared, N(0)));
        }
        waiter.Join();
        Assert.Equal(1, woken);
        Assert.Equal("ok", result);
    }

    static string WaitOnOtherThread(BackingStore store) =>
        V8Sharp.Builtins.FutexEmulation.WaitSync(store, 0, 0, false, double.PositiveInfinity);
}
