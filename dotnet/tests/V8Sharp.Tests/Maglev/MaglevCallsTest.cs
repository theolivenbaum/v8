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
    };

    [Theory]
    [MemberData(nameof(Snippets))]
    public void SameResultWhenOptimized(string source) => MaglevCompilerTest.AssertSameWhenOptimized(source);

    [Fact]
    public void KnownCallsEnterTheCalleesMaglevCodeDirectly()
    {
        // Both functions optimized; the callee is too big to inline, so the
        // caller's CallKnownJSFunction enters its code (a deopt in it
        // continues in the interpreter and returns to the caller's code).
        Assert.Equal("5,7,8,7.5,8,0", MaglevCompilerTest.Run("--maglev", """
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
