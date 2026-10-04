// Tests of Maglev's inlined allocations and escape analysis
// (MaglevVirtualObjects.cs, MaglevGraphBuilder.Allocation.cs): objects of
// known initial maps allocated by the code, the fields of non-escaping ones
// as SSA values, their elision, and their materialization at deopts (the
// translation's captured objects). Differential (interpreter vs forced
// optimization), plus facts on the graphs: which allocations are elided.
using V8Sharp.Codegen;
using V8Sharp.Maglev;

namespace V8Sharp.Tests.Maglev;

public class MaglevEscapeAnalysisTest
{
    public static TheoryData<string> Snippets => new()
    {
        // A constructor's object read back and dropped (elided), then a field
        // of another type: the transition's checks deoptimize with a partly
        // initialized object, which the interpreter finishes.
        """
        (function() {
          function P(x, y) { this.x = x; this.y = y; }
          function f(a, b) { var p = new P(a, b); return p.x * 2 + p.y; }
          var out = [];
          for (var k = 0; k < 40; k++) out.push(f(k, k + 1));
          out.push(f(1.5, 2), f('s', 1), f(1, 's'), f(undefined, null), f({}, []));
          for (var k = 0; k < 10; k++) out.push(f(k, 0.5));
          return out.join();
        })()
        """,
        // Deopts while the object is live in several registers and frames:
        // one materialized object (identity), with the fields of its point.
        """
        (function() {
          function P(x, y) { this.x = x; this.y = y; }
          function g(p, q, z) { return p.x - z + q.y; }
          function f(a, b, z) {
            var p = new P(a, b);
            var q = p;
            var s = g(p, q, z);
            return [s, p === q, p.x, p.y, Object.keys(p).join('')].join(':');
          }
          var out = [];
          for (var k = 0; k < 40; k++) out.push(f(k, 2, 1));
          out.push(f(1, 2, 'z'), f(1, 2, {}), f(2.5, 3, 1));
          return out.join();
        })()
        """,
        // Stores after construction, loads of fields that were never stored
        // (undefined), prototype fields, and a deopt in between.
        """
        (function() {
          function V(x) { this.x = x; this.y = 0; }
          V.prototype.z = 7;
          function f(a, d) {
            var v = new V(a);
            v.y = v.x + 1;
            v.x = d;
            var t = v.x + v.y + v.z;
            return t + ',' + v.x + ',' + v.y;
          }
          var out = [];
          for (var k = 0; k < 40; k++) out.push(f(k, k * 2));
          out.push(f(1, 'd'), f(0.5, 1), f(1, 2));
          return out.join(';');
        })()
        """,
        // Escapes: returned, stored in another object, passed to a call,
        // compared, in a phi, in a closure's context, thrown.
        """
        (function() {
          function P(x) { this.x = x; }
          var keep = [];
          function a(x) { return new P(x); }
          function b(x) { var p = new P(x); keep.push(p); return p.x; }
          function c(x) { var p = new P(x); return JSON.stringify(p); }
          function d(x, o) { var p = new P(x); return p === o; }
          function e(x, c) { var p = c ? new P(x) : new P(-x); return p.x; }
          function g(x) { var p = new P(x); return function() { return p.x; }; }
          function h(x) { try { throw new P(x); } catch (q) { return q.x; } }
          function i(x) { var p = new P(x); var o = { p: p }; o.p.x = 5; return p.x; }
          var out = [];
          for (var k = 0; k < 40; k++) out.push(a(k).x, b(k), c(k), d(k, null), e(k, k & 1), g(k)(), h(k), i(k));
          out.push(keep.length);
          return out.join();
        })()
        """,
        // Allocations in loops: per iteration (elided), and one from before
        // the loop read and written in it.
        """
        (function() {
          function P(x, y) { this.x = x; this.y = y; }
          function f(n) {
            var s = 0;
            var acc = new P(0, 0);
            for (var i = 0; i < n; i++) {
              var p = new P(i, i * 2);
              s += p.x + p.y;
              acc.x = acc.x + p.y;
            }
            return s + ':' + acc.x + ':' + acc.y;
          }
          var out = [];
          for (var k = 0; k < 40; k++) out.push(f(k));
          out.push(f(2.5), f('3'));
          return out.join();
        })()
        """,
        // Branches: different stores on the two paths (the versions differ
        // at the merge), and the same object on both.
        """
        (function() {
          function P(x) { this.x = x; this.y = 1; }
          function f(a, c) {
            var p = new P(a);
            if (c) p.y = 2; else p.y = 3;
            var q = new P(a);
            if (c) a = a + 1;
            return p.x + p.y + q.y + a;
          }
          var out = [];
          for (var k = 0; k < 40; k++) out.push(f(k, k & 1));
          out.push(f('a', 1), f(0.5, 0));
          return out.join();
        })()
        """,
        // try/catch around stores and a throw after them.
        """
        (function() {
          function P(x) { this.x = x; }
          function f(a) {
            var p = new P(a);
            try {
              p.x = p.x + 1;
              if (a > 30) throw p.x;
              p.x = p.x * 2;
            } catch (e) {
              return 'c' + e + p.x;
            }
            return p.x;
          }
          var out = [];
          for (var k = 0; k < 40; k++) out.push(f(k));
          return out.join();
        })()
        """,
        // Constructors with more than four fields, nested news, getters.
        """
        (function() {
          function Big(a) { this.a = a; this.b = a + 1; this.c = a + 2; this.d = a + 3; this.e = a + 4; this.f = a + 5; }
          function Pair(car, cdr) { this.car = car; this.cdr = cdr; }
          Object.defineProperty(Big.prototype, 'sum', { get() { return this.a + this.f; } });
          function f(k) {
            var b = new Big(k);
            var p = new Pair(k, new Pair(k + 1, null));
            return b.a + b.b + b.c + b.d + b.e + b.f + b.sum + p.car + p.cdr.car;
          }
          var out = [];
          for (var k = 0; k < 40; k++) out.push(f(k));
          out.push(f('x'), f(0.25));
          return out.join();
        })()
        """,
        // Inlined functions with arguments objects: apply(this, arguments)
        // forwards the call's arguments (Class.create's constructors), and
        // escaping arguments objects; deopts inside them with more arguments
        // than formal parameters (the extra ones are in the deopt frames).
        """
        (function() {
          var Class = { create: function() { return function() { this.initialize.apply(this, arguments); } } };
          var V = Class.create();
          V.prototype = { initialize: function(x, y, z) { this.x = x ? x : 0; this.y = y ? y : 0; this.z = z; } };
          function mk(a) { return new V(a, a + 1); }
          function len() { return arguments.length + ':' + arguments[arguments.length - 1]; }
          function strict() { 'use strict'; return Array.prototype.join.call(arguments, '-'); }
          function extra(a) { var t = a * 2; return t + ',' + arguments[1] + ',' + arguments.length; }
          function g(k) { var v = mk(k); return v.x + v.y + ':' + v.z + ';' + len(k, k + 1, 'e') + ';' + strict(k, 2) + ';' + extra(k, 'b', 'c'); }
          var out = [];
          for (var k = 0; k < 40; k++) out.push(g(k));
          out.push(g('s'), g(1.5), g(null));
          return out.join('|');
        })()
        """,
        // A deopt in an inlined callee called with extra arguments, which
        // the interpreter then reads through arguments.
        """
        (function() {
          function callee(a) { var t = a - 1; return t + ':' + arguments.length + ':' + arguments[2]; }
          function caller(a, b, c) { return callee(a, b, c, 4); }
          var out = [];
          for (var k = 0; k < 40; k++) out.push(caller(k, 'b', 'c' + k));
          out.push(caller('x', 1, 2), caller({}, 3, 4));
          return out.join();
        })()
        """,
    };

    [Theory]
    [MemberData(nameof(Snippets))]
    public void SameResultWhenOptimized(string source) => MaglevCompilerTest.AssertSameWhenOptimized(source);

    /// <summary>
    /// Builds the optimized graph of the function <paramref name="source"/>
    /// returns (after it ran, so it has feedback) and returns the
    /// InlinedAllocations that remain and those elided.
    /// </summary>
    static (int Remaining, int Elided) Allocations(string flags, string source)
    {
        var flagList = new FlagList();
        flagList.SetFlagsFromString("--allow-natives-syntax --maglev " + flags);
        Isolate isolate = Isolate.New(flagList);
        using (isolate.Enter())
        {
            var function = (JSFunction)Compiler.CompileAndRun(isolate, source).Object;
            MaglevCompilationInfo info = MaglevCompiler.BuildAndOptimizeGraph(isolate, function, -1, concurrent: false);
            int remaining = 0;
            var all = new HashSet<InlinedAllocation>();
            foreach (BasicBlock block in info.Graph.Blocks)
            {
                if (block.IsDead) continue;
                foreach (Node node in block.Nodes)
                {
                    if (node is InlinedAllocation a)
                    {
                        remaining++;
                        Assert.False(a.IsElided);
                    }
                }
            }
            int elided = 0;
            foreach (BasicBlock block in info.Graph.Blocks)
            {
                foreach (Node node in block.Nodes)
                {
                    foreach (ValueNode input in node.Inputs)
                    {
                        if (input is InlinedAllocation { IsElided: true } && node.Opcode != Opcode.EnterInlinedFrame) Assert.Fail($"n{node.Id} uses an elided allocation");
                    }
                    void Collect(DeoptInfo? d)
                    {
                        for (DeoptFrame? f = d?.TopFrame; f is not null; f = f.Parent)
                        {
                            foreach ((V8Sharp.Interpreter.Register _, ValueNode v) in ((InterpretedDeoptFrame)f).Values)
                            {
                                if (v is InlinedAllocation { IsElided: true } a) all.Add(a);
                            }
                        }
                    }
                    Collect(node.EagerDeoptInfo);
                    Collect(node.LazyDeoptInfo);
                }
            }
            elided = all.Count;
            return (remaining, elided);
        }
    }

    const string kVectorSetup = """
        function V(x, y, z) { this.x = x; this.y = y; this.z = z; }
        V.prototype.dot = function (w) { return this.x * w.x + this.y * w.y + this.z * w.z; };
        function sub(v, w) { return new V(v.x - w.x, v.y - w.y, v.z - w.z); }
        """;

    [Fact]
    public void NonEscapingConstructorObjectIsElided()
    {
        // The object only flows into an inlined method: no allocation is left
        // (and the deopt frames of the checks after it capture it).
        (int remaining, int elided) = Allocations("", kVectorSetup + """
            function f(a, b) { var d = sub(a, b); return d.dot(d) + d.x; }
            %PrepareFunctionForOptimization(f);
            for (var i = 0; i < 100; i++) f(new V(1, 2, 3), new V(0.5, 0.25, 0.125));
            f;
            """);
        Assert.Equal(0, remaining);
        Assert.True(elided >= 1);
    }

    [Fact]
    public void EscapingObjectIsAllocated()
    {
        (int remaining, int _) = Allocations("", kVectorSetup + """
            var keep;
            function f(a, b) { var d = sub(a, b); keep = d; return d.x; }
            %PrepareFunctionForOptimization(f);
            for (var i = 0; i < 100; i++) f(new V(1, 2, 3), new V(0.5, 0.25, 0.125));
            f;
            """);
        Assert.Equal(1, remaining);
    }

    [Fact]
    public void WithoutEscapeAnalysisTheObjectIsAllocated()
    {
        (int remaining, int elided) = Allocations("--no-maglev-escape-analysis", kVectorSetup + """
            function f(a, b) { var d = sub(a, b); return d.dot(d); }
            %PrepareFunctionForOptimization(f);
            for (var i = 0; i < 100; i++) f(new V(1, 2, 3), new V(0.5, 0.25, 0.125));
            f;
            """);
        Assert.Equal(1, remaining);
        Assert.Equal(0, elided);
    }

    static void AssertSameAsInterpreter(string source) =>
        Assert.Equal(MaglevCompilerTest.Run("--no-maglev --no-sparkplug", source), MaglevCompilerTest.Run("--maglev", source));

    [Fact]
    public void DeoptMaterializesTheElidedObject()
    {
        // f is optimized with the object elided; the call with a string
        // deoptimizes at the subtraction after the allocation, and the
        // interpreter continues with the materialized object (its map, its
        // fields, its identity in both registers).
        AssertSameAsInterpreter("""
            function V(x, y, z) { this.x = x; this.y = y; this.z = z; }
            function f(a, b) {
              var v = new V(a, 2, 3);
              var w = v;
              var t = v.x - b;
              return [t, w === v, Object.keys(v).join(), [v.x, v.y, v.z].join(), v instanceof V, v.x + b, %GetOptimizationStatus(f) & 16].join('|');
            }
            %PrepareFunctionForOptimization(f);
            f(1, 0.5); f(2, 0.5);
            %OptimizeFunctionOnNextCall(f);
            var r = [f(1, 0.5), f(1, {}), f(3, 0.5)];
            r.join('/').replace(/\|(16|0)(\/|$)/g, '$2');
            """);
    }

    [Fact]
    public void DeoptInsideTheInlinedConstructorMaterializesAPartialObject()
    {
        // The constructor is inlined; the second field's Smi check fails
        // after the first field was stored (the virtual object has the first
        // transition only).
        AssertSameAsInterpreter("""
            function P(x, y) { this.x = x; this.y = y; }
            function f(a, b) { var p = new P(a, b); return p.x + ',' + p.y + ',' + p.constructor.name + ':' + Object.keys(p).join(); }
            %PrepareFunctionForOptimization(f);
            f(1, 2); f(3, 4);
            %OptimizeFunctionOnNextCall(f);
            [f(5, 6), f(1, 's'), f(7, 8)].join('/');
            """);
    }
}
