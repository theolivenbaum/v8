// Parse + compile micro-benchmarks: the front end (scanner, parser,
// preparser, scope analysis) and the bytecode generator on Octane's large
// sources, scored as compiles per CPU second of the thread (best of five
// rounds after a warm-up). Every compile is salted with a unique comment so
// neither engine's compilation cache hits.
//
//   Compile<X>      top-level compile of a source wrapped in a function
//                   expression: the wrapper is compiled eagerly and every
//                   inner function is preparsed (nothing runs).
//   Run<X>          what Octane CodeLoad does: a global script evaluated
//                   with a salt, so its top-level code runs and compiles
//                   (lazily) the functions it calls.
var __octane = '../../../artifacts/octane/';
var __sources = {
  TypeScript: read(__octane + 'typescript-compiler.js'),
  PdfJS: read(__octane + 'pdfjs.js'),
};
(function () {
  // code-load.js keeps Closure and jQuery in string variables; load it with
  // stubs for the parts of Octane's base.js it calls.
  var g = globalThis;
  g.BenchmarkSuite = function () {};
  g.Benchmark = function () {};
  (0, eval)(read(__octane + 'code-load.js'));
  __sources.Closure = g.BASE_JS;
  __sources.JQuery = g.JQUERY_JS;
})();

var __salt = 0;
function benchCompile(name, source, n, run) {
  var indirect = eval;
  var wrapped = run ? source : '(function(){' + source + '\n})';
  function once() {
    indirect('//' + (++__salt) + '\n' + wrapped);
  }
  once();
  var best = 1e300;
  for (var round = 0; round < 5; round++) {
    var t0 = cpuTimeMs();
    for (var i = 0; i < n; i++) once();
    var t = cpuTimeMs() - t0;
    if (t < best) best = t;
  }
  print(name + '(Score): ' + Math.round(n / best * 1000 * 100) / 100);
}

benchCompile('CompileTypeScript', __sources.TypeScript, 4, false);
benchCompile('CompilePdfJS', __sources.PdfJS, 8, false);
benchCompile('CompileClosure', __sources.Closure, 20, false);
benchCompile('CompileJQuery', __sources.JQuery, 20, false);
benchCompile('RunClosure',
  'var googsalt = 1;' + __sources.Closure + '(function(){return goog.cloneObject(googsalt);})();', 20, true);
benchCompile('RunJQuery',
  "var windowmock = {'document':new MockElement(), 'location':{'href':''}, 'navigator':{'userAgent':''}};" +
  'var jQuerySalt = 1;' + __sources.JQuery +
  '(function(){return windowmock.jQuery.grep([jQuerySalt], function(a,b){return true;})[0];})();', 20, true);
