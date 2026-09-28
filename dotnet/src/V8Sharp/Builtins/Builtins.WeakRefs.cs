// Port of src/builtins/weak-ref.tq (WeakRefConstructor, WeakRefDeref),
// src/builtins/finalization-registry.tq (FinalizationRegistryConstructor,
// FinalizationRegistryRegister) and src/builtins/builtins-weak-refs.cc
// (FinalizationRegistryUnregister), with the runtime functions they call
// (JSWeakRefAddToKeptObjects,
// JSFinalizationRegistryRegisterWeakCellWithUnregisterToken).
namespace V8Sharp.Builtins;

public static partial class BuiltinRegistry
{
    static partial void RegisterWeakRefs()
    {
        Register(Builtin.WeakRefConstructor, WeakRefBuiltins.WeakRefConstructor);
        Register(Builtin.WeakRefDeref, WeakRefBuiltins.WeakRefDeref);
        Register(Builtin.FinalizationRegistryConstructor, WeakRefBuiltins.FinalizationRegistryConstructor);
        Register(Builtin.FinalizationRegistryRegister, WeakRefBuiltins.FinalizationRegistryRegister);
        Register(Builtin.FinalizationRegistryUnregister, WeakRefBuiltins.FinalizationRegistryUnregister);
    }
}

/// <summary>The WeakRef and FinalizationRegistry builtins.</summary>
public static class WeakRefBuiltins
{
    /// <summary>WeakRef ( target ) (ES #sec-weak-ref-target).</summary>
    public static JSValue WeakRefConstructor(Isolate isolate, in BuiltinArguments args)
    {
        // 1. If NewTarget is undefined, throw a TypeError exception.
        if (args.NewTarget.IsUndefined)
        {
            return isolate.ThrowTypeError(MessageTemplate.ConstructorNotFunction, isolate.Factory.NewStringFromAsciiChecked("WeakRef"));
        }

        if (args.ArgcWithoutReceiver == 0)
        {
            return isolate.ThrowTypeError(MessageTemplate.InvalidWeakRefsWeakRefConstructorTarget);
        }

        // 2. If CanBeHeldWeakly(weakTarget) is false, throw a TypeError exception.
        JSValue weakTarget = args[1];
        if (!CollectionsBuiltins.CanBeHeldWeakly(weakTarget))
        {
            return isolate.ThrowTypeError(MessageTemplate.InvalidWeakRefsWeakRefConstructorTarget);
        }

        // 3. Let weakRef be ? OrdinaryCreateFromConstructor(NewTarget,
        // "%WeakRefPrototype%", « [[WeakRefTarget]] »).
        Map map = JSFunction.GetDerivedMap(isolate, args.Target, args.NewTarget.As<JSReceiver>());
        var weakRef = (JSWeakRef)JSObject.NewFastOrSlowJSObjectFromMap(isolate, map);
        // 5. Set weakRef.[[WeakRefTarget]] to target.
        weakRef.Target = new WeakReference<HeapObject>(weakTarget.Object);
        // 4. Perfom ! AddToKeptObjects(target).
        isolate.KeepDuringJob(weakTarget.Object);
        // 6. Return weakRef.
        return weakRef;
    }

    /// <summary>WeakRef.prototype.deref ( ).</summary>
    public static JSValue WeakRefDeref(Isolate isolate, in BuiltinArguments args)
    {
        // 1. Let weakRef be the this value.
        // 2. Perform ? RequireInternalSlot(weakRef, [[WeakRefTarget]]).
        if (args.Receiver.HeapObjectOrNull is not JSWeakRef weakRef)
        {
            return isolate.ThrowTypeError(MessageTemplate.IncompatibleMethodReceiver,
                isolate.Factory.NewStringFromAsciiChecked("WeakRef.prototype.deref"), args.Receiver);
        }
        // 3. Let target be the value of weakRef.[[WeakRefTarget]].
        // 4. If target is not empty,
        //   a. Perform ! AddToKeptObjects(target).
        //   b. Return target.
        // 5. Return undefined.
        HeapObject? target = weakRef.GetTarget();
        if (target is null)
        {
            weakRef.Target = null;
            return JSValue.Undefined;
        }
        isolate.KeepDuringJob(target);
        return target;
    }

    /// <summary>FinalizationRegistry ( cleanupCallback ).</summary>
    public static JSValue FinalizationRegistryConstructor(Isolate isolate, in BuiltinArguments args)
    {
        // 1. If NewTarget is undefined, throw a TypeError exception.
        if (args.NewTarget.IsUndefined)
        {
            return isolate.ThrowTypeError(MessageTemplate.ConstructorNotFunction,
                isolate.Factory.NewStringFromAsciiChecked("FinalizationRegistry"));
        }
        // 2. If IsCallable(cleanupCallback) is false, throw a TypeError exception.
        if (args.ArgcWithoutReceiver == 0 || !ObjectOps.IsCallable(args[1]))
        {
            return isolate.ThrowTypeError(MessageTemplate.WeakRefsCleanupMustBeCallable);
        }
        JSValue cleanupCallback = args[1];
        // 3. Let finalizationRegistry be ? OrdinaryCreateFromConstructor(NewTarget,
        // "%FinalizationRegistryPrototype%", « [[Realm]], [[CleanupCallback]],
        // [[Cells]] »).
        Map map = JSFunction.GetDerivedMap(isolate, args.Target, args.NewTarget.As<JSReceiver>());
        var finalizationRegistry = (JSFinalizationRegistry)JSObject.NewFastOrSlowJSObjectFromMap(isolate, map);
        // 4. Let fn be the active function object.
        // 5. Set finalizationRegistry.[[Realm]] to fn.[[Realm]].
        finalizationRegistry.NativeContext = args.Target.Context.NativeContext;
        // 6. Set finalizationRegistry.[[CleanupCallback]] to cleanupCallback.
        finalizationRegistry.Cleanup = cleanupCallback;
        finalizationRegistry.ScheduledForCleanup = false;
        // 7. Set finalizationRegistry.[[Cells]] to be an empty List.
        // 8. Return finalizationRegistry.
        return finalizationRegistry;
    }

    /// <summary>FinalizationRegistry.prototype.register ( target, heldValue [ , unregisterToken ] ).</summary>
    public static JSValue FinalizationRegistryRegister(Isolate isolate, in BuiltinArguments args)
    {
        // 1. Let finalizationRegistry be the this value.
        // 2. Perform ? RequireInternalSlot(finalizationRegistry, [[Cells]]).
        if (args.Receiver.HeapObjectOrNull is not JSFinalizationRegistry finalizationRegistry)
        {
            return isolate.ThrowTypeError(MessageTemplate.IncompatibleMethodReceiver,
                isolate.Factory.NewStringFromAsciiChecked("FinalizationRegistry.prototype.register"), args.Receiver);
        }
        // 3. If CanBeHeldWeakly(target) is false, throw a TypeError exception.
        JSValue targetValue = args.AtOrUndefined(1);
        if (!CollectionsBuiltins.CanBeHeldWeakly(targetValue))
        {
            return isolate.ThrowTypeError(MessageTemplate.InvalidWeakRefsRegisterTarget);
        }

        HeapObject target = targetValue.Object;
        JSValue heldValue = args.AtOrUndefined(2);
        // 4. If SameValue(target, heldValue), throw a TypeError exception.
        if (ReferenceEquals(target, heldValue.HeapObjectOrNull))
        {
            return isolate.ThrowTypeError(MessageTemplate.WeakRefsRegisterTargetAndHoldingsMustNotBeSame);
        }
        // 5. If CanBeHeldWeakly(unregisterToken) is false,
        //   a. If unregisterToken is not undefined, throw a TypeError exception.
        //   b. Set unregisterToken to empty.
        JSValue unregisterTokenRaw = args.AtOrUndefined(3);
        HeapObject? unregisterToken = null;
        if (!unregisterTokenRaw.IsUndefined)
        {
            if (!CollectionsBuiltins.CanBeHeldWeakly(unregisterTokenRaw))
            {
                return isolate.ThrowTypeError(MessageTemplate.InvalidWeakRefsUnregisterToken, unregisterTokenRaw);
            }
            unregisterToken = unregisterTokenRaw.Object;
        }

        // 6. Let cell be the Record { [[WeakRefTarget]] : target, [[HeldValue]]:
        //    heldValue, [[UnregisterToken]]: unregisterToken }.
        var cell = new WeakCell(finalizationRegistry, heldValue, target, unregisterToken);
        // 7. Append cell to finalizationRegistry.[[Cells]].
        finalizationRegistry.PushCell(cell);
        if (unregisterToken is not null)
        {
            // If an unregister token is provided, a runtime call is needed to
            // do some OrderedHashTable operations and register the mapping.
            JSFinalizationRegistry.RegisterWeakCellWithUnregisterToken(finalizationRegistry, cell, unregisterToken);
        }
        isolate.TrackFinalizationRegistry(finalizationRegistry);
        // 8. Return undefined.
        return JSValue.Undefined;
    }

    /// <summary>FinalizationRegistry.prototype.unregister ( unregisterToken ).</summary>
    public static JSValue FinalizationRegistryUnregister(Isolate isolate, in BuiltinArguments args)
    {
        // 1. Let finalizationGroup be the this value.
        //
        // 2. Perform ? RequireInternalSlot(finalizationRegistry, [[Cells]]).
        if (args.Receiver.HeapObjectOrNull is not JSFinalizationRegistry finalizationRegistry)
        {
            return isolate.ThrowTypeError(MessageTemplate.IncompatibleMethodReceiver,
                isolate.Factory.NewStringFromAsciiChecked("FinalizationRegistry.prototype.unregister"), args.Receiver);
        }

        JSValue unregisterToken = args.AtOrUndefined(1);

        // 3. If CanBeHeldWeakly(unregisterToken) is false, throw a TypeError
        // exception.
        if (!CollectionsBuiltins.CanBeHeldWeakly(unregisterToken))
        {
            return isolate.ThrowTypeError(MessageTemplate.InvalidWeakRefsUnregisterToken, unregisterToken);
        }

        bool success = JSFinalizationRegistry.Unregister(finalizationRegistry, unregisterToken.Object);
        return JSValue.FromBoolean(success);
    }
}
