// CPU-time micro-benchmarks of JavaScript accessor calls (getters and
// setters on prototypes, classes, object literals and own properties) and,
// for scale, a method call; scored as cpu.js scores them: millions of
// iterations per CPU second of the thread, best of five rounds after a warm-up.
function benchCpu(name, fn, n) {
  fn(n >> 4);
  var best = 1e300;
  for (var round = 0; round < 5; round++) {
    var t0 = cpuTimeMs();
    fn(n);
    var t = cpuTimeMs() - t0;
    if (t < best) best = t;
  }
  print(name + '(Score): ' + Math.round(n / best / 1000 * 100) / 100);
}

benchCpu('AccProtoGetter', function (n) {
  function P() { this._x = 1; }
  Object.defineProperty(P.prototype, 'x', { get: function () { return this._x; } });
  var p = new P(), s = 0;
  for (var i = 0; i < n; i++) { s = s + p.x; }
  return s;
}, 2000000);
benchCpu('AccProtoSetter', function (n) {
  function P() { this._x = 1; }
  Object.defineProperty(P.prototype, 'x', { set: function (v) { this._x = v; } });
  var p = new P();
  for (var i = 0; i < n; i++) { p.x = i; }
  return p._x;
}, 2000000);
benchCpu('AccClassGetter', function (n) {
  class Q { constructor() { this._x = 1; } get x() { return this._x; } }
  var q = new Q(), s = 0;
  for (var i = 0; i < n; i++) { s = s + q.x; }
  return s;
}, 2000000);
benchCpu('AccClassSetter', function (n) {
  class Q { constructor() { this._x = 1; } set x(v) { this._x = v; } }
  var q = new Q();
  for (var i = 0; i < n; i++) { q.x = i; }
  return q._x;
}, 2000000);
benchCpu('AccInheritedGetter', function (n) {
  class A { constructor() { this._x = 1; } get x() { return this._x; } }
  class B extends A { }
  class C extends B { }
  var c = new C(), s = 0;
  for (var i = 0; i < n; i++) { s = s + c.x; }
  return s;
}, 2000000);
benchCpu('AccLiteralGetter', function (n) {
  var o = { _x: 1, get x() { return this._x; } }, s = 0;
  for (var i = 0; i < n; i++) { s = s + o.x; }
  return s;
}, 2000000);
benchCpu('AccOwnDefinedGetter', function (n) {
  function P() { this._x = 1; Object.defineProperty(this, 'x', { get: function () { return this._x; } }); }
  var p = new P(), s = 0;
  for (var i = 0; i < n; i++) { s = s + p.x; }
  return s;
}, 2000000);
benchCpu('AccMethodCall', function (n) {
  class Q { constructor() { this._x = 1; } getX() { return this._x; } }
  var q = new Q(), s = 0;
  for (var i = 0; i < n; i++) { s = s + q.getX(); }
  return s;
}, 2000000);
