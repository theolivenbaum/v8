// Tests of inlining calls inside try blocks (MaglevGraphBuilder: a node of
// the inlined code that can throw continues at the caller's catch block,
// whose state merges the caller's frame at the call).
namespace V8Sharp.Tests.Maglev;

public class MaglevInliningTest
{
    public static TheoryData<string> Snippets => new()
    {
        """
        (function() {
          function thrower(x) { if (x & 1) throw new Error('e' + x); return x * 2; }
          function direct(x) { if (x % 3 == 0) throw x; return x; }
          function mid(x) { let y = x + 1; return thrower(y) + direct(y) + y; }
          function f(n) {
            let s = 0, log = [];
            for (let i = 0; i < n; i++) {
              let t = i * 10, u = i + 0.5;
              try { s += mid(i); t += 1; u *= 2; } catch (e) { log.push((e.message ?? e) + ':' + t + ':' + u + ':' + s); }
              try { s += direct(i); } catch (e) { s -= e; } finally { s += 1; }
            }
            return s + '|' + log.join(',');
          }
          var out = [];
          for (var k = 0; k < 15; k++) out.push(f(12));
          function g(o) { try { return o.v.w; } catch (e) { return e.constructor.name; } }
          for (var k = 0; k < 15; k++) out.push(g({v: {w: k}}), g({v: null}), g({}));
          return out.join(';');
        })()
        """,
    };

    [Theory]
    [MemberData(nameof(Snippets))]
    public void SameResultWhenOptimized(string source) => MaglevCompilerTest.AssertSameWhenOptimized(source);

    [Fact]
    public void CallsInsideTryBlocksAreInlined()
    {
        Assert.Equal("true,8,true", MaglevCompilerTest.Run("--maglev", """
            function thrower(x) { if (x & 1) throw new Error('e' + x); return x * 2; }
            %NeverOptimizeFunction(thrower);
            function mid(x) { let y = x + 1; return thrower(y) + y; }
            function f(n) {
              let s = 0, log = [];
              for (let i = 0; i < n; i++) { let t = i; try { s += mid(i); t += 1; } catch (e) { log.push(e.message + t); } }
              return s + '|' + log.join();
            }
            %PrepareFunctionForOptimization(f);
            %PrepareFunctionForOptimization(mid);
            const a = f(10); f(10);
            %OptimizeFunctionOnNextCall(f);
            const b = f(10);
            const stackOk = (function() { try { mid(0); } catch (e) { return e.stack.includes('mid'); } })();
            [a === b, %GetOptimizationStatus(f) & 8, stackOk].join();
            """));
    }
}
