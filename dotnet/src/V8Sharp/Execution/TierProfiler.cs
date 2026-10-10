// V8Sharp diagnostics, no V8 counterpart (V8 answers the same question with
// --prof or perf and its code kinds): a sampling profiler that records which
// tier the running JavaScript frame executes in (Ignition, the baseline IL
// tier, Maglev) and in which function, so hot code that stays below Maglev
// can be found together with the reason (a bailout, a deopt loop, tiering
// that never triggered).
//
// A background thread reads the isolate's frame records every millisecond
// while sampling is on. It only reads: the top record's flags and frame
// pointer and, for frames that keep their closure in the register stack, the
// closure slot. A lazy Maglev frame keeps its closure in a MaglevActivation
// on the .NET stack, which another thread cannot read safely, so its samples
// count for Maglev without a function. Frameless Maglev code pushes no
// record and counts for its caller. Runtime, IC and builtin time counts for
// the JavaScript frame that called it.
using V8Sharp.Interpreter;

namespace V8Sharp;

public sealed class TierProfiler
{
    public enum Tier : byte { Interpreter, Baseline, Maglev, MaglevLazy, Outside }

    readonly Isolate _isolate;
    readonly Thread _thread;
    volatile bool _sampling;
    volatile bool _stop;
    readonly long[] _tierSamples = new long[5];
    // Per function: samples by tier, and how many of the lower-tier samples ran
    // while the closure's feedback vector already had Maglev code.
    readonly Dictionary<SharedFunctionInfo, long[]> _functions = new(ReferenceEqualityComparer.Instance);
    readonly object _lock = new();

    TierProfiler(Isolate isolate)
    {
        _isolate = isolate;
        _thread = new Thread(Run) { IsBackground = true, Name = "V8Sharp tier profiler" };
        _thread.Start();
    }

    /// <summary>Starts the sampling thread (sampling itself starts with <see cref="Resume"/>).</summary>
    public static TierProfiler Attach(Isolate isolate) => new(isolate);

    // Wall time and GC pauses of the sampled intervals.
    long _resumedAt, _wallTicks;
    TimeSpan _pauseAt, _gcPause;
    int _gen0At, _gen1At, _gen2At, _gen0, _gen1, _gen2;

    public void Resume()
    {
        if (_sampling) return;
        _resumedAt = System.Diagnostics.Stopwatch.GetTimestamp();
        _pauseAt = GC.GetTotalPauseDuration();
        (_gen0At, _gen1At, _gen2At) = (GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2));
        IC.ICIsolateState stats = IC.ICIsolateState.Get(_isolate);
        _missesAt = (stats.LoadMisses, stats.KeyedLoadMisses, stats.StoreMisses, stats.KeyedStoreMisses);
        _sampling = true;
    }

    public void Pause()
    {
        if (!_sampling) return;
        _sampling = false;
        _wallTicks += System.Diagnostics.Stopwatch.GetTimestamp() - _resumedAt;
        _gcPause += GC.GetTotalPauseDuration() - _pauseAt;
        _gen0 += GC.CollectionCount(0) - _gen0At;
        _gen1 += GC.CollectionCount(1) - _gen1At;
        _gen2 += GC.CollectionCount(2) - _gen2At;
        IC.ICIsolateState stats = IC.ICIsolateState.Get(_isolate);
        _misses.Load += stats.LoadMisses - _missesAt.Load;
        _misses.KeyedLoad += stats.KeyedLoadMisses - _missesAt.KeyedLoad;
        _misses.Store += stats.StoreMisses - _missesAt.Store;
        _misses.KeyedStore += stats.KeyedStoreMisses - _missesAt.KeyedStore;
    }

    (long Load, long KeyedLoad, long Store, long KeyedStore) _missesAt, _misses;

    public void Stop()
    {
        Pause();
        _stop = true;
        _thread.Join();
    }

    void Run()
    {
        while (!_stop)
        {
            if (_sampling) Sample();
            Thread.Sleep(1);
        }
    }

    void Sample()
    {
        int depth = _isolate.InterpreterFrameDepth;
        InterpreterFrameRecord[] frames = _isolate.InterpreterFrames;
        int i = Math.Min(depth, frames.Length) - 1;
        while (i >= 0 && (frames[i].Flags & InterpreterFrameFlags.Builtin) != 0) i--;
        if (i < 0)
        {
            _tierSamples[(int)Tier.Outside]++;
            return;
        }
        InterpreterFrameFlags flags = frames[i].Flags;
        int fp = frames[i].Fp;
        Tier tier = (flags & InterpreterFrameFlags.Lazy) != 0 ? Tier.MaglevLazy
            : (flags & InterpreterFrameFlags.Maglev) != 0 ? Tier.Maglev
            : (flags & InterpreterFrameFlags.Baseline) != 0 ? Tier.Baseline
            : Tier.Interpreter;
        _tierSamples[(int)tier]++;
        if (tier == Tier.MaglevLazy) return;
        int slot = fp + InterpreterRuntime.kClosureOffset;
        JSValue[] stack = _isolate.RegisterStack;
        if ((uint)slot >= (uint)stack.Length) return;
        if (stack[slot]._obj is not JSFunction function) return;
        SharedFunctionInfo shared = function.Shared;
        bool hasMaglevCode = function.RawFeedbackCell.Value is FeedbackVector { MaglevCode: not null };
        lock (_lock)
        {
            if (!_functions.TryGetValue(shared, out long[]? counts)) _functions[shared] = counts = new long[5];
            counts[(int)tier]++;
            if (hasMaglevCode && tier is Tier.Interpreter or Tier.Baseline) counts[4]++;
        }
    }

    /// <summary>Writes the tier shares and the hottest functions (run on the isolate's thread, after sampling).</summary>
    public void Report(TextWriter writer, int top = 40)
    {
        Pause();
        double wallMs = _wallTicks * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
        writer.WriteLine($"@tier-gc sampled-wall={wallMs:F0}ms gc-pause={_gcPause.TotalMilliseconds:F0}ms " +
                         $"({(wallMs > 0 ? 100 * _gcPause.TotalMilliseconds / wallMs : 0):F1}%) gen0={_gen0} gen1={_gen1} gen2={_gen2}");
        writer.WriteLine($"@tier-ic-misses load={_misses.Load} keyed-load={_misses.KeyedLoad} store={_misses.Store} keyed-store={_misses.KeyedStore}");
        long inJs = _tierSamples[0] + _tierSamples[1] + _tierSamples[2] + _tierSamples[3];
        long all = inJs + _tierSamples[4];
        writer.WriteLine($"@tier-samples total={all} interpreter={_tierSamples[0]} baseline={_tierSamples[1]} " +
                         $"maglev={_tierSamples[2] + _tierSamples[3]} (frameful/inlined {_tierSamples[2]}, lazy {_tierSamples[3]}) outside={_tierSamples[4]}");
        if (inJs > 0)
        {
            writer.WriteLine($"@tier-share interpreter={100.0 * _tierSamples[0] / inJs:F1}% baseline={100.0 * _tierSamples[1] / inJs:F1}% " +
                             $"maglev={100.0 * (_tierSamples[2] + _tierSamples[3]) / inJs:F1}%");
        }
        List<KeyValuePair<SharedFunctionInfo, long[]>> rows;
        lock (_lock) rows = [.. _functions];
        // The functions with the most samples below Maglev.
        rows.Sort(static (a, b) => (b.Value[0] + b.Value[1]).CompareTo(a.Value[0] + a.Value[1]));
        writer.WriteLine("@tier-functions (samples below Maglev, desc): interp baseline maglev(frameful) withMaglevCode | name bytes state");
        for (int k = 0; k < Math.Min(top, rows.Count); k++)
        {
            (SharedFunctionInfo shared, long[] c) = (rows[k].Key, rows[k].Value);
            if (c[0] + c[1] == 0) break;
            int length = shared.FunctionData is BytecodeArray bytecode ? bytecode.Length : -1;
            string? disabled = Maglev.MaglevCompiler.DisabledReason(shared);
            (int deopts, int compiles, int invalidations) = Maglev.MaglevCompiler.Stats(shared);
            writer.WriteLine($"@tier-fn {c[0],6} {c[1],6} {c[2],6} {c[4],6} | {Maglev.MaglevCompiler.DebugName(shared)}#{shared.FunctionLiteralId} " +
                             $"{length}B tiering={shared.CachedTieringDecision} baseline={shared.HasBaselineCode} compiles={compiles} " +
                             $"deopts={deopts} invalidations={invalidations}" + (disabled is null ? "" : " disabled=\"" + disabled + "\""));
        }
    }
}
