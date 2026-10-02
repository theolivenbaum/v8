// Port of src/baseline/baseline-batch-compiler.{h,cc}: functions that reach
// their first interrupt tick are queued, and the queue is compiled as one
// batch once the estimated code size of its functions exceeds
// --baseline-batch-compilation-threshold. With --concurrent-sparkplug (V8's
// default on x64) the batch is compiled on a background thread
// (ConcurrentBaselineCompiler) and installed on the main thread at its next
// interrupt check (StackGuard's INSTALL_BASELINE_CODE).
//
// Deviation: V8's concurrent compiler is a JobTask on the platform's worker
// threads, one per isolate. V8Sharp has one background thread per process
// that compiles the jobs of every isolate in order (concurrent_sparkplug_max_threads
// is 1 in V8 as well). Its work is generating the IL, creating the type and
// having RyuJIT compile the method (RuntimeHelpers.PrepareMethod), which is
// most of a function's compile cost; installing is setting the code on the
// SharedFunctionInfo.
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using V8Sharp.Interpreter;

namespace V8Sharp.Baseline;

public sealed class BaselineBatchCompiler(Isolate isolate)
{
    public const int kInitialQueueSize = 32;

    // Weak references to the functions of the current batch (V8: a
    // WeakFixedArray of SharedFunctionInfos behind a global handle; V8Sharp
    // keeps the closure, whose feedback vector guides the code, see
    // BaselineCompiler.Feedback.cs).
    readonly List<WeakReference<JSFunction>> _compilationQueue = new(kInitialQueueSize);

    // Estimated instruction size of the current batch.
    int _estimatedInstructionSize;

    ConcurrentBaselineCompiler? _concurrentCompiler;

    /// <summary>Batch compilation can be dynamically disabled (V8: when creating snapshots).</summary>
    public bool IsEnabled { get; set; } = true;

    /// <summary>Enqueues the SharedFunctionInfo of <paramref name="function"/> for compilation.</summary>
    public void EnqueueFunction(JSFunction function)
    {
        SharedFunctionInfo shared = function.Shared;
        // Immediately compile the function if batch compilation is disabled.
        if (!IsEnabled)
        {
            Codegen.Compiler.CompileBaseline(isolate, function);
            return;
        }
        if (ShouldCompileBatch(shared))
        {
            if (isolate.Flags.concurrent_sparkplug)
            {
                CompileBatchConcurrent(function);
            }
            else
            {
                CompileBatch(function);
            }
        }
        else
        {
            Enqueue(function);
        }
    }

    void Enqueue(JSFunction function) => _compilationQueue.Add(new WeakReference<JSFunction>(function));

    /// <summary>Returns true if the current batch exceeds the threshold and should be compiled.</summary>
    bool ShouldCompileBatch(SharedFunctionInfo shared)
    {
        // Early return if the function is compiled with baseline already or it is not
        // suitable for baseline compilation.
        if (shared.HasBaselineCode) return false;
        if (!BaselineSupport.CanCompileWithBaseline(isolate, shared)) return false;

        long estimatedSize = BaselineCompiler.EstimateInstructionSize((BytecodeArray)shared.FunctionData!);
        _estimatedInstructionSize = (int)Math.Min(int.MaxValue, _estimatedInstructionSize + estimatedSize);
        if (isolate.Flags.trace_baseline_batch_compilation)
        {
            Console.WriteLine("[Baseline batch compilation] Enqueued SFI " + shared.Name() + " with estimated size " +
                              estimatedSize + " (current budget: " + _estimatedInstructionSize + "/" +
                              isolate.Flags.baseline_batch_compilation_threshold + ")");
        }
        if (_estimatedInstructionSize >= isolate.Flags.baseline_batch_compilation_threshold)
        {
            if (isolate.Flags.trace_baseline_batch_compilation)
            {
                Console.WriteLine("[Baseline batch compilation] Compiling current batch of " + (_compilationQueue.Count + 1) +
                                  " functions");
            }
            return true;
        }
        return false;
    }

    /// <summary>Compiles the current batch.</summary>
    void CompileBatch(JSFunction function)
    {
        Codegen.Compiler.CompileBaseline(isolate, function);
        foreach (WeakReference<JSFunction> entry in _compilationQueue)
        {
            MaybeCompileFunction(entry);
        }
        ClearBatch();
    }

    /// <summary>Hands the current batch, with <paramref name="function"/>, to the concurrent compiler.</summary>
    void CompileBatchConcurrent(JSFunction function)
    {
        Enqueue(function);
        _concurrentCompiler ??= new ConcurrentBaselineCompiler(isolate);
        _concurrentCompiler.CompileBatch(_compilationQueue);
        ClearBatch();
    }

    /// <summary>Installs the code of the batches the concurrent compiler finished (StackGuard's INSTALL_BASELINE_CODE).</summary>
    public void InstallBatch() => _concurrentCompiler?.InstallBatch();

    /// <summary>
    /// Tries to compile an enqueued function. Returns false if compilation was
    /// not possible (the weak reference is no longer valid, ...).
    /// </summary>
    bool MaybeCompileFunction(WeakReference<JSFunction> entry)
    {
        // Skip functions where the weak reference is no longer valid.
        if (!entry.TryGetTarget(out JSFunction? function)) return false;
        SharedFunctionInfo shared = function.Shared;
        // Skip functions where the bytecode has been flushed.
        if (!shared.IsCompiled) return false;
        return Codegen.Compiler.CompileSharedWithBaseline(isolate, shared);
    }

    void ClearBatch()
    {
        _estimatedInstructionSize = 0;
        _compilationQueue.Clear();
    }
}

/// <summary>
/// ConcurrentBaselineCompiler: compiles batches on the background thread and
/// queues the results for installation on the main thread.
/// </summary>
internal sealed class ConcurrentBaselineCompiler(Isolate isolate)
{
    readonly ConcurrentQueue<BaselineBatchCompilerJob> _outgoingQueue = new();

    // The functions of jobs not installed yet (SharedFunctionInfo::is_sparkplug_compiling;
    // main thread only).
    readonly HashSet<SharedFunctionInfo> _compiling = new(ReferenceEqualityComparer.Instance);

    /// <summary>ConcurrentBaselineCompiler::CompileBatch.</summary>
    public void CompileBatch(List<WeakReference<JSFunction>> taskQueue)
    {
        var job = new BaselineBatchCompilerJob(isolate, taskQueue, _compiling);
        if (job.IsEmpty) return;
        BaselineCompileThread.Post(() =>
        {
            job.Compile();
            _outgoingQueue.Enqueue(job);
            isolate.StackGuard.RequestInterrupt(StackGuard.InterruptFlag.INSTALL_BASELINE_CODE);
        });
    }

    /// <summary>ConcurrentBaselineCompiler::InstallBatch.</summary>
    public void InstallBatch()
    {
        while (_outgoingQueue.TryDequeue(out BaselineBatchCompilerJob? job)) job.Install(_compiling);
    }
}

/// <summary>BaselineBatchCompilerJob: the tasks of one batch.</summary>
internal sealed class BaselineBatchCompilerJob
{
    readonly List<BaselineCompilerTask> _tasks = [];

    /// <summary>Main thread: takes the functions of the batch that can still be compiled.</summary>
    public BaselineBatchCompilerJob(Isolate isolate, List<WeakReference<JSFunction>> taskQueue,
        HashSet<SharedFunctionInfo> compiling)
    {
        foreach (WeakReference<JSFunction> entry in taskQueue)
        {
            // Skip functions where weak reference is no longer valid.
            if (!entry.TryGetTarget(out JSFunction? function)) continue;
            SharedFunctionInfo shared = function.Shared;
            // Skip functions where the bytecode has been flushed.
            if (!shared.IsCompiled || !CanCompileWithConcurrentBaseline(shared, isolate)) continue;
            // Skip functions that are already being compiled.
            if (!compiling.Add(shared)) continue;
            _tasks.Add(new BaselineCompilerTask(isolate, shared, function.RawFeedbackCell.Value as FeedbackVector));
        }
    }

    public bool IsEmpty => _tasks.Count == 0;

    /// <summary>Background thread.</summary>
    public void Compile()
    {
        foreach (BaselineCompilerTask task in _tasks) task.Compile();
    }

    /// <summary>Main thread.</summary>
    public void Install(HashSet<SharedFunctionInfo> compiling)
    {
        foreach (BaselineCompilerTask task in _tasks)
        {
            compiling.Remove(task.SharedFunctionInfo);
            task.Install();
        }
    }

    internal static bool CanCompileWithConcurrentBaseline(SharedFunctionInfo shared, Isolate isolate) =>
        !shared.HasBaselineCode && BaselineSupport.CanCompileWithBaseline(isolate, shared);
}

/// <summary>BaselineCompilerTask: one function of a concurrent batch.</summary>
internal sealed class BaselineCompilerTask
{
    readonly Isolate _isolate;
    readonly BaselineCode _code;
    readonly string _name;
    readonly FeedbackVector? _feedback;
    bool _compiled;
    double _ms, _cpuMs;

    /// <summary>The CPU time of the current thread (Linux /proc; 0 elsewhere), for --trace-baseline.</summary>
    static double ThreadCpuMilliseconds()
    {
        try
        {
            string text = File.ReadAllText("/proc/thread-self/schedstat");
            return long.Parse(text.AsSpan(0, text.IndexOf(' ')), System.Globalization.CultureInfo.InvariantCulture) / 1e6;
        }
        catch (IOException)
        {
            return 0;
        }
        catch (UnauthorizedAccessException)
        {
            return 0;
        }
    }

    /// <summary>Main thread: everything the background thread reads is prepared here.</summary>
    public BaselineCompilerTask(Isolate isolate, SharedFunctionInfo shared, FeedbackVector? feedback)
    {
        _isolate = isolate;
        _feedback = feedback;
        SharedFunctionInfo = shared;
        var bytecode = (BytecodeArray)shared.FunctionData!;
        // The compiler reads the materialized constant pool (jump tables,
        // constant jump offsets) and the function's name.
        if (bytecode.ConstantPoolValues is null) InterpreterRuntime.MaterializeConstantPool(isolate, bytecode);
        _name = BaselineCompiler.MethodName(shared);
        _code = new BaselineCode(isolate, shared, bytecode);
    }

    public SharedFunctionInfo SharedFunctionInfo { get; }

    /// <summary>Background thread: generates the code and has RyuJIT compile it.</summary>
    public void Compile()
    {
        bool trace = _isolate.Flags.trace_baseline;
        long start = System.Diagnostics.Stopwatch.GetTimestamp();
        double cpuStart = trace ? ThreadCpuMilliseconds() : 0;
        try
        {
            _code.GenerateConcurrently(_name, _feedback);
            _compiled = true;
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            // The function stays in the interpreter (V8: an empty MaybeHandle).
            if (_isolate.Flags.trace_baseline) Console.WriteLine("[Concurrent Sparkplug] " + _name + " failed: " + e.Message);
        }
        _ms = System.Diagnostics.Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        if (trace) _cpuMs = ThreadCpuMilliseconds() - cpuStart;
    }

    /// <summary>Main thread: BaselineCompilerTask::Install.</summary>
    public void Install()
    {
        if (!_compiled) return;
        // Don't install the code if the bytecode has been flushed or has
        // already some baseline code installed.
        if (!SharedFunctionInfo.IsCompiled ||
            !BaselineBatchCompilerJob.CanCompileWithConcurrentBaseline(SharedFunctionInfo, _isolate))
        {
            return;
        }
        SharedFunctionInfo.BaselineCode = _code;
        _isolate.MayHaveBaselineCode = true;
        if (_isolate.Flags.trace_baseline)
        {
            Console.WriteLine("[Concurrent Sparkplug Off Thread] Function " + _name + " installed (" +
                              _ms.ToString("0.000", System.Globalization.CultureInfo.InvariantCulture) + " ms, cpu " +
                              _cpuMs.ToString("0.000", System.Globalization.CultureInfo.InvariantCulture) + " ms)");
        }
    }
}

/// <summary>The process-wide background thread of the concurrent baseline compiler.</summary>
internal static class BaselineCompileThread
{
    static readonly BlockingCollection<Action> s_work = [];
    static Thread? s_thread;
    static readonly Lock s_lock = new();

    public static void Post(Action work)
    {
        if (s_thread is null)
        {
            lock (s_lock)
            {
                if (s_thread is null)
                {
                    var thread = new Thread(Run, 16 * 1024 * 1024)
                    {
                        IsBackground = true,
                        Name = "V8Sharp concurrent Sparkplug",
                        Priority = ThreadPriority.BelowNormal,
                    };
                    thread.Start();
                    s_thread = thread;
                }
            }
        }
        s_work.Add(work);
    }

    static void Run()
    {
        foreach (Action work in s_work.GetConsumingEnumerable()) work();
    }
}
