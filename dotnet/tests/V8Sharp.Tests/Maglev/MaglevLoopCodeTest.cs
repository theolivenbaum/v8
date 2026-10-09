// Tests of the IL Maglev emits for hot loops (MaglevCodeGenerator): cold
// deopt exits (a try region whose spill chains end in a throw), and that
// each gives the interpreter's results and deoptimizes where it must.
using V8Sharp.Codegen;

namespace V8Sharp.Tests.Maglev;

public class MaglevLoopCodeTest
{
    public static TheoryData<string> DeoptSnippets => new()
    {
        // Eager deopts inside a loop: a non-Smi element, an overflow, an out
        // of bounds index, a map change; the frame state of each iteration.
        """
        (function() {
          function sum(a, n) { var s = 0; for (var i = 0; i < n; i++) s = s + a[i] * 3; return s; }
          var out = [];
          var a = [];
          for (var k = 0; k < 50; k++) a[k] = k;
          for (var k = 0; k < 40; k++) out.push(sum(a, 50));
          a[25] = 0.5; out.push(sum(a, 50));
          a[25] = 2147483647; out.push(sum(a, 50));
          out.push(sum(a, 60));
          var b = a.slice(); b.x = 1; out.push(sum(b, 50));
          return out.join();
        })()
        """,
        // am3 of Crypto: int32 arithmetic, two arrays, a store per iteration.
        """
        (function() {
          function am3(i, x, w, j, c, n) {
            var this_array = this.array, w_array = w.array;
            var xl = x & 0x3fff, xh = x >> 14;
            while (--n >= 0) {
              var l = this_array[i] & 0x3fff;
              var h = this_array[i++] >> 14;
              var m = xh * l + h * xl;
              l = xl * l + ((m & 0x3fff) << 14) + w_array[j] + c;
              c = (l >> 28) + (m >> 14) + xh * h;
              w_array[j++] = l & 0xfffffff;
            }
            return c;
          }
          function BI() { this.array = []; }
          BI.prototype.am = am3;
          var a = new BI(), b = new BI();
          for (var k = 0; k < 30; k++) { a.array[k] = (k * 7919) & 0xfffffff; b.array[k] = (k * 104729) & 0xfffffff; }
          var out = [];
          for (var r = 0; r < 60; r++) out.push(a.am(r % 10, a.array[r % 30], b, r % 7, r, 20));
          b.array[5] = 'x'; out.push(a.am(0, 77, b, 0, 0, 20));
          b.array[5] = 1.5; out.push(a.am(0, 77, b, 0, 0, 20));
          out.push(a.am(0, 77, b, 25, 0, 20));
          return out.join();
        })()
        """,
        // A lazy deopt after a call in a loop (the callee changes a map the
        // loop depends on) and a leaf function deopting in its loop (the
        // frameless entry).
        """
        (function() {
          var proto = { v: 1 };
          function O() {}
          O.prototype = proto;
          function poke(k) { if (k == 33) proto.v = 'changed'; return k; }
          function walk(o, n) { var s = ''; for (var k = 0; k < n; k++) { s += o.v; poke(k); } return s.length; }
          function leaf(a) { var s = 0; for (var k = 0; k < a.length; k++) s += a[k]; return s; }
          var out = [];
          for (var r = 0; r < 30; r++) out.push(walk(new O(), 20), leaf([1, 2, 3, r]));
          out.push(walk(new O(), 40), leaf([1, 2, 3.5]), leaf(['a', 1]), leaf([1, , 3]));
          return out.join();
        })()
        """,
        // Deopts in a loop of a function with a catch block, and in code
        // split into regions.
        """
        (function() {
          function f(a, n) {
            var s = 0;
            for (var i = 0; i < n; i++) {
              try { s += a[i]; if (s > 1e6) throw s; } catch (e) { s = -1; }
            }
            return s;
          }
          var a = [];
          for (var k = 0; k < 20; k++) a[k] = k;
          var out = [];
          for (var r = 0; r < 40; r++) out.push(f(a, 20));
          a[3] = 0.25; out.push(f(a, 20)); a[4] = 2e6; out.push(f(a, 20)); a[5] = {}; out.push(f(a, 20));
          return out.join();
        })()
        """,
    };

    public static TheoryData<string> TransitionSnippets => new()
    {
        // Keyed loads whose feedback transitions packed Smi arrays to holey or
        // double ones, in loops: the transition is skipped once the map is
        // known, other maps stay known, aliases see the new map.
        """
        (function() {
          function sum(a, b, n) { var s = 0; for (var i = 0; i < n; i++) s += a[i] + b[i]; return s; }
          function mk(k, n) { var a = []; for (var i = 0; i < n; i++) a[i] = i * k; return a; }
          var out = [];
          for (var r = 0; r < 40; r++) {
            var a = mk(1, 10), b = mk(2, 10);
            if (r & 1) { a[20] = 1; }
            if (r % 3 == 0) b[3] = 1.5;
            out.push(sum(a, b, 10), sum(a, a, 10));
          }
          var c = mk(3, 10); out.push(sum(c, c, 10)); c[2] = 0.25; out.push(sum(c, c, 10));
          var d = mk(1, 10); d[30] = 2; out.push(sum(d, mk(1, 10), 10));
          return out.join();
        })()
        """,
        // Stores that transition (Smi to double, packed to holey) in loops
        // between loads of the same and of aliased arrays.
        """
        (function() {
          function fill(a, b, n, v) { var s = 0; for (var i = 0; i < n; i++) { s += b[i]; a[i] = v + i; s += a[i] + b[i]; } return s; }
          var out = [];
          for (var r = 0; r < 40; r++) {
            var a = [1, 2, 3, 4, 5], b = [5, 4, 3, 2, 1];
            out.push(fill(a, b, 5, r & 1 ? 0.5 : 1), fill(a, a, 5, 2), a.join(':'));
          }
          var h = [1, 2, 3]; h[10] = 4; out.push(fill(h, h, 3, 1.25), h.join(':'));
          return out.join();
        })()
        """,
    };

    public static TheoryData<string> GlobalSnippets => new()
    {
        // Loads of mutable global cells in loops (load elimination of the
        // cell's value): stores in the loop, in callees, and of other cells.
        """
        (function() {
          globalThis.gA = new Int32Array(16); globalThis.gB = new Int32Array(16); globalThis.gN = 16;
          for (var i = 0; i < 16; i++) gA[i] = i;
          function copy() { for (var i = 0; i < gN; i++) gB[i] = gA[i] * 2; return gB[5] + gB[15]; }
          function swap() { var t = gA; gA = gB; gB = t; }
          function grow(k) { for (var i = 0; i < 4; i++) { gN = 8 + i; if (i == k) swap(); } return gN; }
          function sw() { var s = 0; for (var i = 0; i < 6; i++) { s += gA[1]; gA = (i & 1) ? gB : new Int32Array(16).fill(i); s += gA[1]; } return s; }
          var out = [];
          for (var r = 0; r < 40; r++) { out.push(copy(), grow(r & 3), copy()); if (r % 7 == 0) swap(); gN = 16; }
          out.push(sw(), sw());
          return out.join();
        })()
        """,
        // Typed array lengths in loops (load elimination of the length): a
        // call that detaches or resizes the buffer forgets it.
        """
        (function() {
          function sum(a, n) { var s = 0; for (var i = 0; i < n; i++) s += a[i] | 0; return s; }
          function sumDetach(a, n, k) {
            var s = 0;
            for (var i = 0; i < n; i++) { if (i == k) a.buffer.transfer(); s += a[i] | 0; }
            return s;
          }
          function sumResize(a, n, k) {
            var s = 0;
            for (var i = 0; i < n; i++) { if (i == k) a.buffer.resize(8); s += a[i] | 0; }
            return s;
          }
          var out = [];
          for (var r = 0; r < 30; r++) {
            var a = new Int32Array(32); for (var i = 0; i < 32; i++) a[i] = i + r;
            out.push(sum(a, 32), sum(a, 40), sumDetach(a, 32, 40));
            var rab = new ArrayBuffer(128, { maxByteLength: 256 }); var b = new Int32Array(rab);
            for (var i = 0; i < 32; i++) b[i] = i;
            out.push(sumResize(b, 32, r < 25 ? 99 : 3));
          }
          var d = new Int32Array(16).fill(2); out.push(sumDetach(d, 16, 5));
          return out.join();
        })()
        """,
    };

    [Theory]
    [MemberData(nameof(GlobalSnippets))]
    public void GlobalLoadsInLoopsGiveTheSameResults(string source) => MaglevCompilerTest.AssertSameWhenOptimized(source);

    [Theory]
    [MemberData(nameof(TransitionSnippets))]
    public void ElementsKindTransitionsInLoopsGiveTheSameResults(string source) => MaglevCompilerTest.AssertSameWhenOptimized(source);

    [Theory]
    [MemberData(nameof(DeoptSnippets))]
    public void DeoptsFromLoopsGiveTheSameResults(string source) => MaglevCompilerTest.AssertSameWhenOptimized(source);

    [Fact]
    public void ColdDeoptExitResumesInTheInterpreter()
    {
        // A deopt through the cold exits' throw continues in the interpreter
        // with the frame state of the failing iteration, invalidates the code,
        // and the function is optimized again.
        Assert.Equal("190,190.5,0,8", MaglevCompilerTest.Run("--maglev", """
            function sum(a) { var s = 0; for (var i = 0; i < a.length; i++) s += a[i]; return s; }
            var a = []; for (var k = 0; k < 20; k++) a[k] = k;
            %PrepareFunctionForOptimization(sum);
            sum(a); sum(a);
            %OptimizeMaglevOnNextCall(sum);
            var r = [sum(a)];
            a[19] = 19.5;
            r.push(sum(a), %GetOptimizationStatus(sum) & 8);
            %OptimizeMaglevOnNextCall(sum);
            sum(a);
            r.push(%GetOptimizationStatus(sum) & 8);
            r.join();
            """));
    }

    [Fact]
    public void PendingInterruptExitsTheLoopWithoutInvalidating()
    {
        // A loop's interrupt check exits to the interpreter at the back edge
        // when an interrupt is pending (kInterrupt): the interpreter serves it,
        // the loop finishes there with the same result, and the code stays
        // valid (the next call runs it).
        var flags = new FlagList();
        flags.SetFlagsFromString("--allow-natives-syntax --maglev --no-concurrent-recompilation");
        Isolate isolate = Isolate.New(flags);
        using (isolate.Enter())
        {
            Compiler.CompileAndRun(isolate, """
                function spin(n) { var s = 0; for (var i = 0; i < n; i++) s = (s + i * 7) | 0; return s; }
                %PrepareFunctionForOptimization(spin);
                spin(10); spin(10);
                %OptimizeMaglevOnNextCall(spin);
                spin(10);
                """);
            int served = 0;
            using var stop = new CancellationTokenSource();
            var requester = new Thread(() =>
            {
                while (!stop.IsCancellationRequested)
                {
                    isolate.StackGuard.RequestApiInterrupt((_, _) => Interlocked.Increment(ref served), null);
                    Thread.Sleep(1);
                }
            });
            requester.Start();
            string result;
            try
            {
                result = ObjectOps.ToString(isolate, Compiler.CompileAndRun(isolate,
                    "var r = []; for (var k = 0; k < 20; k++) r.push(spin(3000000)); r.push(%GetOptimizationStatus(spin) & 8); r.join()")).ToString();
            }
            finally
            {
                stop.Cancel();
                requester.Join();
            }
            string expected = ObjectOps.ToString(isolate, Compiler.CompileAndRun(isolate, """
                var s = 0; for (var i = 0; i < 3000000; i++) s = (s + i * 7) | 0;
                var e = []; for (var k = 0; k < 20; k++) e.push(s); e.push(8); e.join()
                """)).ToString();
            Assert.Equal(expected, result);
            Assert.True(served > 0);
        }
    }
}
