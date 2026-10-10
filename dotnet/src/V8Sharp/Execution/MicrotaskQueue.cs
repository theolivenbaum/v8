// Port of src/execution/microtask-queue.{h,cc}, src/objects/microtask.h, the
// job task shapes of src/objects/promise.h, and the RunMicrotasks /
// RunSingleMicrotask / EnqueueMicrotask / GlobalQueueMicrotask builtins of
// src/builtins/builtins-microtask-queue-gen.cc.
//
// The promise jobs themselves (PromiseFulfillReactionJob,
// PromiseRejectReactionJob, PromiseResolveThenableJob) are in
// Builtins/Builtins.Promise.Jobs.cs. The PromiseReactionJobTasks are
// PromiseReaction objects in their job state (Objects/JSPromise.cs).
using V8Sharp.Builtins;
using V8Sharp.Objects;

namespace V8Sharp;

/// <summary>V8's Microtask: a unit of work in a MicrotaskQueue.</summary>
public abstract class Microtask(InstanceType type) : HeapObject(type)
{
}

/// <summary>V8's CallableTask: calls a function with an undefined receiver.</summary>
public sealed class CallableTask(JSReceiver callable, NativeContext context) : Microtask(InstanceType.MicrotaskType)
{
    public readonly JSReceiver Callable = callable;
    public readonly NativeContext Context = context;
}

/// <summary>V8's CallbackTask: an embedder callback.</summary>
public sealed class CallbackTask(Action<Isolate, object?> callback, object? data) : Microtask(InstanceType.MicrotaskType)
{
    public readonly Action<Isolate, object?> Callback = callback;
    public readonly object? Data = data;
}

/// <summary>V8's PromiseResolveThenableJobTask.</summary>
public sealed class PromiseResolveThenableJobTask(NativeContext context, JSPromise promiseToResolve, JSReceiver thenable,
    JSReceiver then) : Microtask(InstanceType.MicrotaskType)
{
    public readonly NativeContext Context = context;
    public readonly JSPromise PromiseToResolve = promiseToResolve;
    public readonly JSReceiver Thenable = thenable;
    public readonly JSReceiver Then = then;
}

/// <summary>
/// V8's AsyncResumeTask: resumes an async function (kAsyncFunctionAwait) or
/// an async generator's yield (kYield) with a non-thenable value, without the
/// closures and the throwaway promise of a full await.
/// </summary>
public sealed class AsyncResumeTask(JSGeneratorObject generator, JSValue value, AsyncResumeTask.Kind kind)
    : Microtask(InstanceType.MicrotaskType)
{
    /// <summary>AsyncResumeTask::Kind.</summary>
    public enum Kind { kAsyncFunctionAwait, kYield }

    public readonly JSGeneratorObject Generator = generator;
    public readonly JSValue Value = value;
    public readonly Kind TaskKind = kind;
}

/// <summary>V8's MicrotaskQueue: a ring buffer of pending microtasks.</summary>
public sealed class MicrotaskQueue(Isolate isolate)
{
    public const int kMinimumCapacity = 8;

    Microtask?[] _ringBuffer = [];
    int _capacity;
    int _size;
    int _start;
    long _finishedMicrotaskCount;
    bool _isRunningMicrotasks;
    int _microtasksScopeDepth;
    int _microtasksSuppressions;
    readonly Isolate _isolate = isolate;

    /// <summary>Callbacks run after each checkpoint (v8::MicrotasksCompletedCallback).</summary>
    public event Action<Isolate>? MicrotasksCompleted;

    /// <summary>
    /// Reports an exception thrown by a microtask (V8:
    /// Runtime_ReportMessageFromMicrotask, which reports to the message listeners).
    /// </summary>
    public event Action<Isolate, JavaScriptException>? UncaughtException;

    public Isolate Isolate => _isolate;
    public int Size => _size;
    public int Capacity => _capacity;
    public bool IsRunningMicrotasks => _isRunningMicrotasks;
    public long FinishedMicrotaskCount => _finishedMicrotaskCount;

    /// <summary>MicrotaskQueue::EnqueueMicrotask (and the EnqueueMicrotask builtin).</summary>
    public void EnqueueMicrotask(Microtask microtask)
    {
        if (_size == _capacity)
        {
            // Keep the capacity of the ring buffer a power of 2, so that the JIT
            // implementation can calculate the modulo easily.
            int newCapacity = Math.Max(kMinimumCapacity, _capacity << 1);
            ResizeBuffer(newCapacity);
        }
        _ringBuffer[(_start + _size) & (_capacity - 1)] = microtask;
        ++_size;
    }

    /// <summary>MicrotaskQueue::EnqueueMicrotask(v8::Local&lt;Function&gt;): a CallableTask in the current context.</summary>
    public void EnqueueMicrotask(Isolate isolate, JSReceiver function) =>
        EnqueueMicrotask(new CallableTask(function, isolate.NativeContext));

    /// <summary>MicrotaskQueue::EnqueueMicrotask(MicrotaskCallback, data).</summary>
    public void EnqueueMicrotask(Action<Isolate, object?> callback, object? data) =>
        EnqueueMicrotask(new CallbackTask(callback, data));

    void ResizeBuffer(int newCapacity)
    {
        var newRingBuffer = new Microtask?[newCapacity];
        for (int i = 0; i < _size; i++)
        {
            newRingBuffer[i] = _ringBuffer[(_start + i) % _capacity];
        }
        _ringBuffer = newRingBuffer;
        _capacity = newCapacity;
        _start = 0;
    }

    /// <summary>MicrotaskQueue::ShouldPerformCheckpoint.</summary>
    public bool ShouldPerformCheckpoint() =>
        !_isRunningMicrotasks && _microtasksScopeDepth == 0 && _microtasksSuppressions == 0;

    /// <summary>MicrotaskQueue::PerformCheckpoint.</summary>
    public void PerformCheckpoint(Isolate isolate)
    {
        if (!ShouldPerformCheckpoint()) return;
        PerformCheckpointInternal(isolate);
    }

    /// <summary>MicrotaskQueue::PerformCheckpointInternal.</summary>
    void PerformCheckpointInternal(Isolate isolate)
    {
        // Fast path: Checkpoints occur frequently when exiting script or microtask
        // scopes. If there are no microtasks to drain, no completion callbacks to
        // notify, and no kept objects from FinalizationRegistry / WeakRefs to clear,
        // we can bail out immediately.
        if (_size == 0 && MicrotasksCompleted is null && !isolate.HasKeptObjects) return;
        RunMicrotasks(isolate);
        isolate.ClearKeptObjects();
    }

    /// <summary>
    /// MicrotaskQueue::RunMicrotasks: runs until the queue is empty and returns
    /// the number of microtasks run. On termination the queue is dropped and
    /// the TerminationException propagates (V8 returns -1 with the termination
    /// exception pending).
    /// </summary>
    public int RunMicrotasks(Isolate isolate)
    {
        Debug.Assert(!_isRunningMicrotasks);
        _isRunningMicrotasks = true;
        // v8::Isolate::SuppressMicrotaskExecutionScope.
        _microtasksSuppressions++;
        try
        {
            if (_size == 0)
            {
                OnCompleted(isolate);
                return 0;
            }

            long baseCount = _finishedMicrotaskCount;
            try
            {
                RunMicrotasksBuiltin(isolate);
            }
            catch (TerminationException)
            {
                _ringBuffer = [];
                _capacity = 0;
                _size = 0;
                _start = 0;
                OnCompleted(isolate);
                throw;
            }
            Debug.Assert(_size == 0);
            OnCompleted(isolate);
            return (int)(_finishedMicrotaskCount - baseCount);
        }
        finally
        {
            _microtasksSuppressions--;
            _isRunningMicrotasks = false;
        }
    }

    void OnCompleted(Isolate isolate) => MicrotasksCompleted?.Invoke(isolate);

    /// <summary>The RunMicrotasks builtin: the loop over the ring buffer.</summary>
    void RunMicrotasksBuiltin(Isolate isolate)
    {
        // Load the current context from the isolate; it is restored at the end.
        Context? currentContext = isolate.Context;
        try
        {
            while (_size > 0)
            {
                Microtask microtask = _ringBuffer[_start]!;
                _ringBuffer[_start] = null;
                // Remove |microtask| from |ring_buffer| before running it, since its
                // invocation may add another microtask into |ring_buffer|.
                _size--;
                _start = (_start + 1) & (_capacity - 1);

                // Stash the microtask for async stack trace captures
                // (RootIndex::kCurrentMicrotask), cleared again afterwards.
                Microtask? previousMicrotask = isolate.CurrentMicrotask;
                isolate.CurrentMicrotask = microtask;
                try
                {
                    RunSingleMicrotask(isolate, currentContext, microtask);
                }
                finally
                {
                    isolate.CurrentMicrotask = previousMicrotask;
                }
                _finishedMicrotaskCount++;
            }
        }
        finally
        {
            isolate.Context = currentContext;
        }
    }

    /// <summary>
    /// PrepareForContext: enters the microtask's native context; false (skip
    /// the microtask) if the context was shut down (its queue detached).
    /// </summary>
    static bool PrepareForContext(Isolate isolate, NativeContext nativeContext)
    {
        // Skip the microtask execution if the associated context is shutdown.
        if (nativeContext.MicrotaskQueue is null) return false;
        isolate.Context = nativeContext;
        return true;
    }

    /// <summary>MicrotaskQueueBuiltinsAssembler::RunSingleMicrotask.</summary>
    void RunSingleMicrotask(Isolate isolate, Context? currentContext, Microtask microtask)
    {
        switch (microtask)
        {
            case CallableTask callable:
            {
                // Enter the context of the {microtask}.
                NativeContext microtaskContext = callable.Context;
                if (!PrepareForContext(isolate, microtaskContext)) return;
                try
                {
                    Execution.Call(isolate, callable.Callable, JSValue.Undefined, []);
                }
                catch (JavaScriptException e)
                {
                    // Report unhandled microtask exceptions in respective native context.
                    ReportMessageFromMicrotask(isolate, e);
                }
                return;
            }
            case CallbackTask callback:
                // For C++ microtasks we can use an arbitrary context; V8 uses the
                // current context. Only termination can escape (Runtime_RunMicrotaskCallback).
                isolate.Context = currentContext;
                callback.Callback(isolate, callback.Data);
                return;
            case PromiseResolveThenableJobTask thenable:
            {
                NativeContext microtaskContext = thenable.Context;
                if (!PrepareForContext(isolate, microtaskContext)) return;
                PromiseBuiltins.RunAllPromiseHooks(isolate, PromiseHookType.kBefore, thenable.PromiseToResolve);
                try
                {
                    PromiseBuiltins.PromiseResolveThenableJob(isolate, thenable.PromiseToResolve, thenable.Thenable, thenable.Then);
                }
                catch (JavaScriptException e)
                {
                    ReportMessageFromMicrotask(isolate, e);
                    return;
                }
                PromiseBuiltins.RunAllPromiseHooks(isolate, PromiseHookType.kAfter, thenable.PromiseToResolve);
                return;
            }
            case PromiseReaction task:
            {
                NativeContext microtaskContext = task.Context!.NativeContext;
                if (!PrepareForContext(isolate, microtaskContext)) return;
                JSValue promiseOrCapability = task.PromiseOrCapability;
                // Run the promise before/debug hook if enabled.
                PromiseBuiltins.RunAllPromiseHooks(isolate, PromiseHookType.kBefore, promiseOrCapability);
                try
                {
                    if (task.State == PromiseReaction.Kind.FulfillReactionJobTask)
                    {
                        PromiseBuiltins.PromiseFulfillReactionJob(isolate, task.Argument, task.Handler, promiseOrCapability);
                    }
                    else
                    {
                        Debug.Assert(task.State == PromiseReaction.Kind.RejectReactionJobTask);
                        PromiseBuiltins.PromiseRejectReactionJob(isolate, task.Argument, task.Handler, promiseOrCapability);
                    }
                }
                catch (JavaScriptException e)
                {
                    ReportMessageFromMicrotask(isolate, e);
                    return;
                }
                // Run the promise after/debug hook if enabled.
                PromiseBuiltins.RunAllPromiseHooks(isolate, PromiseHookType.kAfter, promiseOrCapability);
                return;
            }
            case AsyncResumeTask resume:
            {
                // Specialized handler for resuming async generators/functions when the
                // awaited/yielded value is a non-thenable.
                JSGeneratorObject generator = resume.Generator;
                NativeContext microtaskContext = generator.GetCreationContext() ?? generator.Context.NativeContext;
                if (!PrepareForContext(isolate, microtaskContext)) return;
                try
                {
                    if (resume.TaskKind == AsyncResumeTask.Kind.kAsyncFunctionAwait)
                    {
                        // kAsyncFunctionAwait: resume the async function with kNext.
                        generator.Mode = JSGeneratorObject.ResumeMode.kNext;
                        PromiseBuiltins.CallResumeGeneratorTrampoline(isolate, resume.Value, generator);
                    }
                    else
                    {
                        // kYield: AsyncGeneratorYieldWithAwaitResolveClosure logic.
                        var asyncGenerator = (JSAsyncGeneratorObject)generator;
                        // 1. SetGeneratorNotAwaiting
                        asyncGenerator.IsAwaiting = false;
                        // 2. AsyncGeneratorResolve(generator, value, false)
                        PromiseBuiltins.CallAsyncGeneratorResolve(isolate, asyncGenerator, resume.Value, false);
                        // 3. AsyncGeneratorResumeNext(generator)
                        PromiseBuiltins.CallAsyncGeneratorResumeNext(isolate, asyncGenerator);
                    }
                }
                catch (JavaScriptException e)
                {
                    ReportMessageFromMicrotask(isolate, e);
                }
                return;
            }
            default:
                throw new InvalidOperationException("RunSingleMicrotask: unreachable " + microtask.GetType().Name);
        }
    }

    /// <summary>Runtime_ReportMessageFromMicrotask.</summary>
    public void ReportMessageFromMicrotask(Isolate isolate, JavaScriptException e) => UncaughtException?.Invoke(isolate, e);

    /// <summary>v8::MicrotasksScope depth.</summary>
    public void IncrementMicrotasksScopeDepth() => ++_microtasksScopeDepth;
    public void DecrementMicrotasksScopeDepth() => --_microtasksScopeDepth;
    public void IncrementMicrotasksSuppressions() => ++_microtasksSuppressions;
    public void DecrementMicrotasksSuppressions() => --_microtasksSuppressions;
    public int GetMicrotasksScopeDepth() => _microtasksScopeDepth;
    public bool HasMicrotasksSuppressions() => _microtasksSuppressions != 0;
}
