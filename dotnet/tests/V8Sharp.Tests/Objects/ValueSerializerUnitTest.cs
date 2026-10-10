// Port of a subset of test/unittests/objects/value-serializer-unittest.cc:
// round trips through a second context, the wire bytes of the checked cases,
// decoding of older format versions, and the failure cases.
using V8Sharp.Codegen;
using V8Sharp.Init;

namespace V8Sharp.Tests.Objects;

public class ValueSerializerUnitTest : TestWithContext
{
    readonly NativeContext _serializationContext;
    readonly NativeContext _deserializationContext;

    public ValueSerializerUnitTest()
    {
        _serializationContext = i_isolate.NativeContext;
        _deserializationContext = Bootstrapper.CreateEnvironment(i_isolate);
    }

    JSValue EvaluateScriptForInput(string source)
    {
        using var scope = i_isolate.EnterContext(_serializationContext);
        return Compiler.CompileAndRun(i_isolate, source);
    }

    byte[] EncodeTest(JSValue value)
    {
        using var scope = i_isolate.EnterContext(_serializationContext);
        var serializer = new ValueSerializer(i_isolate);
        serializer.WriteHeader();
        serializer.WriteObject(value);
        return serializer.Release();
    }

    byte[] EncodeTest(string source) => EncodeTest(EvaluateScriptForInput(source));

    string InvalidEncodeTest(string source)
    {
        JSValue value = EvaluateScriptForInput(source);
        using var scope = i_isolate.EnterContext(_serializationContext);
        var e = Assert.Throws<JavaScriptException>(() =>
        {
            var serializer = new ValueSerializer(i_isolate);
            serializer.WriteHeader();
            serializer.WriteObject(value);
        });
        return ObjectOps.ToString(i_isolate, e.Value).ToString();
    }

    JSValue DecodeTest(byte[] data)
    {
        using var scope = i_isolate.EnterContext(_deserializationContext);
        var deserializer = new ValueDeserializer(i_isolate, data);
        deserializer.SetSupportsLegacyWireFormat(true);
        deserializer.ReadHeader();
        JSValue result = deserializer.ReadValue();
        JSObject.SetOwnPropertyIgnoreAttributes(i_isolate, _deserializationContext.GlobalObject, factory.InternalizeString("result"), result, PropertyAttributes.NONE);
        return result;
    }

    string InvalidDecodeTest(byte[] data)
    {
        using var scope = i_isolate.EnterContext(_deserializationContext);
        var e = Assert.Throws<JavaScriptException>(() =>
        {
            var deserializer = new ValueDeserializer(i_isolate, data);
            deserializer.SetSupportsLegacyWireFormat(true);
            deserializer.ReadHeader();
            deserializer.ReadValue();
        });
        return ObjectOps.ToString(i_isolate, e.Value).ToString();
    }

    JSValue RoundTripTest(string source) => DecodeTest(EncodeTest(source));

    /// <summary>ExpectScriptTrue: evaluates <paramref name="source"/> in the deserialization context.</summary>
    void ExpectScriptTrue(string source)
    {
        using var scope = i_isolate.EnterContext(_deserializationContext);
        JSValue value = Compiler.CompileAndRun(i_isolate, source);
        Assert.True(value.IsTrue, source);
    }

    [Fact]
    public void RoundTripOddball()
    {
        Assert.True(RoundTripTest("undefined").IsUndefined);
        Assert.True(RoundTripTest("true").IsTrue);
        Assert.True(RoundTripTest("false").IsFalse);
        Assert.True(RoundTripTest("null").IsNull);
    }

    [Fact]
    public void DecodeOddball()
    {
        // What this code is expected to generate.
        Assert.True(DecodeTest([0xFF, 0x09, 0x5F]).IsUndefined);
        Assert.True(DecodeTest([0xFF, 0x09, 0x54]).IsTrue);
        Assert.True(DecodeTest([0xFF, 0x09, 0x46]).IsFalse);
        Assert.True(DecodeTest([0xFF, 0x09, 0x30]).IsNull);
        // What v9 of the Blink code generates.
        Assert.True(DecodeTest([0xFF, 0x09, 0x3F, 0x00, 0x5F, 0x00]).IsUndefined);
        Assert.True(DecodeTest([0xFF, 0x09, 0x3F, 0x00, 0x54, 0x00]).IsTrue);
        // v0 (with no explicit version).
        Assert.True(DecodeTest([0x5F, 0x00]).IsUndefined);
        Assert.True(DecodeTest([0x30, 0x00]).IsNull);
    }

    [Fact]
    public void EncodeNumbers()
    {
        Assert.Equal(new byte[] { 0xFF, 0x10, 0x49, 0x54 }, EncodeTest("42"));
        Assert.Equal(new byte[] { 0xFF, 0x10, 0x49, 0x01 }, EncodeTest("-1"));
        Assert.Equal(new byte[] { 0xFF, 0x10, 0x4E, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0xF4, 0x3F }, EncodeTest("1.25"));
        Assert.Equal(42, DecodeTest([0xFF, 0x09, 0x49, 0x54]).Number);
        Assert.Equal(0xFFFFFFFFu, (uint)DecodeTest([0xFF, 0x09, 0x55, 0xFF, 0xFF, 0xFF, 0xFF, 0x0F]).Number);
    }

    [Fact]
    public void RoundTripString()
    {
        Assert.Equal("", RoundTripTest("''").ToString());
        Assert.Equal("Hello world!", ObjectOps.ToString(i_isolate, RoundTripTest("'Hello world!'")).ToString());
        Assert.Equal("été", ObjectOps.ToString(i_isolate, RoundTripTest("'\\u00e9t\\u00e9'")).ToString());
        Assert.Equal("\U0001F44A", ObjectOps.ToString(i_isolate, RoundTripTest("'\\u{1F44A}'")).ToString());
    }

    [Fact]
    public void EncodeTwoByteStringUsesPadding()
    {
        byte[] data = EncodeTest("' '.repeat(200) + '\\u{1F44A}'");
        Assert.Equal(0xFF, data[0]);
        Assert.Equal(new byte[] { 0x00, 0x63, 0x94, 0x03 }, data.AsSpan(2, 4).ToArray());
    }

    [Fact]
    public void RoundTripObjectsAndArrays()
    {
        RoundTripTest("({ a: 1, 42: 'x', b: [1, , 3], c: { d: null } })");
        ExpectScriptTrue("Object.getOwnPropertyNames(result).toString() === '42,a,b,c'");
        ExpectScriptTrue("result.b.length === 3 && !(1 in result.b) && result.b[2] === 3 && result.c.d === null");
        ExpectScriptTrue("Object.getPrototypeOf(result) === Object.prototype");

        // Self-references keep their identity.
        RoundTripTest("(() => { var o = { x: [] }; o.x.push(o); o.y = o.x; return o; })()");
        ExpectScriptTrue("result.x[0] === result && result.y === result.x");

        // Dense arrays with extra properties; sparse arrays.
        RoundTripTest("(() => { var a = [1, 2]; a.p = 'q'; return a; })()");
        ExpectScriptTrue("Array.isArray(result) && result.length === 2 && result.p === 'q'");
        RoundTripTest("(() => { var a = []; a[1000] = 7; return a; })()");
        ExpectScriptTrue("result.length === 1001 && result[1000] === 7 && Object.keys(result).length === 1");
    }

    [Fact]
    public void RoundTripBuiltinObjects()
    {
        RoundTripTest("new Date(1e12)");
        ExpectScriptTrue("result instanceof Date && result.getTime() === 1e12");
        RoundTripTest("[new Boolean(true), new Number(-0), new String('s'), Object(12n)]");
        ExpectScriptTrue("result[0] instanceof Boolean && result[0].valueOf() === true");
        ExpectScriptTrue("Object.is(result[1].valueOf(), -0) && result[2] instanceof String && result[2].valueOf() === 's'");
        ExpectScriptTrue("typeof result[3] === 'object' && result[3].valueOf() === 12n");
        RoundTripTest("[/ab+c/gi, new Map([[1, 'a'], ['b', {}]]), new Set([1, 'x'])]");
        ExpectScriptTrue("result[0] instanceof RegExp && result[0].source === 'ab+c' && result[0].flags === 'gi'");
        ExpectScriptTrue("result[1] instanceof Map && result[1].get(1) === 'a' && result[1].size === 2");
        ExpectScriptTrue("result[2] instanceof Set && result[2].has('x') && result[2].size === 2");
        RoundTripTest("[-(2n ** 100n), 0n, 18446744073709551616n]");
        ExpectScriptTrue("result[0] === -(2n ** 100n) && result[1] === 0n && result[2] === 18446744073709551616n");
    }

    [Fact]
    public void RoundTripArrayBuffersAndViews()
    {
        RoundTripTest("(() => { var b = new ArrayBuffer(8); var u = new Uint8Array(b, 2, 4); u.set([1, 2, 3, 4]); " +
            "return [b, u, new DataView(b, 1, 3), new Float64Array([1.5])]; })()");
        ExpectScriptTrue("result[0] instanceof ArrayBuffer && result[0].byteLength === 8");
        ExpectScriptTrue("result[1] instanceof Uint8Array && result[1].buffer === result[0] && result[1].byteOffset === 2");
        ExpectScriptTrue("result[1].join() === '1,2,3,4' && result[2] instanceof DataView && result[2].byteLength === 3");
        ExpectScriptTrue("result[3][0] === 1.5");
        RoundTripTest("new ArrayBuffer(4, { maxByteLength: 16 })");
        ExpectScriptTrue("result.resizable && result.maxByteLength === 16");
    }

    [Fact]
    public void RoundTripErrors()
    {
        RoundTripTest("(() => { var e = new RangeError('bad', { cause: 42 }); return e; })()");
        ExpectScriptTrue("result instanceof RangeError && result.message === 'bad' && result.cause === 42");
        ExpectScriptTrue("typeof result.stack === 'string' && result.stack.startsWith('RangeError: bad')");
    }

    [Fact]
    public void InvalidEncode()
    {
        Assert.Equal("Error: Symbol(a) could not be cloned.", InvalidEncodeTest("Symbol('a')"));
        Assert.Equal("Error: function() {} could not be cloned.", InvalidEncodeTest("(function() {})"));
        Assert.Equal("Error: #<Object> could not be cloned.", InvalidEncodeTest("new Proxy({}, {})"));
        Assert.StartsWith("Error: An ArrayBuffer is detached",
            InvalidEncodeTest("(() => { var b = new ArrayBuffer(1); b.transfer(); return b; })()"));
        // A shared array buffer without a delegate cannot be cloned.
        Assert.Equal("Error: #<SharedArrayBuffer> could not be cloned.", InvalidEncodeTest("new SharedArrayBuffer(4)"));
        // Too deep recursion.
        Assert.StartsWith("RangeError: Maximum call stack size exceeded",
            InvalidEncodeTest("var a = []; for (var i = 0; i < 1E5; i++) a = [a]; a"));
    }

    [Fact]
    public void InvalidDecode()
    {
        // A version beyond the latest.
        Assert.Equal("Error: Unable to deserialize cloned data due to invalid or unsupported version.",
            InvalidDecodeTest([0xFF, 0x7F, 0x5F]));
        // Truncated data.
        Assert.Equal("Error: Unable to deserialize cloned data.", InvalidDecodeTest([0xFF, 0x10, 0x6F]));
        // A dense array whose length exceeds the data.
        Assert.Equal("Error: Unable to deserialize cloned data.", InvalidDecodeTest([0xFF, 0x10, 0x41, 0x7F, 0x49, 0x02]));
    }
}
