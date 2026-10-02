// CPU-time micro-benchmarks: a fixed number of loop iterations, scored as
// millions of iterations per CPU second of the thread (cpuTimeMs, which both
// hosts provide); the best of five rounds after a warm-up, so the score is
// steady-state and insensitive to other load on the machine.
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

benchCpu('CpuEmptyLoop', function (n) { var s = 0; for (var i = 0; i < n; i++) { } return s; }, 4000000);
benchCpu('CpuAddLoop', function (n) { var s = 0; for (var i = 0; i < n; i++) { s = s + i; } return s; }, 4000000);
benchCpu('CpuIntArith', function (n) {
  var s = 0;
  for (var i = 0; i < n; i++) { s = (s + i * 3) | 0; s = s ^ (i >> 1); }
  return s;
}, 2000000);
benchCpu('CpuPropLoad', function (n) {
  var o = { a: 1, b: 2, c: 3 }, s = 0;
  for (var i = 0; i < n; i++) { s = s + o.b; }
  return s;
}, 2000000);
benchCpu('CpuPropStore', function (n) {
  var o = { a: 1, b: 2, c: 3 };
  for (var i = 0; i < n; i++) { o.b = i; }
  return o.b;
}, 2000000);
benchCpu('CpuProtoMethod', function (n) {
  function C() { this.v = 1; }
  C.prototype.get = function () { return this.v; };
  var c = new C(), s = 0;
  for (var i = 0; i < n; i++) { s = s + c.get(); }
  return s;
}, 1000000);
function cpuAdd(a, b) { return a + b; }
benchCpu('CpuCall', function (n) { var s = 0; for (var i = 0; i < n; i++) { s = cpuAdd(s, i); } return s; }, 1000000);
benchCpu('CpuArrayRead', function (n) {
  var a = [1, 2, 3, 4, 5, 6, 7, 8], s = 0;
  for (var i = 0; i < n; i++) { s = s + a[i & 7]; }
  return s;
}, 2000000);
benchCpu('CpuArrayWrite', function (n) {
  var a = [1, 2, 3, 4, 5, 6, 7, 8];
  for (var i = 0; i < n; i++) { a[i & 7] = i; }
  return a[3];
}, 2000000);
benchCpu('CpuDoubleArray', function (n) {
  var a = [0.5, 1.5, 2.5, 3.5, 4.5, 5.5, 6.5, 7.5], s = 0;
  for (var i = 0; i < n; i++) { s = s + a[i & 7] * 1.5; }
  return s;
}, 2000000);
benchCpu('CpuNewObject', function (n) {
  function P(x, y) { this.x = x; this.y = y; }
  var p;
  for (var i = 0; i < n; i++) { p = new P(i, i); }
  return p.x;
}, 500000);
benchCpu('CpuObjectLiteral', function (n) {
  var p;
  for (var i = 0; i < n; i++) { p = { x: i, y: i }; }
  return p.x;
}, 1000000);
benchCpu('CpuStringConcat', function (n) {
  var s;
  for (var i = 0; i < n; i++) { s = 'a' + i; }
  return s;
}, 500000);
benchCpu('CpuClosure', function (n) {
  var s = 0;
  for (var i = 0; i < n; i++) { var f = function () { return i; }; s = s + f(); }
  return s;
}, 500000);
benchCpu('CpuNullCheck', function (n) {
  var o = { next: null }, c = 0;
  for (var i = 0; i < n; i++) { if (o.next == null) c++; }
  return c;
}, 2000000);
