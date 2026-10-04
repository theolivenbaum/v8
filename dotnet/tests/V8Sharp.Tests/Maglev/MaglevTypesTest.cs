// Tests of Maglev's type and map-check elimination: phi types from their
// inputs (MergePointInterpreterFrameState::MergeValue, post-loop types), map
// loads without the receiver check when the input is known to be a
// JSReceiver (CheckType), and prototype chain validity as a code dependency
// (the handlers' validity cells) instead of a check per access. Each must
// give the interpreter's results and deoptimize where it must.
namespace V8Sharp.Tests.Maglev;

public class MaglevTypesTest
{
    public static TheoryData<string> PrototypeChainSnippets => new()
    {
        // A method from the prototype, then the prototype's method replaced,
        // shadowed by an own property, deleted, and the prototype swapped.
        """
        (function() {
          function P() { this.x = 1; }
          P.prototype.m = function() { return 'p' + this.x; };
          function call(o) { return o.m(); }
          var out = [];
          for (var i = 0; i < 40; i++) out.push(call(new P()));
          P.prototype.m = function() { return 'q' + this.x; };
          for (var i = 0; i < 5; i++) out.push(call(new P()));
          var o = new P(); o.m = function() { return 'own'; };
          out.push(call(o), call(new P()));
          delete P.prototype.m;
          try { out.push(call(new P())); } catch (e) { out.push(e.constructor.name); }
          Object.prototype.m = function() { return 'object'; };
          out.push(call(new P()));
          var swapped = new P();
          Object.setPrototypeOf(swapped, { m: function() { return 'swapped'; } });
          out.push(call(swapped), call(new P()));
          delete Object.prototype.m;
          return out.join();
        })()
        """,
        // A load that finds nothing (undefined through the chain), then a
        // property appears on the prototype, then on Object.prototype.
        """
        (function() {
          function A() { this.a = 1; }
          function B() { this.a = 2; }
          B.prototype = Object.create(A.prototype);
          function get(o) { return o.missing; }
          var out = [];
          for (var i = 0; i < 40; i++) out.push(get(i & 1 ? new A() : new B()));
          A.prototype.missing = 'onA';
          out.push(get(new A()), get(new B()));
          B.prototype.missing = 'onB';
          out.push(get(new A()), get(new B()));
          delete A.prototype.missing; delete B.prototype.missing;
          Object.prototype.missing = 'onObject';
          out.push(get(new A()), get(new B()));
          delete Object.prototype.missing;
          out.push(get(new A()));
          return out.join();
        })()
        """,
        // Stores that transition (the transition's validity cell), then a
        // setter on the prototype chain and a read-only prototype property.
        """
        (function() {
          function C() {}
          function add(o, v) { o.y = v; return o.y; }
          var out = [];
          for (var i = 0; i < 40; i++) out.push(add(new C(), i));
          Object.defineProperty(C.prototype, 'y', { set: function(v) { out.push('setter' + v); }, get: function() { return 'getter'; }, configurable: true });
          out.push(add(new C(), 7));
          delete C.prototype.y;
          Object.defineProperty(C.prototype, 'y', { value: 'ro', writable: false, configurable: true });
          out.push(add(new C(), 8));
          delete C.prototype.y;
          out.push(add(new C(), 9));
          return out.join();
        })()
        """,
        // The prototype changes inside the optimized function's loop.
        """
        (function() {
          function K() { this.v = 1; }
          K.prototype.f = function() { return this.v; };
          function loop(n, flipAt) {
            var s = 0;
            for (var i = 0; i < n; i++) {
              s += new K().f();
              if (i === flipAt) K.prototype.f = function() { return 100; };
            }
            return s;
          }
          var out = [];
          for (var i = 0; i < 30; i++) out.push(loop(20, -1));
          out.push(loop(20, 10), loop(5, -1));
          return out.join();
        })()
        """,
    };

    public static TheoryData<string> PhiTypeSnippets => new()
    {
        // Merges of objects and null/undefined/numbers, then property loads
        // and map checks of the merged value.
        """
        (function() {
          function P(x) { this.x = x; this.n = null; }
          function Q(x) { this.y = 0; this.x = x; }
          function pick(c, a, b) { var o = c ? a : b; return o.x; }
          function pick2(c, a) { var o; if (c === 0) o = a; else if (c === 1) o = new P(c); else o = new Q(c); return o.x + (o instanceof P ? 'P' : 'Q'); }
          function maybe(c, a) { var o = c ? a : null; return o === null ? 'null' : o.x; }
          var out = [];
          for (var i = 0; i < 40; i++) {
            out.push(pick(i & 1, new P(i), new Q(i)), pick2(i % 3, new Q(-i)), maybe(i & 1, new P(i)));
          }
          out.push(pick(1, { x: 'lit' }, 1), pick(0, 1, 2), maybe(1, 5), pick2(0, 'str'));
          try { pick(0, 1, null); } catch (e) { out.push(e.constructor.name); }
          try { pick2(0, undefined); } catch (e) { out.push(e.constructor.name); }
          return out.join();
        })()
        """,
        // Loop phis: a linked list walk (the phi after the loop is an object
        // or null), a value reassigned in a loop to objects of other maps.
        """
        (function() {
          function Node(v, next) { this.v = v; this.next = next; }
          function build(n) { var l = null; for (var i = 0; i < n; i++) l = new Node(i, l); return l; }
          function last(l) { var p = l; while (p.next !== null) p = p.next; return p.v; }
          function sum(l) { var s = 0; for (var p = l; p !== null; p = p.next) s += p.v; return s; }
          function morph(n) { var o = { a: 1 }; for (var i = 0; i < n; i++) o = i & 1 ? { a: i, b: 2 } : { b: 3, a: -i }; return o.a; }
          var out = [];
          for (var i = 0; i < 40; i++) { var l = build(i + 1); out.push(last(l), sum(l), morph(i)); }
          var odd = build(3); odd.next.next.next = undefined;
          try { out.push(sum(odd)); } catch (e) { out.push(e.constructor.name); }
          out.push(last({ v: 'x', next: null }));
          return out.join();
        })()
        """,
        // An inlined callee returning objects of different maps or a number:
        // the result phi's type, then loads and calls on it.
        """
        (function() {
          function A() { this.k = 'a'; } A.prototype.who = function() { return 'A' + this.k; };
          function B() { this.j = 0; this.k = 'b'; } B.prototype.who = function() { return 'B' + this.k; };
          function make(i) { if (i % 3 === 0) return new A(); if (i % 3 === 1) return new B(); return new A(); }
          function use(i) { var o = make(i); return o.who() + o.k; }
          function mixed(i) { return i & 1 ? new A() : i; }
          function useMixed(i) { var o = mixed(i); return typeof o === 'object' ? o.k : o + 1; }
          var out = [];
          for (var i = 0; i < 50; i++) out.push(use(i), useMixed(i));
          return out.join();
        })()
        """,
    };

    [Theory]
    [MemberData(nameof(PrototypeChainSnippets))]
    public void PrototypeChainDependenciesGiveTheSameResults(string source) => MaglevCompilerTest.AssertSameWhenOptimized(source);

    [Theory]
    [MemberData(nameof(PhiTypeSnippets))]
    public void PhiTypesGiveTheSameResults(string source) => MaglevCompilerTest.AssertSameWhenOptimized(source);
}
