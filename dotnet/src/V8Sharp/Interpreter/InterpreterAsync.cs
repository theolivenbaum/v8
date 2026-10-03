// Port of the async function and async generator builtins the interpreter's
// intrinsics call: src/builtins/builtins-async-gen.cc (Await,
// BranchIfNonThenable, AllocateAwaitContext, EnqueueAsyncResumeTask),
// builtins-async-function-gen.cc (AsyncFunctionEnter, AsyncFunctionAwait,
// AsyncFunctionResolve, AsyncFunctionReject and the await closures) and
// builtins-async-generator-gen.cc (AsyncGeneratorAwait, AsyncGeneratorResumeNext,
// AsyncGeneratorResolve, AsyncGeneratorReject, AsyncGeneratorYieldWithAwait,
// AsyncGeneratorReturn, the enqueue of next/return/throw and the closures).
//
// The promise machinery is the promise builtins' (PromiseBuiltins); the
// generator resumption is the interpreter's ResumeGeneratorTrampoline.
// Deviation: V8Sharp has no debugger, so the debugger half of
// Runtime_DebugAsyncFunctionSuspended is not ported.
using V8Sharp.Builtins;

namespace V8Sharp.Interpreter;

/// <summary>V8's AsyncGeneratorRequest: one queued next/return/throw of an async generator.</summary>
public sealed class AsyncGeneratorRequest(JSGeneratorObject.ResumeMode resumeMode, JSValue value, JSPromise promise)
    : HeapObject(InstanceType.AsyncGeneratorRequestType)
{
    /// <summary>The next request in the queue, or undefined.</summary>
    public JSValue Next = JSValue.Undefined;
    public readonly JSGeneratorObject.ResumeMode ResumeMode = resumeMode;
    public readonly JSValue Value = value;
    public readonly JSPromise Promise = promise;
}

public static partial class InterpreterAsync
{
    /// <summary>Registers the builtins the promise machinery resumes generators through.</summary>
    internal static void InstallHooks()
    {
        PromiseBuiltins.ResumeGeneratorTrampoline = InterpreterGenerators.ResumeGeneratorTrampoline;
        PromiseBuiltins.AsyncGeneratorResumeNext = AsyncGeneratorResumeNext;
        PromiseBuiltins.AsyncGeneratorResolve = AsyncGeneratorResolve;
    }

    /// <summary>%_CreateAsyncFromSyncIterator.</summary>
    public static JSValue CreateAsyncFromSyncIterator(Isolate isolate, JSValue syncIterator) =>
        AsyncFromSyncIteratorBuiltins.CreateAsyncFromSyncIterator(isolate, syncIterator);

    // ---- Await (builtins-async-gen.cc) --------------------------------------------------------

    /// <summary>AwaitBehavior: kResumeOnly awaits may use the generator itself as the handler.</summary>
    enum AwaitBehavior { kNormal, kResumeOnly }

    /// <summary>IsIsolatePromiseHookEnabledOrDebugIsActiveOrHasAsyncEventDelegate (no debugger in V8Sharp).</summary>
    static bool HasInstrumentation(Isolate isolate) => isolate.PromiseHook is not null;

    /// <summary>AsyncBuiltinsAssembler::AllocateAwaitContext: a context whose extension is the generator.</summary>
    static Context AllocateAwaitContext(Isolate isolate, NativeContext nativeContext, JSGeneratorObject generator)
    {
        Context awaitContext = isolate.Factory.NewBuiltinContext(nativeContext,
            (int)Context.Field.MIN_CONTEXT_EXTENDED_SLOTS);
        awaitContext.Extension = generator;
        return awaitContext;
    }

    /// <summary>The generator an await closure resumes (its context's extension).</summary>
    internal static T GeneratorOfAwaitClosure<T>(in BuiltinArguments args) where T : JSGeneratorObject =>
        args.Target.Context.Extension.As<T>();

    /// <summary>AsyncBuiltinsAssembler::BranchIfNonThenable: true when <paramref name="value"/> is known not to be thenable.</summary>
    static bool IsNonThenable(Isolate isolate, NativeContext nativeContext, JSValue value)
    {
        // Fast-check that no promise hooks or debug machinery is active.
        if (HasInstrumentation(isolate) || isolate.HasContextPromiseHooks) return false;
        // Non-JSReceiver values (Smis, strings, etc.) are guaranteed non-thenable
        // per ECMA-262 CreateResolvingFunctions step 2.d.
        if (value.HeapObjectOrNull is not JSReceiver receiver) return true;
        // JSReceiver: "then" is an interesting property, so the map's
        // may_have_interesting_properties bit tells us whether the object could
        // have a "then" own property.
        Map map = receiver.Map;
        if (map.MayHaveInterestingProperties) return false;
        if (!ReferenceEquals(map.Prototype, nativeContext.InitialObjectPrototype)) return false;
        return Protectors.IsPromiseThenLookupChainIntact(isolate);
    }

    /// <summary>
    /// AsyncBuiltinsAssembler::Await: awaits <paramref name="value"/> for
    /// <paramref name="generator"/>. The closures come from
    /// <paramref name="resolveBuiltin"/> / <paramref name="rejectBuiltin"/> (or the
    /// reusable ones of an async function object when they are Builtin.NoBuiltinId).
    /// </summary>
    static JSValue Await(Isolate isolate, JSGeneratorObject generator, JSValue value, JSPromise outerPromise,
        Builtin resolveBuiltin, Builtin rejectBuiltin, AwaitBehavior awaitBehavior)
    {
        NativeContext nativeContext = isolate.NativeContext;
        bool hasInstrumentation = HasInstrumentation(isolate);
        bool bypassClosures = awaitBehavior == AwaitBehavior.kResumeOnly && !hasInstrumentation;

        // Fast path for non-thenable values: skip creating a wrapper promise and
        // directly enqueue a microtask that will call the resolve handler.
        if (IsNonThenable(isolate, nativeContext, value))
        {
            HeapObject onResolveFast = bypassClosures
                ? generator
                : GetClosures(isolate, nativeContext, generator, resolveBuiltin, rejectBuiltin).OnResolve;
            PromiseBuiltins.AsyncAwaitNonThenableFastPath(isolate, value, onResolveFast, JSValue.Undefined);
            return JSValue.Undefined;
        }

        // We do the `PromiseResolve(%Promise%,value)` avoiding to unnecessarily
        // create wrapper promises. Now if {value} is already a promise with the
        // intrinsics %Promise% constructor as its "constructor", we don't need
        // to allocate the wrapper promise.
        JSPromise promise;
        if (value.HeapObjectOrNull is JSPromise valuePromise &&
            ((ReferenceEquals(valuePromise.Map.Prototype, nativeContext.PromisePrototype) &&
              Protectors.IsPromiseSpeciesLookupChainIntact(isolate)) ||
             ReferenceEquals(ObjectOps.GetProperty(isolate, valuePromise, ReadOnlyRoots.constructor_string).HeapObjectOrNull,
                 nativeContext.PromiseFunction)))
        {
            promise = valuePromise;
        }
        else
        {
            // We need to mark the {value} wrapper as having {outer_promise}
            // as its parent, which is why we need to inline a good chunk of
            // logic from the `PromiseResolve` builtin here.
            promise = PromiseBuiltins.NewJSPromise(isolate, outerPromise);
            PromiseBuiltins.ResolvePromise(isolate, promise, value);
        }

        // Get or allocate resolve and reject handlers conditionally.
        HeapObject onResolve = generator;
        HeapObject onReject = generator;
        if (!bypassClosures)
        {
            (onResolve, onReject) = GetClosures(isolate, nativeContext, generator, resolveBuiltin, rejectBuiltin);
        }

        // Deal with PromiseHooks in the runtime. This also allocates the
        // throwaway promise, which is only needed in case of PromiseHooks.
        JSValue throwaway = JSValue.Undefined;
        if (hasInstrumentation)
        {
            // Runtime_DebugAsyncFunctionSuspended: the throwaway promise fires the
            // init hook with {promise} as its parent and is never reported as
            // unhandled.
            JSPromise throwawayPromise = PromiseBuiltins.NewJSPromise(isolate, promise);
            throwawayPromise.HasHandler = true;
            throwaway = throwawayPromise;
        }
        else if (isolate.HasContextPromiseHooks)
        {
            // This call to NewJSPromise is to keep behaviour parity with what
            // happens in Runtime::kDebugAsyncFunctionSuspended if native hooks are set.
            throwaway = PromiseBuiltins.NewJSPromise(isolate, promise);
        }

        // Fast-path: if outer promise hooks are disabled, and value is a fulfilled
        // JSPromise, we can skip PerformPromiseThen and directly enqueue a Fulfill
        // reaction job.
        if (throwaway.IsUndefined && promise.Status == PromiseState.kFulfilled)
        {
            // Mark the promise as handled.
            promise.HasHandler = true;
            PromiseBuiltins.AsyncAwaitNonThenableFastPath(isolate, promise.Result, onResolve, JSValue.Undefined);
            return JSValue.Undefined;
        }
        return PromiseBuiltins.PerformPromiseThen(isolate, promise, onResolve, onReject, throwaway);
    }

    /// <summary>The closures of an await: the reusable ones of an async function, or fresh ones in an AwaitContext.</summary>
    static (HeapObject OnResolve, HeapObject OnReject) GetClosures(Isolate isolate, NativeContext nativeContext,
        JSGeneratorObject generator, Builtin resolveBuiltin, Builtin rejectBuiltin)
    {
        if (resolveBuiltin == Builtin.NoBuiltinId)
        {
            // AwaitWithReusableClosures: lazily allocate the closures on the first
            // await, then reuse them for subsequent awaits.
            var asyncFunctionObject = (JSAsyncFunctionObject)generator;
            if (asyncFunctionObject.AwaitResolveClosure.HeapObjectOrNull is JSFunction resolve)
            {
                return (resolve, asyncFunctionObject.AwaitRejectClosure.As<JSFunction>());
            }
            Context awaitContext = AllocateAwaitContext(isolate, nativeContext, generator);
            JSFunction resolveClosure = RootSharedFunctions.AllocateRootFunctionWithContext(isolate,
                Builtin.AsyncFunctionAwaitResolveClosure, awaitContext, nativeContext);
            JSFunction rejectClosure = RootSharedFunctions.AllocateRootFunctionWithContext(isolate,
                Builtin.AsyncFunctionAwaitRejectClosure, awaitContext, nativeContext);
            asyncFunctionObject.AwaitResolveClosure = resolveClosure;
            asyncFunctionObject.AwaitRejectClosure = rejectClosure;
            return (resolveClosure, rejectClosure);
        }
        Context context = AllocateAwaitContext(isolate, nativeContext, generator);
        return (RootSharedFunctions.AllocateRootFunctionWithContext(isolate, resolveBuiltin, context, nativeContext),
            RootSharedFunctions.AllocateRootFunctionWithContext(isolate, rejectBuiltin, context, nativeContext));
    }

    /// <summary>AsyncBuiltinsAssembler::EnqueueAsyncResumeTask.</summary>
    static void EnqueueAsyncResumeTask(NativeContext nativeContext, JSGeneratorObject generator, JSValue value,
        AsyncResumeTask.Kind kind) =>
        PromiseBuiltins.EnqueueMicrotask(nativeContext, new AsyncResumeTask(generator, value, kind));

    // ---- Async functions (builtins-async-function-gen.cc) --------------------------------------

    /// <summary>AsyncFunctionEnter: allocates the async function object and its promise.</summary>
    public static JSValue AsyncFunctionEnter(Isolate isolate, JSFunction closure, JSValue receiver)
    {
        // Compute the number of registers and parameters.
        var bytecode = (BytecodeArray)closure.Shared.FunctionData!;
        int parametersAndRegisterLength = bytecode.RegisterCount + closure.Shared.InternalFormalParameterCountWithoutReceiver;

        // Allocate and initialize the register file (V8 fills it with undefined).
        var parametersAndRegisters = new FixedArray(parametersAndRegisterLength);

        // Allocate and initialize the promise.
        JSPromise promise = PromiseBuiltins.NewJSPromise(isolate);

        // Allocate and initialize the async function object.
        NativeContext nativeContext = isolate.NativeContext;
        var asyncFunctionObject = (JSAsyncFunctionObject)isolate.Factory.NewJSObjectFromMap(nativeContext.AsyncFunctionObjectMap);
        asyncFunctionObject.Function = closure;
        asyncFunctionObject.Context = isolate.Context!;
        asyncFunctionObject.Receiver = receiver;
        asyncFunctionObject.InputOrDebugPos = JSValue.Zero;
        asyncFunctionObject.Mode = JSGeneratorObject.ResumeMode.kNext;
        asyncFunctionObject.ContinuationValue = JSGeneratorObject.kGeneratorExecuting;
        asyncFunctionObject.ParametersAndRegisters = parametersAndRegisters;
        asyncFunctionObject.Promise = promise;
        // The closures are lazily allocated on the first await.
        asyncFunctionObject.AwaitResolveClosure = JSValue.Undefined;
        asyncFunctionObject.AwaitRejectClosure = JSValue.Undefined;
        return asyncFunctionObject;
    }

    /// <summary>
    /// AsyncFunctionAwait (ES#abstract-ops-async-function-await): the parser
    /// desugars `await value` into
    /// `yield AsyncFunctionAwait{Caught,Uncaught}(.generator_object, value)`.
    /// </summary>
    public static JSValue AsyncFunctionAwait(Isolate isolate, JSAsyncFunctionObject asyncFunctionObject, JSValue value)
    {
        JSPromise outerPromise = asyncFunctionObject.Promise;
        NativeContext nativeContext = isolate.NativeContext;

        // Fast path: if value is an already-fulfilled JSPromise with intact species
        // protector and no hooks/debug active, skip closures entirely and enqueue
        // an AsyncResumeTask directly.
        if (value.HeapObjectOrNull is JSPromise jsPromise && !HasInstrumentation(isolate) && !isolate.HasContextPromiseHooks &&
            // The promise must be on the canonical initial map of this native
            // context (no own "constructor", created in this realm).
            ReferenceEquals(jsPromise.Map, nativeContext.PromiseFunction.InitialMap) &&
            jsPromise.Status == PromiseState.kFulfilled &&
            Protectors.IsPromiseSpeciesLookupChainIntact(isolate))
        {
            // Mark the promise as handled.
            jsPromise.HasHandler = true;
            EnqueueAsyncResumeTask(nativeContext, asyncFunctionObject, jsPromise.Result,
                AsyncResumeTask.Kind.kAsyncFunctionAwait);
            return outerPromise;
        }

        Await(isolate, asyncFunctionObject, value, outerPromise, Builtin.NoBuiltinId, Builtin.NoBuiltinId,
            AwaitBehavior.kNormal);

        // Return outer promise to avoid adding a load of the outer promise before
        // suspending in BytecodeGenerator.
        return outerPromise;
    }

    /// <summary>AsyncFunctionResolve.</summary>
    public static JSValue AsyncFunctionResolve(Isolate isolate, JSAsyncFunctionObject asyncFunctionObject, JSValue value)
    {
        JSPromise promise = asyncFunctionObject.Promise;
        PromiseBuiltins.ResolvePromise(isolate, promise, value);
        return promise;
    }

    /// <summary>AsyncFunctionReject.</summary>
    public static JSValue AsyncFunctionReject(Isolate isolate, JSAsyncFunctionObject asyncFunctionObject, JSValue reason)
    {
        JSPromise promise = asyncFunctionObject.Promise;
        // Reject the {promise} for the given {reason}, disabling the
        // additional debug event for the rejection since a debug event
        // already happend for the exception that got us here.
        PromiseBuiltins.RejectPromise(isolate, promise, reason, false);
        return promise;
    }

    /// <summary>AsyncFunctionAwaitResumeClosure: resumes the async function in <paramref name="resumeMode"/>.</summary>
    internal static JSValue AsyncFunctionAwaitResumeClosure(Isolate isolate, JSAsyncFunctionObject asyncFunctionObject,
        JSValue sentValue, JSGeneratorObject.ResumeMode resumeMode)
    {
        // Inline version of GeneratorPrototypeNext / GeneratorPrototypeReturn with
        // unnecessary runtime checks removed.
        asyncFunctionObject.Mode = resumeMode;
        InterpreterGenerators.ResumeGeneratorTrampoline(isolate, sentValue, asyncFunctionObject);
        // The resulting Promise is a throwaway, so it doesn't matter what it
        // resolves to.
        return JSValue.Undefined;
    }

    // ---- Async generators (builtins-async-generator-gen.cc) ------------------------------------

    static JSAsyncGeneratorRequestQueue Queue(JSAsyncGeneratorObject generator) => new(generator);

    /// <summary>The request queue of an async generator (a linked list of AsyncGeneratorRequests).</summary>
    readonly struct JSAsyncGeneratorRequestQueue(JSAsyncGeneratorObject generator)
    {
        /// <summary>LoadFirstAsyncGeneratorRequestFromQueue (null when empty).</summary>
        public AsyncGeneratorRequest? First => generator.Queue.HeapObjectOrNull as AsyncGeneratorRequest;

        /// <summary>AddAsyncGeneratorRequestToQueue.</summary>
        public void Add(AsyncGeneratorRequest request)
        {
            if (generator.Queue.HeapObjectOrNull is not AsyncGeneratorRequest current)
            {
                generator.Queue = request;
                return;
            }
            while (current.Next.HeapObjectOrNull is AsyncGeneratorRequest next) current = next;
            current.Next = request;
        }

        /// <summary>TakeFirstAsyncGeneratorRequestFromQueue.</summary>
        public AsyncGeneratorRequest TakeFirst()
        {
            var request = generator.Queue.As<AsyncGeneratorRequest>();
            generator.Queue = request.Next;
            return request;
        }
    }

    /// <summary>
    /// AsyncGeneratorEnqueue: the shared implementation of next/return/throw.
    /// Produces a new promise and appends a request; if the generator is not
    /// executing, AsyncGeneratorResumeNext processes the queue.
    /// </summary>
    public static JSValue AsyncGeneratorEnqueue(Isolate isolate, JSValue receiver, JSValue value,
        JSGeneratorObject.ResumeMode resumeMode, string methodName)
    {
        JSPromise promise = PromiseBuiltins.NewJSPromise(isolate);
        if (receiver.HeapObjectOrNull is not JSAsyncGeneratorObject generator)
        {
            JSObject error = isolate.Factory.NewTypeError(MessageTemplate.IncompatibleMethodReceiver,
                isolate.Factory.NewStringFromAsciiChecked(methodName), receiver);
            PromiseBuiltins.RejectPromise(isolate, promise, error, true);
            return promise;
        }
        Queue(generator).Add(new AsyncGeneratorRequest(resumeMode, value, promise));

        // Let state be generator.[[AsyncGeneratorState]]
        // If state is not "executing", then
        //     Perform AsyncGeneratorResumeNext(Generator)
        if (generator.ContinuationValue != JSGeneratorObject.kGeneratorExecuting)
        {
            AsyncGeneratorResumeNext(isolate, generator);
        }
        return promise;
    }

    /// <summary>
    /// AsyncGeneratorResumeNext: loops while there is a request in the queue
    /// and the generator is not suspended on an await.
    /// </summary>
    public static JSValue AsyncGeneratorResumeNext(Isolate isolate, JSAsyncGeneratorObject generator)
    {
        int state = generator.ContinuationValue;
        AsyncGeneratorRequest? next = Queue(generator).First;
        while (true)
        {
            // Stop resuming if suspended for Await.
            if (generator.IsAwaiting) return JSValue.Undefined;
            // Stop resuming if request queue is empty.
            if (next is null) return JSValue.Undefined;

            JSGeneratorObject.ResumeMode resumeType = next.ResumeMode;
            if (resumeType != JSGeneratorObject.ResumeMode.kNext)
            {
                // Abrupt completion.
                if (state == 0)
                {
                    // Suspended at start: close the generator.
                    generator.ContinuationValue = JSGeneratorObject.kGeneratorClosed;
                    state = JSGeneratorObject.kGeneratorClosed;
                }
                JSValue nextValue = next.Value;
                if (resumeType == JSGeneratorObject.ResumeMode.kReturn)
                {
                    // For "return" completions, await the sent value.
                    return AsyncGeneratorReturn(isolate, generator, nextValue);
                }
                // if_throw:
                if (state == JSGeneratorObject.kGeneratorClosed)
                {
                    AsyncGeneratorReject(isolate, generator, nextValue);
                    next = Queue(generator).First;
                    continue;
                }
            }
            else if (state == JSGeneratorObject.kGeneratorClosed)
            {
                AsyncGeneratorResolve(isolate, generator, JSValue.Undefined, true);
                state = generator.ContinuationValue;
                next = Queue(generator).First;
                continue;
            }

            // resume_generator: remember the {resume_type} for the {generator}.
            generator.Mode = resumeType;
            InterpreterGenerators.ResumeGeneratorTrampoline(isolate, next.Value, generator);
            state = generator.ContinuationValue;
            next = Queue(generator).First;
        }
    }

    /// <summary>AsyncGeneratorResolve: resolves the first request's promise with {value, done}.</summary>
    public static JSValue AsyncGeneratorResolve(Isolate isolate, JSAsyncGeneratorObject generator, JSValue value, bool done)
    {
        AsyncGeneratorRequest next = Queue(generator).TakeFirst();
        JSPromise promise = next.Promise;

        // Let iteratorResult be CreateIterResultObject(value, done).
        JSObject iterResult = InterpreterRuntime.NewJSIteratorResult(isolate, value, done);

        // We know that {iter_result} itself doesn't have any "then" property and
        // its [[Prototype]] is %ObjectPrototype%, so when the Promise#then()
        // protector holds and no hooks run, fulfill the promise directly.
        if (!HasInstrumentation(isolate) && !isolate.HasContextPromiseHooks && Protectors.IsPromiseThenLookupChainIntact(isolate))
        {
            PromiseBuiltins.FulfillPromise(isolate, promise, iterResult);
        }
        else
        {
            PromiseBuiltins.ResolvePromise(isolate, promise, iterResult);
        }
        // Per spec, AsyncGeneratorResolve() returns undefined. However, for the
        // benefit of %TraceExit(), return the Promise.
        return promise;
    }

    /// <summary>AsyncGeneratorReject.</summary>
    public static JSValue AsyncGeneratorReject(Isolate isolate, JSAsyncGeneratorObject generator, JSValue value)
    {
        AsyncGeneratorRequest next = Queue(generator).TakeFirst();
        // No debug event needed, there was already a debug event that got us here.
        return PromiseBuiltins.RejectPromise(isolate, next.Promise, value, false);
    }

    /// <summary>AsyncGeneratorAwait.</summary>
    public static JSValue AsyncGeneratorAwait(Isolate isolate, JSAsyncGeneratorObject generator, JSValue value)
    {
        JSPromise outerPromise = Queue(generator).First!.Promise;
        Await(isolate, generator, value, outerPromise, Builtin.AsyncGeneratorAwaitResolveClosure,
            Builtin.AsyncGeneratorAwaitRejectClosure, AwaitBehavior.kResumeOnly);
        generator.IsAwaiting = true;
        return JSValue.Undefined;
    }

    /// <summary>AsyncGeneratorYieldWithAwait.</summary>
    public static JSValue AsyncGeneratorYieldWithAwait(Isolate isolate, JSAsyncGeneratorObject generator, JSValue value)
    {
        JSPromise outerPromise = Queue(generator).First!.Promise;
        NativeContext nativeContext = isolate.NativeContext;

        // Fast path: for non-thenable values without hooks/debug active, enqueue
        // a specialized AsyncResumeTask instead of going through Await() which
        // allocates closures.
        if (IsNonThenable(isolate, nativeContext, value))
        {
            EnqueueAsyncResumeTask(nativeContext, generator, value, AsyncResumeTask.Kind.kYield);
            generator.IsAwaiting = true;
            return JSValue.Undefined;
        }

        // Thenable or hooks active: fall back to full Await with closures.
        Await(isolate, generator, value, outerPromise, Builtin.AsyncGeneratorYieldWithAwaitResolveClosure,
            Builtin.AsyncGeneratorAwaitRejectClosure, AwaitBehavior.kNormal);
        generator.IsAwaiting = true;
        return JSValue.Undefined;
    }

    /// <summary>
    /// AsyncGeneratorReturn: resumes a "return" request by awaiting its value,
    /// with closures that depend on whether the generator is closed.
    /// </summary>
    static JSValue AsyncGeneratorReturn(Isolate isolate, JSAsyncGeneratorObject generator, JSValue value)
    {
        AsyncGeneratorRequest req = Queue(generator).First!;
        int state = generator.ContinuationValue;
        bool closed = state == JSGeneratorObject.kGeneratorClosed;
        Builtin onResolve = closed ? Builtin.AsyncGeneratorReturnClosedResolveClosure : Builtin.AsyncGeneratorReturnResolveClosure;
        Builtin onReject = closed ? Builtin.AsyncGeneratorReturnClosedRejectClosure : Builtin.AsyncGeneratorAwaitRejectClosure;

        generator.IsAwaiting = true;
        try
        {
            Await(isolate, generator, value, req.Promise, onResolve, onReject, AwaitBehavior.kNormal);
        }
        catch (JavaScriptException e)
        {
            if (!closed) return AsyncGeneratorAwaitResume(isolate, generator, e.Value, JSGeneratorObject.ResumeMode.kThrow);
            return AsyncGeneratorReturnClosedReject(isolate, generator, e.Value);
        }
        return JSValue.Undefined;
    }

    /// <summary>AsyncGeneratorAwaitResume: resumes after an await and continues the queue.</summary>
    internal static JSValue AsyncGeneratorAwaitResume(Isolate isolate, JSAsyncGeneratorObject generator, JSValue value,
        JSGeneratorObject.ResumeMode resumeMode)
    {
        generator.IsAwaiting = false;
        // Remember the {resume_mode} for the {async_generator_object}.
        generator.Mode = resumeMode;
        InterpreterGenerators.ResumeGeneratorTrampoline(isolate, value, generator);
        return AsyncGeneratorResumeNext(isolate, generator);
    }

    /// <summary>AsyncGeneratorReturnClosedReject.</summary>
    internal static JSValue AsyncGeneratorReturnClosedReject(Isolate isolate, JSAsyncGeneratorObject generator, JSValue value)
    {
        generator.IsAwaiting = false;
        // Return ! AsyncGeneratorReject(_F_.[[Generator]], _reason_).
        AsyncGeneratorReject(isolate, generator, value);
        return AsyncGeneratorResumeNext(isolate, generator);
    }
}
