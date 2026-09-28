function add(a, b) { return a + b; }
var obj = { m: function (a) { return a + 1; } };
bench('CallLoop', function () {
  var s = 0;
  for (var i = 0; i < 100000; i++) s = add(s, i);
  return s;
});
bench('MethodCall', function () {
  var s = 0;
  for (var i = 0; i < 100000; i++) s = obj.m(s);
  return s;
});
bench('ClosureCreate', function () {
  var s = 0;
  for (var i = 0; i < 100000; i++) { var f = function () { return i; }; s += f(); }
  return s;
});
