// Port of src/maglev/maglev-concurrent-dispatcher.{h,cc} (MaglevCompilationJob:
// PrepareJobImpl, ExecuteJobImpl, FinalizeJobImpl; MaglevConcurrentDispatcher:
// the incoming and outgoing queues, the worker threads, FinalizeFinishedJobs,
// AwaitCompileJobs) and the Maglev parts of compiler.cc
// (Compiler::FinalizeMaglevCompilationJob, AbortMaglevCompilationJob).
//
// As in V8, a concurrent job is prepared on the main thread, builds the graph
// and generates the code on a worker thread (ExecuteJob), and is finalized on
// the main thread at the next INSTALL_MAGLEV_CODE interrupt, which commits
// the dependencies and installs the code. The worker also has RyuJIT compile
// the IL (fully optimized), so no JIT time is left for the first call.
//
// Heap access from the worker (V8: the JSHeapBroker with persistent handles
// and the heap's concurrent-access rules). V8Sharp's heap is managed .NET
// objects, so a read can never see freed or moved memory, but it can see a
// state the main thread is changing. The graph builder therefore (a) reads a
// property IC's (map, handler) pairs under the feedback vector's write
// sequence (FeedbackVector.PairWriteSequence, a seqlock: V8's
// feedback_vector_access mutex), (b) does not write the heap: map updates do
// not record migration targets (Map.TryUpdateNoWrite; V8: TryUpdateNoLock
// under map_updater_access), a function whose constant pool the interpreter
// has not materialized is not inlined, and protector cells are created under
// a lock, and (c) commits its dependencies on the main thread against the
// dependency log (DependentCode.RecordInvalidation): any invalidation of an
// object the code depends on since the job was prepared discards the code
// (V8's CompilationDependencies::Commit, which re-checks each dependency).
// Values the code folds from mutable slots are recorded as dependencies too.
// Anything else that goes wrong on the worker (an exception from a racing
// read) fails the job without disabling optimization; the function tiers up
// again later.
using System.Runtime.CompilerServices;
using V8Sharp.Interpreter;

namespace V8Sharp.Maglev;

/// <summary>MaglevCompilationJob: one compilation of a function (or an OSR entry).</summary>
public sealed class MaglevCompilationJob
{
    /// <summary>CompilationJob::State.</summary>
    public enum State : byte
    {
        kReadyToPrepare,
        kReadyToExecute,
        kReadyToFinalize,
        kSucceeded,
        kFailed,
    }

    MaglevCompilationJob(Isolate isolate, JSFunction function, int osrOffset, bool concurrent, bool byTieringManager)
    {
        Isolate = isolate;
        Function = function;
        OsrOffset = osrOffset;
        IsConcurrent = concurrent;
        ByTieringManager = byTieringManager;
    }

    /// <summary>MaglevCompilationJob::New.</summary>
    public static MaglevCompilationJob New(Isolate isolate, JSFunction function, int osrOffset, bool concurrent, bool byTieringManager) =>
        new(isolate, function, osrOffset, concurrent, byTieringManager);

    public Isolate Isolate { get; }
    public JSFunction Function { get; }
    /// <summary>The JumpLoop offset of an OSR compilation, or -1.</summary>
    public int OsrOffset { get; }
    public bool IsOsr => OsrOffset >= 0;
    /// <summary>ConcurrencyMode::kConcurrent: ExecuteJob runs on a worker thread.</summary>
    public bool IsConcurrent { get; }
    /// <summary>A heuristic (TieringManager) request rather than an explicit one (the natives).</summary>
    public bool ByTieringManager { get; }
    public State CurrentState { get; private set; } = State.kReadyToPrepare;
    /// <summary>bailout_reason_.</summary>
    public string? BailoutReason { get; private set; }
    /// <summary>The failure disables the function's optimization (AbortOptimization), rather than a retry later.</summary>
    public bool DisablesOptimization { get; private set; }
    public MaglevCode? Code { get; private set; }

    MaglevCompilationInfo? _info;
    /// <summary>The position of the isolate's dependency log when the job was prepared.</summary>
    long _dependencyLogStart;
    bool _logOpen;

    void CloseLog()
    {
        if (!_logOpen) return;
        _logOpen = false;
        Isolate.MaglevConcurrentDispatcher.EndDependencyLog(_dependencyLogStart);
    }

    public double PrepareMs { get; private set; }
    /// <summary>The time from EnqueueJob to the start of ExecuteJob (waiting for a worker).</summary>
    public double QueuedMs { get; private set; }
    internal long EnqueuedAt;
    /// <summary>The job's node in the dispatcher's incoming queue while it waits for a worker.</summary>
    internal LinkedListNode<MaglevCompilationJob>? QueueNode;
    public double ExecuteMs { get; private set; }
    public double FinalizeMs { get; private set; }
    public double GraphMs { get; private set; }
    /// <summary>RyuJIT's time compiling the code on the worker.</summary>
    public double JitMs { get; private set; }
    public int NodeCount => _info?.Graph.NodeCount ?? 0;

    void Fail(string reason, bool disable)
    {
        BailoutReason = reason;
        DisablesOptimization = disable;
        CurrentState = State.kFailed;
    }

    /// <summary>
    /// MaglevCompilationJob::PrepareJobImpl (main thread): the bailouts that
    /// need no graph, the constant pool, and the start of the dependency log.
    /// </summary>
    public State PrepareJob()
    {
        long start = System.Diagnostics.Stopwatch.GetTimestamp();
        SharedFunctionInfo shared = Function.Shared;
        if (Function.RawFeedbackCell.Value is not FeedbackVector || shared.FunctionData is not BytecodeArray bytecode)
        {
            Fail("no feedback vector", disable: false);
        }
        else if (bytecode.Length > Isolate.Flags.max_maglev_optimized_bytecode_size)
        {
            Fail("Function is too big to be optimized", disable: true);
        }
        else if (MaglevGraphBuilder.UnsupportedReason(shared, bytecode) is { } unsupported)
        {
            Fail(unsupported, disable: true);
        }
        else if (!shared.IsUserJavaScript())
        {
            Fail("not user JavaScript", disable: true);
        }
        else
        {
            if (bytecode.ConstantPoolValues is null) InterpreterRuntime.MaterializeConstantPool(Isolate, bytecode);
            _dependencyLogStart = Isolate.MaglevConcurrentDispatcher.BeginDependencyLog();
            _logOpen = true;
            CurrentState = State.kReadyToExecute;
            if (IsConcurrent && MaglevConcurrentDispatcher.GraphOnMainThread && !BuildGraphAndRunPasses(concurrent: false)) CloseLog();
        }
        PrepareMs = System.Diagnostics.Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        return CurrentState;
    }

    /// <summary>
    /// MaglevCompilationJob::ExecuteJobImpl (a worker thread for a concurrent
    /// job): MaglevCompiler::Compile (graph building and the passes) and the
    /// code generator, then RyuJIT.
    /// </summary>
    public State ExecuteJob()
    {
        if (CurrentState != State.kReadyToExecute) return CurrentState;
        long start = System.Diagnostics.Stopwatch.GetTimestamp();
        if (EnqueuedAt != 0) QueuedMs = System.Diagnostics.Stopwatch.GetElapsedTime(EnqueuedAt, start).TotalMilliseconds;
        try
        {
            if (IsConcurrent && Isolate.Flags.concurrent_recompilation_delay > 0) Thread.Sleep(Isolate.Flags.concurrent_recompilation_delay);
            if (_info is null && !BuildGraphAndRunPasses(IsConcurrent)) return CurrentState;
            MaglevCompilationInfo info = _info!;
            var code = new MaglevCode(Function, info.Toplevel.Feedback, OsrOffset)
            {
                OsrEntryPoint = IsOsr ? info.Toplevel.BytecodeAnalysis.OsrEntryPoint : -1,
                NodeCount = info.Graph.NodeCount,
            };
            var generator = new MaglevCodeGenerator(info, code, optimizeFully: IsConcurrent);
            (MaglevCodeEntry entry, int ilSize) = generator.Generate();
            // RyuJIT compiles the methods now, on this thread (the direct call
            // entry too: otherwise its first call compiles it on the main thread).
            if (IsConcurrent)
            {
                TimeSpan jitBefore = System.Runtime.JitInfo.GetCompilationTime(currentThread: true);
                RuntimeHelpers.PrepareMethod(entry.Method.MethodHandle);
                if (code.FastCall is { } fastCall) RuntimeHelpers.PrepareMethod(fastCall.Method.MethodHandle);
                JitMs = (System.Runtime.JitInfo.GetCompilationTime(currentThread: true) - jitBefore).TotalMilliseconds;
            }
            code.Entry = entry;
            code.ILSize = ilSize;
            code.CompiledConcurrently = IsConcurrent;
            code.Dependencies = info.Dependencies.ToArray();
            code.InstanceSizePredictions = info.InstanceSizePredictions.ToArray();
            Code = code;
            CurrentState = State.kReadyToFinalize;
        }
        catch (MaglevBailoutException e)
        {
            Fail(e.Message, disable: true);
        }
        catch (MaglevConcurrentRetryException e)
        {
            Fail(e.Message, disable: false);
        }
        catch (Exception e) when (IsConcurrent && e is not OutOfMemoryException)
        {
            // A read that raced with the main thread (or a bug): the function
            // stays in its tier and tiers up again later.
            Fail("exception on the compile thread: " + e.GetType().Name + ": " + e.Message, disable: false);
            MaglevConcurrentDispatcher.ReportBackgroundException(this, e);
        }
        finally
        {
            ExecuteMs = System.Diagnostics.Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        }
        return CurrentState;
    }

    /// <summary>
    /// MaglevGraphBuilder::Build and the passes of MaglevCompiler::Compile;
    /// false (and the job failed) on a bailout.
    /// </summary>
    bool BuildGraphAndRunPasses(bool concurrent)
    {
        long start = System.Diagnostics.Stopwatch.GetTimestamp();
        try
        {
            _info = MaglevCompiler.BuildAndOptimizeGraph(Isolate, Function, OsrOffset, concurrent);
            if (ByTieringManager && _info.Graph.NodeCount > MaglevCompiler.kMaxTieringGraphNodes)
            {
                Fail($"graph too big for the IL backend ({_info.Graph.NodeCount} nodes)", disable: true);
                return false;
            }
            return true;
        }
        catch (MaglevBailoutException e)
        {
            Fail(e.Message, disable: true);
            return false;
        }
        finally
        {
            GraphMs = System.Diagnostics.Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        }
    }

    /// <summary>
    /// MaglevCompilationJob::FinalizeJobImpl and Compiler::FinalizeMaglevCompilationJob
    /// (main thread): commits the dependencies (CompilationDependencies::Commit)
    /// and installs the code, or aborts.
    /// </summary>
    public State FinalizeJob()
    {
        long start = System.Diagnostics.Stopwatch.GetTimestamp();
        MaglevConcurrentDispatcher dispatcher = Isolate.MaglevConcurrentDispatcher;
        try
        {
            if (CurrentState != State.kReadyToFinalize) return CurrentState;
            MaglevCode code = Code!;
            // CompilationDependencies::Commit: a dependency invalidated since the
            // job started reading the heap discards the code (it is retried
            // later, not disabled).
            if (dispatcher.InvalidatedSince(_dependencyLogStart, code.Dependencies) is { } invalidated)
            {
                Fail("bailed out due to dependency change (" + invalidated + ")", disable: false);
                return CurrentState;
            }
            foreach ((Map map, int inObjectProperties) in code.InstanceSizePredictions)
            {
                // InitialMapInstanceSizePredictionDependency::IsValid (the worker
                // read the map while slack tracking could finish).
                if (map.IsInobjectSlackTrackingInProgress() || map.GetInObjectProperties() != inObjectProperties)
                {
                    Fail("bailed out due to dependency change (instance size)", disable: false);
                    return CurrentState;
                }
            }
            if (MaglevCompiler.OptimizationDisabled(Function.Shared) && ByTieringManager)
            {
                Fail("optimization disabled meanwhile", disable: false);
                return CurrentState;
            }
            foreach (CompilationDependency dependency in code.Dependencies)
            {
                if (dependency.Object is { } obj) Objects.DependentCode.InstallDependency(Isolate, code, obj, dependency.Groups);
            }
            CurrentState = State.kSucceeded;
            return CurrentState;
        }
        finally
        {
            CloseLog();
            FinalizeMs = System.Diagnostics.Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        }
    }

    /// <summary>The job does not reach FinalizeJob: it releases its log position.</summary>
    internal void Dispose() => CloseLog();
}

/// <summary>
/// The graph builder needs something only the main thread can do (a heap
/// write): the concurrent job fails without disabling optimization.
/// </summary>
public sealed class MaglevConcurrentRetryException(string reason) : Exception(reason)
{
}

/// <summary>MaglevConcurrentDispatcher: the concurrent Maglev jobs of an isolate.</summary>
public sealed class MaglevConcurrentDispatcher
{
    readonly Isolate _isolate;

    internal MaglevConcurrentDispatcher(Isolate isolate) => _isolate = isolate;

    /// <summary>
    /// V8SHARP_MAGLEV_GRAPH_ON_MAIN_THREAD=1 builds the graph of a concurrent
    /// job in PrepareJob on the main thread (as V8Sharp did before the graph
    /// builder ran on the worker), for comparison.
    /// </summary>
    internal static readonly bool GraphOnMainThread = Environment.GetEnvironmentVariable("V8SHARP_MAGLEV_GRAPH_ON_MAIN_THREAD") == "1";

    /// <summary>
    /// V8SHARP_MAGLEV_REPORT_BACKGROUND_EXCEPTIONS: "1" prints a worker's
    /// exceptions to stderr; any other value is a file they are appended to
    /// (for conformance runs, whose tests pass whether or not a job failed).
    /// </summary>
    static readonly string? s_reportBackgroundExceptions = Environment.GetEnvironmentVariable("V8SHARP_MAGLEV_REPORT_BACKGROUND_EXCEPTIONS");

    /// <summary>Jobs that failed with an exception on a worker (V8SHARP_JIT_STATS).</summary>
    internal static int BackgroundExceptions;

    internal static void ReportBackgroundException(MaglevCompilationJob job, Exception e)
    {
        Interlocked.Increment(ref BackgroundExceptions);
        string message = $"[maglev: exception compiling {MaglevCompiler.DebugName(job.Function.Shared)} on the compile thread: {e}]";
        if (s_reportBackgroundExceptions is null or "1" || job.Isolate.Flags.trace_opt)
        {
            if (s_reportBackgroundExceptions is not null || job.Isolate.Flags.trace_opt) Console.Error.WriteLine(message);
            return;
        }
        lock (s_threadsLock) File.AppendAllText(s_reportBackgroundExceptions, message + Environment.NewLine);
    }

    // ---- The worker threads (V8: the platform's workers running JobTask) ------------------------------

    // The incoming queue (V8: a LockedQueue per dispatcher; the workers are the
    // platform's): process-wide, so the workers serve every isolate, in order
    // except for the jobs Prioritize moved to the front.
    static readonly LinkedList<MaglevCompilationJob> s_incoming = [];
    static readonly SemaphoreSlim s_incomingCount = new(0);
    static readonly Lock s_threadsLock = new();
    static int s_threads;

    /// <summary>Starts worker threads up to concurrent_maglev_max_threads (JobTask::GetMaxConcurrency).</summary>
    static void EnsureWorkers(Isolate isolate)
    {
        int max = Math.Max(1, (int)isolate.Flags.concurrent_maglev_max_threads);
        if (Volatile.Read(ref s_threads) >= max) return;
        lock (s_threadsLock)
        {
            while (s_threads < max)
            {
                var thread = new Thread(RunWorker, 16 * 1024 * 1024)
                {
                    IsBackground = true,
                    Name = "V8Sharp concurrent Maglev " + s_threads,
                    Priority = ThreadPriority.BelowNormal,
                };
                thread.Start();
                s_threads++;
            }
        }
    }

    /// <summary>JobTask::Run: executes the incoming jobs and queues them for finalization.</summary>
    static void RunWorker()
    {
        while (true)
        {
            s_incomingCount.Wait();
            MaglevCompilationJob job;
            lock (s_incoming)
            {
                job = s_incoming.First!.Value;
                s_incoming.RemoveFirst();
                job.QueueNode = null;
            }
            job.ExecuteJob();
            MaglevConcurrentDispatcher dispatcher = job.Isolate.MaglevConcurrentDispatcher;
            dispatcher._outgoing.Enqueue(job);
            // Requests the interrupt that finalizes the job on the main thread,
            // which happens even in case of failure.
            job.Isolate.StackGuard.RequestInterrupt(StackGuard.InterruptFlag.INSTALL_MAGLEV_CODE);
            dispatcher.JobDone();
        }
    }

    // ---- Queues ------------------------------------------------------------------------------------------

    readonly System.Collections.Concurrent.ConcurrentQueue<MaglevCompilationJob> _outgoing = new();
    // A Monitor (AwaitCompileJobs waits on it), not a System.Threading.Lock.
    readonly object _inFlightLock = new();
    /// <summary>Jobs enqueued and not yet in the outgoing queue.</summary>
    int _inFlight;

    void JobDone()
    {
        lock (_inFlightLock)
        {
            if (--_inFlight == 0) Monitor.PulseAll(_inFlightLock);
        }
    }

    /// <summary>Jobs posted to the workers and not yet finished.</summary>
    public int JobsInFlight => Volatile.Read(ref _inFlight);

    /// <summary>MaglevConcurrentDispatcher::EnqueueJob (main thread).</summary>
    public void EnqueueJob(MaglevCompilationJob job)
    {
        job.EnqueuedAt = System.Diagnostics.Stopwatch.GetTimestamp();
        EnsureWorkers(_isolate);
        lock (_inFlightLock) _inFlight++;
        lock (s_incoming) job.QueueNode = s_incoming.AddLast(job);
        s_incomingCount.Release();
    }

    /// <summary>
    /// OptimizingCompileDispatcher::Prioritize (V8's
    /// --concurrent-recompilation-front-running, which V8 applies to Turbofan
    /// jobs): a function that keeps getting budget interrupts while its job
    /// waits for a worker moves to the front of the queue. V8Sharp applies it
    /// to Maglev jobs, whose RyuJIT compiles queue for hundreds of
    /// milliseconds at start-up (V8 found two priorities not worth it for its
    /// own, much faster, Maglev compiles).
    /// </summary>
    public static void Prioritize(MaglevCompilationJob job)
    {
        lock (s_incoming)
        {
            if (job.QueueNode is not { } node || ReferenceEquals(s_incoming.First, node)) return;
            s_incoming.Remove(node);
            s_incoming.AddFirst(node);
        }
    }

    /// <summary>MaglevConcurrentDispatcher::FinalizeFinishedJobs (main thread, INSTALL_MAGLEV_CODE).</summary>
    public void FinalizeFinishedJobs()
    {
        while (_outgoing.TryDequeue(out MaglevCompilationJob? job))
        {
            MaglevCompiler.FinalizeMaglevCompilationJob(_isolate, job);
        }
    }

    /// <summary>MaglevConcurrentDispatcher::AwaitCompileJobs: waits for the jobs in flight.</summary>
    public void AwaitCompileJobs()
    {
        lock (_inFlightLock)
        {
            while (_inFlight > 0) Monitor.Wait(_inFlightLock);
        }
    }

    // ---- The dependency log (CompilationDependencies::Commit) ----------------------------------------

    // While a job is between PrepareJob and FinalizeJob, every invalidation of
    // dependent code (DependentCode.DeoptimizeDependencyGroups, on the main
    // thread) is logged; FinalizeJob discards code depending on an object
    // invalidated since its job was prepared. A job reads the heap at some time
    // in that window; the log covers whatever changed after the read. Only the
    // main thread touches the log.
    readonly List<(HeapObject Object, Objects.DependentCode.DependencyGroups Groups)> _log = [];
    /// <summary>The log position of _log[0].</summary>
    long _logBase;
    /// <summary>The log positions of the jobs between PrepareJob and FinalizeJob, in the order they were prepared.</summary>
    readonly List<long> _openJobs = [];

    /// <summary>Whether invalidations are being logged (a job is open).</summary>
    public bool IsLogging => _openJobs.Count > 0;

    internal long BeginDependencyLog()
    {
        long start = _logBase + _log.Count;
        _openJobs.Add(start);
        return start;
    }

    /// <summary>A job is finalized: the entries no open job needs any more are dropped.</summary>
    internal void EndDependencyLog(long start)
    {
        _openJobs.Remove(start);
        long keepFrom = _openJobs.Count == 0 ? _logBase + _log.Count : _openJobs[0];
        int drop = (int)(keepFrom - _logBase);
        if (drop <= 0) return;
        if (drop == _log.Count) _log.Clear();
        else if (drop >= 1024 || drop * 2 >= _log.Count) _log.RemoveRange(0, drop);
        else return;
        _logBase = keepFrom;
    }

    /// <summary>DependentCode.DeoptimizeDependencyGroups: records an invalidation while jobs are open.</summary>
    internal void RecordInvalidation(HeapObject obj, Objects.DependentCode.DependencyGroups groups)
    {
        if (_openJobs.Count > 0) _log.Add((obj, groups));
    }

    /// <summary>The first dependency invalidated since <paramref name="start"/>, or null.</summary>
    internal string? InvalidatedSince(long start, CompilationDependency[] dependencies)
    {
        int first = (int)(start - _logBase);
        if (first < _log.Count && dependencies.Length != 0)
        {
            var groupsOf = new Dictionary<HeapObject, Objects.DependentCode.DependencyGroups>(ReferenceEqualityComparer.Instance);
            foreach (CompilationDependency dependency in dependencies)
            {
                if (dependency.Object is { } o) groupsOf[o] = groupsOf.GetValueOrDefault(o) | dependency.Groups;
            }
            for (int i = first; i < _log.Count; i++)
            {
                (HeapObject obj, Objects.DependentCode.DependencyGroups groups) = _log[i];
                if (groupsOf.TryGetValue(obj, out Objects.DependentCode.DependencyGroups depends) && (depends & groups) != 0)
                {
                    return obj.GetType().Name + " " + (depends & groups);
                }
            }
        }
        foreach (CompilationDependency dependency in dependencies)
        {
            if (dependency.Validate is { } validate && !validate(_isolate)) return dependency.Object?.GetType().Name ?? "value";
        }
        return null;
    }
}
