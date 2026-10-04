// Tests of calls between Maglev code (MaglevCalls, "Direct calls"): a call
// of a known function enters the callee's Maglev code through its direct
// entry (the receiver and arguments as values, the frame built with the
// callee's constants), and everything a frame provides still works: fewer
// arguments, arguments objects and function.arguments, receiver
// conversion, deopts and exceptions in the callee, stack traces, stack
// overflow, new.target.
namespace V8Sharp.Tests.Maglev;

public class MaglevCallsTest
{
    public static TheoryData<string> Snippets => new()
    {
        """
        (function() {
          'use strict';
          function big(o, a, b) {
            if (a === -1) { o.x = a; o.y = b; o.z = a + b; o.w = a * b; o.x = a; o.y = b; o.z = a + b; o.w = a * b; o.x = a; o.y = b; o.z = a + b; }
            return (o.v + a) * 2 + (b === undefined ? 1000 : b);
          }
          function args() { return arguments.length + ':' + [].join.call(arguments, '/'); }
          function self() { return this === undefined ? 'u' : typeof this; }
          function nt() { return new.target === undefined; }
          var o = { v: 1, x: 0, y: 0, z: 0, w: 0 };
          var out = [];
          for (var k = 0; k < 40; k++) {
            out.push(big(o, k, 2), big(o, k), big(o, 1.5, 'b'), args(k, 'x'), args(), self(), nt());
          }
          return out.join();
        })()
        """,
        """
        (function() {
          function big(o, a) {
            if (a < -100) { o.x = a; o.y = a; o.z = a; o.w = a; o.x = a; o.y = a; o.z = a; o.w = a; o.x = a; o.y = a; o.z = a; }
            return this === o ? 'self' : typeof this === 'object' ? (this === globalThis ? 'global' : 'obj') : typeof this;
          }
          function callIt(f, r, o) { return f.call(r, o, 1); }
          function plain(o) { return big(o, 1); }
          var o = {};
          var out = [];
          for (var k = 0; k < 40; k++) out.push(plain(o), callIt(big, o, o), callIt(big, 5, o), callIt(big, 'str', o), callIt(big, undefined, o));
          return out.join();
        })()
        """,
        // Deopts in the callee (and in the caller after the call), exceptions
        // through direct calls, stack traces.
        """
        (function() {
          function callee(o, k) {
            var s = 0;
            for (var i = 0; i < 3; i++) s += o.v;
            if (k === 33) return o.missing.x;
            if (k > 35) s += 0.5;
            return s;
          }
          function caller(o, k) {
            try { return callee(o, k) + 1; } catch (e) { return e.constructor.name + ':' + e.stack.split('\n')[1].trim().split(' ')[1]; }
          }
          var out = [];
          for (var k = 0; k < 40; k++) out.push(caller({ v: k }, k), caller(k == 20 ? { w: 1, v: 's' } : { v: 2 }, k));
          return out.join();
        })()
        """,
        // Recursion through direct calls up to a stack overflow.
        """
        (function() {
          function rec(n) { if (n === 0) return 0; return 1 + rec(n - 1); }
          var out = [];
          for (var k = 0; k < 40; k++) out.push(rec(k));
          try { rec(1e7); } catch (e) { out.push(e instanceof RangeError); }
          out.push(rec(100));
          return out.join();
        })()
        """,
        // Constructs entering the constructor's direct entry: new.target,
        // arguments, an object result, the Class.create pattern.
        """
        (function() {
          var Class = { create: function() { return function() { this.initialize.apply(this, arguments); }; } };
          var P = Class.create();
          P.prototype = { initialize: function(x, y) { this.x = x; this.y = y; this.nt = typeof new.target; } };
          function Q(a, b) { this.s = arguments.length + ':' + (new.target === Q) + ':' + a; if (b === 'obj') return { o: 1 }; }
          function make(k) { var p = new P(k, k + 1), q = new Q(k), r = new Q(k, 'obj'); return [p.x + p.y, p.nt, q.s, r.o, r.s].join('/'); }
          var out = [];
          for (var k = 0; k < 40; k++) out.push(make(k));
          return out.join();
        })()
        """,
        // Functions reading their actual arguments (arguments objects, rest
        // parameters) entered directly with fewer, as many and more arguments
        // than formal parameters, and beyond the direct entry's arity; apply
        // forwarding them with megamorphic targets.
        """
        (function() {
          function count(a) { var s = arguments.length + ':'; for (var i = 0; i < arguments.length; i++) s += arguments[i]; return s + a; }
          function rest(a, ...r) { 'use strict'; return a + '/' + r.length + '/' + r.join('') + '/' + arguments.length; }
          function sum() { var t = 0; for (var i = 0; i < arguments.length; i++) t += arguments[i]; return t; }
          var fs = [];
          for (var j = 0; j < 5; j++) fs.push(new Function('a', 'b', 'return a + b * ' + j + ';'));
          function fwd() { return fs[arguments[0] % 5].apply(this, arguments); }
          function C() { this.v = sum.apply(null, arguments); this.n = arguments.length; }
          var out = [];
          for (var k = 0; k < 40; k++) {
            out.push(count(), count(k), count(k, 1), count(k, 1, 2, 3, 4, 5), count(1, 2, 3, 4, 5, 6, 7, 8));
            out.push(rest(), rest(k), rest(k, 1, 2), rest(1, 2, 3, 4, 5, 6, 7, 8));
            out.push(sum(), sum(k), sum(1, 2, 3, 4, 5, 6), sum(1, 2, 3, 4, 5, 6, 7), fwd(k, 2), fwd(k, 3, 4));
            var c = new C(k, 1, 2), d = new C();
            out.push(c.v, c.n, d.v, d.n);
          }
          return out.join();
        })()
        """,
        // function.arguments of a frame entered through a direct call.
        """
        (function() {
          function inner() { return outer.arguments.length + '/' + outer.arguments[0] + '/' + outer.arguments[1]; }
          function outer(a, b) { var r = inner(); a = 7; return r + '|' + inner(); }
          var out = [];
          for (var k = 0; k < 40; k++) out.push(outer(k), outer(k, 'x'));
          return out.join();
        })()
        """,
        // Generic calls (megamorphic targets) with the arguments as values
        // (MaglevCalls.CallWithValuesN): fewer and more arguments than the
        // callee's parameters, arguments objects, sloppy receivers, class
        // constructors, builtins, deopts and throws in the callee.
        """
        (function() {
          function f0() { return 'f0:' + arguments.length + (this === undefined ? 'u' : typeof this); }
          function f1(a) { return 'f1:' + a + ':' + arguments.length; }
          function f2(a, b) { 'use strict'; return 'f2:' + a + ':' + b + ':' + (this === undefined ? 'u' : typeof this); }
          function f3(a, b, c) { return 'f3:' + a + b + c + (c === undefined); }
          function f5(a, b, c, d, e) { return 'f5:' + a + b + c + d + e; }
          function rest(...r) { return 'rest:' + r.length + r.join(''); }
          function thrower(a) { if (a === 13) throw new Error('t' + a); return 'ok' + a; }
          function deopter(a) { return a.x + 1; }
          class K { constructor(a) { this.a = a; } }
          var fs = [f0, f1, f2, f3, f5, rest, thrower, deopter, K, Math.max, String];
          function call0(f) { return f(); }
          function call1(f, a) { return f(a); }
          function call2(f, a, b) { return f(a, b); }
          function call3(f, a, b, c) { return f(a, b, c); }
          function m1(o, f, a) { o.f = f; return o.f(a); }
          var out = [];
          for (var k = 0; k < 40; k++) {
            for (var f of fs) {
              var arg = f === deopter ? (k < 30 ? { x: k } : { y: 1, x: 0.5 }) : k;
              try { out.push(call0(f)); } catch (e) { out.push(e.constructor.name); }
              try { out.push(call1(f, arg)); } catch (e) { out.push(e.constructor.name + e.message); }
              try { out.push(call2(f, arg, 'b')); } catch (e) { out.push(e.constructor.name); }
              try { out.push(call3(f, arg, 'b', 'c')); } catch (e) { out.push(e.constructor.name); }
              try { out.push(m1({ v: k }, f, arg)); } catch (e) { out.push(e.constructor.name); }
            }
          }
          return out.join();
        })()
        """,
        // Polymorphic method calls (polymorphic load continuations): each map's
        // arm calls its own method (inlined when small, a direct call
        // otherwise); argument computations between the load and the call,
        // throws and deopts in an arm, a new map after optimization, calls
        // in try blocks, the arms' frames merging after the call.
        """
        (function() {
          function A(v) { this.v = v; } A.prototype.run = function (p, q) { return this.v + p + q; };
          function B(v) { this.v = v; this.w = 1; }
          B.prototype.run = function (p, q) { var s = 0; for (var i = 0; i < 3; i++) s += this.v * p + this.w + q; return s; };
          function C(v) { this.c = 0; this.v = v; } C.prototype.run = function (p) { this.c++; return this.v - p; };
          function D(v) { this.v = v; } D.prototype.run = function (p) { if (p % 13 === 0) throw new Error('d' + p); return 'd' + p; };
          function E(v) { this.v = v; } E.prototype.run = function (p) { return this.v.x + p; };
          function drive(objs, n, base) {
            var out = [];
            for (var k = 0; k < n; k++) {
              var o = objs[k % objs.length];
              var a = k * 2, b = base + 'x';
              try {
                var r = o.run(a + 1, b.length);
                out.push(r);
              } catch (e) { out.push('caught:' + e.message); }
              out.push(o.run(k, 1) + ':' + a);
            }
            return out.join();
          }
          var objs = [new A(1), new B(2), new C(3)], res = [];
          for (var j = 0; j < 40; j++) res.push(drive(objs, 12, 'b' + j));
          objs.push(new D(4));
          for (var j = 0; j < 10; j++) res.push(drive(objs, 12, 'c'));
          objs.push(new E({ x: 5 }));
          for (var j = 0; j < 10; j++) res.push(drive(objs, 12, 'e'));
          objs[4].v = { y: 1, x: 2.5 };
          res.push(drive(objs, 12, 'f'));
          return res.join(';');
        })()
        """,
    };

    public static TheoryData<string> FramelessSnippets => new()
    {
        // Leaf callees entered without a frame (frameless direct entries):
        // eager deopts of the leaf (wrong map, overflow, a non-number) build
        // the frame and continue in the interpreter, from direct calls and
        // constructs; the caller's frame and stack traces stay exact.
        """
        (function() {
          function get(o) { return o.x * 2 + o.y; }
          function add(a, b) { return a + b | 0; }
          function Pt(x, y) { this.x = x; this.y = y; }
          function where() { try { null.f(); } catch (e) { return e.stack.split('at ').length; } }
          function caller(o, k) {
            var r = get(o) + add(k, 1);
            var p = new Pt(k, r);
            return r + ':' + p.x + ':' + p.y + ':' + where();
          }
          var out = [];
          for (var k = 0; k < 60; k++) {
            var o = k < 40 ? { x: k, y: 1 } : k < 50 ? { y: 2, x: k } : { x: 'a' + k, y: 3 };
            out.push(caller(o, k == 55 ? 2147483647 : k));
          }
          return out.join();
        })()
        """,
    };

    [Theory]
    [MemberData(nameof(FramelessSnippets))]
    public void FramelessEntriesGiveTheSameResults(string source)
    {
        MaglevCompilerTest.AssertSameWhenOptimized(source);
        // With little inlining, so the leaves are called directly.
        string interpreted = MaglevCompilerTest.Run("--no-maglev --no-sparkplug", source);
        Assert.Equal(interpreted, MaglevCompilerTest.Run(
            "--maglev --no-concurrent-recompilation --invocation-count-for-maglev=2 --invocation-count-for-feedback-allocation=1 " +
            "--max-maglev-inlined-bytecode-size=0 --max-maglev-inlined-bytecode-size-small=0", source));
    }

    [Theory]
    [MemberData(nameof(Snippets))]
    public void SameResultWhenOptimized(string source) => MaglevCompilerTest.AssertSameWhenOptimized(source);

    [Fact]
    public void KnownCallsEnterTheCalleesMaglevCodeDirectly()
    {
        // Both functions optimized; the callee is too big to inline (V8's
        // default limit, not the top-tier one), so the
        // caller's CallKnownJSFunction enters its code (a deopt in it
        // continues in the interpreter and returns to the caller's code).
        Assert.Equal("5,7,8,7.5,8,0", MaglevCompilerTest.Run("--maglev --max-maglev-inlined-bytecode-size=100", """
            function callee(a, b) {
              if (a === 'never') { a = 1; a = 2; a = 3; a = 4; a = 5; a = 6; a = 7; a = 8; a = 9; a = 10; a = 11; a = 12; a = 13; a = 14;
                a = 15; a = 16; a = 17; a = 18; a = 19; a = 20; a = 21; a = 22; a = 23; a = 24; a = 25; a = 26; a = 27; a = 28; }
              return a + b;
            }
            function caller(x) { return callee(x, 3); }
            %PrepareFunctionForOptimization(callee);
            %PrepareFunctionForOptimization(caller);
            caller(1); caller(2);
            %OptimizeMaglevOnNextCall(callee);
            %OptimizeMaglevOnNextCall(caller);
            var r = [caller(2), caller(4)];
            r.push(%GetOptimizationStatus(caller) & 8, caller(4.5), %GetOptimizationStatus(caller) & 8, %GetOptimizationStatus(callee) & 8);
            r.join();
            """));
    }
}
