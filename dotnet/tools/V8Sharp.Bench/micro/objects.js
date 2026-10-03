class Point { constructor(x, y) { this.x = x; this.y = y; } }
function Vec(x, y) { this.x = x; this.y = y; }
bench('NewClass', function () {
  var p;
  for (var i = 0; i < 100000; i++) p = new Point(i, i);
  return p.x;
});
bench('NewFunction', function () {
  var p;
  for (var i = 0; i < 100000; i++) p = new Vec(i, i);
  return p.x;
});
bench('ObjectLiteral', function () {
  var p;
  for (var i = 0; i < 100000; i++) p = { a: i, b: i };
  return p.a;
});
