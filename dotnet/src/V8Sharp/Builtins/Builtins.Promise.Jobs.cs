// Port of src/builtins/promise-reaction-job.tq (PromiseFulfillReactionJob,
// PromiseRejectReactionJob and their inline fast paths), promise-jobs.tq
// (PromiseResolveThenableJob), AsyncAwaitNonThenableFastPath
// (promise-misc.tq) and GlobalQueueMicrotask (builtins-microtask-queue-gen.cc).
namespace V8Sharp.Builtins;

public static partial class PromiseBuiltins
{
    // ---- Generator hooks ---------------------------------------------------------------------
    //
    // A reaction whose handler is a JSGeneratorObject (await in an async
    // function or async generator) resumes the generator. V8 calls the
    // ResumeGeneratorTrampoline and AsyncGeneratorResumeNext builtins; they
    // belong to the interpreter, which registers them here.

    /// <summary>
    /// The ResumeGeneratorTrampoline builtin: resumes <c>generator</c> with
    /// <c>value</c> in its <see cref="JSGeneratorObject.Mode"/>. Set by the interpreter.
    /// </summary>
    public static Func<Isolate, JSValue, JSGeneratorObject, JSValue>? ResumeGeneratorTrampoline;

    /// <summary>The AsyncGeneratorResumeNext builtin. Set by the interpreter.</summary>
    public static Func<Isolate, JSAsyncGeneratorObject, JSValue>? AsyncGeneratorResumeNext;

    /// <summary>The AsyncGeneratorResolve builtin (generator, value, done). Set by the interpreter.</summary>
    public static Func<Isolate, JSAsyncGeneratorObject, JSValue, bool, JSValue>? AsyncGeneratorResolve;

    internal static JSValue CallResumeGeneratorTrampoline(Isolate isolate, JSValue value, JSGeneratorObject generator) =>
        (ResumeGeneratorTrampoline ?? throw new NotImplementedException("V8Sharp: ResumeGeneratorTrampoline is not registered"))
        (isolate, value, generator);

    internal static JSValue CallAsyncGeneratorResumeNext(Isolate isolate, JSAsyncGeneratorObject generator) =>
        (AsyncGeneratorResumeNext ?? throw new NotImplementedException("V8Sharp: AsyncGeneratorResumeNext is not registered"))
        (isolate, generator);

    internal static JSValue CallAsyncGeneratorResolve(Isolate isolate, JSAsyncGeneratorObject generator, JSValue value, bool done) =>
        (AsyncGeneratorResolve ?? throw new NotImplementedException("V8Sharp: AsyncGeneratorResolve is not registered"))
        (isolate, generator, value, done);

    /// <summary>ResumeGenerator (promise-reaction-job.tq).</summary>
    static void ResumeGenerator(Isolate isolate, JSGeneratorObject generator, JSValue argument, PromiseReaction.Type reactionType)
    {
        generator.Mode = reactionType == PromiseReaction.Type.kFulfill
            ? JSGeneratorObject.ResumeMode.kNext
            : JSGeneratorObject.ResumeMode.kThrow;
        if (generator is JSAsyncGeneratorObject asyncGen) asyncGen.IsAwaiting = false;
        CallResumeGeneratorTrampoline(isolate, argument, generator);
        if (generator is JSAsyncGeneratorObject asyncGen2) CallAsyncGeneratorResumeNext(isolate, asyncGen2);
    }

    // ---- PromiseReactionJob (promise-reaction-job.tq) ----------------------------------------

    /// <summary>RejectPromiseReactionJob.</summary>
    static JSValue RejectPromiseReactionJob(Isolate isolate, JSValue promiseOrCapability, JSValue reason,
        PromiseReaction.Type reactionType)
    {
        if (reactionType == PromiseReaction.Type.kReject)
        {
            switch (promiseOrCapability.HeapObjectOrNull)
            {
                case JSPromise promise:
                    // For fast native promises we can skip the indirection via the
                    // promiseCapability.[[Reject]] function and run the resolve logic
                    // directly from here.
                    return RejectPromise(isolate, promise, reason, false);
                case null:
                    return JSValue.Undefined;
                default:
                {
                    // In the general case we need to call the (user provided)
                    // promiseCapability.[[Reject]] function.
                    var capability = (PromiseCapability)promiseOrCapability.Object;
                    return Execution.Call(isolate, capability.Reject, JSValue.Undefined, [reason]);
                }
            }
        }
        else
        {
            // We have to call out to the dedicated PromiseRejectReactionJob
            // builtin here, instead of just doing the work inline, as otherwise
            // the catch predictions in the debugger will be wrong, which just
            // walks the stack and checks for certain builtins.
            return PromiseRejectReactionJob(isolate, reason, JSValue.Undefined, promiseOrCapability);
        }
    }

    /// <summary>FuflfillPromiseReactionJob (sic).</summary>
    static JSValue FulfillPromiseReactionJob(Isolate isolate, JSValue promiseOrCapability, JSValue result,
        PromiseReaction.Type reactionType)
    {
        switch (promiseOrCapability.HeapObjectOrNull)
        {
            case JSPromise promise:
                // For fast native promises we can skip the indirection via the
                // promiseCapability.[[Resolve]] function and run the resolve logic
                // directly from here.
                return ResolvePromise(isolate, promise, result);
            case null:
                return JSValue.Undefined;
            default:
            {
                // In the general case we need to call the (user provided)
                // promiseCapability.[[Resolve]] function.
                var capability = (PromiseCapability)promiseOrCapability.Object;
                try
                {
                    return Execution.Call(isolate, capability.Resolve, JSValue.Undefined, [result]);
                }
                catch (JavaScriptException e)
                {
                    return RejectPromiseReactionJob(isolate, promiseOrCapability, e.Value, reactionType);
                }
            }
        }
    }

    /// <summary>PromiseReactionJob (ES #sec-promisereactionjob).</summary>
    static JSValue PromiseReactionJob(Isolate isolate, JSValue argument, JSValue handler, JSValue promiseOrCapability,
        PromiseReaction.Type reactionType)
    {
        switch (handler.HeapObjectOrNull)
        {
            case JSGeneratorObject generator:
                ResumeGenerator(isolate, generator, argument, reactionType);
                return JSValue.Undefined;
            case null:
                if (reactionType == PromiseReaction.Type.kFulfill)
                {
                    return FulfillPromiseReactionJob(isolate, promiseOrCapability, argument, reactionType);
                }
                return RejectPromiseReactionJob(isolate, promiseOrCapability, argument, reactionType);
            default:
            {
                JSValue result;
                try
                {
                    result = Execution.Call(isolate, handler, JSValue.Undefined, [argument]);
                }
                catch (JavaScriptException e)
                {
                    return RejectPromiseReactionJob(isolate, promiseOrCapability, e.Value, reactionType);
                }
                if (promiseOrCapability.IsUndefined)
                {
                    // There's no [[Capability]] for this promise reaction job, which
                    // means that this is a specification-internal operation (aka
                    // await) where the result does not matter (see the specification
                    // change in https://github.com/tc39/ecma262/pull/1146 for
                    // details).
                    return JSValue.Undefined;
                }
                // V8's FuflfillPromiseReactionJob runs inside the handler's try
                // block, so an exception from a capability's [[Resolve]] is turned
                // into a rejection there too; FulfillPromiseReactionJob already
                // catches those, and ResolvePromise does not throw.
                try
                {
                    return FulfillPromiseReactionJob(isolate, promiseOrCapability, result, reactionType);
                }
                catch (JavaScriptException e)
                {
                    return RejectPromiseReactionJob(isolate, promiseOrCapability, e.Value, reactionType);
                }
            }
        }
    }

    /// <summary>The PromiseFulfillReactionJob builtin.</summary>
    public static JSValue PromiseFulfillReactionJob(Isolate isolate, JSValue value, JSValue handler, JSValue promiseOrCapability) =>
        PromiseReactionJob(isolate, value, handler, promiseOrCapability, PromiseReaction.Type.kFulfill);

    /// <summary>The PromiseRejectReactionJob builtin.</summary>
    public static JSValue PromiseRejectReactionJob(Isolate isolate, JSValue reason, JSValue handler, JSValue promiseOrCapability) =>
        PromiseReactionJob(isolate, reason, handler, promiseOrCapability, PromiseReaction.Type.kReject);

    // ---- PromiseResolveThenableJob (promise-jobs.tq) -----------------------------------------

    /// <summary>PromiseResolveThenableJobFast: true if the fast path applied.</summary>
    static bool PromiseResolveThenableJobFast(Isolate isolate, JSPromise promiseToResolve, JSReceiver thenable, JSValue then)
    {
        // We can use a simple optimization here if we know that {then} is the
        // initial Promise.prototype.then method, and {thenable} is a JSPromise
        // whose @@species lookup chain is intact: We can connect {thenable} and
        // {promise_to_resolve} directly in that case and avoid the allocation of a
        // temporary JSPromise and the closures plus context.
        //
        // We take the generic (slow-)path if a PromiseHook is enabled or the
        // debugger is active, to make sure we expose spec compliant behavior.
        NativeContext nativeContext = isolate.NativeContext;
        if (ReferenceEquals(then.HeapObjectOrNull, nativeContext.PromiseThen) && thenable is JSPromise thenablePromise &&
            !NeedsAnyPromiseHooks(isolate) &&
            IsPromiseSpeciesLookupChainIntact(isolate, nativeContext, thenable.Map))
        {
            // PerformPromiseThen(thenable, undefined, undefined, promise_to_resolve)
            // performs exactly the same (observable) steps as
            //   resolve, reject = CreateResolvingFunctions(promise_to_resolve)
            //   result_capability = NewPromiseCapability(%Promise%)
            //   PerformPromiseThen(thenable, resolve, reject, result_capability)
            PerformPromiseThen(isolate, thenablePromise, JSValue.Undefined, JSValue.Undefined, promiseToResolve);
            return true;
        }
        return false;
    }

    /// <summary>PromiseResolveThenableJob (ES #sec-promiseresolvethenablejob).</summary>
    public static JSValue PromiseResolveThenableJob(Isolate isolate, JSPromise promiseToResolve, JSReceiver thenable, JSValue then)
    {
        if (PromiseResolveThenableJobFast(isolate, promiseToResolve, thenable, then)) return promiseToResolve;

        NativeContext nativeContext = isolate.NativeContext;
        (JSFunction resolve, JSFunction reject, _) = CreatePromiseResolvingFunctions(isolate, promiseToResolve, false, nativeContext);
        try
        {
            return Execution.Call(isolate, then, thenable, [resolve, reject]);
        }
        catch (JavaScriptException e)
        {
            return Execution.Call(isolate, reject, JSValue.Undefined, [e.Value]);
        }
    }

    // ---- AsyncAwaitNonThenableFastPath (promise-misc.tq) -------------------------------------

    /// <summary>
    /// AsyncAwaitNonThenableFastPath: for await on a non-thenable value,
    /// directly enqueue the fulfill reaction job of <paramref name="onResolve"/>
    /// (a closure or the generator) instead of wrapping the value in a promise.
    /// </summary>
    public static void AsyncAwaitNonThenableFastPath(Isolate isolate, JSValue value, HeapObject onResolve, JSValue throwaway)
    {
        // Extract the handler context from the resolve closure/generator.
        Context handlerContext = onResolve switch
        {
            JSFunction f => f.Context,
            JSGeneratorObject g => g.Context,
            _ => throw new ArgumentException("onResolve must be a JSFunction or a JSGeneratorObject", nameof(onResolve)),
        };

        // Create a PromiseFulfillReactionJobTask directly with the non-thenable
        // value as the argument. The handler will be called with this value when
        // the microtask runs.
        var task = new PromiseReaction(PromiseReaction.Kind.FulfillReactionJobTask, handlerContext, value, onResolve, throwaway);

        // Enqueue the microtask.
        EnqueueMicrotask(handlerContext, task);
    }

    // ---- queueMicrotask (builtins-microtask-queue-gen.cc) -------------------------------------

    /// <summary>
    /// GlobalQueueMicrotask: the HTML queueMicrotask API
    /// (https://html.spec.whatwg.org/multipage/timers-and-user-prompts.html#microtask-queuing).
    /// </summary>
    public static JSValue GlobalQueueMicrotask(Isolate isolate, in BuiltinArguments args)
    {
        JSValue callback = args.AtOrUndefined(1);
        if (!ObjectOps.IsCallable(callback))
        {
            return isolate.ThrowTypeError(MessageTemplate.NotCallable, callback);
        }

        // Use the callback's context as the microtask context, per
        // https://webidl.spec.whatwg.org/#js-invoking-callback-functions
        // specifically, getting the "associated realm" of the callback.
        var receiver = (JSReceiver)callback.Object;
        NativeContext callbackContext = receiver.GetCreationContext() ?? isolate.NativeContext;

        var microtask = new CallableTask(receiver, callbackContext);
        EnqueueMicrotask(isolate.Context!, microtask);
        return JSValue.Undefined;
    }

    /// <summary>
    /// Runtime_EnqueueMicrotask: a CallableTask for <paramref name="function"/>
    /// on the queue of its native context.
    /// </summary>
    public static void EnqueueMicrotaskForFunction(Isolate isolate, JSFunction function)
    {
        NativeContext nativeContext = function.NativeContext;
        var microtask = new CallableTask(function, nativeContext);
        nativeContext.MicrotaskQueue?.EnqueueMicrotask(microtask);
    }
}
