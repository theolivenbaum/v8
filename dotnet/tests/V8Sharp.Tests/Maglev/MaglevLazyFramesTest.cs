// Lazy optimized frames (MaglevCalls, "Lazy optimized frames"; V8's
// MaglevFrame, src/execution/frames.cc OptimizedJSFrame::Summarize and
// src/deoptimizer/deoptimizer.cc): direct calls into Maglev code that calls
// out push a frame record pointing at the activation instead of building
// the interpreter frame. Stack traces, function.arguments and .caller,
// Error.captureStackTrace, exceptions and deopts (eager and lazy) must see
// the same frames as in the interpreter.
using V8Sharp.Codegen;
using V8Sharp.Maglev;

namespace V8Sharp.Tests.Maglev;

public class MaglevLazyFramesTest
{
    /// <summary>Forced optimization without inlining, so the callees are entered through their direct entries.</summary>
    const string kNoInlining =
        "--maglev --no-concurrent-recompilation --invocation-count-for-maglev=2 --invocation-count-for-feedback-allocation=1 " +
        "--max-maglev-inlined-bytecode-size=0 --max-maglev-inlined-bytecode-size-small=0";

    public static TheoryData<string> Snippets => new()
    {
        // Stack traces through lazy frames: method names (the receiver),
        // positions of the calls, constructors, Error.captureStackTrace.
        """
        (function() {
          function Pt(x) { this.x = x; }
          Pt.prototype.leafTrace = function (k) { return new Error('e' + k).stack.split('\n').slice(1, 5).map(function (s) { return s.trim(); }).join('|'); };
          Pt.prototype.mid = function (k) { var r = this.leafTrace(k); return r + '#' + this.x; };
          function Ctor(p, k) { this.s = p.mid(k); }
          function top(p, k) { return k % 3 == 0 ? new Ctor(p, k).s : p.mid(k); }
          function capture(k) { var o = {}; Error.captureStackTrace(o); return o.stack.split('\n').length + ':' + k; }
          function viaCapture(k) { return capture(k) + '/' + k; }
          var out = [];
          for (var k = 0; k < 30; k++) out.push(top(new Pt(k), k), viaCapture(k));
          return out.join('\n').replace(/\(.*?:(\d+):(\d+)\)/g, '($1:$2)');
        })()
        """,
        // function.arguments and function.caller of lazy frames, with
        // parameters reassigned before the call.
        """
        (function() {
          function peek() { return Array.prototype.join.call(mid.arguments, ',') + ';' + (peek.caller === mid) + ';' + (mid.caller === outer); }
          function mid(a, b) { if (b > 20) a = a + 'x'; return peek(); }
          function outer(a, b) { return mid(a, b) + '|' + a; }
          var out = [];
          for (var k = 0; k < 30; k++) out.push(outer(k, k + 1));
          return out.join(' ');
        })()
        """,
        // Exceptions through lazy frames, caught in optimized and
        // interpreted callers; the frame depth and the context are restored.
        """
        (function() {
          function thrower(k) { if (k % 4 == 1) throw new Error('t' + k); return k; }
          function mid(k) { return thrower(k) + 1; }
          function catcher(k) { try { return mid(k); } catch (e) { return e.message + ':' + e.stack.split('\n').length; } }
          function outer(k) { var x = catcher(k); return x + '|' + new Error().stack.split('\n').length; }
          var out = [];
          for (var k = 0; k < 40; k++) out.push(outer(k));
          return out.join();
        })()
        """,
        // Eager deopts of lazy frames (a changed map, a string, overflow), and
        // lazy deopts after a call (the callee changes a constant the caller
        // depends on).
        """
        (function() {
          var config = { scale: 2 };
          function leaf(o) { return o.v; }
          function mid(o, k) { var a = leaf(o); var b = a * config.scale; return (b + k) | 0; }
          function changer(o, k) { if (k == 33) config.scale = 3; return mid(o, k); }
          function outer(o, k) { return changer(o, k) + ':' + mid(o, k); }
          var out = [];
          for (var k = 0; k < 50; k++) {
            var o = k < 25 ? { v: k } : k < 40 ? { w: 0, v: k } : { v: 'q' + k };
            out.push(outer(o, k == 45 ? 2147483647 : k));
          }
          return out.join();
        })()
        """,
        // Lazy inlined frames (mid inlined into outer's lazy entry): stack
        // traces, mid.arguments with a reassigned parameter, a lazy deopt
        // after the call in the inlined body, exceptions through it.
        """
        (function() {
          var config = { k: 1 };
          function peek(t) {
            if (t === 1) return new Error('x').stack.split('\n').slice(1, 5).map(function (s) { return s.trim().replace(/\(.*?:(\d+):(\d+)\)/, '($1:$2)'); }).join('|');
            if (t === 2) return Array.prototype.join.call(mid.arguments, ',') + ';' + (peek.caller === mid) + ';' + (mid.caller === outer);
            if (t === 3 && config.k < 3) { config.k++; return 'changed'; }
            if (t === 4) throw new Error('boom');
            return 'p' + t;
          }
          function mid(t, a) { a = a + 1; var r = peek(t); return r + '/' + a + '/' + config.k; }
          function outer(t, a) { return mid(t, a) + '#' + a; }
          function driver(t, a) { try { return outer(t, a); } catch (e) { return 'caught ' + e.message + ' ' + e.stack.split('\n').length; } }
          var out = [];
          for (var i = 0; i < 200; i++) out.push(driver(i % 5, i));
          return out.join('\n');
        })()
        """,
        // Deep recursion through lazy frames, and the stack overflow RangeError.
        """
        (function() {
          function rec(n) { return n === 0 ? 0 : 1 + rec(n - 1); }
          var out = [];
          for (var k = 0; k < 20; k++) out.push(rec(k * 50));
          try { rec(1e7); } catch (e) { out.push(e instanceof RangeError); }
          out.push(rec(500));
          return out.join();
        })()
        """,
    };

    [Theory]
    [MemberData(nameof(Snippets))]
    public void SameResultWhenOptimized(string source)
    {
        MaglevCompilerTest.AssertSameWhenOptimized(source);
        string interpreted = MaglevCompilerTest.Run("--no-maglev --no-sparkplug", source);
        Assert.Equal(interpreted, MaglevCompilerTest.Run(kNoInlining, source));
    }

    [Fact]
    public void CalleesThatCallOutGetLazyFrames()
    {
        var flagList = new FlagList();
        flagList.SetFlagsFromString("--allow-natives-syntax --maglev --no-concurrent-recompilation --max-maglev-inlined-bytecode-size=0 " +
                                    "--max-maglev-inlined-bytecode-size-small=0");
        Isolate isolate = Isolate.New(flagList);
        using (isolate.Enter())
        {
            JSValue result = Compiler.CompileAndRun(isolate, """
                function leaf(a) { return a + 1; }
                function mid(a, b) { return leaf(a) + leaf(b); }
                function args() { return arguments.length; }
                %PrepareFunctionForOptimization(leaf);
                %PrepareFunctionForOptimization(mid);
                %PrepareFunctionForOptimization(args);
                mid(1, 2); args(1);
                %OptimizeFunctionOnNextCall(leaf);
                %OptimizeFunctionOnNextCall(mid);
                %OptimizeFunctionOnNextCall(args);
                mid(1, 2); args(1);
                [leaf, mid, args]
                """);
            var array = (JSArray)result.Object;
            MaglevDirectEntryKind Kind(int i)
            {
                var function = (JSFunction)JSReceiver.GetElement(isolate, array, (uint)i).Object;
                return ((FeedbackVector)function.RawFeedbackCell.Value!).MaglevCode!.DirectEntryKind;
            }
            Assert.Equal(MaglevDirectEntryKind.Frameless, Kind(0));
            Assert.Equal(MaglevDirectEntryKind.LazyFrame, Kind(1));
            // A function reading its actual arguments keeps the frameful entry.
            Assert.Equal(MaglevDirectEntryKind.Frameful, Kind(2));
        }
    }
}
