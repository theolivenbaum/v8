// Port of src/execution/execution.{h,cc} together with the semantics of the
// Call/Construct builtins (Builtins::Call, CallFunction, CallBoundFunction,
// Construct, ConstructFunction, ConstructBoundFunction; builtins-<arch>.cc and
// builtins-call-gen.cc), which V8 implements in generated code.
using System.Runtime.CompilerServices;
using V8Sharp.Builtins;
using V8Sharp.Common;
using V8Sharp.Objects;
using V8Sharp.Roots;

namespace V8Sharp;

/// <summary>V8's Execution: calling and constructing JavaScript values from engine code.</summary>
public static class Execution
{
    /// <summary>
    /// Execution::Call: [[Call]] of <paramref name="callable"/> with
    /// <paramref name="receiver"/> and <paramref name="args"/>. Throws
    /// TypeError "x is not a function" for non-callables.
    /// </summary>
    public static JSValue Call(Isolate isolate, JSValue callable, JSValue receiver, ReadOnlySpan<JSValue> args)
    {
        isolate.StackGuard.StackCheck(isolate);
        switch (callable.HeapObjectOrNull)
        {
            case JSFunction function:
                return CallFunction(isolate, function, receiver, args);
            case JSBoundFunction bound:
                return CallBoundFunction(isolate, bound, args);
            case JSProxy proxy:
                return JSProxy.Call(isolate, proxy, receiver, args);
            case JSWrappedFunction wrapped:
                return JSWrappedFunction.Call(isolate, wrapped, receiver, args);
            default:
                return isolate.Throw(ErrorUtils.NewCalledNonCallableError(isolate, callable));
        }
    }

    /// <summary>
    /// Builtins::CallFunction: class constructors throw; sloppy non-native
    /// functions get their receiver converted (ConvertReceiverMode::kAny).
    /// </summary>
    public static JSValue CallFunction(Isolate isolate, JSFunction function, JSValue receiver, ReadOnlySpan<JSValue> args)
    {
        SharedFunctionInfo shared = function.Shared;
        if (Globals.IsClassConstructor(shared.Kind))
        {
            return isolate.ThrowTypeError(MessageTemplate.ConstructorNonCallable, shared.Name());
        }

        // Enter the context of the function; ToObject has to run in the
        // function context, which is why we don't use the current context.
        Context context = function.Context;
        if (!shared.Native && shared.LanguageMode == LanguageMode.Sloppy)
        {
            receiver = ConvertReceiver(isolate, context.NativeContext, receiver);
        }

        Context? saved = isolate.Context;
        isolate.Context = context;
        try
        {
            return InvokeFunctionCode(isolate, function, receiver, args, JSValue.Undefined);
        }
        finally
        {
            isolate.Context = saved;
        }
    }

    /// <summary>ConvertReceiver: undefined/null become the global proxy, primitives are wrapped.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static JSValue ConvertReceiver(Isolate isolate, NativeContext nativeContext, JSValue receiver)
    {
        if (receiver.IsJSReceiver) return receiver;
        if (receiver.IsNullOrUndefined) return nativeContext.GlobalProxyObject;
        return ObjectOps.ToObjectImpl(isolate, receiver, nativeContext);
    }

    /// <summary>Runs the code of a function: its builtin, or its bytecode (compiling it lazily first).</summary>
    static JSValue InvokeFunctionCode(Isolate isolate, JSFunction function, JSValue receiver, ReadOnlySpan<JSValue> args,
        JSValue newTarget)
    {
        SharedFunctionInfo shared = function.Shared;
        if (shared.HasBuiltinId && shared.BuiltinId != Builtin.CompileLazy)
        {
            return BuiltinRegistry.Invoke(isolate, shared.BuiltinId, function, newTarget, receiver, args);
        }
        if (!shared.IsCompiled)
        {
            // Runtime_CompileLazy with KEEP_EXCEPTION: a lazy SyntaxError propagates.
            CompileLazyDelegate? compile = isolate.CompileLazyHook;
            if (compile is null || !compile(isolate, function))
            {
                throw new InvalidOperationException("V8Sharp: no compiler registered (Isolate.CompileLazyHook)");
            }
        }
        InterpreterEntryDelegate? entry = isolate.InterpreterEntry;
        if (entry is null) throw new InvalidOperationException("V8Sharp: no interpreter registered (Isolate.InterpreterEntry)");
        return entry(isolate, function, receiver, args, newTarget);
    }

    /// <summary>Builtins::CallBoundFunction: prepends the bound arguments and calls the target.</summary>
    public static JSValue CallBoundFunction(Isolate isolate, JSBoundFunction function, ReadOnlySpan<JSValue> args)
    {
        FixedArray boundArgs = function.BoundArguments;
        if (boundArgs.Length == 0) return Call(isolate, function.BoundTargetFunction, function.BoundThis, args);
        JSValue[] all = new JSValue[boundArgs.Length + args.Length];
        boundArgs.Data.AsSpan().CopyTo(all);
        args.CopyTo(all.AsSpan(boundArgs.Length));
        return Call(isolate, function.BoundTargetFunction, function.BoundThis, all);
    }

    /// <summary>Execution::New: [[Construct]] with new.target equal to the constructor.</summary>
    public static JSValue New(Isolate isolate, JSValue constructor, ReadOnlySpan<JSValue> args) =>
        New(isolate, constructor, constructor, args);

    /// <summary>
    /// Execution::New / Builtins::Construct: [[Construct]] of
    /// <paramref name="constructor"/> with <paramref name="newTarget"/>.
    /// Throws TypeError "x is not a constructor" for non-constructors.
    /// </summary>
    public static JSValue New(Isolate isolate, JSValue constructor, JSValue newTarget, ReadOnlySpan<JSValue> args)
    {
        isolate.StackGuard.StackCheck(isolate);
        if (constructor.HeapObjectOrNull is JSReceiver receiver && receiver.Map.IsConstructor)
        {
            switch (receiver)
            {
                case JSFunction function:
                    return ConstructFunction(isolate, function, newTarget, args);
                case JSBoundFunction bound:
                    return ConstructBoundFunction(isolate, bound, newTarget, args);
                case JSProxy proxy:
                    return JSProxy.Construct(isolate, proxy, args, newTarget);
            }
        }
        return isolate.Throw(ErrorUtils.NewConstructedNonConstructable(isolate, constructor));
    }

    /// <summary>
    /// Builtins::ConstructFunction: builtins construct themselves
    /// (JSBuiltinsConstructStub); other functions go through
    /// JSConstructStubGeneric, which allocates the receiver for base
    /// constructors and picks the result.
    /// </summary>
    public static JSValue ConstructFunction(Isolate isolate, JSFunction function, JSValue newTarget, ReadOnlySpan<JSValue> args)
    {
        SharedFunctionInfo shared = function.Shared;
        Context? saved = isolate.Context;
        isolate.Context = function.Context;
        try
        {
            if (shared.HasBuiltinId && shared.BuiltinId != Builtin.CompileLazy)
            {
                // JSBuiltinsConstructStub: the builtin creates its own receiver.
                return BuiltinRegistry.Invoke(isolate, shared.BuiltinId, function, newTarget, JSValue.TheHole, args);
            }

            // JSConstructStubGeneric.
            JSValue implicitReceiver;
            if (!Globals.IsDerivedConstructor(shared.Kind))
            {
                // If not derived class constructor: Allocate the new receiver object.
                implicitReceiver = JSObject.New(isolate, function, newTarget.As<JSReceiver>(), null);
            }
            else
            {
                // Else: use TdzHoleValue as receiver for constructor call
                implicitReceiver = JSValue.TheHole;
            }

            JSValue result = InvokeFunctionCode(isolate, function, implicitReceiver, args, newTarget);

            // If the result is an object (in the ECMA sense), we should get rid
            // of the receiver and use the result; see ECMA-262 section 13.2.2-7
            // on page 74.
            if (result.IsJSReceiver) return result;

            // Throw away the result of the constructor invocation and use the
            // on-stack receiver as the result.
            if (implicitReceiver.IsTheHole)
            {
                return isolate.ThrowTypeError(MessageTemplate.DerivedConstructorReturnedNonObject);
            }
            return implicitReceiver;
        }
        finally
        {
            isolate.Context = saved;
        }
    }

    /// <summary>Builtins::ConstructBoundFunction.</summary>
    public static JSValue ConstructBoundFunction(Isolate isolate, JSBoundFunction function, JSValue newTarget, ReadOnlySpan<JSValue> args)
    {
        // Patch new.target to [[BoundTargetFunction]] if new.target equals target.
        if (ReferenceEquals(newTarget.HeapObjectOrNull, function)) newTarget = function.BoundTargetFunction;
        FixedArray boundArgs = function.BoundArguments;
        if (boundArgs.Length == 0) return New(isolate, function.BoundTargetFunction, newTarget, args);
        JSValue[] all = new JSValue[boundArgs.Length + args.Length];
        boundArgs.Data.AsSpan().CopyTo(all);
        args.CopyTo(all.AsSpan(boundArgs.Length));
        return New(isolate, function.BoundTargetFunction, newTarget, all);
    }

    /// <summary>
    /// Execution::TryCall: like Call, but a JavaScript exception is returned in
    /// <paramref name="exception"/> instead of propagating. Termination still propagates.
    /// </summary>
    public static bool TryCall(Isolate isolate, JSValue callable, JSValue receiver, ReadOnlySpan<JSValue> args,
        out JSValue result, out JavaScriptException? exception)
    {
        try
        {
            result = Call(isolate, callable, receiver, args);
            exception = null;
            return true;
        }
        catch (JavaScriptException e)
        {
            result = JSValue.Undefined;
            exception = e;
            return false;
        }
    }

    /// <summary>Execution::CallBuiltin: calls a builtin function object directly.</summary>
    public static JSValue CallBuiltin(Isolate isolate, JSFunction builtin, JSValue receiver, ReadOnlySpan<JSValue> args) =>
        Call(isolate, builtin, receiver, args);

    /// <summary>Runs pending microtasks of the default queue (v8::MicrotasksScope::PerformCheckpoint).</summary>
    public static void PerformMicrotaskCheckpoint(Isolate isolate) => isolate.DefaultMicrotaskQueue.PerformCheckpoint(isolate);
}
