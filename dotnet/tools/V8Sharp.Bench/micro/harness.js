// Micro-benchmark harness: bench(name, fn) runs fn() (one call does a fixed
// amount of work) until 500 ms have passed and prints calls per second.
function bench(name, fn) {
  fn();
  var n = 0, start = Date.now(), elapsed = 0;
  do { fn(); n++; elapsed = Date.now() - start; } while (elapsed < 500);
  print(name + '(Score): ' + Math.round(n * 1000 / elapsed));
}
