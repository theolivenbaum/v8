// Port of src/builtins/promise-abstract-operations.tq, promise-misc.tq,
// promise-constructor.tq, promise-then.tq, promise-resolve.tq,
// builtins-promise.h and builtins-promise-gen.cc: the promise machinery
// (NewJSPromise, CreatePromiseResolvingFunctions, NewPromiseCapability,
// FulfillPromise / RejectPromise / ResolvePromise, PerformPromiseThen,
// TriggerPromiseReactions, the context promise hooks) and the Promise
// constructor, Promise.prototype.then / catch, Promise.resolve / reject.
//
// The internal API other components build on (the interpreter's await, the
// async iterators, module evaluation) is the public surface of
// PromiseBuiltins: NewJSPromise, NewPromiseCapability,
// CreatePromiseResolvingFunctions, ResolvePromise, RejectPromise,
// FulfillPromise, PerformPromiseThen, PromiseResolve, EnqueueMicrotask.
namespace V8Sharp.Builtins
{
    public static partial class BuiltinRegistry
    {
        static partial void RegisterPromise()
        {
            Register(Builtin.PromiseConstructor, PromiseBuiltins.PromiseConstructor);
            Register(Builtin.PromisePrototypeThen, PromiseBuiltins.PromisePrototypeThen);
            Register(Builtin.PromisePrototypeCatch, PromiseBuiltins.PromisePrototypeCatch);
            Register(Builtin.PromisePrototypeFinally, PromiseBuiltins.PromisePrototypeFinally);
            Register(Builtin.PromiseResolveTrampoline, PromiseBuiltins.PromiseResolveTrampoline);
            Register(Builtin.PromiseReject, PromiseBuiltins.PromiseReject);
            Register(Builtin.PromiseAll, PromiseBuiltins.PromiseAll);
            Register(Builtin.PromiseAllSettled, PromiseBuiltins.PromiseAllSettled);
            Register(Builtin.PromiseAny, PromiseBuiltins.PromiseAny);
            Register(Builtin.PromiseRace, PromiseBuiltins.PromiseRace);
            Register(Builtin.PromiseTry, PromiseBuiltins.PromiseTry);
            Register(Builtin.PromiseWithResolvers, PromiseBuiltins.PromiseWithResolvers);
            Register(Builtin.PerformPromiseThenFunction, PromiseBuiltins.PerformPromiseThenFunction);
            Register(Builtin.PromiseCapabilityDefaultResolve, PromiseBuiltins.PromiseCapabilityDefaultResolve);
            Register(Builtin.PromiseCapabilityDefaultReject, PromiseBuiltins.PromiseCapabilityDefaultReject);
            Register(Builtin.PromiseGetCapabilitiesExecutor, PromiseBuiltins.PromiseGetCapabilitiesExecutor);
            Register(Builtin.PromiseAllResolveElementClosure, PromiseBuiltins.PromiseAllResolveElementClosure);
            Register(Builtin.PromiseAllSettledResolveElementClosure, PromiseBuiltins.PromiseAllSettledResolveElementClosure);
            Register(Builtin.PromiseAllSettledRejectElementClosure, PromiseBuiltins.PromiseAllSettledRejectElementClosure);
            Register(Builtin.PromiseAnyRejectElementClosure, PromiseBuiltins.PromiseAnyRejectElementClosure);
            Register(Builtin.PromiseThenFinally, PromiseBuiltins.PromiseThenFinally);
            Register(Builtin.PromiseCatchFinally, PromiseBuiltins.PromiseCatchFinally);
            Register(Builtin.PromiseValueThunkFinally, PromiseBuiltins.PromiseValueThunkFinally);
            Register(Builtin.PromiseThrowerFinally, PromiseBuiltins.PromiseThrowerFinally);
            Register(Builtin.GlobalQueueMicrotask, PromiseBuiltins.GlobalQueueMicrotask);
        }
    }

    /// <summary>The promise builtins and the promise abstract operations.</summary>
    public static partial class PromiseBuiltins
    {
        // ---- builtins-promise.h: the context slots of the builtin closures -------------------

        /// <summary>PromiseBuiltins::PromiseResolvingFunctionContextSlot.</summary>
        public const int kPromiseIfNotResolvedSlot = (int)Context.Field.MIN_CONTEXT_SLOTS;
        public const int kDebugEventSlot = kPromiseIfNotResolvedSlot + 1;
        public const int kPromiseContextLength = kDebugEventSlot + 1;

        /// <summary>PromiseBuiltins::PromiseAllResolveElementContextSlots.</summary>
        public const int kPromiseAllResolveElementRemainingSlot = (int)Context.Field.MIN_CONTEXT_SLOTS;
        public const int kPromiseAllResolveElementCapabilitySlot = kPromiseAllResolveElementRemainingSlot + 1;
        public const int kPromiseAllResolveElementValuesSlot = kPromiseAllResolveElementCapabilitySlot + 1;
        public const int kPromiseAllResolveElementLength = kPromiseAllResolveElementValuesSlot + 1;

        /// <summary>PromiseBuiltins::PromiseAnyRejectElementContextSlots.</summary>
        public const int kPromiseAnyRejectElementRemainingSlot = (int)Context.Field.MIN_CONTEXT_SLOTS;
        public const int kPromiseAnyRejectElementCapabilitySlot = kPromiseAnyRejectElementRemainingSlot + 1;
        public const int kPromiseAnyRejectElementErrorsSlot = kPromiseAnyRejectElementCapabilitySlot + 1;
        public const int kPromiseAnyRejectElementLength = kPromiseAnyRejectElementErrorsSlot + 1;

        /// <summary>PromiseBuiltins::FunctionContextSlot.</summary>
        public const int kCapabilitySlot = (int)Context.Field.MIN_CONTEXT_SLOTS;
        public const int kCapabilitiesContextLength = kCapabilitySlot + 1;

        /// <summary>PromiseBuiltins::PromiseFinallyContextSlot.</summary>
        public const int kOnFinallySlot = (int)Context.Field.MIN_CONTEXT_SLOTS;
        public const int kConstructorSlot = kOnFinallySlot + 1;
        public const int kPromiseFinallyContextLength = kConstructorSlot + 1;

        /// <summary>PromiseBuiltins::PromiseValueThunkOrReasonContextSlot.</summary>
        public const int kValueSlot = (int)Context.Field.MIN_CONTEXT_SLOTS;
        public const int kPromiseValueThunkOrReasonContextLength = kValueSlot + 1;

        // ---- Promise hooks (promise-misc.tq, Isolate::RunAllPromiseHooks) --------------------

        /// <summary>
        /// PromiseHookFlags: whether any hook needs to run. V8 also counts the
        /// debugger and the async event delegate, which are not ported.
        /// </summary>
        public static bool NeedsAnyPromiseHooks(Isolate isolate) =>
            isolate.HasContextPromiseHooks || isolate.PromiseHook is not null;

        /// <summary>
        /// v8::Context::SetPromiseHooks: installs the JavaScript promise hooks of
        /// <paramref name="nativeContext"/> (d8.promise.setHooks).
        /// </summary>
        public static void SetPromiseHooks(Isolate isolate, NativeContext nativeContext, JSValue init, JSValue before,
            JSValue after, JSValue resolve)
        {
            nativeContext.PromiseHookInitFunction = init;
            nativeContext.PromiseHookBeforeFunction = before;
            nativeContext.PromiseHookAfterFunction = after;
            nativeContext.PromiseHookResolveFunction = resolve;
            // Isolate::UpdatePromiseHookProtector / SetHasContextPromiseHooks.
            if (!init.IsUndefined || !before.IsUndefined || !after.IsUndefined || !resolve.IsUndefined)
            {
                isolate.HasContextPromiseHooks = true;
                if (Protectors.IsPromiseHookIntact(isolate)) Protectors.InvalidatePromiseHook(isolate);
            }
        }

        /// <summary>RunContextPromiseHook (promise-misc.tq).</summary>
        static void RunContextPromiseHook(Isolate isolate, JSValue hookValue, JSValue promiseOrCapability)
        {
            if (!ObjectOps.IsCallable(hookValue)) return;
            JSPromise? promise = promiseOrCapability.HeapObjectOrNull switch
            {
                JSPromise p => p,
                PromiseCapability capability => capability.Promise.HeapObjectOrNull as JSPromise,
                _ => null,
            };
            if (promise is null) return;
            try
            {
                Execution.Call(isolate, hookValue, JSValue.Undefined, [promise]);
            }
            catch (JavaScriptException e)
            {
                ReportMessageFromMicrotask(isolate, e);
            }
        }

        /// <summary>RunContextPromiseHookResolve.</summary>
        public static void RunContextPromiseHookResolve(Isolate isolate, JSPromise promise)
        {
            if (!isolate.HasContextPromiseHooks) return;
            RunContextPromiseHook(isolate, isolate.NativeContext.PromiseHookResolveFunction, promise);
        }

        /// <summary>RunContextPromiseHookInit.</summary>
        static void RunContextPromiseHookInit(Isolate isolate, JSPromise promise, JSValue parent)
        {
            JSValue hook = isolate.NativeContext.PromiseHookInitFunction;
            if (!ObjectOps.IsCallable(hook)) return;
            JSValue parentObject = parent.HeapObjectOrNull is JSPromise ? parent : JSValue.Undefined;
            try
            {
                Execution.Call(isolate, hook, JSValue.Undefined, [promise, parentObject]);
            }
            catch (JavaScriptException e)
            {
                ReportMessageFromMicrotask(isolate, e);
            }
        }

        /// <summary>RunAnyPromiseHookInit.</summary>
        public static void RunAnyPromiseHookInit(Isolate isolate, JSPromise promise, JSValue parent)
        {
            // Fast return if no hooks are set.
            if (!NeedsAnyPromiseHooks(isolate)) return;
            if (isolate.HasContextPromiseHooks) RunContextPromiseHookInit(isolate, promise, parent);
            // Runtime_PromiseHookInit.
            isolate.PromiseHook?.Invoke(PromiseHookType.kInit, promise, parent);
        }

        /// <summary>
        /// Isolate::RunAllPromiseHooks / MicrotaskQueueBuiltinsAssembler::RunAllPromiseHooks:
        /// the context hook of the current native context, then the isolate hook.
        /// For kBefore/kAfter, <paramref name="promiseOrCapability"/> may be a
        /// PromiseCapability or undefined.
        /// </summary>
        public static void RunAllPromiseHooks(Isolate isolate, PromiseHookType type, JSValue promiseOrCapability,
            JSValue parent = default)
        {
            if (!NeedsAnyPromiseHooks(isolate)) return;
            if (isolate.HasContextPromiseHooks)
            {
                NativeContext nativeContext = isolate.NativeContext;
                switch (type)
                {
                    case PromiseHookType.kInit:
                        if (promiseOrCapability.HeapObjectOrNull is JSPromise initPromise)
                        {
                            RunContextPromiseHookInit(isolate, initPromise, parent);
                        }
                        break;
                    case PromiseHookType.kResolve:
                        RunContextPromiseHook(isolate, nativeContext.PromiseHookResolveFunction, promiseOrCapability);
                        break;
                    case PromiseHookType.kBefore:
                        RunContextPromiseHook(isolate, nativeContext.PromiseHookBeforeFunction, promiseOrCapability);
                        break;
                    case PromiseHookType.kAfter:
                        RunContextPromiseHook(isolate, nativeContext.PromiseHookAfterFunction, promiseOrCapability);
                        break;
                }
            }
            if (isolate.PromiseHook is { } hook)
            {
                JSValue promise = promiseOrCapability.HeapObjectOrNull is PromiseCapability capability
                    ? capability.Promise
                    : promiseOrCapability;
                if (promise.HeapObjectOrNull is JSPromise p) hook(type, p, parent);
            }
        }

        /// <summary>Runtime_ReportMessageFromMicrotask.</summary>
        static void ReportMessageFromMicrotask(Isolate isolate, JavaScriptException e) =>
            (isolate.NativeContext.MicrotaskQueue ?? isolate.DefaultMicrotaskQueue).ReportMessageFromMicrotask(isolate, e);

        // ---- Allocation (promise-misc.tq) -----------------------------------------------------

        /// <summary>PromiseInit.</summary>
        public static void PromiseInit(JSPromise promise)
        {
            promise.ReactionsOrResult = JSValue.Zero;
            promise.Status = PromiseState.kPending;
            promise.IsNativeResolverInvoked = false;
            promise.HasHandler = false;
            promise.IsSilent = false;
            promise.AsyncTaskId = JSPromise.kInvalidAsyncTaskId;
        }

        /// <summary>InnerNewJSPromise: a pending promise with the initial promise map of the current realm.</summary>
        static JSPromise InnerNewJSPromise(Isolate isolate)
        {
            JSFunction promiseFun = isolate.NativeContext.PromiseFunction;
            return (JSPromise)isolate.Factory.NewJSObjectFromMap(promiseFun.InitialMap);
        }

        /// <summary>NewJSPromise(parent): a pending promise; <paramref name="parent"/> is passed to the init hook.</summary>
        public static JSPromise NewJSPromise(Isolate isolate, JSValue parent = default)
        {
            JSPromise instance = InnerNewJSPromise(isolate);
            RunAnyPromiseHookInit(isolate, instance, parent);
            return instance;
        }

        /// <summary>NewJSPromise(status, result): an already settled promise.</summary>
        public static JSPromise NewJSPromise(Isolate isolate, PromiseState status, JSValue result)
        {
            Debug.Assert(status != PromiseState.kPending);
            JSPromise instance = InnerNewJSPromise(isolate);
            instance.ReactionsOrResult = result;
            instance.Status = status;
            RunAnyPromiseHookInit(isolate, instance, JSValue.Undefined);
            return instance;
        }

        // ---- Resolving functions and capabilities (promise-abstract-operations.tq) ------------

        /// <summary>CreatePromiseResolvingFunctionsContext.</summary>
        public static Context CreatePromiseResolvingFunctionsContext(Isolate isolate, JSPromise promise, bool debugEvent,
            NativeContext nativeContext)
        {
            Context resolveContext = RootSharedFunctions.AllocateSyntheticFunctionContext(isolate, nativeContext,
                kPromiseContextLength);
            resolveContext[kPromiseIfNotResolvedSlot] = promise;
            resolveContext[kDebugEventSlot] = JSValue.FromBoolean(debugEvent);
            return resolveContext;
        }

        /// <summary>CreatePromiseResolvingFunctions: the [[Resolve]] and [[Reject]] functions of <paramref name="promise"/>.</summary>
        public static (JSFunction Resolve, JSFunction Reject, Context Context) CreatePromiseResolvingFunctions(Isolate isolate,
            JSPromise promise, bool debugEvent, NativeContext nativeContext)
        {
            Context promiseContext = CreatePromiseResolvingFunctionsContext(isolate, promise, debugEvent, nativeContext);
            JSFunction resolve = RootSharedFunctions.AllocateRootFunctionWithContext(isolate,
                Builtin.PromiseCapabilityDefaultResolve, promiseContext, nativeContext);
            JSFunction reject = RootSharedFunctions.AllocateRootFunctionWithContext(isolate,
                Builtin.PromiseCapabilityDefaultReject, promiseContext, nativeContext);
            return (resolve, reject, promiseContext);
        }

        /// <summary>InnerNewPromiseCapability.</summary>
        static PromiseCapability InnerNewPromiseCapability(Isolate isolate, JSReceiver constructor, bool debugEvent)
        {
            NativeContext nativeContext = isolate.NativeContext;
            if (ReferenceEquals(constructor, nativeContext.PromiseFunction))
            {
                JSPromise promise = NewJSPromise(isolate);
                (JSFunction resolve, JSFunction reject, _) = CreatePromiseResolvingFunctions(isolate, promise, debugEvent,
                    nativeContext);
                return new PromiseCapability(promise, resolve, reject);
            }
            else
            {
                // We have to create the capability before the associated promise
                // because the builtin PromiseConstructor uses the executor.
                var capability = new PromiseCapability(JSValue.Undefined, JSValue.Undefined, JSValue.Undefined);
                Context executorContext = RootSharedFunctions.AllocateSyntheticFunctionContext(isolate, nativeContext,
                    kCapabilitiesContextLength);
                executorContext[kCapabilitySlot] = capability;
                JSFunction executor = RootSharedFunctions.AllocateRootFunctionWithContext(isolate,
                    Builtin.PromiseGetCapabilitiesExecutor, executorContext, nativeContext);

                JSValue promise = Execution.New(isolate, constructor, [executor]);
                capability.Promise = promise;

                if (!ObjectOps.IsCallable(capability.Resolve) || !ObjectOps.IsCallable(capability.Reject))
                {
                    isolate.ThrowTypeError(MessageTemplate.PromiseNonCallable);
                }
                return capability;
            }
        }

        /// <summary>NewPromiseCapability (ES #sec-newpromisecapability).</summary>
        public static PromiseCapability NewPromiseCapability(Isolate isolate, JSValue maybeConstructor, bool debugEvent)
        {
            if (!ObjectOps.IsConstructor(maybeConstructor))
            {
                isolate.ThrowTypeError(MessageTemplate.NotConstructor, maybeConstructor);
            }
            return InnerNewPromiseCapability(isolate, (JSReceiver)maybeConstructor.Object, debugEvent);
        }

        /// <summary>PromiseCapabilityDefaultReject (ES #sec-promise-reject-functions).</summary>
        public static JSValue PromiseCapabilityDefaultReject(Isolate isolate, in BuiltinArguments args)
        {
            Context context = args.Target.Context;
            JSValue reason = args.AtOrUndefined(1);
            // 2. Let promise be promiseOrEmpty.[[Value]].
            // 1. If promiseOrEmpty.[[Value]] is ~empty~, return undefined.
            if (context[kPromiseIfNotResolvedSlot].HeapObjectOrNull is not JSPromise promise) return JSValue.Undefined;

            // 3. Set promiseOrEmpty.[[Value]] to ~empty~.
            context[kPromiseIfNotResolvedSlot] = JSValue.Undefined;

            // 4. Perform RejectPromise(promise, reason).
            // 5. Return undefined.
            bool debugEvent = context[kDebugEventSlot].IsTrue;
            return RejectPromise(isolate, promise, reason, debugEvent);
        }

        /// <summary>PromiseCapabilityDefaultResolve (ES #sec-promise-resolve-functions).</summary>
        public static JSValue PromiseCapabilityDefaultResolve(Isolate isolate, in BuiltinArguments args)
        {
            Context context = args.Target.Context;
            JSValue resolution = args.AtOrUndefined(1);
            // 2. Let promise be promiseOrEmpty.[[Value]].
            // 1. If promiseOrEmpty.[[Value]] is ~empty~, return undefined.
            if (context[kPromiseIfNotResolvedSlot].HeapObjectOrNull is not JSPromise promise) return JSValue.Undefined;

            // 3. Set promiseOrEmpty.[[Value]] to ~empty~.
            context[kPromiseIfNotResolvedSlot] = JSValue.Undefined;

            // The rest of the logic (and the catch prediction) is
            // encapsulated in the dedicated ResolvePromise builtin.
            return ResolvePromise(isolate, promise, resolution);
        }

        /// <summary>PromiseGetCapabilitiesExecutor (ES #sec-getcapabilitiesexecutor-functions).</summary>
        public static JSValue PromiseGetCapabilitiesExecutor(Isolate isolate, in BuiltinArguments args)
        {
            var capability = args.Target.Context[kCapabilitySlot].As<PromiseCapability>();
            if (!capability.Resolve.IsUndefined || !capability.Reject.IsUndefined)
            {
                isolate.ThrowTypeError(MessageTemplate.PromiseExecutorAlreadyInvoked);
            }
            capability.Resolve = args.AtOrUndefined(1);
            capability.Reject = args.AtOrUndefined(2);
            return JSValue.Undefined;
        }

        // ---- Settling (promise-abstract-operations.tq, promise-resolve.tq) ---------------------

        /// <summary>FulfillPromise (ES #sec-fulfillpromise).</summary>
        public static JSValue FulfillPromise(Isolate isolate, JSPromise promise, JSValue value) =>
            JSPromise.Fulfill(isolate, promise, value);

        /// <summary>
        /// RejectPromise (ES #sec-rejectpromise). V8 hands the no-handler and
        /// hook cases to the runtime (JSPromise::Reject), which takes the same
        /// steps and reports the rejection; V8Sharp always takes that path.
        /// </summary>
        public static JSValue RejectPromise(Isolate isolate, JSPromise promise, JSValue reason, bool debugEvent) =>
            JSPromise.Reject(isolate, promise, reason, debugEvent);

        /// <summary>ResolvePromise (ES #sec-promise-resolve-functions).</summary>
        public static JSValue ResolvePromise(Isolate isolate, JSPromise promise, JSValue resolution)
        {
            // 7. If SameValue(resolution, promise) is true, then
            // If promise hook is enabled or the debugger is active, let
            // the runtime handle this operation, which greatly reduces
            // the complexity here and also avoids a couple of back and
            // forth between JavaScript and C++ land.
            // We also let the runtime handle it if promise == resolution.
            if (isolate.PromiseHook is not null || ReferenceEquals(promise, resolution.HeapObjectOrNull))
            {
                return JSPromise.Resolve(isolate, promise, resolution);
            }

            // 8. If Type(resolution) is not Object, then
            // 8.a Return FulfillPromise(promise, resolution).
            if (resolution.HeapObjectOrNull is not JSReceiver resolutionReceiver)
            {
                return FulfillPromise(isolate, promise, resolution);
            }

            JSValue then;
            Map resolutionMap = resolutionReceiver.Map;
            NativeContext nativeContext = isolate.NativeContext;
            // We can skip the "then" lookup on {resolution} if its [[Prototype]]
            // is the (initial) Promise.prototype and the Promise#then protector
            // is intact, as that guards the lookup path for the "then" property
            // on JSPromise instances which have the (initial) %PromisePrototype%.
            if (Protectors.IsPromiseThenLookupChainIntact(isolate) &&
                resolutionMap.InstanceType == InstanceType.JSPromiseType &&
                ReferenceEquals(resolutionMap.Prototype, nativeContext.PromisePrototype))
            {
                // The {resolution} is a native Promise in this case.
                then = nativeContext.PromiseThen;
            }
            else if (Protectors.IsPromiseThenLookupChainIntact(isolate) &&
                     resolutionMap.InstanceType != InstanceType.JSPromiseType &&
                     ReferenceEquals(resolutionMap, nativeContext.IteratorResultMap))
            {
                // We can skip the lookup of "then" if the {resolution} is a (newly
                // created) IterResultObject, as the Promise#then() protector also
                // ensures that the intrinsic %ObjectPrototype% doesn't contain any
                // "then" property. This helps to avoid negative lookups on iterator
                // results from async generators.
                return FulfillPromise(isolate, promise, resolution);
            }
            else
            {
                // 9. Let then be Get(resolution, "then").
                // 10. If then is an abrupt completion, then
                try
                {
                    then = JSReceiver.GetProperty(isolate, resolutionReceiver, ReadOnlyRoots.then_string);
                }
                catch (JavaScriptException e)
                {
                    // a. Return RejectPromise(promise, then.[[Value]]).
                    return RejectPromise(isolate, promise, e.Value, false);
                }

                // 11. Let thenAction be then.[[Value]].
                // 12. If IsCallable(thenAction) is false, then
                if (!ObjectOps.IsCallable(then))
                {
                    // a. Return FulfillPromise(promise, resolution).
                    return FulfillPromise(isolate, promise, resolution);
                }
            }

            // 13. Let job be NewPromiseResolveThenableJob(promise, resolution,
            //                                             thenAction).
            PromiseResolveThenableJobTask task = NewPromiseResolveThenableJobTask(isolate, promise, resolutionReceiver,
                (JSReceiver)then.Object);

            // 14. Perform HostEnqueuePromiseJob(job.[[Job]], job.[[Realm]]).
            // 15. Return undefined.
            EnqueueMicrotask(task.Context, task);
            return JSValue.Undefined;
        }

        /// <summary>NewPromiseResolveThenableJobTask (ES #sec-newpromiseresolvethenablejob).</summary>
        static PromiseResolveThenableJobTask NewPromiseResolveThenableJobTask(Isolate isolate, JSPromise promiseToResolve,
            JSReceiver thenable, JSReceiver then)
        {
            // 2. Let getThenRealmResult be GetFunctionRealm(then).
            // 3. If getThenRealmResult is a normal completion, then let thenRealm be
            //    getThenRealmResult.[[Value]].
            // 4. Otherwise, let thenRealm be null.
            //
            // The only cases where |thenRealm| can be null is when |then| is a revoked
            // Proxy object, which would throw when it is called anyway. So instead of
            // setting the context to null as the spec does, we just use the current
            // realm.
            Context thenContext = ExtractHandlerContext(isolate, then);
            NativeContext nativeContext = thenContext.NativeContext;

            // 1. Let job be a new Job abstract closure with no parameters that
            //    captures promiseToResolve, thenable, and then...
            // 5. Return { [[Job]]: job, [[Realm]]: thenRealm }.
            return new PromiseResolveThenableJobTask(nativeContext, promiseToResolve, thenable, then);
        }

        // ---- Reactions (promise-abstract-operations.tq) ---------------------------------------

        /// <summary>ExtractHandlerContextInternal: the context of the handler function or generator.</summary>
        static Context? ExtractHandlerContextInternal(JSValue handler)
        {
            HeapObject? iter = handler.HeapObjectOrNull;
            while (true)
            {
                switch (iter)
                {
                    case JSBoundFunction b:
                        iter = b.BoundTargetFunction;
                        continue;
                    case JSProxy p:
                        iter = p.Target.HeapObjectOrNull;
                        continue;
                    case JSFunction f:
                        return f.Context;
                    case JSGeneratorObject g:
                        return g.Context;
                    default:
                        return null;
                }
            }
        }

        /// <summary>ExtractHandlerContext(handler).</summary>
        static Context ExtractHandlerContext(Isolate isolate, JSValue handler) =>
            ExtractHandlerContextInternal(handler) ?? isolate.Context!;

        /// <summary>ExtractHandlerContext(primary, secondary).</summary>
        static Context ExtractHandlerContext(Isolate isolate, JSValue primary, JSValue secondary) =>
            ExtractHandlerContextInternal(primary) ?? ExtractHandlerContextInternal(secondary) ?? isolate.Context!;

        /// <summary>MorphAndEnqueuePromiseReaction.</summary>
        static void MorphAndEnqueuePromiseReaction(Isolate isolate, PromiseReaction promiseReaction, JSValue argument,
            PromiseReaction.Type reactionType)
        {
            JSValue primaryHandler, secondaryHandler;
            if (reactionType == PromiseReaction.Type.kFulfill)
            {
                primaryHandler = promiseReaction.FulfillHandler;
                secondaryHandler = promiseReaction.RejectHandler;
            }
            else
            {
                primaryHandler = promiseReaction.RejectHandler;
                secondaryHandler = promiseReaction.FulfillHandler;
            }

            // According to HTML, we use the context of the appropriate handler as the
            // context of the microtask. See step 3 of HTML's EnqueueJob:
            // https://html.spec.whatwg.org/C/#enqueuejob(queuename,-job,-arguments)
            Context handlerContext = ExtractHandlerContext(isolate, primaryHandler, secondaryHandler);

            // Morph {current} from a PromiseReaction into a PromiseReactionJobTask
            // and schedule that on the microtask queue.
            if (reactionType == PromiseReaction.Type.kFulfill)
            {
                promiseReaction.State = PromiseReaction.Kind.FulfillReactionJobTask;
            }
            else
            {
                promiseReaction.State = PromiseReaction.Kind.RejectReactionJobTask;
                promiseReaction.FulfillHandler = primaryHandler;
            }
            promiseReaction.Argument = argument;
            promiseReaction.Context = handlerContext;
            promiseReaction.Next = JSValue.Zero;
            EnqueueMicrotask(handlerContext, promiseReaction);
        }

        /// <summary>TriggerPromiseReactions (ES #sec-triggerpromisereactions).</summary>
        public static void TriggerPromiseReactions(Isolate isolate, JSValue reactions, JSValue argument,
            PromiseReaction.Type reactionType)
        {
            // Fast path for 0 or 1 reaction.
            if (reactions.HeapObjectOrNull is not PromiseReaction reaction) return;
            if (reaction.Next.HeapObjectOrNull is null)
            {
                MorphAndEnqueuePromiseReaction(isolate, reaction, argument, reactionType);
                return;
            }

            // We need to reverse the {reactions} here, since we record them on the
            // JSPromise in the reverse order.
            PromiseReaction? current = reaction;
            PromiseReaction? reversed = null;
            while (current is not null)
            {
                var next = current.Next.HeapObjectOrNull as PromiseReaction;
                current.Next = reversed is null ? JSValue.Zero : reversed;
                reversed = current;
                current = next;
            }
            // Morph the {reactions} into PromiseReactionJobTasks and push them
            // onto the microtask queue.
            current = reversed;
            while (current is not null)
            {
                var next = current.Next.HeapObjectOrNull as PromiseReaction;
                MorphAndEnqueuePromiseReaction(isolate, current, argument, reactionType);
                current = next;
            }
        }

        /// <summary>
        /// PerformPromiseThenImpl: records a reaction on a pending promise, or
        /// enqueues the reaction job of a settled one.
        /// <paramref name="resultPromiseOrCapability"/> is a JSPromise, a
        /// PromiseCapability, or undefined (await, where the result is dropped).
        /// </summary>
        public static void PerformPromiseThenImpl(Isolate isolate, JSPromise promise, JSValue onFulfilled, JSValue onRejected,
            JSValue resultPromiseOrCapability)
        {
            if (promise.Status == PromiseState.kPending)
            {
                // The {promise} is still in "Pending" state, so we just record a new
                // PromiseReaction holding both the onFulfilled and onRejected callbacks.
                // Once the {promise} is resolved we decide on the concrete handler to
                // push onto the microtask queue.
                JSValue promiseReactions = promise.ReactionsOrResult;
                var reaction = new PromiseReaction(promiseReactions, resultPromiseOrCapability, onFulfilled, onRejected);
                promise.ReactionsOrResult = reaction;
            }
            else
            {
                JSValue reactionsOrResult = promise.ReactionsOrResult;
                PromiseReaction microtask;
                Context handlerContext;
                if (promise.Status == PromiseState.kFulfilled)
                {
                    handlerContext = ExtractHandlerContext(isolate, onFulfilled, onRejected);
                    microtask = new PromiseReaction(PromiseReaction.Kind.FulfillReactionJobTask, handlerContext,
                        reactionsOrResult, onFulfilled, resultPromiseOrCapability);
                }
                else
                {
                    Debug.Assert(promise.Status == PromiseState.kRejected);
                    handlerContext = ExtractHandlerContext(isolate, onRejected, onFulfilled);
                    microtask = new PromiseReaction(PromiseReaction.Kind.RejectReactionJobTask, handlerContext,
                        reactionsOrResult, onRejected, resultPromiseOrCapability);
                    if (!promise.HasHandler)
                    {
                        // Runtime_PromiseRevokeReject.
                        isolate.ReportPromiseReject(promise, JSValue.Undefined, PromiseRejectEvent.kPromiseHandlerAddedAfterReject);
                    }
                }
                EnqueueMicrotask(handlerContext, microtask);
            }
            promise.HasHandler = true;
        }

        /// <summary>PerformPromiseThen (ES #sec-performpromisethen): returns <paramref name="resultPromise"/>.</summary>
        public static JSValue PerformPromiseThen(Isolate isolate, JSPromise promise, JSValue onFulfilled, JSValue onRejected,
            JSValue resultPromise)
        {
            PerformPromiseThenImpl(isolate, promise, onFulfilled, onRejected, resultPromise);
            return resultPromise;
        }

        /// <summary>PerformPromiseThenFunction: the native_context()->perform_promise_then() JS builtin.</summary>
        public static JSValue PerformPromiseThenFunction(Isolate isolate, in BuiltinArguments args)
        {
            var jsPromise = args.Receiver.As<JSPromise>();
            PerformPromiseThenImpl(isolate, jsPromise, args.AtOrUndefined(1), args.AtOrUndefined(2), args.AtOrUndefined(3));
            return JSValue.Undefined;
        }

        /// <summary>
        /// EnqueueMicrotask builtin: enqueues on the microtask queue of the
        /// native context of <paramref name="context"/>. Nothing is enqueued if
        /// the context was shut down.
        /// </summary>
        public static void EnqueueMicrotask(Context context, Microtask microtask) =>
            context.NativeContext.MicrotaskQueue?.EnqueueMicrotask(microtask);

        // ---- Protector checks -------------------------------------------------------------------

        /// <summary>IsPromiseThenLookupChainIntact(nativeContext, receiverMap).</summary>
        public static bool IsPromiseThenLookupChainIntact(Isolate isolate, NativeContext nativeContext, Map receiverMap)
        {
            if (receiverMap.InstanceType != InstanceType.JSPromiseType) return false;
            if (!ReferenceEquals(receiverMap.Prototype, nativeContext.PromisePrototype)) return false;
            return Protectors.IsPromiseThenLookupChainIntact(isolate);
        }

        /// <summary>IsPromiseSpeciesLookupChainIntact(nativeContext, promiseMap).</summary>
        public static bool IsPromiseSpeciesLookupChainIntact(Isolate isolate, NativeContext nativeContext, Map promiseMap)
        {
            if (!ReferenceEquals(promiseMap.Prototype, nativeContext.PromisePrototype)) return false;
            return Protectors.IsPromiseSpeciesLookupChainIntact(isolate);
        }

        /// <summary>IsPromiseResolveLookupChainIntact(nativeContext, constructor).</summary>
        static bool IsPromiseResolveLookupChainIntact(Isolate isolate, NativeContext nativeContext, JSReceiver constructor) =>
            ReferenceEquals(nativeContext.PromiseFunction, constructor) && Protectors.IsPromiseResolveLookupChainIntact(isolate);

        // ---- InvokeThen, GetPromiseResolve, CallResolve -----------------------------------------

        /// <summary>InvokeThen(nativeContext, receiver, arg1[, arg2]).</summary>
        public static JSValue InvokeThen(Isolate isolate, NativeContext nativeContext, JSValue receiver, JSValue arg1,
            JSValue arg2, int argc)
        {
            // We can skip the "then" lookup on {receiver} if it's [[Prototype]]
            // is the (initial) Promise.prototype and the Promise#then protector
            // is intact, as that guards the lookup path for the "then" property
            // on JSPromise instances which have the (initial) %PromisePrototype%.
            JSValue then;
            if (receiver.HeapObjectOrNull is JSReceiver r && IsPromiseThenLookupChainIntact(isolate, nativeContext, r.Map))
            {
                then = nativeContext.PromiseThen;
            }
            else
            {
                then = ObjectOps.GetProperty(isolate, receiver, ReadOnlyRoots.then_string);
            }
            return argc == 1
                ? Execution.Call(isolate, then, receiver, [arg1])
                : Execution.Call(isolate, then, receiver, [arg1, arg2]);
        }

        /// <summary>GetPromiseResolve (ES #sec-getpromiseresolve): undefined means "use Promise.resolve directly".</summary>
        public static JSValue GetPromiseResolve(Isolate isolate, NativeContext nativeContext, JSReceiver constructor)
        {
            // We can skip the "resolve" lookup on {constructor} if it's the
            // Promise constructor and the Promise.resolve protector is intact,
            // as that guards the lookup path for the "resolve" property on the
            // Promise constructor. In this case, promiseResolveFunction is undefined,
            // and when CallResolve is called with it later, it will call Promise.resolve.
            JSValue promiseResolveFunction = JSValue.Undefined;

            if (!IsPromiseResolveLookupChainIntact(isolate, nativeContext, constructor))
            {
                // 2. Let promiseResolve be ? Get(constructor, "resolve").
                JSValue promiseResolve = JSReceiver.GetProperty(isolate, constructor, ReadOnlyRoots.resolve_string);

                // 3. If IsCallable(promiseResolve) is false, throw a TypeError exception.
                if (!ObjectOps.IsCallable(promiseResolve))
                {
                    isolate.ThrowTypeError(MessageTemplate.CalledNonCallable, isolate.Factory.NewStringFromUtf16("resolve"));
                }
                promiseResolveFunction = promiseResolve;
            }
            // 4. return promiseResolve.
            return promiseResolveFunction;
        }

        /// <summary>CallResolve.</summary>
        public static JSValue CallResolve(Isolate isolate, JSReceiver constructor, JSValue resolve, JSValue value)
        {
            // Undefined can never be a valid value for the resolve function,
            // instead it is used as a special marker for the fast path.
            if (resolve.IsUndefined) return PromiseResolve(isolate, constructor, value);
            return Execution.Call(isolate, resolve, constructor, [value]);
        }

        // ---- Promise.resolve (promise-resolve.tq) ------------------------------------------------

        /// <summary>PromiseResolve (ES #sec-promise-resolve): the builtin behind Promise.resolve and await.</summary>
        public static JSValue PromiseResolve(Isolate isolate, JSReceiver constructor, JSValue value)
        {
            NativeContext nativeContext = isolate.NativeContext;
            JSFunction promiseFun = nativeContext.PromiseFunction;
            // Check if {value} is a JSPromise.
            if (value.HeapObjectOrNull is JSPromise valuePromise)
            {
                // We can skip the "constructor" lookup on {value} if it's [[Prototype]]
                // is the (initial) Promise.prototype and the @@species protector is
                // intact, as that guards the lookup path for "constructor" on
                // JSPromise instances which have the (initial) Promise.prototype.
                if (ReferenceEquals(valuePromise.Map.Prototype, nativeContext.PromisePrototype) &&
                    Protectors.IsPromiseSpeciesLookupChainIntact(isolate) &&
                    ReferenceEquals(promiseFun, constructor))
                {
                    // If the {constructor} is the Promise function, we just immediately
                    // return the {value} here and don't bother wrapping it into a
                    // native Promise.
                    return valuePromise;
                }

                // At this point, value or/and constructor are not native promises, but
                // they could be of the same subclass.
                JSValue valueConstructor = JSReceiver.GetProperty(isolate, valuePromise, ReadOnlyRoots.constructor_string);
                if (ReferenceEquals(valueConstructor.HeapObjectOrNull, constructor)) return valuePromise;
            }

            // NeedToAllocate:
            if (ReferenceEquals(promiseFun, constructor))
            {
                // This adds a fast path for native promises that don't need to
                // create NewPromiseCapability.
                JSPromise result = NewJSPromise(isolate);
                ResolvePromise(isolate, result, value);
                return result;
            }
            else
            {
                PromiseCapability capability = NewPromiseCapability(isolate, constructor, true);
                Execution.Call(isolate, capability.Resolve, JSValue.Undefined, [value]);
                return capability.Promise;
            }
        }

        /// <summary>Promise.resolve ( x ) (ES #sec-promise.resolve).</summary>
        public static JSValue PromiseResolveTrampoline(Isolate isolate, in BuiltinArguments args)
        {
            // 1. Let C be the this value.
            // 2. If Type(C) is not Object, throw a TypeError exception.
            if (args.Receiver.HeapObjectOrNull is not JSReceiver receiver)
            {
                return isolate.ThrowTypeError(MessageTemplate.CalledOnNonObject, isolate.Factory.NewStringFromUtf16("PromiseResolve"));
            }
            // 3. Return ? PromiseResolve(C, x).
            return PromiseResolve(isolate, receiver, args.AtOrUndefined(1));
        }

        /// <summary>Promise.reject ( r ) (ES #sec-promise.reject).</summary>
        public static JSValue PromiseReject(Isolate isolate, in BuiltinArguments args)
        {
            // 1. Let C be the this value.
            // 2. If Type(C) is not Object, throw a TypeError exception.
            if (args.Receiver.HeapObjectOrNull is not JSReceiver receiver)
            {
                return isolate.ThrowTypeError(MessageTemplate.CalledOnNonObject, isolate.Factory.NewStringFromUtf16("PromiseReject"));
            }
            JSValue reason = args.AtOrUndefined(1);

            JSFunction promiseFun = isolate.NativeContext.PromiseFunction;
            if (ReferenceEquals(promiseFun, receiver))
            {
                JSPromise promise = NewJSPromise(isolate, PromiseState.kRejected, reason);
                // Runtime_PromiseRejectEventFromStack.
                RunAllPromiseHooks(isolate, PromiseHookType.kResolve, promise, JSValue.Undefined);
                // Report only if we don't actually have a handler.
                if (!promise.HasHandler)
                {
                    isolate.ReportPromiseReject(promise, reason, PromiseRejectEvent.kPromiseRejectWithNoHandler);
                }
                return promise;
            }
            else
            {
                // 3. Let promiseCapability be ? NewPromiseCapability(C).
                PromiseCapability capability = NewPromiseCapability(isolate, receiver, true);

                // 4. Perform ? Call(promiseCapability.[[Reject]], undefined, « r »).
                Execution.Call(isolate, capability.Reject, JSValue.Undefined, [reason]);

                // 5. Return promiseCapability.[[Promise]].
                return capability.Promise;
            }
        }

        // ---- The Promise constructor and prototype (promise-constructor.tq, promise-then.tq) ----

        /// <summary>Promise ( executor ) (ES #sec-promise-executor).</summary>
        public static JSValue PromiseConstructor(Isolate isolate, in BuiltinArguments args)
        {
            JSValue executor = args.AtOrUndefined(1);
            // 1. If NewTarget is undefined, throw a TypeError exception.
            if (args.NewTarget.IsUndefined)
            {
                return isolate.ThrowTypeError(MessageTemplate.PromiseNewTargetUndefined);
            }

            // 2. If IsCallable(executor) is false, throw a TypeError exception.
            if (!ObjectOps.IsCallable(executor))
            {
                return isolate.ThrowTypeError(MessageTemplate.ResolverNotAFunction, executor);
            }

            NativeContext nativeContext = isolate.NativeContext;
            JSFunction promiseFun = nativeContext.PromiseFunction;

            // Throw no access type error if the stack looks fishy
            // (BranchIfAccessCheckFailed). Deviation: that check only fails when
            // Runtime_AllowDynamicFunction does, which it never does in V8Sharp
            // (Builtins::AllowDynamicFunction is always true, see deviations.md).

            JSPromise result;
            if (ReferenceEquals(promiseFun, args.NewTarget.HeapObjectOrNull))
            {
                result = NewJSPromise(isolate);
            }
            else
            {
                result = (JSPromise)JSObject.New(isolate, promiseFun, args.NewTarget.As<JSReceiver>());
                PromiseInit(result);
                RunAnyPromiseHookInit(isolate, result, JSValue.Undefined);
            }

            (JSFunction resolve, JSFunction reject, Context promiseContext) =
                CreatePromiseResolvingFunctions(isolate, result, true, nativeContext);
            try
            {
                Execution.Call(isolate, executor, JSValue.Undefined, [resolve, reject]);
            }
            catch (JavaScriptException e)
            {
                // We need to disable the debug event, as we have already paused on this
                // exception.
                promiseContext[kDebugEventSlot] = JSValue.False;
                Execution.Call(isolate, reject, JSValue.Undefined, [e.Value]);
            }

            return result;
        }

        /// <summary>Promise.prototype.then ( onFulfilled, onRejected ) (ES #sec-promise.prototype.then).</summary>
        public static JSValue PromisePrototypeThen(Isolate isolate, in BuiltinArguments args)
        {
            // 1. Let promise be the this value.
            // 2. If IsPromise(promise) is false, throw a TypeError exception.
            if (args.Receiver.HeapObjectOrNull is not JSPromise promise)
            {
                return isolate.ThrowTypeError(MessageTemplate.IncompatibleMethodReceiver,
                    isolate.Factory.NewStringFromUtf16("Promise.prototype.then"), args.Receiver);
            }

            // 3. Let C be ? SpeciesConstructor(promise, %Promise%).
            NativeContext nativeContext = isolate.NativeContext;
            JSFunction promiseFun = nativeContext.PromiseFunction;

            // 4. Let resultCapability be ? NewPromiseCapability(C).
            JSValue resultPromiseOrCapability;
            JSValue resultPromise;
            JSValue constructor = IsPromiseSpeciesLookupChainIntact(isolate, nativeContext, promise.Map)
                ? promiseFun
                : ObjectOps.SpeciesConstructor(isolate, promise, promiseFun);
            if (ReferenceEquals(constructor.HeapObjectOrNull, promiseFun))
            {
                JSPromise resultJSPromise = NewJSPromise(isolate, promise);
                resultPromiseOrCapability = resultJSPromise;
                resultPromise = resultJSPromise;
            }
            else
            {
                PromiseCapability promiseCapability = NewPromiseCapability(isolate, constructor, true);
                resultPromiseOrCapability = promiseCapability;
                resultPromise = promiseCapability.Promise;
            }

            // We do some work of the PerformPromiseThen operation here, in that
            // we check the handlers and turn non-callable handlers into undefined.
            // This is because this is the one and only callsite of PerformPromiseThen
            // that has to do this.

            // 3. If IsCallable(onFulfilled) is false, then
            //    a. Set onFulfilled to undefined.
            JSValue onFulfilled = args.AtOrUndefined(1);
            if (!ObjectOps.IsCallable(onFulfilled)) onFulfilled = JSValue.Undefined;

            // 4. If IsCallable(onRejected) is false, then
            //    a. Set onRejected to undefined.
            JSValue onRejected = args.AtOrUndefined(2);
            if (!ObjectOps.IsCallable(onRejected)) onRejected = JSValue.Undefined;

            // 5. Return PerformPromiseThen(promise, onFulfilled, onRejected,
            //    resultCapability).
            PerformPromiseThenImpl(isolate, promise, onFulfilled, onRejected, resultPromiseOrCapability);
            return resultPromise;
        }

        /// <summary>Promise.prototype.catch ( onRejected ) (ES #sec-promise.prototype.catch).</summary>
        public static JSValue PromisePrototypeCatch(Isolate isolate, in BuiltinArguments args)
        {
            // 1. Let promise be the this value.
            // 2. Return ? Invoke(promise, "then", « undefined, onRejected »).
            return InvokeThen(isolate, isolate.NativeContext, args.Receiver, JSValue.Undefined, args.AtOrUndefined(1), 2);
        }
    }
}

namespace V8Sharp
{
    public sealed partial class Isolate
    {
        /// <summary>
        /// The isolate-wide promise hook (v8::Isolate::SetPromiseHook). V8 calls
        /// it with (type, promise, parent) for kInit, kResolve, kBefore and kAfter.
        /// </summary>
        public Action<PromiseHookType, JSPromise, JSValue>? PromiseHook;

        /// <summary>
        /// Isolate::HasContextPromiseHooks: set once a native context got
        /// JavaScript promise hooks (v8::Context::SetPromiseHooks).
        /// </summary>
        public bool HasContextPromiseHooks;
    }
}
