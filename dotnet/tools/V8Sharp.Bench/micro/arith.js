bench('ArithLoop', function () {
  var s = 0;
  for (var i = 0; i < 100000; i++) { s = (s + i * 3) | 0; s = s ^ (i >> 1); }
  return s;
});
bench('DoubleArith', function () {
  var s = 0.5;
  for (var i = 0; i < 100000; i++) { s = s * 1.000001 + 0.25; }
  return s;
});
