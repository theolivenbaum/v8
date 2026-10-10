// Calls between optimized functions that are not inlined (each callee calls
// out, so it needs a frame the stack walker can find): recursion, method
// calls through a polymorphic receiver, super calls through
// Function.prototype.call, and new Array(). Warmed up and compiled before
// measuring (the warm suites' protocol).
function fib(n) { return n < 2 ? n : fib(n - 1) + fib(n - 2); }

function Shape(k) { this.k = k; }
Shape.prototype.area = function () { return this.size() * this.k; };
function Square(s) { Shape.call(this, 2); this.s = s; }
Square.prototype = Object.create(Shape.prototype);
Square.prototype.size = function () { return this.s * this.s; };
function Circle(r) { Shape.call(this, 3); this.r = r; }
Circle.prototype = Object.create(Shape.prototype);
Circle.prototype.size = function () { return this.r * this.r; };
var shapes = [];
for (var i = 0; i < 64; i++) shapes.push(i & 1 ? new Square(i) : new Circle(i));
function areas() { var s = 0; for (var i = 0; i < shapes.length; i++) s += shapes[i].area(); return s; }

function Coll() { this.elms = new Array(); }
Coll.prototype.add = function (x) { this.elms.push(x); };
function colls() { var n = 0; for (var i = 0; i < 64; i++) { var c = new Coll(); c.add(i); n += c.elms.length; } return n; }

function warm(fn) {
  for (var i = 0; i < 300; i++) fn();
  if (typeof waitForCompilations === 'function') waitForCompilations();
  for (var i = 0; i < 300; i++) fn();
}

warm(function () { return fib(12); });
warm(areas);
warm(colls);
bench('Fib', function () { return fib(20); });
bench('PolymorphicMethods', areas);
bench('NewArray', colls);
