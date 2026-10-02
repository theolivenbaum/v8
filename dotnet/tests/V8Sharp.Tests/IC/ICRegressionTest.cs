// Regression tests for IC handlers that must not depend on the receiver they
// were created for (V8Sharp bugs, no V8 counterpart). Every test runs with
// eager feedback allocation so the first execution already goes through the ICs.
using V8Sharp.Codegen;

namespace V8Sharp.Tests.IC;

public class ICRegressionTest : TestWithContext
{
    public ICRegressionTest()
    {
        i_isolate.Flags.lazy_feedback_allocation = false;
    }

    JSValue Run(string source) => Compiler.CompileAndRun(i_isolate, source);

    string RunString(string source) => ObjectOps.ToString(i_isolate, Run(source)).ToString();

    [Fact]
    public void ArrayLengthStoreTruncatesEachReceiver()
    {
        Assert.Equal("1,1,undefined|1,1,undefined|1,1,undefined", RunString("""
            function f(a) { a.length = 1; return a.length; }
            var out = [];
            for (var i = 0; i < 3; i++) { var arr = [1, 2, 3]; out.push([f(arr), arr.length, String(arr[1])]); }
            out.join('|');
            """));
    }

    [Fact]
    public void FunctionPrototypeLoadAllocatesLazyPrototype()
    {
        Assert.Equal("1,2", RunString("""
            function S() {}
            S.prototype.n = 1;
            function R() {}
            R.prototype.d = 2;
            [S.prototype.n, R.prototype.d].join();
            """));
    }

    [Fact]
    public void TypedArrayLoadAfterOutOfBounds()
    {
        // An out-of-bounds load puts the handler in OOB mode; in-bounds loads
        // must still read the typed array (mjsunit/compiler/regress-934175).
        Assert.Equal("undefined,42,42", RunString("""
            var ar = new Float32Array(1);
            ar[0] = 42;
            function f(i) { return ar[i]; }
            [String(f(1)), f(0), f(0)].join();
            """));
    }

    [Fact]
    public void HoleyStoreSeesPrototypeSetter()
    {
        // mjsunit/regress/regress-5275-1: the element store handler is guarded
        // by the prototype chain validity cell.
        Assert.Equal("1|undefined", RunString("""
            function f(x) { var a = new Array(1); a[0] = x; return a; }
            var r = [f(1)[0]];
            Array.prototype.__defineSetter__('0', function() {});
            r.push(String(f(1)[0]));
            delete Array.prototype[0];
            r.join('|');
            """));
    }

    [Fact]
    public void PolymorphicElementStoreKeepsInvalidatedCell()
    {
        // mjsunit/regress/regress-crbug-1053939: a typed array on the prototype
        // chain swallows the out-of-bounds store.
        Assert.Equal("1", RunString("""
            function f(a, b) { a[b] = 1; }
            var v = [];
            f(v, 1);
            var saved = Array.prototype.__proto__;
            v.__proto__.__proto__ = new Int32Array();
            f(Object(), 1);
            f(v, 2);
            var keys = Object.keys(v).join();
            Array.prototype.__proto__ = saved;
            keys;
            """));
    }

    [Fact]
    public void StoreToUndefinedMessage()
    {
        Assert.Equal("TypeError: Cannot set properties of undefined (setting 'x')", RunString("""
            var m;
            try { var u; u.x = 1; } catch (e) { m = String(e); }
            m;
            """));
    }

    [Fact]
    public void TypedArrayElementStores()
    {
        // Typed array element store handlers: in bounds, out of bounds (ignored
        // once the handler ignores OOB), negative and fractional keys, values
        // that need ToNumber, polymorphic receivers and BigInt arrays.
        Assert.Equal("0,44,88,132|undefined,undefined|7,9|1,2.5,3|5,5", RunString("""
            function g(x, i, v) { x[i] = v; }
            var d = new Uint8Array(4);
            for (var k = 0; k < 10; k++) g(d, k, k * 300);
            g(d, -1, 1); g(d, 1.5, 1);
            var o = new Uint8Array(2);
            g(o, 0, "7"); g(o, 1, { valueOf() { return 9; } });
            var f = new Float64Array(3), i32 = new Int32Array(3);
            for (var k = 0; k < 3; k++) { g(f, k, k + 1); g(i32, k, k + 1); }
            g(f, 1, 2.5);
            var e = new BigInt64Array(2);
            for (var k = 0; k < 4; k++) g(e, k, 5n);
            [d.join(), String(d[-1]) + ',' + String(d[1.5]), o.join(), f.join(), e.join()].join('|');
            """));
    }

    [Fact]
    public void TypedArrayCachedDataSeesDetachAndOffsets()
    {
        // The element fast paths read the data cached in the typed array: it
        // must honour the view's byte offset, the element kind's conversions,
        // a detach after the data was cached, and never apply to views of a
        // resizable buffer.
        Assert.Equal("9|undefined,0|9|7,7,undefined|255,4294967295,1.100000023841858|9,undefined,2|undefined,undefined", RunString("""
            function ld(a, i) { return a[i]; }
            function st(a, i, v) { a[i] = v; }
            var b = new ArrayBuffer(16), a = new Int32Array(b);
            for (var k = 0; k < 10; k++) { st(a, 1, k); ld(a, 1); }
            var r1 = ld(a, 1);
            var c = b.transfer();
            st(a, 1, 5);
            var r2 = String(ld(a, 1)) + ',' + a.length;
            var r3 = ld(new Int32Array(c), 1);
            var big = new Int16Array(8), sub = big.subarray(2, 4);
            for (var k = 0; k < 10; k++) st(sub, 1, 7);
            var r4 = [big[3], ld(sub, 1), String(ld(sub, 2))].join();
            var u8 = new Uint8ClampedArray(1), u32 = new Uint32Array(1), f32 = new Float32Array(1);
            for (var k = 0; k < 10; k++) { st(u8, 0, 300.7); st(u32, 0, -1); st(f32, 0, 1.1); }
            var r5 = [ld(u8, 0), ld(u32, 0), ld(f32, 0)].join();
            var rb = new ArrayBuffer(8, { maxByteLength: 16 }), ra = new Uint8Array(rb);
            for (var k = 0; k < 10; k++) { st(ra, 0, 9); ld(ra, 0); }
            var r6a = ld(ra, 0);
            rb.resize(2);
            var r6 = [r6a, String(ld(ra, 3)), ra.length].join();
            var d = new ArrayBuffer(8), da = new Float64Array(d);
            for (var k = 0; k < 10; k++) { st(da, 0, 1.5); ld(da, 0); }
            d.transfer();
            st(da, 0, 2);
            var r7 = String(da[0]) + ',' + String(ld(da, 0));
            [r1, r2, r3, r4, r5, r6, r7].join('|');
            """));
    }

    [Fact]
    public void ThirtyThirdPropertyIsNotADuplicate()
    {
        // Adding the 33rd property sorts the descriptors; the collision check
        // must start at the new descriptor's own sorted key index, as V8's
        // DescriptorArray::Append does (the Octane TypeScript compiler hit a
        // false "duplicate descriptor" here).
        Assert.Equal("100", RunString("""
            var n = 0;
            for (var k = 0; k < 100; k++) {
              var body = '';
              for (var j = 0; j < 32; j++) body += 'this.p' + (k % 7) + '_' + j + ' = ' + j + ';';
              body += 'this.q' + k + ' = ' + k + ';';
              var o = new (new Function(body))();
              if (o['q' + k] === k) n++;
            }
            String(n);
            """));
    }
}
