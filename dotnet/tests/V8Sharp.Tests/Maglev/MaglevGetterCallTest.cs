// Loads that call a getter from the prototype chain (TryBuildPropertyGetterCall,
// maglev-graph-builder.cc): inlined, direct and builtin getters, deopts and
// throws inside them, getters redefined after optimization, receivers of
// several maps that share the getter, and getters that change the receiver.
namespace V8Sharp.Tests.Maglev;

public class MaglevGetterCallTest
{
    public static TheoryData<string> Snippets => new()
    {
        // Class getters (inlined), a getter on a grandparent prototype, keyed access by a constant name.
        """
        (function() {
          class P { constructor(x) { this.x = x; } get double() { return this.x * 2; } }
          class Q extends P { get key() { return 'k' + this.x; } }
          function f(o) { return o.double + o.key.length + o['double']; }
          var out = 0;
          for (var i = 0; i < 200; i++) out += f(new Q(i));
          return out;
        })()
        """,
        // A getter whose type feedback changes (a deopt inside the inlined getter) and that throws.
        """
        (function() {
          var proto = { get v() { if (this.t === 3) throw new Error('boom ' + this.n); return this.n + 1; } };
          function mk(n, t) { var o = Object.create(proto); o.n = n; o.t = t; return o; }
          function f(o) { return o.v; }
          var out = [];
          for (var i = 0; i < 150; i++) out.push(f(mk(i, 0)));
          out.push(f(mk('s', 0)), f(mk(1.5, 0)));
          try { f(mk(7, 3)); } catch (e) { out.push(e.message); }
          for (var i = 0; i < 20; i++) out.push(f(mk(i, 0)));
          return out.join(',');
        })()
        """,
        // The getter is redefined (and turned into a data property) after optimization.
        """
        (function() {
          function A(x) { this.x = x; }
          Object.defineProperty(A.prototype, 'g', { get: function() { return this.x; }, configurable: true });
          function f(o) { return o.g; }
          var out = [];
          for (var i = 0; i < 150; i++) out.push(f(new A(i)));
          Object.defineProperty(A.prototype, 'g', { get: function() { return -this.x; }, configurable: true });
          out.push(f(new A(5)));
          Object.defineProperty(A.prototype, 'g', { value: 'data', configurable: true });
          out.push(f(new A(6)));
          var b = new A(7); Object.defineProperty(b, 'g', { value: 'own' }); out.push(f(b));
          return out.slice(-5).join(',');
        })()
        """,
        // Two receiver maps sharing the getter; a getter that adds a property to the receiver; a builtin getter.
        """
        (function() {
          function B() {} B.prototype = { get c() { this.count = (this.count || 0) + 1; return this.count; } };
          function f(o) { return o.c + o.c; }
          function sizeOf(m) { return m.size; }
          var out = 0;
          for (var i = 0; i < 200; i++) {
            var o = new B(); if (i & 1) o.extra = i;
            out += f(o);
            var m = new Map(); for (var k = 0; k < (i % 5); k++) m.set(k, k);
            out += sizeOf(m);
          }
          return out;
        })()
        """,
        // A sloppy getter (receiver is the object), a getter reading arguments, a recursive getter.
        """
        (function() {
          var proto = {
            get self() { return this; },
            get argc() { return arguments.length; },
            get depth() { return this.next ? 1 + this.next.depth : 0; },
          };
          function node(next) { var o = Object.create(proto); o.next = next; return o; }
          function f(o) { return (o.self === o ? 1 : 0) + o.argc + o.depth; }
          var list = null, out = 0;
          for (var i = 0; i < 150; i++) { list = node(i % 10 ? list : null); out += f(list); }
          return out;
        })()
        """,
        // Setters: the expression's value is the assigned value, a setter that
        // deopts its caller lazily (redefines the getter the caller depends on),
        // a throwing setter, a setter redefined after optimization.
        """
        (function() {
          var log = [];
          function C(x) { this._x = x; }
          Object.defineProperty(C.prototype, 'x', {
            get: function() { return this._x; },
            set: function(v) { if (v === 'redefine') Object.defineProperty(C.prototype, 'y', { get: function() { return 'new'; }, configurable: true });
                               if (v === 'throw') throw new Error('bad ' + this._x); this._x = v; return 'ignored'; },
            configurable: true });
          Object.defineProperty(C.prototype, 'y', { get: function() { return 'old'; }, configurable: true });
          function f(o, v) { var r = (o.x = v); log.push(r, o.y); return o.x; }
          var out = [];
          for (var i = 0; i < 150; i++) out.push(f(new C(0), i));
          out.push(f(new C(1), 'redefine'));
          try { f(new C(2), 'throw'); } catch (e) { out.push(e.message); }
          Object.defineProperty(C.prototype, 'x', { set: function(v) { this._x = v * 10; }, get: function() { return this._x; }, configurable: true });
          out.push(f(new C(3), 4));
          return out.slice(-6).join(',') + '|' + log.slice(-8).join(',');
        })()
        """,
        // __proto__ of the global proxy set to a new prototype on every call
        // (mjsunit/opt-proto-seq/proto-seq-opt-global-proxy), then loaded through.
        """
        (function() {
          function f() {
            function T() {} T.prototype.f1 = function () { return 'OK'; };
            globalThis.__proto__ = T.prototype;
            return globalThis.f1();
          }
          var out = [];
          for (var i = 0; i < 100; i++) out.push(f());
          return out.join();
        })()
        """,
        // A builtin setter (__proto__) and a setter shared by two receiver maps.
        """
        (function() {
          function D() { this.a = 1; }
          D.prototype = { set s(v) { this.a += v; } };
          function g(o, v) { o.s = v; return o.a; }
          function p(o, q) { o.__proto__ = q; return Object.getPrototypeOf(o) === q; }
          var out = 0, q = {};
          for (var i = 0; i < 200; i++) {
            var o = new D(); if (i & 1) o.b = 2;
            out += g(o, i);
            out += p({}, q) ? 1 : 0;
          }
          return out;
        })()
        """,
    };

    [Theory]
    [MemberData(nameof(Snippets))]
    public void SameResultWhenOptimized(string source) => MaglevCompilerTest.AssertSameWhenOptimized(source);
}
