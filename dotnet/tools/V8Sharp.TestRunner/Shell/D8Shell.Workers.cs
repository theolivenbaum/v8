// d8's Worker (src/d8/d8.cc: Shell::WorkerNew / WorkerPostMessage /
// WorkerGetMessage / WorkerOnMessage{Getter,Setter} / WorkerTerminate(AndWait),
// class Worker with ExecuteInThread / ProcessMessage(s) / PostMessageOut /
// Close, the OnMessageFromWorkerTask / CheckMessageFromWorkerTask /
// CleanUpWorkerTask tasks, PerIsolateData's worker registry, and
// Shell::WaitForRunningWorkers), plus d8.serializer. A worker is a new
// isolate of the same engine on its own thread with its own D8Shell; messages
// are the engine's structured-clone data (IJsRealm.SerializeValue).
using V8Sharp.TestRunner.Engines;

namespace V8Sharp.TestRunner.Shell;

public sealed partial class D8Shell
{
    /// <summary>The main shell of this d8 process (this, for the main shell).</summary>
    readonly D8Shell _root;

    /// <summary>The Worker this shell runs, or null for the main shell.</summary>
    readonly WorkerState? _worker;

    /// <summary>PerIsolateData::registered_workers_ (index + 1 is the JS-visible id).</summary>
    readonly List<WorkerState> _workers = [];

    /// <summary>Shell::running_workers_ and allow_new_workers_ (on the root shell).</summary>
    readonly HashSet<WorkerState> _runningWorkers = [];
    bool _allowNewWorkers = true;

    bool _taskFailed;

    enum WorkerRunState { Ready, PrepareRunning, Running, Terminating, Terminated }

    sealed class WorkerState(D8Shell parent, string script)
    {
        public D8Shell Parent { get; } = parent;
        public string Script { get; } = script;
        public readonly object Lock = new();
        public volatile WorkerRunState State = WorkerRunState.Ready;
        public D8Shell? Shell;
        public Thread? Thread;
        public bool IsJoined;
        public readonly ManualResetEventSlim Started = new(false);
        // The worker's outgoing messages (out_queue_ / out_semaphore_); null wakes GetMessage at exit.
        public readonly System.Collections.Concurrent.ConcurrentQueue<object?> OutQueue = new();
        public readonly SemaphoreSlim OutSignal = new(0);
        // PerIsolateData::worker_message_callbacks_ entry of the parent (callback and its realm).
        public object? OnMessage;
        public RealmState? OnMessageRealm;
        public bool Registered = true;

        public bool IsRunning => State == WorkerRunState.Running;
    }

    /// <summary>quit(): Shell::Quit exits the process, whichever isolate calls it.</summary>
    void Quit(int code)
    {
        _quitCode ??= code;
        _isolate?.TerminateExecution();
        TerminateAllWorkers();
    }

    void RunTimeoutTask(RealmState realm, object callback)
    {
        var c = realm.Realm.Call(callback, JsUndefined.Value);
        if (c.Kind == CompletionKind.Throw)
        {
            ReportException(realm, c.Exception!);
            _taskFailed = true;
        }
    }

    /// <summary>PerIsolateData::HasRunningSubscribedWorkers.</summary>
    bool HasRunningSubscribedWorkers()
    {
        foreach (var w in _workers)
        {
            if (w.Registered && w.OnMessage is not null) return true;
        }
        return false;
    }

    /// <summary>Terminates every running worker of the process (watchdog, quit()).</summary>
    void TerminateAllWorkers()
    {
        WorkerState[] workers;
        lock (_root._runningWorkers) workers = [.. _root._runningWorkers];
        foreach (var w in workers) TerminateWorker(w);
    }

    /// <summary>Shell::WaitForRunningWorkers: terminates and joins the running workers.</summary>
    void WaitForRunningWorkers()
    {
        if (!ReferenceEquals(_root, this)) return;
        WorkerState[] workers;
        lock (_runningWorkers)
        {
            _allowNewWorkers = false;
            workers = [.. _runningWorkers];
            _runningWorkers.Clear();
        }
        foreach (var w in workers) TerminateAndWaitForThread(w, TimeSpan.FromSeconds(10));
        lock (_runningWorkers) _allowNewWorkers = true;
    }

    // --- the parent side: Worker objects ---

    bool DispatchWorker(IJsRealm caller, string op, object?[] args, out object? result)
    {
        object? A(int i) => i + 1 < args.Length ? args[i + 1] : JsUndefined.Value;
        result = JsUndefined.Value;
        switch (op)
        {
            case "workerNew":
                result = WorkerNew((string)A(0)!);
                return true;
            case "workerPostMessage":
            {
                WorkerState? worker = WorkerAt(A(0));
                if (worker is null) return true;
                object message = SerializeOrThrow(caller, A(1), A(2));
                PostMessageToWorker(worker, message);
                return true;
            }
            case "workerGetMessage":
            {
                WorkerState? worker = WorkerAt(A(0));
                if (worker is null) return true;
                object? data = GetMessage(worker);
                if (data is not null) result = DeserializeOrThrow(caller, data);
                return true;
            }
            case "workerOnMessageGet":
            {
                WorkerState? worker = WorkerAt(A(0));
                if (worker is not null && worker.OnMessage is not null) result = worker.OnMessage;
                return true;
            }
            case "workerOnMessageSet":
            {
                WorkerState? worker = WorkerAt(A(0));
                if (worker is null) return true;
                // PerIsolateData::SubscribeWorkerOnMessage.
                if (!worker.Registered)
                {
                    lock (_stdout)
                    {
                        _stderr.Append("Trying to subscribe to message events from a terminated worker -- " +
                            "consider registering the event handler before the event loop runs.\n");
                    }
                    return true;
                }
                worker.OnMessage = A(1);
                worker.OnMessageRealm = StateOf(caller);
                return true;
            }
            case "workerTerminate":
            {
                WorkerState? worker = WorkerAt(A(0));
                if (worker is not null) TerminateWorker(worker);
                return true;
            }
            case "workerTerminateAndWait":
            {
                WorkerState? worker = WorkerAt(A(0));
                if (worker is not null) TerminateAndWaitForThread(worker, System.Threading.Timeout.InfiniteTimeSpan);
                return true;
            }
            case "postMessageOut":
            {
                // Worker::PostMessageOut: from the worker to its parent.
                WorkerState worker = _worker ?? throw new JsHostError("Error", "postMessage is only available in a Worker");
                object message = SerializeOrThrow(caller, A(0), A(1));
                worker.OutQueue.Enqueue(message);
                worker.OutSignal.Release();
                worker.Parent.PostTask(() => worker.Parent.CheckMessageFromWorker(worker));
                return true;
            }
            case "workerClose":
                if (_worker is not null) TerminateWorker(_worker);
                return true;
            case "serializerSerialize":
            {
                var values = new object?[Math.Max(0, args.Length - 1)];
                Array.Copy(args, 1, values, 0, values.Length);
                result = Unwrap(caller.SerializerSerialize(values));
                return true;
            }
            case "serializerDeserialize":
                result = Unwrap(caller.SerializerDeserialize(A(0)));
                return true;
        }
        return false;
    }

    static object? Unwrap(Completion c) => c.Kind switch
    {
        CompletionKind.Normal => c.Value,
        CompletionKind.Throw => throw new JsThrowValue(c.Exception!.Exception),
        _ => throw new JsTermination(),
    };

    static object SerializeOrThrow(IJsRealm realm, object? value, object? transfer) => Unwrap(realm.SerializeValue(value, transfer))!;

    static object? DeserializeOrThrow(IJsRealm realm, object data) => Unwrap(realm.DeserializeValue(data));

    WorkerState? WorkerAt(object? id)
    {
        // An id of 0: the Worker was not created (the process is shutting down).
        if (id is not double d || d < 1 || d > _workers.Count) return null;
        return _workers[(int)d - 1];
    }

    /// <summary>Shell::WorkerNew after ReadSource: starts the worker thread and waits until it runs.</summary>
    object WorkerNew(string script)
    {
        lock (_root._runningWorkers)
        {
            // Don't allow workers to create more workers if the main thread
            // is waiting for existing running workers to terminate.
            if (!_root._allowNewWorkers) return 0.0;
        }
        var worker = new WorkerState(this, script);
        worker.Shell = new D8Shell(this, worker);
        worker.State = WorkerRunState.PrepareRunning;
        var thread = new Thread(() => worker.Shell.ExecuteInThread(), 256 * 1024 * 1024)
        {
            IsBackground = true,
            Name = "WorkerThread",
        };
        worker.Thread = thread;
        thread.Start();
        // Wait until the worker is ready to receive messages.
        while (!worker.Started.Wait(50))
        {
            if (Stopped) throw new JsTermination();
        }
        lock (_root._runningWorkers) _root._runningWorkers.Add(worker);
        _workers.Add(worker);
        return (double)_workers.Count;
    }

    /// <summary>Worker::PostMessage.</summary>
    static void PostMessageToWorker(WorkerState worker, object data)
    {
        lock (worker.Lock)
        {
            if (!worker.IsRunning) return;
            D8Shell shell = worker.Shell!;
            shell.PostTask(() => shell.ProcessMessage(data));
        }
    }

    /// <summary>Worker::GetMessage: blocks until the worker posts a message or stops.</summary>
    object? GetMessage(WorkerState worker)
    {
        object? result;
        while (!worker.OutQueue.TryDequeue(out result))
        {
            // If the worker is no longer running, and there are no messages in the
            // queue, don't expect any more messages from it.
            if (!worker.IsRunning)
            {
                // Do try to read one more message from the queue though, in case a
                // message was posted between the previous Dequeue and the is_running()
                // check.
                worker.OutQueue.TryDequeue(out result);
                break;
            }
            worker.OutSignal.Wait(50);
            if (Stopped) throw new JsTermination();
        }
        return result;
    }

    /// <summary>Worker::Terminate.</summary>
    static void TerminateWorker(WorkerState worker)
    {
        lock (worker.Lock)
        {
            if (worker.State != WorkerRunState.Running) return;
            worker.State = WorkerRunState.Terminating;
            D8Shell shell = worker.Shell!;
            // TerminateTask.
            shell.PostTask(() =>
            {
                lock (worker.Lock)
                {
                    if (worker.State == WorkerRunState.Terminating) worker.State = WorkerRunState.Terminated;
                }
            });
            // Also schedule an interrupt in case the worker is running code and never
            // returning to the event queue.
            shell._isolate?.TerminateExecution();
        }
    }

    /// <summary>Worker::TerminateAndWaitForThread.</summary>
    static void TerminateAndWaitForThread(WorkerState worker, TimeSpan timeout)
    {
        TerminateWorker(worker);
        lock (worker.Lock)
        {
            // Prevent double-joining.
            if (worker.IsJoined) return;
            worker.IsJoined = true;
        }
        if (worker.Thread is { } thread && thread != Thread.CurrentThread) thread.Join(timeout);
    }

    /// <summary>CheckMessageFromWorkerTask: hands queued messages to the onmessage subscription.</summary>
    void CheckMessageFromWorker(WorkerState worker)
    {
        // Bail out if there's no callback -- leave the message queue untouched so
        // that we don't lose the messages and can read them with GetMessage later.
        object? callback = worker.OnMessage;
        RealmState? realm = worker.OnMessageRealm;
        if (callback is null || realm is null) return;
        while (worker.OutQueue.TryDequeue(out object? data) && data is not null)
        {
            // Each onmessage callback call is posted as a separate task.
            PostTask(() => OnMessageFromWorker(realm, callback, data));
        }
    }

    /// <summary>OnMessageFromWorkerTask: callback({data}) with the global as receiver.</summary>
    void OnMessageFromWorker(RealmState realm, object callback, object data)
    {
        if (Stopped) return;
        var value = realm.Realm.DeserializeValue(data);
        if (value.Kind == CompletionKind.Terminated) return;
        if (value.Kind == CompletionKind.Throw)
        {
            ReportException(realm, value.Exception!);
            return;
        }
        object? evt = CallHelper(realm, "makeEvent", value.Value);
        var c = realm.Realm.Call(callback, realm.Realm.GlobalObject, evt);
        if (c.Kind == CompletionKind.Throw) ReportException(realm, c.Exception!);
    }

    // --- the worker side ---

    /// <summary>Worker::ExecuteInThread.</summary>
    void ExecuteInThread()
    {
        WorkerState worker = _worker!;
        try
        {
            _isolate = _engine.CreateIsolate(this);
            lock (worker.Lock)
            {
                if (worker.State == WorkerRunState.PrepareRunning) worker.State = WorkerRunState.Running;
            }
            // The Worker is now ready to receive messages.
            worker.Started.Set();

            _realms.Add(Install(_isolate.MainRealm, isMain: true));
            bool success = true;
            if (!Stopped && worker.IsRunning)
            {
                // First run the script.
                var c = RunInCurrentRealm(worker.Script, "unnamed", isModule: false);
                if (c.Kind == CompletionKind.Throw)
                {
                    ReportException(CurrentRealm, c.Exception!);
                    success = false;
                }
            }
            if (worker.IsRunning && !FinishExecuting()) success = false;
            if (success && worker.IsRunning)
            {
                // Check that there's a message handler.
                var onmessage = CurrentRealm.Realm.GetProperty(CurrentRealm.Realm.GlobalObject, "onmessage");
                bool handlerPresent = onmessage.Kind == CompletionKind.Normal &&
                    CallHelper(CurrentRealm, "isFunction", onmessage.Value) is true;
                // Now wait for messages.
                if (handlerPresent) ProcessMessages();
            }
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            Out("V8Sharp.TestRunner: worker host error: " + e + "\n");
        }
        finally
        {
            // EnterTerminatedState.
            lock (worker.Lock) worker.State = WorkerRunState.Terminated;
            worker.Started.Set();
            try { _isolate?.Dispose(); } catch { }
            // Post null to wake the thread waiting on GetMessage() if there is one.
            worker.OutQueue.Enqueue(null);
            worker.OutSignal.Release();
            // Also post a cleanup task to the parent isolate, so that it sees that this
            // worker is terminated and can clean it up in a thread-safe way.
            D8Shell parent = worker.Parent;
            parent.PostTask(() =>
            {
                // CleanUpWorkerTask -> PerIsolateData::UnregisterWorker.
                worker.Registered = false;
                worker.OnMessage = null;
                worker.OnMessageRealm = null;
            });
            lock (_root._runningWorkers) _root._runningWorkers.Remove(worker);
        }
    }

    /// <summary>Worker::ProcessMessages: the worker's message loop until it is terminated.</summary>
    void ProcessMessages()
    {
        WorkerState worker = _worker!;
        while (worker.IsRunning && !Stopped)
        {
            if (_isolate!.PumpMessageLoop()) continue;
            if (RunOneTask()) continue;
            _taskSignal.Wait(10);
        }
    }

    /// <summary>Worker::ProcessMessage: onmessage({data}) with the global as receiver.</summary>
    void ProcessMessage(object data)
    {
        WorkerState worker = _worker!;
        if (!worker.IsRunning || Stopped) return;
        RealmState realm = _realms[0]!;
        // Get the message handler.
        var onmessage = realm.Realm.GetProperty(realm.Realm.GlobalObject, "onmessage");
        if (onmessage.Kind != CompletionKind.Normal || CallHelper(realm, "isFunction", onmessage.Value) is not true) return;
        var value = realm.Realm.DeserializeValue(data);
        if (value.Kind == CompletionKind.Terminated) return;
        if (value.Kind == CompletionKind.Throw)
        {
            ReportException(realm, value.Exception!);
            return;
        }
        object? evt = CallHelper(realm, "makeEvent", value.Value);
        var c = realm.Realm.Call(onmessage.Value!, realm.Realm.GlobalObject, evt);
        if (c.Kind == CompletionKind.Throw) ReportException(realm, c.Exception!);
    }
}
