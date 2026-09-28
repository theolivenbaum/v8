// Regression tests for engine-side conformance fixes (declarations, realms,
// eval, stack traces). Each test names the mjsunit/test262 test it stands for.
using V8Sharp.Codegen;
using V8Sharp.Init;

namespace V8Sharp.Tests.Engine;

public class EngineConformanceTest : TestWithContext
{
    JSValue Run(string source) => Compiler.CompileAndRun(i_isolate, source);

    string RunString(string source) => ObjectOps.ToString(i_isolate, Run(source)).ToString();

    /// <summary>Installs a second realm's global object as <c>other</c> (same security token).</summary>
    void InstallOtherRealm()
    {
        NativeContext main = i_isolate.NativeContext;
        NativeContext other = Bootstrapper.CreateEnvironment(i_isolate);
        other.SecurityToken = main.SecurityToken;
        JSObject.AddProperty(i_isolate, main.GlobalObject, "other", other.GlobalProxyObject, PropertyAttributes.NONE);
    }

    // test262 language/global-code/script-decl-var-err, staging/sm/global/adding-global-var-nonextensible-error,
    // language/eval-code/{direct,indirect}/non-definable-global-var.
    [Fact]
    public void VarOnNonExtensibleGlobalThrows()
    {
        Assert.Equal("TypeError: Cannot add property x, object is not extensible|false", RunString("""
            var executed = false, m;
            Object.preventExtensions(this);
            try { eval("executed = true; var x;"); } catch (e) { m = String(e); }
            m + "|" + executed;
            """));
    }

    // test262 language/global-code/script-decl-func-err-non-extensible (through indirect eval).
    [Fact]
    public void FunctionOnNonExtensibleGlobalThrows()
    {
        Assert.Equal("TypeError", RunString("""
            Object.preventExtensions(this);
            var m;
            try { (0, eval)("function g() {}"); } catch (e) { m = e.name; }
            m;
            """));
    }

    // test262 built-ins/Function/internals/Construct/derived-return-val-realm.
    [Fact]
    public void DerivedConstructorReturnErrorComesFromCallerRealm()
    {
        InstallOtherRealm();
        Assert.Equal("true,false", RunString("""
            var C = other.eval('0, class extends Object { constructor() { return null; } }');
            var r;
            try { new C(); } catch (e) { r = [e.constructor === TypeError, e.constructor === other.TypeError]; }
            r.join();
            """));
    }
}
