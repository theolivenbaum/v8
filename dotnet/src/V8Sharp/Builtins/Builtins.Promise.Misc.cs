// Port of src/builtins/promise-finally.tq (Promise.prototype.finally and its
// closures), promise-try.tq (Promise.try) and promise-withresolvers.tq
// (Promise.withResolvers).
namespace V8Sharp.Builtins;

public static partial class PromiseBuiltins
{
    // ---- Promise.prototype.finally (promise-finally.tq) ----------------------------------------------

    /// <summary>PromiseValueThunkFinally: returns the captured value.</summary>
    public static JSValue PromiseValueThunkFinally(Isolate isolate, in BuiltinArguments args) =>
        args.Target.Context[kValueSlot];

    /// <summary>PromiseThrowerFinally: throws the captured reason.</summary>
    public static JSValue PromiseThrowerFinally(Isolate isolate, in BuiltinArguments args) =>
        isolate.Throw(args.Target.Context[kValueSlot]);

    /// <summary>CreateThrowerFunction / CreateValueThunkFunction.</summary>
    static JSFunction CreateValueThunkOrThrower(Isolate isolate, NativeContext nativeContext, JSValue value, Builtin builtin)
    {
        Context context = RootSharedFunctions.AllocateSyntheticFunctionContext(isolate, nativeContext,
            kPromiseValueThunkOrReasonContextLength);
        context[kValueSlot] = value;
        return RootSharedFunctions.AllocateRootFunctionWithContext(isolate, builtin, context, nativeContext);
    }

    /// <summary>PromiseCatchFinally: Catch Finally Functions.</summary>
    public static JSValue PromiseCatchFinally(Isolate isolate, in BuiltinArguments args)
    {
        Context context = args.Target.Context;
        JSValue reason = args.AtOrUndefined(1);
        // 1. Let onFinally be F.[[OnFinally]].
        // 2. Assert: IsCallable(onFinally) is true.
        JSValue onFinally = context[kOnFinallySlot];

        // 3. Let result be ? Call(onFinally).
        JSValue result = Execution.Call(isolate, onFinally, JSValue.Undefined, []);

        // 4. Let C be F.[[Constructor]].
        // 5. Assert: IsConstructor(C) is true.
        var constructor = context[kConstructorSlot].As<JSReceiver>();

        // 6. Let promise be ? PromiseResolve(C, result).
        JSValue promise = PromiseResolve(isolate, constructor, result);

        // 7. Let thrower be equivalent to a function that throws reason.
        NativeContext nativeContext = context.NativeContext;
        JSFunction thrower = CreateValueThunkOrThrower(isolate, nativeContext, reason, Builtin.PromiseThrowerFinally);

        // 8. Return ? Invoke(promise, "then", « thrower »).
        return InvokeThen(isolate, nativeContext, promise, thrower, JSValue.Undefined, 1);
    }

    /// <summary>PromiseThenFinally: Then Finally Functions.</summary>
    public static JSValue PromiseThenFinally(Isolate isolate, in BuiltinArguments args)
    {
        Context context = args.Target.Context;
        JSValue value = args.AtOrUndefined(1);
        // 1. Let onFinally be F.[[OnFinally]].
        // 2.  Assert: IsCallable(onFinally) is true.
        JSValue onFinally = context[kOnFinallySlot];

        // 3. Let result be ?  Call(onFinally).
        JSValue result = Execution.Call(isolate, onFinally, JSValue.Undefined, []);

        // 4. Let C be F.[[Constructor]].
        // 5. Assert: IsConstructor(C) is true.
        var constructor = context[kConstructorSlot].As<JSReceiver>();

        // 6. Let promise be ? PromiseResolve(C, result).
        JSValue promise = PromiseResolve(isolate, constructor, result);

        // 7. Let valueThunk be equivalent to a function that returns value.
        NativeContext nativeContext = context.NativeContext;
        JSFunction valueThunk = CreateValueThunkOrThrower(isolate, nativeContext, value, Builtin.PromiseValueThunkFinally);

        // 8. Return ? Invoke(promise, "then", « valueThunk »).
        return InvokeThen(isolate, nativeContext, promise, valueThunk, JSValue.Undefined, 1);
    }

    /// <summary>Promise.prototype.finally ( onFinally ) (ES #sec-promise.prototype.finally).</summary>
    public static JSValue PromisePrototypeFinally(Isolate isolate, in BuiltinArguments args)
    {
        JSValue receiver = args.Receiver;
        JSValue onFinally = args.AtOrUndefined(1);
        // 1. Let promise be the this value.
        // 2. If Type(promise) is not Object, throw a TypeError exception.
        if (receiver.HeapObjectOrNull is not JSReceiver jsReceiver)
        {
            return isolate.ThrowTypeError(MessageTemplate.CalledOnNonObject,
                isolate.Factory.NewStringFromAsciiChecked("Promise.prototype.finally"));
        }

        // 3. Let C be ? SpeciesConstructor(promise, %Promise%).
        NativeContext nativeContext = isolate.NativeContext;
        JSFunction promiseFun = nativeContext.PromiseFunction;

        JSReceiver constructor = promiseFun;
        Map receiverMap = jsReceiver.Map;
        if (receiverMap.InstanceType != InstanceType.JSPromiseType ||
            !IsPromiseSpeciesLookupChainIntact(isolate, nativeContext, receiverMap))
        {
            constructor = ObjectOps.SpeciesConstructor(isolate, jsReceiver, promiseFun).As<JSReceiver>();
        }

        // 4. Assert: IsConstructor(C) is true.
        // 5. If IsCallable(onFinally) is not true,
        //    a. Let thenFinally be onFinally.
        //    b. Let catchFinally be onFinally.
        // 6. Else,
        //   a. Let thenFinally be a new built-in function object as defined
        //   in ThenFinally Function.
        //   b. Let catchFinally be a new built-in function object as
        //   defined in CatchFinally Function.
        //   c. Set thenFinally and catchFinally's [[Constructor]] internal
        //   slots to C.
        //   d. Set thenFinally and catchFinally's [[OnFinally]] internal
        //   slots to onFinally.
        JSValue thenFinally;
        JSValue catchFinally;
        if (ObjectOps.IsCallable(onFinally))
        {
            // CreatePromiseFinallyFunctions.
            Context promiseContext = RootSharedFunctions.AllocateSyntheticFunctionContext(isolate, nativeContext,
                kPromiseFinallyContextLength);
            promiseContext[kOnFinallySlot] = onFinally;
            promiseContext[kConstructorSlot] = constructor;
            thenFinally = RootSharedFunctions.AllocateRootFunctionWithContext(isolate, Builtin.PromiseThenFinally, promiseContext,
                nativeContext);
            catchFinally = RootSharedFunctions.AllocateRootFunctionWithContext(isolate, Builtin.PromiseCatchFinally, promiseContext,
                nativeContext);
        }
        else
        {
            thenFinally = onFinally;
            catchFinally = onFinally;
        }

        // 7. Return ? Invoke(promise, "then", « thenFinally, catchFinally »).
        return InvokeThen(isolate, nativeContext, receiver, thenFinally, catchFinally, 2);
    }

    // ---- Promise.try (promise-try.tq) ----------------------------------------------------------------

    /// <summary>Promise.try ( callbackfn, ...args ) (https://tc39.es/proposal-promise-try/#sec-promise.try).</summary>
    public static JSValue PromiseTry(Isolate isolate, in BuiltinArguments args)
    {
        // 1. Let ctor be the this value.
        // 2. If ctor is not an Object, throw a TypeError exception.
        if (args.Receiver.HeapObjectOrNull is not JSReceiver receiver)
        {
            return isolate.ThrowTypeError(MessageTemplate.CalledOnNonObject, isolate.Factory.NewStringFromAsciiChecked("Promise.try"));
        }

        JSValue callbackfn = args.AtOrUndefined(1);
        JSArray? rest = args.ArgcWithoutReceiver <= 1
            ? null
            : isolate.Factory.NewJSArrayWithElements(new FixedArray(args.Arguments[1..].ToArray()));

        if (!isolate.Flags.js_pr_3883)
        {
            // Legacy behavior before PR 3883:
            // 3. Let promiseCapability be ? NewPromiseCapability(C).
            PromiseCapability capability = NewPromiseCapability(isolate, receiver, false);

            // 4. Let status be Completion(Call(callbackfn, undefined, args)).
            JSValue legacyResult;
            try
            {
                legacyResult = CallWithRest(isolate, callbackfn, rest);
            }
            catch (JavaScriptException e)
            {
                // 5. If status is an abrupt completion, then
                //   a. Perform ? Call(promiseCapability.[[Reject]], undefined, «
                //      status.[[Value]] »).
                Execution.Call(isolate, capability.Reject, JSValue.Undefined, [e.Value]);

                // 7. Return promiseCapability.[[Promise]].
                return capability.Promise;
            }

            // 6. Else,
            //   a. Perform ? Call(promiseCapability.[[Resolve]], undefined, «
            //      status.[[Value]] »).
            Execution.Call(isolate, capability.Resolve, JSValue.Undefined, [legacyResult]);

            // 7. Return promiseCapability.[[Promise]].
            return capability.Promise;
        }

        // 3. Let status be Completion(Call(callback, undefined, args)).
        JSValue result;
        try
        {
            result = CallWithRest(isolate, callbackfn, rest);
        }
        catch (JavaScriptException e)
        {
            // 4. If status is an abrupt completion, then
            //   a. Let promiseCapability be ? NewPromiseCapability(ctor).
            //   b. Perform ? Call(promiseCapability.[[Reject]], undefined, «
            //      status.[[Value]] »).
            //   c. Return promiseCapability.[[Promise]].
            PromiseCapability capability = NewPromiseCapability(isolate, receiver, false);
            Execution.Call(isolate, capability.Reject, JSValue.Undefined, [e.Value]);
            return capability.Promise;
        }

        // 5. Else,
        //   a. Return ? PromiseResolve(ctor, ! status).
        return PromiseResolve(isolate, receiver, result);
    }

    /// <summary>
    /// Call(callbackfn, undefined) without extra arguments, else
    /// Reflect.apply(callbackfn, undefined, rest) as promise-try.tq does.
    /// </summary>
    static JSValue CallWithRest(Isolate isolate, JSValue callbackfn, JSArray? rest) =>
        rest is null
            ? Execution.Call(isolate, callbackfn, JSValue.Undefined, [])
            : Execution.Call(isolate, isolate.NativeContext.ReflectApply, JSValue.Undefined, [callbackfn, JSValue.Undefined, rest]);

    // ---- Promise.withResolvers (promise-withresolvers.tq) --------------------------------------------

    /// <summary>Promise.withResolvers ( ).</summary>
    public static JSValue PromiseWithResolvers(Isolate isolate, in BuiltinArguments args)
    {
        // 1. Let C be the this value.
        if (args.Receiver.HeapObjectOrNull is not JSReceiver receiver)
        {
            return isolate.ThrowTypeError(MessageTemplate.CalledOnNonObject,
                isolate.Factory.NewStringFromAsciiChecked("Promise.withResolvers"));
        }

        // 2. Let promiseCapability be ? NewPromiseCapability(C).
        PromiseCapability capability = NewPromiseCapability(isolate, receiver, false);

        // 3. Let obj be OrdinaryObjectCreate(%Object.prototype%).
        // 4.-6. CreateDataPropertyOrThrow(obj, "promise" / "resolve" / "reject", ...).
        // 7. Return obj.
        // AllocatePromiseWithResolversResult.
        JSObject result = isolate.Factory.NewJSObjectFromMap(isolate.NativeContext.PromiseWithresolversResultMap);
        JSValue[] fields = result.RawFields;
        fields[0] = capability.Promise;
        fields[1] = capability.Resolve;
        fields[2] = capability.Reject;
        return result;
    }
}
