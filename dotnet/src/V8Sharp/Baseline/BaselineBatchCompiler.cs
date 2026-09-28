// Port of src/baseline/baseline-batch-compiler.{h,cc}: functions that reach
// their first interrupt tick are queued, and the queue is compiled as one
// batch once the estimated code size of its functions exceeds
// --baseline-batch-compilation-threshold.
//
// Deviation: V8 compiles the batch on a background thread when
// --concurrent-sparkplug is on (the default on x64). V8Sharp compiles it on the
// main thread (concurrent_sparkplug is off): generating the IL is cheap, and
// RyuJIT compiles each DynamicMethod lazily on its first call anyway.
namespace V8Sharp.Baseline;

public sealed class BaselineBatchCompiler(Isolate isolate)
{
    public const int kInitialQueueSize = 32;

    // Weak references to the SharedFunctionInfos of the current batch
    // (V8: a WeakFixedArray behind a global handle).
    readonly List<WeakReference<SharedFunctionInfo>> _compilationQueue = new(kInitialQueueSize);

    // Estimated instruction size of the current batch.
    int _estimatedInstructionSize;

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
            CompileBatch(function);
        }
        else
        {
            Enqueue(shared);
        }
    }

    void Enqueue(SharedFunctionInfo shared) => _compilationQueue.Add(new WeakReference<SharedFunctionInfo>(shared));

    /// <summary>Returns true if the current batch exceeds the threshold and should be compiled.</summary>
    bool ShouldCompileBatch(SharedFunctionInfo shared)
    {
        // Early return if the function is compiled with baseline already or it is not
        // suitable for baseline compilation.
        if (shared.HasBaselineCode) return false;
        if (!BaselineSupport.CanCompileWithBaseline(isolate, shared)) return false;

        long estimatedSize = BaselineCompiler.EstimateInstructionSize((Interpreter.BytecodeArray)shared.FunctionData!);
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
        foreach (WeakReference<SharedFunctionInfo> entry in _compilationQueue)
        {
            MaybeCompileFunction(entry);
        }
        ClearBatch();
    }

    /// <summary>
    /// Tries to compile an enqueued function. Returns false if compilation was
    /// not possible (the weak reference is no longer valid, ...).
    /// </summary>
    bool MaybeCompileFunction(WeakReference<SharedFunctionInfo> entry)
    {
        // Skip functions where the weak reference is no longer valid.
        if (!entry.TryGetTarget(out SharedFunctionInfo? shared)) return false;
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
