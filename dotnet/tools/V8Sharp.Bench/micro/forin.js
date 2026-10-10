// for-in over objects with an enum cache (MaglevGraphBuilder.ForIn.cs), and
// calls with more arguments than the value calls take (lazy frames with a
// register window).
function Point(x, y, z) { this.x = x; this.y = y; this.z = z; }
var pts = [];
for (var i = 0; i < 100; i++) pts.push(new Point(i, i + 1, i + 2));
function sumFields(o) { var s = 0; for (var k in o) s += o[k]; return s; }
bench('ForInFields', function () {
  var s = 0;
  for (var r = 0; r < 100; r++) for (var i = 0; i < pts.length; i++) s += sumFields(pts[i]);
  return s;
});
function countKeys(o) { var n = 0; for (var k in o) n++; return n; }
bench('ForInKeys', function () {
  var n = 0;
  for (var r = 0; r < 100; r++) for (var i = 0; i < pts.length; i++) n += countKeys(pts[i]);
  return n;
});
function eight(a, b, c, d, e, f, g, h) { return a + b + c + d + e + f + g + h; }
function callsEight(x) { return eight(x, 1, 2, 3, 4, 5, 6, 7) + eight(7, 6, 5, 4, 3, 2, 1, x); }
bench('EightArgCalls', function () {
  var s = 0;
  for (var i = 0; i < 10000; i++) s += callsEight(i);
  return s;
});
