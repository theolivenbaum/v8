// Builtin calls from bytecode: Math, String.prototype, Array.prototype,
// Number.prototype.toString and short string concatenation.
var nums = [];
for (var k = 0; k < 1000; k++) nums.push(k * 1.37 - 300);
bench('MathFloor', function () {
  var s = 0;
  for (var j = 0; j < 20; j++) for (var i = 0; i < 1000; i++) s += Math.floor(nums[i]);
  return s;
});
bench('MathMisc', function () {
  var s = 0;
  for (var j = 0; j < 10; j++) for (var i = 0; i < 1000; i++) {
    var x = nums[i];
    s += Math.abs(x) + Math.max(x, 3) + Math.min(x, 7) + Math.sqrt(i) + Math.round(x);
  }
  return s;
});
var str = '';
for (var k = 0; k < 1000; k++) str += String.fromCharCode(97 + k % 26);
bench('CharCodeAt', function () {
  var s = 0;
  for (var j = 0; j < 20; j++) for (var i = 0; i < 1000; i++) s += str.charCodeAt(i);
  return s;
});
bench('CharAt', function () {
  var s = 0;
  for (var j = 0; j < 20; j++) for (var i = 0; i < 1000; i++) if (str.charAt(i) === 'e') s++;
  return s;
});
bench('StringIndexOf', function () {
  var s = 0;
  for (var i = 0; i < 2000; i++) s += str.indexOf('xyz', i % 900) + str.indexOf('q');
  return s;
});
bench('StringSlice', function () {
  var s = 0;
  for (var i = 0; i < 10000; i++) s += str.slice(i % 900, i % 900 + 5).length + str.substring(3, i % 50).length;
  return s;
});
bench('ArrayIndexOf', function () {
  var s = 0;
  for (var i = 0; i < 2000; i++) s += nums.indexOf(nums[i % 1000]) + (nums.includes(-5) ? 1 : 0);
  return s;
});
bench('NumberToString', function () {
  var s = 0;
  for (var i = 0; i < 10000; i++) s += (i % 500).toString().length + String(i % 300).length;
  return s;
});
bench('ShortConcat', function () {
  var s = 0;
  for (var i = 0; i < 10000; i++) { var t = 'k' + (i % 100) + '_' + 'x'; s += t.length; }
  return s;
});
bench('ConcatFlatten', function () {
  var s = 0;
  for (var i = 0; i < 1000; i++) { var t = 'abc' + i + 'def' + 'ghi'; s += t.charCodeAt(4); }
  return s;
});
