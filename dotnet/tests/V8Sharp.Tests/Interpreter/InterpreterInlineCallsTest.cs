// Calls and constructs the dispatch loop runs without a .NET call
// (InterpreterInlineCalls): the construct fast path and the Wide-prefixed calls.
using V8Sharp.Codegen;

namespace V8Sharp.Tests.Interpreter;

public class InterpreterInlineCallsTest : TestWithContext
{
    string RunString(string source) => ObjectOps.ToString(i_isolate, Compiler.CompileAndRun(i_isolate, source)).ToString();

    /// <summary>Statements that use up more than 256 feedback slots, so the calls after them are Wide.</summary>
    static string Padding(int count = 300)
    {
        var sb = new System.Text.StringBuilder();
        for (int i = 0; i < count; i++) sb.Append("if (pad.f").Append(i).Append(" === 1) n++;\n");
        return sb.ToString();
    }

    [Fact]
    public void ConstructFastPath()
    {
        Assert.Equal("3,4,true,7,5,obj,true,2", RunString("""
            function V(x, y) { this.x = x; this.y = y; this.t = new.target === V; }
            class P { constructor(a) { this.a = a + 2; } }
            function R() { this.v = 1; return { kind: "obj" }; }
            function Missing(a, b) { this.b = b; }
            var v, p, r, m;
            for (var i = 0; i < 20; i++) { v = new V(3, 4); p = new P(5); r = new R(); m = new Missing(1); }
            var thrown = false;
            function T() { throw new Error("x"); }
            try { new T(); } catch (e) { thrown = true; }
            [v.x, v.y, v.t, p.a, p.a - 2, r.kind, thrown && m.b === undefined, Object.keys(v).length - 1].join();
            """));
    }

    [Fact]
    public void WideCalls()
    {
        Assert.Equal("10,11,12,13,20,21,22,23,obj,caught,9", RunString($$"""
            function M() { this.v = 10; }
            M.prototype.m0 = function () { return this.v; };
            M.prototype.m1 = function (a) { return this.v + a; };
            M.prototype.m2 = function (a, b) { return this.v + a + b; };
            M.prototype.m3 = function (a, b, c) { return this.v + a + b + c; };
            function u0() { return 20; }
            function u1(a) { return 20 + a; }
            function u2(a, b) { return 20 + a + b; }
            function u3(a, b, c) { return 20 + a + b + c; }
            function C(k) { this.kind = k; }
            function thrower() { throw new Error("w"); }
            function run() {
              var pad = {}, n = 0, x = new M(), out;
              {{Padding()}}
              for (var i = 0; i < 20; i++) {
                out = [x.m0(), x.m1(1), x.m2(1, 1), x.m3(1, 1, 1), u0(), u1(1), u2(1, 1), u3(1, 1, 1), new C("obj").kind];
                try { thrower(); } catch (e) { out.push("caught"); }
              }
              out.push(n + 9);
              return out.join();
            }
            run();
            """));
    }

    [Fact]
    public void WideCallStackTrace()
    {
        Assert.Contains("at inner", RunString($$"""
            function inner() { return new Error("s").stack; }
            function run() {
              var pad = {}, n = 0;
              {{Padding()}}
              return inner();
            }
            run();
            """));
    }
}
