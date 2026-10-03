// Port of src/objects/js-promise.h, src/objects/promise.h and the JSPromise
// part of src/objects/objects.cc (JSPromise::Fulfill, Reject, Resolve,
// TriggerPromiseReactions): the runtime side of the promise machinery. The
// Torque builtins (FulfillPromise, RejectPromise, ResolvePromise,
// PerformPromiseThen ...) are in Builtins/Builtins.Promise*.cs.
using V8Sharp.Builtins;
using V8Sharp.Common;
using V8Sharp.Roots;

namespace V8Sharp.Objects;

/// <summary>v8::Promise::PromiseState.</summary>
public enum PromiseState { kPending, kFulfilled, kRejected }

/// <summary>v8::PromiseRejectEvent.</summary>
public enum PromiseRejectEvent
{
    kPromiseRejectWithNoHandler = 0,
    kPromiseHandlerAddedAfterReject = 1,
    kPromiseRejectAfterResolved = 2,
    kPromiseResolveAfterResolved = 3,
}

/// <summary>v8::PromiseHookType.</summary>
public enum PromiseHookType { kInit, kResolve, kBefore, kAfter }

/// <summary>
/// V8's PromiseReaction, which is also its PromiseReactionJobTask: V8 morphs
/// a reaction into a PromiseFulfillReactionJobTask or PromiseRejectReactionJobTask
/// in place by changing its map (MorphAndEnqueuePromiseReaction). V8Sharp does
/// the same by changing <see cref="State"/>, so triggering reactions allocates
/// nothing. Reactions are linked in reverse order through <see cref="Next"/>.
/// </summary>
public sealed class PromiseReaction : Microtask
{
    /// <summary>PromiseReaction::Type.</summary>
    public enum Type { kFulfill, kReject }

    /// <summary>What the object currently is (V8: its map).</summary>
    public enum Kind : byte
    {
        /// <summary>A pending reaction on a promise (promise_reaction_map).</summary>
        Reaction,
        /// <summary>promise_fulfill_reaction_job_task_map.</summary>
        FulfillReactionJobTask,
        /// <summary>promise_reject_reaction_job_task_map.</summary>
        RejectReactionJobTask,
    }

    public Kind State;

    /// <summary>The next reaction, or 0 (V8's Smi zero) at the end.</summary>
    public JSValue Next;
    public JSValue RejectHandler;
    public JSValue FulfillHandler;
    /// <summary>A JSPromise, a PromiseCapability or undefined (await).</summary>
    public JSValue PromiseOrCapability;

    /// <summary>PromiseReactionJobTask::argument.</summary>
    public JSValue Argument;
    /// <summary>PromiseReactionJobTask::context (the handler's native context).</summary>
    public Context? Context;

    /// <summary>A pending reaction (NewPromiseReaction).</summary>
    public PromiseReaction(JSValue next, JSValue promiseOrCapability, JSValue fulfillHandler, JSValue rejectHandler)
        : base(InstanceType.PromiseReactionType)
    {
        State = Kind.Reaction;
        Next = next;
        RejectHandler = rejectHandler;
        FulfillHandler = fulfillHandler;
        PromiseOrCapability = promiseOrCapability;
    }

    /// <summary>A job task (NewPromiseFulfillReactionJobTask / NewPromiseRejectReactionJobTask).</summary>
    public PromiseReaction(Kind jobKind, Context handlerContext, JSValue argument, JSValue handler, JSValue promiseOrCapability)
        : base(InstanceType.PromiseReactionType)
    {
        State = jobKind;
        Context = handlerContext;
        Argument = argument;
        FulfillHandler = handler;
        PromiseOrCapability = promiseOrCapability;
        Next = JSValue.Zero;
    }

    /// <summary>
    /// PromiseReactionJobTask::handler. V8's reject job task stores the
    /// reject handler at the fulfill handler's offset; so does V8Sharp.
    /// </summary>
    public JSValue Handler => FulfillHandler;
}

/// <summary>V8's PromiseCapability.</summary>
public sealed class PromiseCapability(JSValue promise, JSValue resolve, JSValue reject)
    : HeapObject(InstanceType.PromiseCapabilityType)
{
    public JSValue Promise = promise;
    public JSValue Resolve = resolve;
    public JSValue Reject = reject;
}

/// <summary>V8's JSPromise.</summary>
public sealed class JSPromise(Map map) : JSObject(map)
{
    /// <summary>JSPromise::kInvalidAsyncTaskId.</summary>
    public const uint kInvalidAsyncTaskId = 0;

    /// <summary>The reactions (a PromiseReaction list, 0 when empty) while pending, the result once settled.</summary>
    public JSValue ReactionsOrResult = JSValue.Zero;
    public PromiseState Status = PromiseState.kPending;
    /// <summary>[has_handler]: whether this promise has a reject handler.</summary>
    public bool HasHandler;
    /// <summary>[is_silent]: whether the debugger should not pause when it is rejected.</summary>
    public bool IsSilent;
    /// <summary>[is_native_resolver_invoked] (v8::Promise::Resolver).</summary>
    public bool IsNativeResolverInvoked;
    public uint AsyncTaskId;

    /// <summary>JSPromise::result (only valid once settled).</summary>
    public JSValue Result
    {
        get
        {
            Debug.Assert(Status != PromiseState.kPending);
            return ReactionsOrResult;
        }
    }

    /// <summary>JSPromise::reactions (only valid while pending).</summary>
    public JSValue Reactions
    {
        get
        {
            Debug.Assert(Status == PromiseState.kPending);
            return ReactionsOrResult;
        }
    }

    /// <summary>JSPromise::Status.</summary>
    public static string StatusName(PromiseState status) => status switch
    {
        PromiseState.kFulfilled => "fulfilled",
        PromiseState.kPending => "pending",
        _ => "rejected",
    };

    /// <summary>JSPromise::Fulfill (ES #sec-fulfillpromise).</summary>
    public static JSValue Fulfill(Isolate isolate, JSPromise promise, JSValue value)
    {
        PromiseBuiltins.RunContextPromiseHookResolve(isolate, promise);

        // 1. Assert: The value of promise.[[PromiseState]] is "pending".
        if (promise.Status != PromiseState.kPending) throw new InvalidOperationException("promise is not pending");

        // 2. Let reactions be promise.[[PromiseFulfillReactions]].
        JSValue reactions = promise.Reactions;

        // 3. Set promise.[[PromiseResult]] to value.
        // 4. Set promise.[[PromiseFulfillReactions]] to undefined.
        // 5. Set promise.[[PromiseRejectReactions]] to undefined.
        promise.ReactionsOrResult = value;

        // 6. Set promise.[[PromiseState]] to "fulfilled".
        promise.Status = PromiseState.kFulfilled;

        // 7. Return TriggerPromiseReactions(reactions, value).
        return TriggerPromiseReactions(isolate, reactions, value, PromiseReaction.Type.kFulfill);
    }

    /// <summary>JSPromise::Reject (ES #sec-rejectpromise).</summary>
    public static JSValue Reject(Isolate isolate, JSPromise promise, JSValue reason, bool debugEvent = true)
    {
        // MoveMessageToPromise: V8Sharp has no pending message to move (the
        // message travels with the JavaScriptException).
        // debug_event: the debugger is not ported.
        PromiseBuiltins.RunAllPromiseHooks(isolate, PromiseHookType.kResolve, promise, JSValue.Undefined);

        // 1. Assert: The value of promise.[[PromiseState]] is "pending".
        if (promise.Status != PromiseState.kPending) throw new InvalidOperationException("promise is not pending");

        // 2. Let reactions be promise.[[PromiseRejectReactions]].
        JSValue reactions = promise.Reactions;

        // 3. Set promise.[[PromiseResult]] to reason.
        // 4. Set promise.[[PromiseFulfillReactions]] to undefined.
        // 5. Set promise.[[PromiseRejectReactions]] to undefined.
        promise.ReactionsOrResult = reason;

        // 6. Set promise.[[PromiseState]] to "rejected".
        promise.Status = PromiseState.kRejected;

        // 7. If promise.[[PromiseIsHandled]] is false, perform
        //    HostPromiseRejectionTracker(promise, "reject").
        if (!promise.HasHandler) isolate.ReportPromiseReject(promise, reason, PromiseRejectEvent.kPromiseRejectWithNoHandler);

        // 8. Return TriggerPromiseReactions(reactions, reason).
        return TriggerPromiseReactions(isolate, reactions, reason, PromiseReaction.Type.kReject);
    }

    /// <summary>JSPromise::Resolve (ES #sec-promise-resolve-functions).</summary>
    public static JSValue Resolve(Isolate isolate, JSPromise promise, JSValue resolutionObj)
    {
        PromiseBuiltins.RunAllPromiseHooks(isolate, PromiseHookType.kResolve, promise, JSValue.Undefined);

        // 7. If SameValue(resolution, promise) is true, then
        if (ReferenceEquals(resolutionObj.HeapObjectOrNull, promise))
        {
            // a. Let selfResolutionError be a newly created TypeError object.
            JSObject selfResolutionError = isolate.Factory.NewTypeError(MessageTemplate.PromiseCyclic, resolutionObj);
            // b. Return RejectPromise(promise, selfResolutionError).
            return Reject(isolate, promise, selfResolutionError);
        }

        // 8. If Type(resolution) is not Object, then
        if (resolutionObj.HeapObjectOrNull is not JSReceiver resolutionRecv)
        {
            // a. Return FulfillPromise(promise, resolution).
            return Fulfill(isolate, promise, resolutionObj);
        }

        // 9. Let then be Get(resolution, "then").
        JSValue thenAction;
        try
        {
            // Make sure a lookup of "then" on any JSPromise whose [[Prototype]] is the
            // initial %PromisePrototype% yields the initial method. In addition this
            // protector also guards the negative lookup of "then" on the intrinsic
            // %ObjectPrototype%, meaning that such lookups are guaranteed to yield
            // undefined without triggering any side-effects.
            if (resolutionRecv is JSPromise && resolutionRecv.Map.Prototype is JSReceiver proto &&
                proto.Map.InstanceType == InstanceType.JSPromisePrototypeType &&
                Protectors.IsPromiseThenLookupChainIntact(isolate))
            {
                // We can skip the "then" lookup on {resolution} if its [[Prototype]]
                // is the (initial) Promise.prototype and the Promise#then protector
                // is intact, as that guards the lookup path for the "then" property
                // on JSPromise instances which have the (initial) %PromisePrototype%.
                NativeContext resolutionProtoContext = proto.GetCreationContext()!;
                thenAction = resolutionProtoContext.PromiseThen;
            }
            else
            {
                thenAction = JSReceiver.GetProperty(isolate, resolutionRecv, ReadOnlyRoots.then_string);
            }
        }
        catch (JavaScriptException e)
        {
            // 10. If then is an abrupt completion, then
            // a. Return RejectPromise(promise, then.[[Value]]).
            return Reject(isolate, promise, e.Value, false);
        }

        // 11. Let thenAction be then.[[Value]].
        // 12. If IsCallable(thenAction) is false, then
        if (!ObjectOps.IsCallable(thenAction))
        {
            // a. Return FulfillPromise(promise, resolution).
            return Fulfill(isolate, promise, resolutionRecv);
        }

        // 13. Let job be NewPromiseResolveThenableJob(promise, resolution,
        //                                             thenAction).
        NativeContext thenContext = JSReceiver.GetContextForMicrotask(isolate, (JSReceiver)thenAction.Object) ?? isolate.NativeContext;

        var task = new PromiseResolveThenableJobTask(thenContext, promise, resolutionRecv, (JSReceiver)thenAction.Object);
        thenContext.MicrotaskQueue?.EnqueueMicrotask(task);

        // 15. Return undefined.
        return JSValue.Undefined;
    }

    /// <summary>
    /// JSPromise::TriggerPromiseReactions: morphs the reactions into jobs on
    /// the microtask queue (the same steps as the Torque TriggerPromiseReactions).
    /// </summary>
    public static JSValue TriggerPromiseReactions(Isolate isolate, JSValue reactions, JSValue argument, PromiseReaction.Type type)
    {
        PromiseBuiltins.TriggerPromiseReactions(isolate, reactions, argument, type);
        return JSValue.Undefined;
    }
}
