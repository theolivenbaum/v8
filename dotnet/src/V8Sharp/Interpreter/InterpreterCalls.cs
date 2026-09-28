// Port of the call and construct paths the bytecode handlers use:
// InterpreterAssembler::CallJSAndDispatch / Construct / ConstructWithSpread /
// ConstructForwardAllArgs, the InterpreterPushArgsThenCall/Construct builtins
// (src/builtins/x64/builtins-x64.cc), Builtins::Call/CallFunction/
// ConstructFunction semantics (receiver conversion, class constructor checks,
// JSConstructStubGeneric result selection), CallWithSpread
// (builtins-call-gen.cc), and the call/construct feedback of
// src/builtins/ic-callable.tq.
//
// Calls from bytecode to bytecode stay inside the interpreter (no
// Execution.Call, no argument copies beyond the callee's parameter slots);
// calls to C# builtins pass a span over the caller's registers and push a
// builtin frame record so that builtins show up in stack traces.
using System.Runtime.CompilerServices;
using V8Sharp.Runtime;

namespace V8Sharp.Interpreter;

public static class InterpreterCalls
{
    /// <summary>CallCountField is shifted by the speculation mode and feedback content bits.</summary>
    const int kCallCountIncrement = 1 << FeedbackNexus.kCallCountShift;

    // ---- Feedback -------------------------------------------------------------------

    /// <summary>CollectCallFeedback (ic-callable.tq), without the Function.prototype.apply receiver case.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void CollectCallFeedback(Isolate isolate, FeedbackVector? fv, int slot, JSValue target) =>
        CollectCallFeedback(isolate, fv, slot, target, JSValue.Undefined);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void CollectCallFeedback(Isolate isolate, FeedbackVector? fv, int slot, JSValue target, JSValue receiver)
    {
        if (fv is null) return;
        JSValue[] slots = fv.Slots;
        // IncrementCallCount.
        ref JSValue count = ref slots[slot + 1];
        count = JSValue.FromNumber(count._num + kCallCountIncrement);
        // IsMonomorphic.
        if (ReferenceEquals(slots[slot]._obj, target._obj)) return;
        CollectCallFeedbackSlow(isolate, fv, slot, target, receiver);
    }

    static bool IsPrototypeApplyFunction(Isolate isolate, JSValue target) =>
        isolate.Context is { } context && ReferenceEquals(target.HeapObjectOrNull, context.NativeContext.FunctionPrototypeApply);

    static bool FeedbackValueIsReceiver(FeedbackVector fv, int slot) =>
        ((((int)fv.Slots[slot + 1].Number) >> FeedbackNexus.kCallFeedbackContentShift) & 1) ==
        (int)CallFeedbackContent.kReceiver;

    static void SetCallFeedbackContent(FeedbackVector fv, int slot, CallFeedbackContent content)
    {
        ref JSValue extra = ref fv.Slots[slot + 1];
        int value = (int)extra.Number;
        value = (value & ~(1 << FeedbackNexus.kCallFeedbackContentShift)) | ((int)content << FeedbackNexus.kCallFeedbackContentShift);
        extra = JSValue.FromInt(value);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static void CollectCallFeedbackSlow(Isolate isolate, FeedbackVector fv, int slot, JSValue target, JSValue receiver)
    {
        ref JSValue feedback = ref fv.Slots[slot];
        HeapObject? feedbackObject = feedback.HeapObjectOrNull;
        if (ReferenceEquals(feedbackObject, ReadOnlyRoots.megamorphic_symbol)) return;
        bool uninitialized = ReferenceEquals(feedbackObject, ReadOnlyRoots.uninitialized_symbol);
        if (uninitialized || FeedbackVector.IsCleared(feedback))
        {
            // If cleared, we have a new chance to become monomorphic.
            if (!uninitialized) SetCallFeedbackContent(fv, slot, CallFeedbackContent.kTarget);
            JSValue recordedFunction = target;
            if (IsPrototypeApplyFunction(isolate, target))
            {
                recordedFunction = receiver;
                SetCallFeedbackContent(fv, slot, CallFeedbackContent.kReceiver);
            }
            TryInitializeAsMonomorphic(isolate, fv, slot, recordedFunction);
            return;
        }
        if (FeedbackValueIsReceiver(fv, slot) && IsPrototypeApplyFunction(isolate, target))
        {
            // If the Receiver is recorded and the target is
            // Function.prototype.apply, check whether we can stay monomorphic based
            // on the receiver.
            if (ReferenceEquals(feedbackObject, receiver.HeapObjectOrNull)) return;
            // If not, reinitialize the feedback with target.
            SetCallFeedbackContent(fv, slot, CallFeedbackContent.kTarget);
            TryInitializeAsMonomorphic(isolate, fv, slot, target);
            return;
        }
        // Try transitioning to a feedback cell.
        // Check if {target}s feedback cell matches the {feedbackValue}.
        if (target.HeapObjectOrNull is JSFunction targetFunction)
        {
            FeedbackCell targetFeedbackCell = targetFunction.RawFeedbackCell;
            if (ReferenceEquals(feedbackObject, targetFeedbackCell)) return;
            // Check if {target} and {feedbackValue} are both JSFunctions with
            // the same feedback vector cell.
            if (feedbackObject is JSFunction feedbackFunction &&
                ReferenceEquals(feedbackFunction.RawFeedbackCell, targetFeedbackCell) &&
                !ReferenceEquals(targetFeedbackCell, FeedbackCell.ManyClosuresCell))
            {
                feedback = targetFeedbackCell;
                return;
            }
        }
        feedback = FeedbackVector.MegamorphicSentinel;
    }

    /// <summary>TryInitializeAsMonomorphic (ic-callable.tq).</summary>
    static void TryInitializeAsMonomorphic(Isolate isolate, FeedbackVector fv, int slot, JSValue target)
    {
        HeapObject? targetObject = target.HeapObjectOrNull;
        HeapObject? unwrapped = targetObject;
        while (unwrapped is JSBoundFunction bound) unwrapped = bound.BoundTargetFunction;
        if (unwrapped is JSFunction function && ReferenceEquals(function.Context.NativeContext, isolate.Context?.NativeContext))
        {
            fv.Slots[slot] = target;
        }
        else
        {
            fv.Slots[slot] = FeedbackVector.MegamorphicSentinel;
        }
    }

    /// <summary>CollectConstructFeedback (ic-callable.tq); returns the AllocationSite for Array construction.</summary>
    internal static AllocationSite? CollectConstructFeedback(Isolate isolate, FeedbackVector? fv, int slot, JSValue target, JSValue newTarget)
    {
        if (fv is null) return null;
        JSValue[] slots = fv.Slots;
        ref JSValue count = ref slots[slot + 1];
        count = JSValue.FromNumber(count._num + kCallCountIncrement);
        ref JSValue feedback = ref slots[slot];
        HeapObject? feedbackObject = feedback.HeapObjectOrNull;
        if (ReferenceEquals(feedbackObject, newTarget.HeapObjectOrNull)) return null;
        if (ReferenceEquals(feedbackObject, ReadOnlyRoots.megamorphic_symbol)) return null;
        bool bothArrayFunction = target.IsIdenticalTo(newTarget) &&
                                 ReferenceEquals(target.HeapObjectOrNull, isolate.Context?.NativeContext.ArrayFunction);
        if (feedbackObject is AllocationSite site)
        {
            if (bothArrayFunction) return site;
            feedback = FeedbackVector.MegamorphicSentinel;
            return null;
        }
        if (ReferenceEquals(feedbackObject, ReadOnlyRoots.uninitialized_symbol) || FeedbackVector.IsCleared(feedback))
        {
            if (bothArrayFunction)
            {
                var allocationSite = new AllocationSite();
                feedback = allocationSite;
                return allocationSite;
            }
            TryInitializeAsMonomorphic(isolate, fv, slot, newTarget);
            return null;
        }
        feedback = FeedbackVector.MegamorphicSentinel;
        return null;
    }

    // ---- Calls ------------------------------------------------------------------------

    /// <summary>
    /// A call with the arguments in the register stack at <paramref name="argsStart"/>
    /// (InterpreterPushArgsThenCall + Builtins::Call).
    /// </summary>
    public static JSValue Call(Isolate isolate, JSValue callee, JSValue receiver, int argsStart, int argc, ConvertReceiverMode mode)
    {
        if (callee.HeapObjectOrNull is JSFunction function)
        {
            SharedFunctionInfo shared = function.Shared;
            if (shared.FunctionData is BytecodeArray && !shared.HasBuiltinId)
            {
                if (shared.IsClassConstructor) return RuntimeClasses.ThrowConstructorNonCallableError(isolate, function);
                if (!shared.Native && shared.LanguageMode == V8Sharp.Common.LanguageMode.Sloppy && !receiver.IsJSReceiver)
                {
                    receiver = ConvertReceiver(isolate, function, receiver);
                }
                return InterpreterExecution.InvokeFromRegisters(isolate, function, receiver, argsStart, argc, JSValue.Undefined, false);
            }
        }
        return CallGeneric(isolate, callee, receiver, isolate.RegisterStack.AsSpan(argsStart, argc));
    }

    /// <summary>
    /// A call with two arguments that may not be adjacent registers
    /// (CallProperty2 / CallUndefinedReceiver2).
    /// </summary>
    public static JSValue Call2(Isolate isolate, JSValue callee, JSValue receiver, JSValue arg0, JSValue arg1, int arg0Start,
        bool adjacent, ConvertReceiverMode mode)
    {
        if (adjacent) return Call(isolate, callee, receiver, arg0Start, 2, mode);
        Span<JSValue> args = [arg0, arg1];
        if (callee.HeapObjectOrNull is JSFunction function)
        {
            SharedFunctionInfo shared = function.Shared;
            if (shared.FunctionData is BytecodeArray && !shared.HasBuiltinId)
            {
                if (shared.IsClassConstructor) return RuntimeClasses.ThrowConstructorNonCallableError(isolate, function);
                if (!shared.Native && shared.LanguageMode == V8Sharp.Common.LanguageMode.Sloppy && !receiver.IsJSReceiver)
                {
                    receiver = ConvertReceiver(isolate, function, receiver);
                }
                return InterpreterExecution.Invoke(isolate, function, receiver, args, JSValue.Undefined, false);
            }
        }
        return CallGeneric(isolate, callee, receiver, args);
    }

    /// <summary>ConvertReceiver for a sloppy function: undefined/null become the global proxy, primitives are wrapped.</summary>
    public static JSValue ConvertReceiver(Isolate isolate, JSFunction function, JSValue receiver)
    {
        if (receiver.IsJSReceiver) return receiver;
        NativeContext nativeContext = function.Context.NativeContext;
        if (receiver.IsNullOrUndefined) return nativeContext.GlobalProxyObject;
        return ObjectOps.ToObjectImpl(isolate, receiver, nativeContext);
    }

    /// <summary>Builtins::Call for everything but bytecode functions (builtins, bound functions, proxies ...).</summary>
    public static JSValue CallGeneric(Isolate isolate, JSValue callee, JSValue receiver, ReadOnlySpan<JSValue> args)
    {
        if (callee.HeapObjectOrNull is JSFunction function)
        {
            SharedFunctionInfo shared = function.Shared;
            if (shared.HasBuiltinId && shared.BuiltinId != Builtin.CompileLazy)
            {
                return CallBuiltin(isolate, function, receiver, args, JSValue.Undefined);
            }
            if (!shared.IsCompiled && !shared.HasBuiltinId)
            {
                // CompileLazy, then call through the bytecode path.
                Codegen.Compiler.CompileLazyOrThrow(isolate, function);
                if (shared.IsClassConstructor) return RuntimeClasses.ThrowConstructorNonCallableError(isolate, function);
                if (!shared.Native && shared.LanguageMode == V8Sharp.Common.LanguageMode.Sloppy && !receiver.IsJSReceiver)
                {
                    receiver = ConvertReceiver(isolate, function, receiver);
                }
                return InterpreterExecution.Invoke(isolate, function, receiver, args, JSValue.Undefined, false);
            }
        }
        return Execution.Call(isolate, callee, receiver, args);
    }

    /// <summary>
    /// Calls a builtin function with a builtin frame record (V8's BuiltinExitFrame),
    /// in the builtin's context.
    /// </summary>
    public static JSValue CallBuiltin(Isolate isolate, JSFunction function, JSValue receiver, ReadOnlySpan<JSValue> args,
        JSValue newTarget)
    {
        int depth = isolate.InterpreterFrameDepth;
        ref InterpreterFrameRecord frame = ref isolate.PushFrame();
        frame.Function = function;
        frame.Bytecode = null;
        frame.Receiver = receiver;
        frame.Fp = 0;
        frame.Pc = 0;
        frame.Argc = args.Length;
        frame.Kind = InterpreterFrameKind.Builtin;
        frame.IsConstructor = !newTarget.IsUndefined;
        Context? saved = isolate.Context;
        isolate.Context = function.Context;
        JSValue result = BuiltinRegistry.Invoke(isolate, function.Shared.BuiltinId, function, newTarget, receiver, args);
        isolate.Context = saved;
        isolate.InterpreterFrameDepth = depth;
        isolate.InterpreterFrames[depth] = default;
        return result;
    }

    /// <summary>CallWithSpread: the last argument is spread (builtins-call-gen.cc CallOrConstructWithSpread).</summary>
    public static JSValue CallWithSpread(Isolate isolate, JSValue callee, ReadOnlySpan<JSValue> receiverAndArgs)
    {
        JSValue receiver = receiverAndArgs[0];
        ReadOnlySpan<JSValue> args = receiverAndArgs[1..];
        JSValue[] spread = SpreadArguments(isolate, args);
        if (callee.HeapObjectOrNull is JSFunction function)
        {
            SharedFunctionInfo shared = function.Shared;
            if (shared.FunctionData is BytecodeArray && !shared.HasBuiltinId)
            {
                if (shared.IsClassConstructor) return RuntimeClasses.ThrowConstructorNonCallableError(isolate, function);
                if (!shared.Native && shared.LanguageMode == V8Sharp.Common.LanguageMode.Sloppy && !receiver.IsJSReceiver)
                {
                    receiver = ConvertReceiver(isolate, function, receiver);
                }
                return InterpreterExecution.Invoke(isolate, function, receiver, spread, JSValue.Undefined, false);
            }
        }
        return CallGeneric(isolate, callee, receiver, spread);
    }

    /// <summary>
    /// The arguments with the last one spread: CallOrConstructWithSpread takes
    /// the elements of a fast JSArray directly when the array iterator is
    /// untouched, else iterates (IterableToList).
    /// </summary>
    public static JSValue[] SpreadArguments(Isolate isolate, ReadOnlySpan<JSValue> args)
    {
        int fixedCount = args.Length - 1;
        JSValue spreadValue = args[fixedCount];
        FixedArray list = InterpreterIterators.IterableToList(isolate, spreadValue);
        int total = fixedCount + list.Length;
        if (total > InterpreterConstants.kMaxArguments)
        {
            isolate.ThrowRangeError(MessageTemplate.TooManyArguments);
        }
        var result = new JSValue[total];
        args[..fixedCount].CopyTo(result);
        list.Data.AsSpan(0, list.Length).CopyTo(result.AsSpan(fixedCount));
        return result;
    }

    // ---- Construct --------------------------------------------------------------------

    /// <summary>Construct (InterpreterAssembler::Construct): new with the arguments in registers.</summary>
    public static JSValue Construct(Isolate isolate, FeedbackVector? fv, int slot, JSValue constructor, JSValue newTarget,
        int argsStart, int argc)
    {
        AllocationSite? site = CollectConstructFeedback(isolate, fv, slot, constructor, newTarget);
        if (constructor.HeapObjectOrNull is JSFunction function && function.Map.IsConstructor)
        {
            SharedFunctionInfo shared = function.Shared;
            if (shared.FunctionData is BytecodeArray && !shared.HasBuiltinId)
            {
                return ConstructInterpreted(isolate, function, newTarget, argsStart, argc, default);
            }
        }
        return ConstructGeneric(isolate, constructor, newTarget, isolate.RegisterStack.AsSpan(argsStart, argc), site);
    }

    /// <summary>
    /// JSConstructStubGeneric for a bytecode function: allocates the receiver
    /// for base constructors, runs the bytecode, and picks the result.
    /// </summary>
    /// <summary>The stack slots of V8's construct stub frame, measured against --jitless V8.</summary>
    internal const int kConstructStubFrameSlots = 16;

    static JSValue ConstructInterpreted(Isolate isolate, JSFunction function, JSValue newTarget, int argsStart, int argc,
        ReadOnlySpan<JSValue> spanArgs, bool useSpan = false)
    {
        JSValue implicitReceiver;
        if (!Globals.IsDerivedConstructor(function.Shared.Kind))
        {
            // If not derived class constructor: Allocate the new receiver object.
            Context? saved = isolate.Context;
            isolate.Context = function.Context;
            try
            {
                implicitReceiver = JSObject.New(isolate, function, newTarget.As<JSReceiver>(), null);
            }
            finally
            {
                isolate.Context = saved;
            }
        }
        else
        {
            // Else: use TdzHoleValue as receiver for constructor call
            implicitReceiver = JSValue.TheHole;
        }

        // The construct stub's frame (JSConstructStubGeneric: its fixed slots and
        // the copied arguments) takes stack space in V8; reserving the same on the
        // register stack keeps the recursion depth at which `new` overflows close
        // to V8's.
        int stubStart = isolate.AllocateRegisters(kConstructStubFrameSlots);
        JSValue result = useSpan
            ? InterpreterExecution.Invoke(isolate, function, implicitReceiver, spanArgs, newTarget, true)
            : InterpreterExecution.InvokeFromRegisters(isolate, function, implicitReceiver, argsStart, argc, newTarget, true);
        isolate.RegisterStackTop = stubStart;

        // If the result is an object (in the ECMA sense), we should get rid
        // of the receiver and use the result; see ECMA-262 section 13.2.2-7
        // on page 74.
        if (result.IsJSReceiver) return result;

        // Throw away the result of the constructor invocation and use the
        // on-stack receiver as the result.
        if (implicitReceiver.IsTheHole) return isolate.ThrowTypeError(MessageTemplate.DerivedConstructorReturnedNonObject);
        return implicitReceiver;
    }

    /// <summary>Builtins::Construct for everything but bytecode functions.</summary>
    public static JSValue ConstructGeneric(Isolate isolate, JSValue constructor, JSValue newTarget, ReadOnlySpan<JSValue> args,
        AllocationSite? site = null)
    {
        if (constructor.HeapObjectOrNull is JSFunction function && function.Map.IsConstructor)
        {
            SharedFunctionInfo shared = function.Shared;
            if (shared.HasBuiltinId && shared.BuiltinId != Builtin.CompileLazy)
            {
                // JSBuiltinsConstructStub: the builtin creates its own receiver.
                return CallBuiltin(isolate, function, JSValue.TheHole, args, newTarget);
            }
            if (!shared.IsCompiled && !shared.HasBuiltinId)
            {
                Codegen.Compiler.CompileLazyOrThrow(isolate, function);
                return ConstructInterpreted(isolate, function, newTarget, 0, 0, args, useSpan: true);
            }
        }
        return Execution.New(isolate, constructor, newTarget, args);
    }

    /// <summary>ConstructWithSpread.</summary>
    public static JSValue ConstructWithSpread(Isolate isolate, FeedbackVector? fv, int slot, JSValue constructor, JSValue newTarget,
        ReadOnlySpan<JSValue> args)
    {
        CollectConstructWithSpreadFeedback(isolate, fv, slot, newTarget);
        JSValue[] spread = SpreadArguments(isolate, args);
        if (constructor.HeapObjectOrNull is JSFunction function && function.Map.IsConstructor)
        {
            SharedFunctionInfo shared = function.Shared;
            if (shared.FunctionData is BytecodeArray && !shared.HasBuiltinId)
            {
                return ConstructInterpreted(isolate, function, newTarget, 0, 0, spread, useSpan: true);
            }
        }
        return ConstructGeneric(isolate, constructor, newTarget, spread);
    }

    /// <summary>The feedback part of InterpreterAssembler::ConstructWithSpread.</summary>
    static void CollectConstructWithSpreadFeedback(Isolate isolate, FeedbackVector? fv, int slot, JSValue newTarget)
    {
        if (fv is null) return;
        JSValue[] slots = fv.Slots;
        ref JSValue count = ref slots[slot + 1];
        count = JSValue.FromNumber(count._num + kCallCountIncrement);
        ref JSValue feedback = ref slots[slot];
        if (ReferenceEquals(feedback.HeapObjectOrNull, newTarget.HeapObjectOrNull)) return;
        if (ReferenceEquals(feedback.HeapObjectOrNull, ReadOnlyRoots.megamorphic_symbol)) return;
        if (ReferenceEquals(feedback.HeapObjectOrNull, ReadOnlyRoots.uninitialized_symbol) || FeedbackVector.IsCleared(feedback))
        {
            TryInitializeAsMonomorphic(isolate, fv, slot, newTarget);
            return;
        }
        feedback = FeedbackVector.MegamorphicSentinel;
    }

    /// <summary>ConstructForwardAllArgs: new with all arguments of the current frame.</summary>
    public static JSValue ConstructForwardAllArgs(Isolate isolate, FeedbackVector? fv, int slot, JSValue constructor,
        JSValue newTarget, int fp, int argc)
    {
        CollectConstructFeedback(isolate, fv, slot, constructor, newTarget);
        JSValue[] args = InterpreterRuntime.GetFrameArguments(isolate, fp, argc);
        if (constructor.HeapObjectOrNull is JSFunction function && function.Map.IsConstructor)
        {
            SharedFunctionInfo shared = function.Shared;
            if (shared.FunctionData is BytecodeArray && !shared.HasBuiltinId)
            {
                return ConstructInterpreted(isolate, function, newTarget, 0, 0, args, useSpan: true);
            }
        }
        return ConstructGeneric(isolate, constructor, newTarget, args);
    }
}
