// CPU-time micro-benchmarks of runtime paths outside the dispatch loop
// (builtins, regexp glue, strings, IC slow paths, dictionary-mode objects,
// accessors), as cpu.js scores them: millions of iterations per CPU second
// of the thread, best of five rounds after a warm-up.
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

benchCpu('RtRegExpExec', function (n) {
  var re = /(\d+)-(\w+)/, s = 'abc 1234-xyz def', c = 0;
  for (var i = 0; i < n; i++) { if (re.exec(s) !== null) c++; }
  return c;
}, 300000);
benchCpu('RtRegExpTest', function (n) {
  var re = /x[yz]/, s = 'abcdefxz', c = 0;
  for (var i = 0; i < n; i++) { if (re.test(s)) c++; }
  return c;
}, 500000);
benchCpu('RtReplaceGlobal', function (n) {
  var s = 'a.b.c.d', r;
  for (var i = 0; i < n; i++) { r = s.replace(/\./g, '-'); }
  return r;
}, 200000);
benchCpu('RtReplaceString', function (n) {
  var s = 'hello world', r;
  for (var i = 0; i < n; i++) { r = s.replace('world', 'there'); }
  return r;
}, 300000);
benchCpu('RtSplit', function (n) {
  var s = 'a,b,c,d', r;
  for (var i = 0; i < n; i++) { r = s.split(','); }
  return r;
}, 300000);
benchCpu('RtFromCharCode', function (n) {
  var s;
  for (var i = 0; i < n; i++) { s = String.fromCharCode(65 + (i & 15)); }
  return s;
}, 1000000);
benchCpu('RtCharCodeAt', function (n) {
  var str = 'abcdefghijklmnop', s = 0;
  for (var i = 0; i < n; i++) { s = s + str.charCodeAt(i & 15); }
  return s;
}, 2000000);
benchCpu('RtIndexOf', function (n) {
  var str = 'abcdefghijklmnop', s = 0;
  for (var i = 0; i < n; i++) { s = s + str.indexOf('jk'); }
  return s;
}, 1000000);
benchCpu('RtSubstring', function (n) {
  var str = 'abcdefghijklmnop', r;
  for (var i = 0; i < n; i++) { r = str.substring(2, 7); }
  return r;
}, 1000000);
benchCpu('RtConsIndex', function (n) {
  var str = 'abcdefghij' + 'klmnopqrstuv', s = 0, c;
  for (var i = 0; i < n; i++) { c = str[i & 15]; }
  return c;
}, 2000000);
benchCpu('RtConsBuild', function (n) {
  var s = '';
  for (var i = 0; i < n; i++) { s = s + 'x'; if ((i & 255) === 0) { s.charCodeAt(0); s = ''; } }
  return s;
}, 1000000);
benchCpu('RtTypedPoly', function (n) {
  var a = new Uint8Array(64), b = new Int32Array(64), c = new Float32Array(64), s = 0;
  var arrs = [a, b, c];
  for (var i = 0; i < n; i++) { var x = arrs[i % 3]; s = s + x[i & 63]; }
  return s;
}, 1000000);
benchCpu('RtDictLoad', function (n) {
  var o = {};
  for (var k = 0; k < 200; k++) o['p' + k] = k;
  delete o.p3;
  var s = 0;
  for (var i = 0; i < n; i++) { s = s + o.p100; }
  return s;
}, 2000000);
benchCpu('RtMegaLoad', function (n) {
  var objs = [];
  for (var k = 0; k < 8; k++) { var o = {}; o['q' + k] = k; o.v = k; objs.push(o); }
  var s = 0;
  for (var i = 0; i < n; i++) { s = s + objs[i & 7].v; }
  return s;
}, 2000000);
benchCpu('RtMegaStore', function (n) {
  var objs = [];
  for (var k = 0; k < 8; k++) { var o = {}; o['q' + k] = k; o.v = k; objs.push(o); }
  for (var i = 0; i < n; i++) { objs[i & 7].v = i; }
  return objs[1].v;
}, 2000000);
benchCpu('RtMegaKeyedLoad', function (n) {
  var objs = [], keys = ['v', 'w'];
  for (var k = 0; k < 8; k++) { var o = {}; o['q' + k] = k; o.v = k; o.w = k; objs.push(o); }
  var s = 0;
  for (var i = 0; i < n; i++) { s = s + objs[i & 7][keys[i & 1]]; }
  return s;
}, 2000000);
benchCpu('RtGetter', function (n) {
  function P() { this._x = 1; }
  Object.defineProperty(P.prototype, 'x', { get: function () { return this._x; } });
  var p = new P(), s = 0;
  for (var i = 0; i < n; i++) { s = s + p.x; }
  return s;
}, 1000000);
benchCpu('RtSetter', function (n) {
  function P() { this._x = 1; }
  Object.defineProperty(P.prototype, 'x', { set: function (v) { this._x = v; } });
  var p = new P();
  for (var i = 0; i < n; i++) { p.x = i; }
  return p._x;
}, 1000000);
benchCpu('RtNewArray', function (n) {
  var a;
  for (var i = 0; i < n; i++) { a = new Array(8); }
  return a;
}, 1000000);
benchCpu('RtSplice', function (n) {
  var a = [1, 2, 3, 4, 5, 6, 7, 8];
  for (var i = 0; i < n; i++) { a.splice(2, 1); a.splice(2, 0, 3); }
  return a;
}, 300000);
benchCpu('RtParseInt', function (n) {
  var s = 0;
  for (var i = 0; i < n; i++) { s = s + parseInt('1234', 10); }
  return s;
}, 1000000);
benchCpu('RtMapGetSet', function (n) {
  var m = new Map(), s = 0;
  for (var i = 0; i < n; i++) { m.set(i & 255, i); s = s + m.get((i * 7) & 255); }
  return s;
}, 1000000);
benchCpu('RtFnCall', function (n) {
  function f(a) { return a + 1; }
  var s = 0;
  for (var i = 0; i < n; i++) { s = f.call(null, s); }
  return s;
}, 1000000);
benchCpu('RtStringAddNum', function (n) {
  var s;
  for (var i = 0; i < n; i++) { s = 'k' + (i & 1023); }
  return s;
}, 1000000);
