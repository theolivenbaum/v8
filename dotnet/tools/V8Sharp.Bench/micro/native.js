// Loop kernels of the native Maglev backend (architecture.md 9.3): int32 and
// double arithmetic, double arrays, fields, Math. Each kernel is warmed up,
// its compiles are waited for (waitForCompilations, settle), then measured
// for 500 ms; prints calls per second. Compare v8sharp:maglev-native with
// v8sharp:maglev-il and V8 (v8:maglev, v8:jit) in one session.
function measure(name, fn) {
  var start = Date.now();
  while (Date.now() - start < 300) fn();
  if (typeof waitForCompilations === 'function') waitForCompilations();
  start = Date.now();
  while (Date.now() - start < 200) fn();
  if (typeof waitForCompilations === 'function') waitForCompilations();
  if (typeof settle === 'function') settle();
  var n = 0, elapsed = 0;
  start = Date.now();
  do { fn(); n++; elapsed = Date.now() - start; } while (elapsed < 500);
  print(name + '(Score): ' + Math.round(n * 1000 / elapsed));
}

var N = 4096;
var xs = [], ys = [], ints = [];
for (var i = 0; i < N; i++) { xs.push(i * 0.5 + 0.25); ys.push(1.5 - i * 0.125); ints.push((i * 2654435761) | 0); }

function intHash(a, n) {
  var h = 0x811c9dc5 | 0;
  for (var i = 0; i < n; i++) { h = ((h ^ a[i]) * 16777619) | 0; h = (h ^ (h >>> 13)) | 0; }
  return h;
}
function intSum(n) { var s = 0; for (var i = 0; i < n; i++) s = (s + ((i * 7) ^ (i >> 2))) | 0; return s; }
function dot(a, b, n) { var s = 0.0; for (var i = 0; i < n; i++) s += a[i] * b[i]; return s; }
function axpy(alpha, x, y, n) { for (var i = 0; i < n; i++) y[i] = alpha * x[i] + y[i] * 0.5; return y[0]; }
function mandel(w) {
  var count = 0;
  for (var py = 0; py < w; py++) {
    for (var px = 0; px < w; px++) {
      var x0 = px * 3.0 / w - 2.0, y0 = py * 2.0 / w - 1.0, x = 0.0, y = 0.0, k = 0;
      while (x * x + y * y < 4.0 && k < 50) { var t = x * x - y * y + x0; y = 2.0 * x * y + y0; x = t; k++; }
      count += k;
    }
  }
  return count;
}
function Vec(x, y, z) { this.x = x; this.y = y; this.z = z; }
var vecs = []; for (var i = 0; i < 256; i++) vecs.push(new Vec(i * 0.5, 1 - i, i * 0.25));
function fieldSum(o, n) { var s = 0.0; for (var i = 0; i < n; i++) s += o.x * o.y - o.z; return s; }
function roots(n) { var s = 0.0; for (var i = 1; i < n; i++) s += Math.sqrt(i) * Math.floor(i / 3) - Math.abs(s * 0.001); return s; }
function leaf(a, b) { return a * b + 1; }
function callLeaf(n) { var s = 0; for (var i = 0; i < n; i++) s = (s + leaf(i, 3)) | 0; return s; }

measure('IntHash', function () { return intHash(ints, N); });
measure('IntSum', function () { return intSum(N); });
measure('Dot', function () { return dot(xs, ys, N); });
measure('Axpy', function () { return axpy(1.25, xs, ys, N); });
measure('Mandelbrot', function () { return mandel(32); });
measure('FieldSum', function () { return fieldSum(vecs[7], N); });
measure('Roots', function () { return roots(N); });
measure('CallLeaf', function () { return callLeaf(N); });
