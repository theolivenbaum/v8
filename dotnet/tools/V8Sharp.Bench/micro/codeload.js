// Octane CodeLoad, split into its parts and scored as iterations per CPU
// second of the thread (best of five rounds after a warm-up): the salted
// Closure and jQuery evals as Octane runs them, and the cacheBust string
// replacement alone (what is left of an iteration that is not compiling
// or running the evaluated code).
var g = globalThis;
g.BenchmarkSuite = function () {};
g.Benchmark = function () {};
(0, eval)(read('../../../artifacts/octane/code-load.js'));
setupCodeLoad();

function benchCodeLoad(name, n, fn) {
  for (var w = 0; w < 10; w++) fn();
  var best = 1e300;
  for (var round = 0; round < 5; round++) {
    var t0 = cpuTimeMs();
    for (var i = 0; i < n; i++) fn();
    var t = cpuTimeMs() - t0;
    if (t < best) best = t;
  }
  print(name + '(Score): ' + Math.round(n / best * 1000 * 100) / 100);
}

benchCodeLoad('CodeLoadClosure', 100, runCodeLoadClosure);
benchCodeLoad('CodeLoadJQuery', 20, runCodeLoadJQuery);
benchCodeLoad('CodeLoadBoth', 20, function () { runCodeLoadClosure(); runCodeLoadJQuery(); });
benchCodeLoad('CacheBustJQuery', 400, function () {
  cacheBust('var jQuerySalt=' + salt + ';' + JQUERY_JS, 'jQuery');
  salt++;
});
