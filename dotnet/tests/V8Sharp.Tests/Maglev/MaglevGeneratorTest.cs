// Tests of generator and async function support in the Maglev port
// (MaglevGraphBuilder::VisitSwitchOnGeneratorState, VisitSuspendGenerator,
// VisitResumeGenerator, resumable loops): the functions are optimized and
// resumed in optimized code, with the same results as in the interpreter.
namespace V8Sharp.Tests.Maglev;

public class MaglevGeneratorTest
{
    const int kOptimized = 1 << 3;

    public static TheoryData<string> Snippets => new()
    {
        // Yields in nested loops (resumable loops), closures over loop variables,
        // values sent into the generator, return values.
        """
        (function() {
          function* nested(n) {
            let total = 0;
            for (let i = 0; i < n; i++) {
              let inner = 0;
              for (let j = 0; j < i; j++) { const got = yield [i, j]; inner += got === undefined ? 1 : got; }
              total += inner;
              let f = () => total + i;
              yield f();
            }
            return total;
          }
          const out = [];
          for (let k = 0; k < 8; k++) {
            const g = nested(4 + (k & 1));
            let r = g.next(), m = 0;
            while (!r.done) { out.push(JSON.stringify(r.value)); r = g.next(m++ % 3 === 0 ? 1.5 : m); }
            out.push('done ' + r.value);
          }
          return out.join(' ');
        })()
        """,
        // try/catch/finally around yields, throw() and return() into suspended
        // generators, yield* delegation.
        """
        (function() {
          function* tryy() {
            let log = [];
            try {
              log.push(yield 1);
              try { log.push(yield 2); throw 'x'; } catch (e) { log.push('c' + e); log.push(yield 3); } finally { log.push('f'); }
              log.push(yield 4);
            } finally { log.push('outer'); }
            return log.join();
          }
          function* deleg() { let a = yield* tryy(); let b = yield* [7, 8]; return a + ':' + b; }
          function drive(gen, mode) {
            const out = [];
            let r = gen.next(), k = 0;
            while (!r.done) {
              out.push(String(r.value)); k++;
              if (mode === 1 && k === 3) { try { r = gen.throw('T'); } catch (e) { out.push('thrown ' + e); break; } continue; }
              if (mode === 2 && k === 2) { r = gen.return('R'); out.push('ret ' + r.value); continue; }
              r = gen.next(k);
            }
            out.push('done ' + r.value);
            return out.join(',');
          }
          const out = [];
          for (let k = 0; k < 10; k++) out.push(drive(tryy(), k % 3), drive(deleg(), k % 3));
          return out.join('|');
        })()
        """,
        // A deopt inside a resumed generator continues it in the interpreter.
        """
        (function() {
          function* g(xs) { let s = 0; for (const x of xs) { s += x; yield s; } return s; }
          const out = [];
          for (let k = 0; k < 12; k++) {
            const xs = k < 10 ? [1, 2, 3, 4] : [1, 2.5, 'a', {}];
            for (const v of g(xs)) out.push(v);
          }
          return out.join();
        })()
        """,
    };

    [Theory]
    [MemberData(nameof(Snippets))]
    public void SameResultWhenOptimized(string source) => MaglevCompilerTest.AssertSameWhenOptimized(source);

    [Fact]
    public void GeneratorsAreOptimizedAndResumedInOptimizedCode()
    {
        Assert.Equal("0,2,4,6,8,145,8", MaglevCompilerTest.Run("--maglev", """
            function* g(n) {
              let s = 0;
              for (let i = 0; i < n; i++) { let x = yield i * 2; s += (x | 0) + i; }
              return s;
            }
            function run(n) { let it = g(n), r = [], x = it.next(); while (!x.done) { r.push(x.value); x = it.next(x.value + 1); } r.push(x.value); return r; }
            %PrepareFunctionForOptimization(g);
            run(10); run(10);
            %OptimizeFunctionOnNextCall(g);
            var r = run(10);
            [r[0], r[1], r[2], r[3], r[4], r[10], %GetOptimizationStatus(g) & 8].join();
            """));
    }

    [Fact]
    public void GeneratorCreationIsInlined()
    {
        // VisitSwitchOnGeneratorState / VisitSuspendGenerator of an inlined
        // generator: only the initialization runs inlined, the resumes do not.
        Assert.Equal("1,2,3,4|5,6,7,8|8", MaglevCompilerTest.Run("--maglev --max-maglev-inlined-bytecode-size=1000", """
            function* g(a, b) { let s = a; for (let i = 0; i < b; i++) s += yield s; return s; }
            function f(k) {
              let it = g(k, 3), r = [], x = it.next();
              while (!x.done) { r.push(x.value); x = it.next(1); }
              r.push(x.value);
              return r.join();
            }
            %PrepareFunctionForOptimization(f);
            %PrepareFunctionForOptimization(g);
            f(1); f(2);
            %OptimizeFunctionOnNextCall(f);
            [f(1), f(5), %GetOptimizationStatus(f) & 8].join('|');
            """));
    }

    [Fact]
    public void AsyncFunctionsAreOptimized()
    {
        Assert.Equal("1007,8", MaglevCompilerTest.Run("--maglev", """
            async function af(xs) {
              let s = 0;
              for (const x of xs) { try { s += await x; } catch (e) { s += 1000; } }
              return s;
            }
            var result;
            %PrepareFunctionForOptimization(af);
            const p = [1, Promise.resolve(2), Promise.reject(3), 4];
            af(p); af(p);
            %PerformMicrotaskCheckpoint();
            %OptimizeFunctionOnNextCall(af);
            af(p).then(v => result = v);
            %PerformMicrotaskCheckpoint();
            [result, %GetOptimizationStatus(af) & 8].join();
            """));
    }
}
