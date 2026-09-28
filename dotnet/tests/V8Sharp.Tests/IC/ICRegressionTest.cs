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
}
