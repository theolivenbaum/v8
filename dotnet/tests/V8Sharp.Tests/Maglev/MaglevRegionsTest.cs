// Tests of the Maglev code generator's region splitting
// (MaglevCodeGenerator.Regions.cs): code beyond RyuJIT's optimization limits
// is split into several IL methods. The Maglev snippets of the other test
// classes run with splitting forced on small code (FlagList.MaglevSplitILBytes),
// so every function of a few hundred bytes of IL is split into regions, and
// must give the interpreter's results.
using V8Sharp.Codegen;

namespace V8Sharp.Tests.Maglev;

public class MaglevRegionsTest
{
    static string RunSplit(string flags, string source, int splitBytes)
    {
        var flagList = new FlagList();
        flagList.SetFlagsFromString("--allow-natives-syntax " + flags);
        flagList.MaglevSplitILBytes = splitBytes;
        Isolate isolate = Isolate.New(flagList);
        using (isolate.Enter())
        {
            try
            {
                JSValue value = Compiler.CompileAndRun(isolate, source);
                Execution.PerformMicrotaskCheckpoint(isolate);
                return ObjectOps.ToString(isolate, value).ToString();
            }
            catch (JavaScriptException e)
            {
                return "threw " + ObjectOps.ToString(isolate, e.Value);
            }
        }
    }

    static void AssertSameWhenSplit(string source)
    {
        string interpreted = MaglevCompilerTest.Run("--no-maglev --no-sparkplug", source);
        foreach (string flags in MaglevCompilerTest.StressConfigurations)
        {
            foreach (int split in new[] { 300, 1500 })
            {
                string optimized = RunSplit(flags, source, split);
                Assert.True(interpreted == optimized, $"{flags} split at {split}:\n  interpreted: {interpreted}\n  optimized:   {optimized}");
            }
        }
    }

    public static TheoryData<string> AllSnippets
    {
        get
        {
            var all = new TheoryData<string>();
            foreach (TheoryData<string> data in new[]
                     {
                         MaglevCompilerTest.Snippets, MaglevBuiltinReductionTest.Snippets, MaglevCallsTest.Snippets,
                         MaglevCallsTest.FramelessSnippets, MaglevCallsTest.FeedbackCellSnippets, MaglevCodeQualityTest.Snippets, MaglevCodeQualityTest.LoadEliminationSnippets,
                         MaglevCodeQualityTest.FieldRepresentationSnippets, MaglevCodeQualityTest.StoreSnippets,
                         MaglevCodeQualityTest.IndexSnippets, MaglevCodeQualityTest.LoopPeelingSnippets, MaglevInliningTest.Snippets, MaglevGeneratorTest.Snippets,
                         MaglevTypesTest.PrototypeChainSnippets, MaglevTypesTest.PhiTypeSnippets, Snippets,
                     })
            {
                foreach (TheoryDataRow<string> row in data) all.Add(row.Data);
            }
            return all;
        }
    }

    public static TheoryData<string> Snippets => new()
    {
        // A big function with nested loops, a switch in a loop (a state
        // machine), values live across many blocks, int32 and double phis.
        """
        (function() {
          function machine(input) {
            var state = 0, i = 0, acc = 0, dbl = 0.5, str = '', last = null, n = input.length;
            while (i < n) {
              var c = input.charCodeAt(i);
              switch (state) {
                case 0:
                  if (c >= 48 && c <= 57) { state = 1; acc = c - 48; }
                  else if (c === 32) { state = 0; }
                  else { state = 2; str = String.fromCharCode(c); }
                  break;
                case 1:
                  if (c >= 48 && c <= 57) { acc = acc * 10 + (c - 48); }
                  else { last = { kind: 'num', value: acc, d: dbl }; dbl = dbl * 1.5 + acc; state = c === 32 ? 0 : 2; str = c === 32 ? '' : String.fromCharCode(c); }
                  break;
                default:
                  if (c === 32) { last = { kind: 'word', value: str, d: dbl }; state = 0; str = ''; }
                  else { str += String.fromCharCode(c); for (var k = 0; k < 3; k++) dbl += k * 0.25; }
              }
              i++;
            }
            return [state, acc, dbl.toFixed(3), str, last && last.kind, last && last.value].join(':');
          }
          var out = [];
          for (var r = 0; r < 30; r++) out.push(machine('12 ab 345 cde ' + r + ' x' + (r * 7)));
          return out.join('|');
        })()
        """,
        // Loops over arrays with values defined before the loops and used
        // after, a loop-carried object, deopts in the middle.
        """
        (function() {
          function work(a, scale) {
            var sum = 0, prod = 1, obj = { x: 0 }, mx = -Infinity, cnt = 0;
            for (var i = 0; i < a.length; i++) {
              sum += a[i] * scale;
              if (a[i] > mx) mx = a[i];
              if ((a[i] & 1) === 0) { cnt++; obj = { x: obj.x + a[i] }; }
              for (var j = 0; j < 3; j++) prod = (prod * 3 + j) % 1000003;
            }
            var t = 0;
            for (var k = a.length - 1; k >= 0; k--) t = (t * 31 + a[k] + cnt) | 0;
            return [sum, prod, obj.x, mx, cnt, t].join(',');
          }
          var out = [];
          for (var r = 0; r < 30; r++) { var a = []; for (var q = 0; q < 20 + r; q++) a.push((q * 7 + r) % 23); out.push(work(a, r % 3 + 1)); }
          out.push(work([1.5, 2.5, 'x'], 2), work([], 1));
          return out.join('|');
        })()
        """,
    };

    [Fact]
    public void LoopPhisWithTheirOwnLocalsKeepTheirInputsLive()
    {
        // A loop header entered from another region keeps its phis in their
        // own locals; the back edge's inputs (i + 1) must stay live to the
        // end of the loop body (they once shared a local with c).
        const string source = """
            (function() {
              function sub(ta, aa, ra, t, at) {
                var i = 0, c = 0, m = Math.min(at, t);
                while (i < m) { c += ta[i] - aa[i]; ra[i++] = c & 0xfffffff; c >>= 28; }
                if (at < t) { while (i < t) { c += ta[i]; ra[i++] = c & 0xfffffff; c >>= 28; } }
                else { while (i < at) { c -= aa[i]; ra[i++] = c & 0xfffffff; c >>= 28; } }
                return i + ':' + c + ':' + ra.join(',');
              }
              var out = [];
              for (var k = 0; k < 300; k++) {
                var n = 5 + (k % 7), ta = [], aa = [];
                for (var q = 0; q < n; q++) { ta.push((k * 13 + q * 7) % 1000); aa.push((k * 3 + q) % 999); }
                out.push(sub(ta, aa, aa, k % 9 == 4 ? n + 3 : n, n - 1), sub(aa, ta, [], n - 1, n));
              }
              return out.join('|');
            })()
            """;
        AssertSameWhenSplit(source);
        // Synchronous compiles: the split certainly happens during the run.
        int before = V8Sharp.Maglev.MaglevCodeGenerator.SplitCompilations;
        string interpreted = MaglevCompilerTest.Run("--no-maglev --no-sparkplug", source);
        Assert.Equal(interpreted, RunSplit("--maglev --no-concurrent-recompilation --invocation-count-for-maglev=2", source, 300));
        Assert.True(V8Sharp.Maglev.MaglevCodeGenerator.SplitCompilations > before, "nothing was split");
    }

    [Theory]
    [MemberData(nameof(AllSnippets))]
    public void SplitCodeGivesTheSameResults(string source) => AssertSameWhenSplit(source);
}
