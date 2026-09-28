bench('ArrayPush', function () {
  var a = [];
  for (var i = 0; i < 100000; i++) a.push(i);
  return a.length;
});
var arr = [];
for (var k = 0; k < 1000; k++) arr.push(k);
bench('ArrayIndex', function () {
  var s = 0;
  for (var j = 0; j < 100; j++) for (var i = 0; i < 1000; i++) s += arr[i];
  return s;
});
bench('ArrayStore', function () {
  for (var j = 0; j < 100; j++) for (var i = 0; i < 1000; i++) arr[i] = i + j;
  return arr[5];
});
bench('StringConcat', function () {
  var s = '';
  for (var i = 0; i < 10000; i++) s += 'ab';
  return s.length;
});
