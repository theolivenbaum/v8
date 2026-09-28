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
    public void StoreToUndefinedMessage()
    {
        Assert.Equal("TypeError: Cannot set properties of undefined (setting 'x')", RunString("""
            var m;
            try { var u; u.x = 1; } catch (e) { m = String(e); }
            m;
            """));
    }
}
