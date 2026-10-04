// The code object of a baseline-compiled function: V8's Code of kind BASELINE
// (src/objects/code.h), which V8 installs as SharedFunctionInfo::baseline_code.
//
// V8's baseline code is machine code with the interpreter's frame layout and a
// bytecode offset table (src/baseline/bytecode-offset-iterator.h) mapping
// machine pcs back to bytecode offsets. V8Sharp's is an IL method over the
// same register-stack frame as the interpreter; it keeps the current bytecode
// offset in the frame record instead of a pc table, and can be entered at
// every entry offset (the function start, exception handlers and loop
// headers for OSR from the interpreter).
//
// Deviation: the IL is generated when the code first runs, not when the code
// object is created. The function counts as baseline-compiled from the
// moment the code is installed (HasBaselineCode, %ActiveTierIsSparkplug), as
// in V8; only the work is deferred. Generating and jitting a method costs
// about a millisecond, and --always-sparkplug installs code for every
// function a script contains, most of which never run.
using System.Runtime.CompilerServices;
using V8Sharp.Interpreter;

namespace V8Sharp.Baseline;

/// <summary>
/// The entry point of baseline code: runs the frame described by
/// <paramref name="state"/> from state.Pc (one of the code's entry offsets)
/// until it returns or suspends, and returns the accumulator.
/// </summary>
public delegate JSValue BaselineCodeEntry(Isolate isolate, ref InterpreterState state);

/// <summary>Code of kind BASELINE.</summary>
public sealed class BaselineCode
{
    readonly Isolate _isolate;
    BaselineCodeEntry? _entry;
    int _ilSize = -1;

    public BaselineCode(Isolate isolate, SharedFunctionInfo shared, BytecodeArray bytecode)
    {
        _isolate = isolate;
        SharedFunctionInfo = shared;
        Bytecode = bytecode;
        HasHandlers = bytecode.HandlerTable.Length != 0;
        FormalParameterCount = bytecode.ParameterCount - 1;
        RegisterCount = bytecode.RegisterCount;
        Register incoming = bytecode.IncomingNewTargetOrGeneratorRegister;
        IncomingNewTargetRegister = incoming.IsValid ? incoming.Index : int.MinValue;
        // Builtins::Call's checks on the callee that do not change after
        // compilation (CallFunction: class constructors throw, sloppy non-native
        // functions convert the receiver).
        CallableDirectly = !shared.IsClassConstructor;
        ConvertsReceiver = !shared.Native && shared.LanguageMode == LanguageMode.Sloppy;
        CheckStackOnEveryCall = bytecode.Length > kLargeFrameBytecodeLength;
    }

    public SharedFunctionInfo SharedFunctionInfo { get; }

    /// <summary>The bytecode the code was compiled from (V8: the Code's bytecode_or_interpreter_data).</summary>
    public BytecodeArray Bytecode { get; }

    /// <summary>The compiled method (generated on first use).</summary>
    public BaselineCodeEntry Entry
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _entry ?? Generate(null);
    }

    /// <summary>
    /// The compiled method, generated (on first use) with the feedback of
    /// <paramref name="vector"/> (the vector of the frame about to run it),
    /// which decides where the code inlines fast paths (BaselineCompiler.Feedback.cs).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public BaselineCodeEntry EntryFor(FeedbackVector? vector) => _entry ?? Generate(vector);

    /// <summary>The size of the generated IL in bytes (V8: instruction_size); -1 before generation.</summary>
    public int ILSize => _ilSize;

    /// <summary>Whether the bytecode has exception handlers (the entry then needs the handler dispatch loop).</summary>
    public bool HasHandlers { get; }

    // What a call needs to know about the callee (BaselineCalls), read from
    // the code object instead of the SharedFunctionInfo and the BytecodeArray.

    /// <summary>
    /// The materialized constant pool and the bytecodes of <see cref="Bytecode"/>,
    /// which the code's prologue loads from its code object (set when the code
    /// is generated; the constant pool is materialized by then).
    /// </summary>
    public JSValue[]? Constants;
    public byte[]? Bytecodes;

    /// <summary>The bytecode's formal parameter count (without the receiver).</summary>
    public readonly int FormalParameterCount;

    /// <summary>The register file size.</summary>
    public readonly int RegisterCount;

    /// <summary>The register index of the incoming new.target or generator, or int.MinValue.</summary>
    public readonly int IncomingNewTargetRegister;

    /// <summary>Not a class constructor: [[Call]] runs the code.</summary>
    public readonly bool CallableDirectly;

    /// <summary>A sloppy, non-native function: a primitive receiver is converted (CallFunction).</summary>
    public readonly bool ConvertsReceiver;

    /// <summary>
    /// A large function, whose .NET frame may be large too: a call into it checks
    /// the .NET stack every time (BaselineCalls checks every fourth level otherwise).
    /// </summary>
    public readonly bool CheckStackOnEveryCall;

    const int kLargeFrameBytecodeLength = 1024;

    public static CodeKind Kind => CodeKind.BASELINE;

    [MethodImpl(MethodImplOptions.NoInlining)]
    BaselineCodeEntry Generate(FeedbackVector? vector)
    {
        // The code reads the materialized constant pool.
        if (Bytecode.ConstantPoolValues is null) InterpreterRuntime.MaterializeConstantPool(_isolate, Bytecode);
        return Generate(BaselineCompiler.MethodName(SharedFunctionInfo), prepare: false, vector);
    }

    /// <summary>
    /// The concurrent compiler's half (BaselineCompilerTask::Compile, on the
    /// background thread): generates the method and has RyuJIT compile it, so
    /// the first call runs compiled code. The constant pool was materialized
    /// and the name computed on the main thread.
    /// </summary>
    internal void GenerateConcurrently(string methodName, FeedbackVector? vector) => Generate(methodName, prepare: true, vector);

    /// <summary>V8SHARP_BASELINE_TIERED=1: concurrently compiled code starts at RyuJIT's tier 0 too (for comparison).</summary>
    static string Ms(long from, long to) =>
        System.Diagnostics.Stopwatch.GetElapsedTime(from, to).TotalMilliseconds.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture);

    static readonly bool s_tieredConcurrentCode = Environment.GetEnvironmentVariable("V8SHARP_BASELINE_TIERED") == "1";

    BaselineCodeEntry Generate(string methodName, bool prepare, FeedbackVector? vector)
    {
        Constants = Bytecode.ConstantPoolValues ?? throw new InvalidOperationException("constant pool not materialized");
        Bytecodes = Bytecode.Bytecodes;
        bool optimizeFully = prepare && !s_tieredConcurrentCode;
        long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        object? cacheKey = BaselineCodeCache.KeyFor(this);
        if (BaselineCodeCache.Get(cacheKey) is { } cached)
        {
            if (_isolate.Flags.trace_baseline)
            {
                Console.WriteLine("[baseline code for " + methodName + ": bytecode=" + Bytecode.Length + " from the code cache" +
                                  (cached.ChunkStarts is null ? "" : " chunks=" + cached.ChunkStarts.Length) + "]");
            }
            _ilSize = cached.ILSize;
            if (cached.ChunkStarts is null) return _entry = (BaselineCodeEntry)cached.Methods[0].CreateDelegate(typeof(BaselineCodeEntry), this);
            var cachedChunks = new BaselineCodeEntry[cached.Methods.Length];
            for (int i = 0; i < cachedChunks.Length; i++)
            {
                cachedChunks[i] = (BaselineCodeEntry)cached.Methods[i].CreateDelegate(typeof(BaselineCodeEntry), this);
            }
            _chunkStarts = cached.ChunkStarts;
            _chunks = cachedChunks;
            return _entry = RunChunks;
        }
        BaselineCompiler compiler;
        string? fullStatistics = null;
        if (BaselineCompiler.s_forceCompact is not null)
        {
            compiler = new BaselineCompiler(_isolate, SharedFunctionInfo, Bytecode, compact: true, methodName: methodName,
                optimizeFully: optimizeFully, feedback: vector);
            compiler.GenerateCode();
        }
        else
        {
            compiler = CompileRange(methodName, optimizeFully, vector, 0, Bytecode.Length, out fullStatistics, out double fullRatio);
            if (compiler.ExceedsOptimizationLimits || s_forceChunkLength > 0 && Bytecode.Length > s_forceChunkLength)
            {
                // Over RyuJIT's limits even with the checks out of line: one method
                // per range of the bytecode (BaselineCompiler's chunks), as few as
                // fit with the out-of-line checks (about a third less than the
                // full form's counts); a range still over the limits is split again.
                int pieces = Math.Max(2, (int)Math.Ceiling(fullRatio / 0.8));
                return GenerateChunks(methodName, prepare, optimizeFully, vector, t0, fullStatistics ?? compiler.Statistics, pieces, cacheKey);
            }
        }
        long t1 = System.Diagnostics.Stopwatch.GetTimestamp();
        (BaselineCodeEntry entry, int ilSize) = compiler.Build(this);
        long t2 = System.Diagnostics.Stopwatch.GetTimestamp();
        if (prepare && compiler.CompiledMethod is { } method) RuntimeHelpers.PrepareMethod(method.MethodHandle);
        if (compiler.CompiledMethod is { } compiled) BaselineCodeCache.Add(cacheKey, new BaselineCodeCache.Entry([compiled], null, ilSize));
        if (_isolate.Flags.trace_baseline)
        {
            long t3 = System.Diagnostics.Stopwatch.GetTimestamp();
            Console.WriteLine("[baseline code for " + methodName + ": bytecode=" + Bytecode.Length + " " + compiler.Statistics +
                              (fullStatistics is null ? "" : " (full: " + fullStatistics + ")") + " emit=" + Ms(t0, t1) + " build=" + Ms(t1, t2) + " jit=" + Ms(t2, t3) + "]");
        }
        _ilSize = ilSize;
        return _entry = entry;
    }

    /// <summary>
    /// The code of bytecode range [start, end): the full form, else the form
    /// with the number checks out of line (BaselineCompiler._outOfLineChecks);
    /// the result may still exceed RyuJIT's limits.
    /// </summary>
    BaselineCompiler CompileRange(string methodName, bool optimizeFully, FeedbackVector? vector, int start, int end, out string? fullStatistics,
        out double fullRatio)
    {
        fullStatistics = null;
        var compiler = new BaselineCompiler(_isolate, SharedFunctionInfo, Bytecode, methodName: methodName, optimizeFully: optimizeFully,
            feedback: vector, chunkStart: start, chunkEnd: end);
        compiler.GenerateCode();
        fullRatio = compiler.LimitRatio;
        if (!compiler.ExceedsOptimizationLimits) return compiler;
        fullStatistics = compiler.Statistics;
        // The out-of-line form saves about a third: not worth emitting far over the limits.
        if (fullRatio > 1.5) return compiler;
        compiler = new BaselineCompiler(_isolate, SharedFunctionInfo, Bytecode, methodName: methodName, optimizeFully: optimizeFully,
            feedback: vector, outOfLineChecks: true, chunkStart: start, chunkEnd: end);
        compiler.GenerateCode();
        return compiler;
    }

    // ---- Chunked code -----------------------------------------------------------------------------

    /// <summary>A range's code is compiled compact rather than split further below this many bytecode bytes.</summary>
    const int kMinChunkLength = 256;

    /// <summary>V8SHARP_BASELINE_CHUNK=n: every function over n bytecode bytes is chunked (for testing).</summary>
    static readonly int s_forceChunkLength =
        int.TryParse(Environment.GetEnvironmentVariable("V8SHARP_BASELINE_CHUNK"), out int n) ? n : 0;

    BaselineCodeEntry[]? _chunks;
    int[]? _chunkStarts;

    /// <summary>The number of methods the code is made of (1 unless chunked).</summary>
    public int ChunkCount => _chunks?.Length ?? 1;

    BaselineCodeEntry GenerateChunks(string methodName, bool prepare, bool optimizeFully, FeedbackVector? vector, long t0,
        string fullStatistics, int pieces, object? cacheKey)
    {
        int[] loopDepth = LoopDepths(Bytecode);
        int[] boundaries = BytecodeBoundaries(Bytecode);
        var compilers = new List<BaselineCompiler>();
        var starts = new List<int>();
        var pending = new Stack<(int Start, int End)>();
        // Ranges in ascending order: the stack holds them last-first.
        int[] cuts = SplitInto(boundaries, loopDepth, Bytecode.Length, s_forceChunkLength > 0
            ? Math.Max(pieces, (Bytecode.Length + s_forceChunkLength - 1) / s_forceChunkLength) : pieces);
        for (int i = cuts.Length - 2; i >= 0; i--) pending.Push((cuts[i], cuts[i + 1]));
        while (pending.Count != 0)
        {
            (int start, int end) = pending.Pop();
            BaselineCompiler compiler = CompileRange(methodName + "#" + start, optimizeFully, vector, start, end, out _, out _);
            bool forced = s_forceChunkLength > 0 && end - start > s_forceChunkLength;
            if (compiler.ExceedsOptimizationLimits || forced)
            {
                int split = end - start >= 2 * kMinChunkLength || forced ? SplitPoint(boundaries, loopDepth, start, end) : -1;
                if (split > start)
                {
                    pending.Push((split, end));
                    pending.Push((start, split));
                    continue;
                }
                compiler = new BaselineCompiler(_isolate, SharedFunctionInfo, Bytecode, compact: true, methodName: methodName + "#" + start,
                    optimizeFully: optimizeFully, feedback: vector, chunkStart: start, chunkEnd: end);
                compiler.GenerateCode();
            }
            compilers.Add(compiler);
            starts.Add(start);
        }
        long t1 = System.Diagnostics.Stopwatch.GetTimestamp();
        var chunks = new BaselineCodeEntry[compilers.Count];
        int ilSize = 0;
        for (int i = 0; i < compilers.Count; i++)
        {
            (chunks[i], int size) = compilers[i].Build(this);
            ilSize += size;
        }
        long t2 = System.Diagnostics.Stopwatch.GetTimestamp();
        if (prepare)
        {
            foreach (BaselineCompiler compiler in compilers)
            {
                if (compiler.CompiledMethod is { } method) RuntimeHelpers.PrepareMethod(method.MethodHandle);
            }
        }
        if (_isolate.Flags.trace_baseline)
        {
            long t3 = System.Diagnostics.Stopwatch.GetTimestamp();
            var line = new System.Text.StringBuilder("[baseline code for " + methodName + ": bytecode=" + Bytecode.Length + " chunks=" +
                                                     compilers.Count + " (full: " + fullStatistics + ")");
            for (int i = 0; i < compilers.Count; i++) line.Append(" [" + starts[i] + ": " + compilers[i].Statistics + "]");
            line.Append(" emit=" + Ms(t0, t1) + " build=" + Ms(t1, t2) + " jit=" + Ms(t2, t3) + "]");
            Console.WriteLine(line.ToString());
        }
        _chunkStarts = starts.ToArray();
        _chunks = chunks;
        _ilSize = ilSize;
        if (compilers.TrueForAll(c => c.CompiledMethod is not null))
        {
            BaselineCodeCache.Add(cacheKey, new BaselineCodeCache.Entry(compilers.ConvertAll(c => c.CompiledMethod!).ToArray(), _chunkStarts, ilSize));
        }
        return _entry = RunChunks;
    }

    /// <summary>
    /// The entry of chunked code: runs the chunk of state.Pc until one returns
    /// a value other than BaselineCompiler.ChunkExitMarker (a chunk exits with
    /// the next offset in state.Pc and the accumulator in the state).
    /// </summary>
    JSValue RunChunks(Isolate isolate, ref InterpreterState state)
    {
        BaselineCodeEntry[] chunks = _chunks!;
        int[] starts = _chunkStarts!;
        while (true)
        {
            int pc = state.Pc;
            int i = starts.Length - 1;
            while (starts[i] > pc) i--;
            JSValue result = chunks[i](isolate, ref state);
            if (!ReferenceEquals(result._obj, BaselineCompiler.ChunkExitMarker)) return result;
        }
    }

    /// <summary>The loop nesting depth of each bytecode offset (JumpLoop back to its header).</summary>
    static int[] LoopDepths(BytecodeArray bytecode)
    {
        var depth = new int[bytecode.Length + 1];
        for (var it = new BytecodeArrayIterator(bytecode); !it.Done(); it.Advance())
        {
            if (it.CurrentBytecode() != Interpreter.Bytecode.JumpLoop) continue;
            int header = it.GetJumpTargetOffset();
            for (int o = header; o <= it.CurrentOffset(); o++) depth[o]++;
        }
        return depth;
    }

    static int[] BytecodeBoundaries(BytecodeArray bytecode)
    {
        var result = new List<int>();
        for (var it = new BytecodeArrayIterator(bytecode); !it.Done(); it.Advance()) result.Add(it.CurrentOffset());
        return result.ToArray();
    }

    /// <summary>
    /// The boundaries of about <paramref name="pieces"/> equal ranges of the
    /// bytecode (0 and the length included), each cut at the least loop depth
    /// within a third of a piece of the even cut.
    /// </summary>
    static int[] SplitInto(int[] boundaries, int[] loopDepth, int length, int pieces)
    {
        var cuts = new List<int> { 0 };
        int piece = length / pieces;
        for (int k = 1; k < pieces; k++)
        {
            int even = k * piece, window = piece / 3;
            int best = -1, bestDepth = int.MaxValue, bestDistance = int.MaxValue;
            foreach (int b in boundaries)
            {
                if (b <= cuts[^1] || b < even - window || b > even + window) continue;
                int d = loopDepth[b], distance = Math.Abs(b - even);
                if (d < bestDepth || d == bestDepth && distance < bestDistance)
                {
                    best = b;
                    bestDepth = d;
                    bestDistance = distance;
                }
            }
            if (best > cuts[^1]) cuts.Add(best);
        }
        cuts.Add(length);
        return cuts.ToArray();
    }

    /// <summary>
    /// Where to split [start, end): a bytecode boundary in its middle third at
    /// the least loop depth (a chunk boundary inside a loop costs a chunk exit
    /// and entry per iteration), nearest the middle; -1 when there is none.
    /// </summary>
    static int SplitPoint(int[] boundaries, int[] loopDepth, int start, int end)
    {
        int lo = start + (end - start) / 3, hi = end - (end - start) / 3, middle = start + (end - start) / 2;
        int best = -1, bestDepth = int.MaxValue, bestDistance = int.MaxValue;
        foreach (int b in boundaries)
        {
            if (b <= lo || b >= hi) continue;
            int d = loopDepth[b], distance = Math.Abs(b - middle);
            if (d < bestDepth || d == bestDepth && distance < bestDistance)
            {
                best = b;
                bestDepth = d;
                bestDistance = distance;
            }
        }
        return best;
    }
}
