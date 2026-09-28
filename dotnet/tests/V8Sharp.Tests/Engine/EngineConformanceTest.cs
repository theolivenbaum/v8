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

    // test262 staging/decorators/{public,private}-auto-accessor, mjsunit/decorators/*:
    // --js-decorators reaches the parser.
    [Fact]
    public void AutoAccessorsWithJsDecorators()
    {
        i_isolate.Flags.js_decorators = true;
        Assert.Equal("2,3,true", RunString("""
            class C { accessor x = 1; static accessor #y = 3; static y() { return C.#y; } }
            var c = new C(); c.x = 2;
            [c.x, C.y(), typeof Object.getOwnPropertyDescriptor(C.prototype, "x").get === "function"].join();
            """));
    }

    // mjsunit/call-intrinsic-fuzzing, natives-builtins: Runtime::IsEnabledForFuzzing
    // and CHECK_UNLESS_FUZZING.
    [Fact]
    public void NativesSyntaxUnderFuzzing()
    {
        i_isolate.Flags.allow_natives_syntax = true;
        i_isolate.Flags.fuzzing = true;
        Assert.Equal("undefined,undefined,undefined,true,undefined,ConcatenatedString", RunString("""
            [String(%ConstructConsString("a", "b")), String(%ConstructConsString(1, 2, 3, 4)), String(%FooBar()),
             %IsBeingInterpreted(1, 2, 3), String(%DeoptimizeFunction()),
             %ConstructConsString("Concatenated", "String")].join();
            """));
    }

    // mjsunit/disallow-codegen-from-strings: direct eval and Function check
    // allow_code_gen_from_strings (--disallow-code-generation-from-strings).
    [Fact]
    public void DisallowCodeGenerationFromStrings()
    {
        i_isolate.NativeContext.AllowCodeGenFromStrings = JSValue.False;
        Assert.Equal("EvalError: Code generation from strings disallowed for this context|EvalError|EvalError|5", RunString("""
            var r = [];
            try { eval("1 + 1"); } catch (e) { r.push(String(e)); }
            try { (0, eval)("1 + 1"); } catch (e) { r.push(e.name); }
            try { Function("x", "return x"); } catch (e) { r.push(e.name); }
            r.push(eval(5));
            r.join("|");
            """));
    }

    // mjsunit/elements-kind, regress/regress-trap-allocation-memento,
    // array-prototype-map-elements-kinds: allocation mementos feed transitions
    // back into the AllocationSite (literals, empty literals, new Array).
    [Fact]
    public void AllocationSiteElementsKindFeedback()
    {
        i_isolate.Flags.allow_natives_syntax = true;
        Assert.Equal("true,true,true,true,true", RunString("""
            function lit() { return [1, 2, 3]; }
            function empty() { return []; }
            function ctor() { return new Array(); }
            %EnsureFeedbackVectorForFunction(lit);
            %EnsureFeedbackVectorForFunction(empty);
            %EnsureFeedbackVectorForFunction(ctor);
            var r = [];
            var a = lit(); r.push(%HasSmiElements(a)); a[0] = 1.5; r.push(%HasDoubleElements(lit()));
            var b = empty(); b.push({}); r.push(%HasObjectElements(empty()));
            var c = ctor(); r.push(%HasSmiElements(c)); c.push(0.5); r.push(%HasDoubleElements(ctor()));
            r.join();
            """));
    }
}
