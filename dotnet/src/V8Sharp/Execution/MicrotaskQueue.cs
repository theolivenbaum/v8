// Port of src/execution/microtask-queue.{h,cc}, src/objects/microtask.h,
// src/objects/promise.h (the job task shapes) and the RunMicrotasks loop of
// src/builtins/builtins-microtask-queue-gen.cc.
//
// The promise reaction and thenable jobs themselves are promise machinery
// (PromiseReactionJob, PromiseResolveThenableJob in promise-*.tq); the
// promise builtins register them through IPromiseJobs.
using V8Sharp.Objects;

namespace V8Sharp;

/// <summary>V8's Microtask: a unit of work in a MicrotaskQueue.</summary>
public abstract class Microtask(InstanceType type) : HeapObject(type)
{
}

/// <summary>V8's CallableTask: calls a function with an undefined receiver.</summary>
public sealed class CallableTask(JSReceiver callable, Context context) : Microtask(InstanceType.MicrotaskType)
{
    public readonly JSReceiver Callable = callable;
    public readonly Context Context = context;
}

/// <summary>V8's CallbackTask: an embedder callback.</summary>
public sealed class CallbackTask(Action<Isolate, object?> callback, object? data) : Microtask(InstanceType.MicrotaskType)
{
    public readonly Action<Isolate, object?> Callback = callback;
    public readonly object? Data = data;
}

/// <summary>V8's PromiseReactionJobTask (PromiseFulfillReactionJobTask / PromiseRejectReactionJobTask).</summary>
public sealed class PromiseReactionJobTask(bool isReject, JSValue argument, Context context, JSValue handler,
    JSValue promiseOrCapability) : Microtask(InstanceType.MicrotaskType)
{
    public readonly bool IsReject = isReject;
    public JSValue Argument = argument;
    public readonly Context Context = context;
    public readonly JSValue Handler = handler;
    /// <summary>A JSPromise, a PromiseCapability or undefined (await).</summary>
    public readonly JSValue PromiseOrCapability = promiseOrCapability;
}

/// <summary>V8's PromiseResolveThenableJobTask.</summary>
public sealed class PromiseResolveThenableJobTask(Context context, JSReceiver promiseToResolve, JSReceiver thenable,
    JSReceiver then) : Microtask(InstanceType.MicrotaskType)
{
    public readonly Context Context = context;
    public readonly JSReceiver PromiseToResolve = promiseToResolve;
    public readonly JSReceiver Thenable = thenable;
    public readonly JSReceiver Then = then;
}

/// <summary>The promise jobs, implemented by the promise builtins.</summary>
public interface IPromiseJobs
{
    /// <summary>PromiseReactionJob.</summary>
    void RunPromiseReactionJob(Isolate isolate, PromiseReactionJobTask task);

    /// <summary>PromiseResolveThenableJob.</summary>
    void RunPromiseResolveThenableJob(Isolate isolate, PromiseResolveThenableJobTask task);
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

    /// <summary>The promise job implementations (set by the promise builtins).</summary>
    public static IPromiseJobs? PromiseJobs { get; set; }

    /// <summary>
    /// Reports an exception thrown by a microtask (V8:
    /// Isolate::ReportMessageFromMicrotask, which reports to the message listeners).
    /// </summary>
    public event Action<Isolate, JavaScriptException>? UncaughtException;

    public int Size => _size;
    public int Capacity => _capacity;
    public bool IsRunningMicrotasks => _isRunningMicrotasks;
    public long FinishedMicrotaskCount => _finishedMicrotaskCount;

    public void EnqueueMicrotask(Microtask microtask)
    {
        if (_size == _capacity)
        {
            int newCapacity = Math.Max(kMinimumCapacity, _capacity << 1);
            ResizeBuffer(newCapacity);
        }
        _ringBuffer[(_start + _size) % _capacity] = microtask;
        ++_size;
    }

    /// <summary>MicrotaskQueue::EnqueueMicrotask(v8::Local&lt;Function&gt;): a CallableTask in the current context.</summary>
    public void EnqueueMicrotask(Isolate isolate, JSReceiver function) =>
        EnqueueMicrotask(new CallableTask(function, isolate.NativeContext));

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

    public bool ShouldPerformCheckpoint() =>
        !_isRunningMicrotasks && _microtasksScopeDepth == 0 && _microtasksSuppressions == 0;

    /// <summary>MicrotaskQueue::PerformCheckpoint.</summary>
    public void PerformCheckpoint(Isolate isolate)
    {
        if (!ShouldPerformCheckpoint()) return;
        if (_size == 0 && MicrotasksCompleted is null) return;
        RunMicrotasks(isolate);
    }

    /// <summary>MicrotaskQueue::RunMicrotasks: runs until the queue is empty; returns the number run.</summary>
    public int RunMicrotasks(Isolate isolate)
    {
        _isRunningMicrotasks = true;
        _microtasksSuppressions++;
        long baseCount = _finishedMicrotaskCount;
        try
        {
            while (_size > 0)
            {
                Microtask microtask = _ringBuffer[_start]!;
                _ringBuffer[_start] = null;
                _start = (_start + 1) % _capacity;
                _size--;
                RunSingleMicrotask(isolate, microtask);
                _finishedMicrotaskCount++;
            }
        }
        catch (TerminationException)
        {
            _ringBuffer = [];
            _capacity = 0;
            _size = 0;
            _start = 0;
            throw;
        }
        finally
        {
            _microtasksSuppressions--;
            _isRunningMicrotasks = false;
            MicrotasksCompleted?.Invoke(isolate);
        }
        return (int)(_finishedMicrotaskCount - baseCount);
    }

    /// <summary>RunSingleMicrotask (builtins-microtask-queue-gen.cc).</summary>
    void RunSingleMicrotask(Isolate isolate, Microtask microtask)
    {
        switch (microtask)
        {
            case CallableTask callable:
            {
                using var _ = isolate.EnterContext(callable.Context);
                try
                {
                    Execution.Call(isolate, callable.Callable, JSValue.Undefined, []);
                }
                catch (JavaScriptException e)
                {
                    ReportException(isolate, e);
                }
                break;
            }
            case CallbackTask callback:
                callback.Callback(isolate, callback.Data);
                break;
            case PromiseReactionJobTask reaction:
            {
                using var _ = isolate.EnterContext(reaction.Context);
                IPromiseJobs jobs = PromiseJobs ?? throw new InvalidOperationException("V8Sharp: promise jobs are not registered");
                try
                {
                    jobs.RunPromiseReactionJob(isolate, reaction);
                }
                catch (JavaScriptException e)
                {
                    ReportException(isolate, e);
                }
                break;
            }
            case PromiseResolveThenableJobTask thenable:
            {
                using var _ = isolate.EnterContext(thenable.Context);
                IPromiseJobs jobs = PromiseJobs ?? throw new InvalidOperationException("V8Sharp: promise jobs are not registered");
                try
                {
                    jobs.RunPromiseResolveThenableJob(isolate, thenable);
                }
                catch (JavaScriptException e)
                {
                    ReportException(isolate, e);
                }
                break;
            }
        }
    }

    void ReportException(Isolate isolate, JavaScriptException e) => UncaughtException?.Invoke(isolate, e);

    /// <summary>v8::MicrotasksScope depth.</summary>
    public void IncrementMicrotasksScopeDepth() => ++_microtasksScopeDepth;
    public void DecrementMicrotasksScopeDepth() => --_microtasksScopeDepth;
    public void IncrementMicrotasksSuppressions() => ++_microtasksSuppressions;
    public void DecrementMicrotasksSuppressions() => --_microtasksSuppressions;
}
