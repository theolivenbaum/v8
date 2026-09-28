function P(x, y) { this.x = x; this.y = y; }
var objs = [];
for (var k = 0; k < 4; k++) objs.push(new P(k, k + 1));
bench('PropertyLoad', function () {
  var s = 0, o = objs[1];
  for (var i = 0; i < 100000; i++) { s += o.x + o.y; }
  return s;
});
bench('PropertyStore', function () {
  var o = objs[2];
  for (var i = 0; i < 100000; i++) { o.x = i; o.y = i; }
  return o.x;
});
var proto = { get: function () { return 1; } };
var derived = Object.create(Object.create(proto));
bench('ProtoMethodLoad', function () {
  var s = 0;
  for (var i = 0; i < 100000; i++) s += derived.get();
  return s;
});
