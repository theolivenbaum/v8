// Calls from Maglev code: the Call_ReceiverIs* / Construct builtins
// (src/builtins/builtins-call-gen.cc, Generate_CallFunction,
// JSConstructStubGeneric) followed by the callee's Maglev prologue
// (maglev-code-generator.cc: frame setup, stack check), for a callee whose
// feedback vector has Maglev code. The frame is the callee's interpreter
// frame, as InterpreterExecution.EnterFrame builds it (architecture.md
// section 9.2); a deopt continues it in the interpreter. Every other callee
// takes the baseline/interpreter call path.
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using V8Sharp.Interpreter;
using V8Sharp.Runtime;

namespace V8Sharp.Maglev;

public static class MaglevCalls
{
    /// <summary>
    /// Calls <paramref name="callee"/> with the arguments in the register stack at
    /// <paramref name="argsStart"/>.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.AggressiveOptimization)]
    public static JSValue Call(Isolate isolate, JSValue callee, JSValue receiver, int argsStart, int argc, ConvertReceiverMode mode)
    {
        if (AsJSFunction(callee) is { } function)
        {
            if (function.RawFeedbackCell.Value is FeedbackVector { MaglevCode: { } code } vector)
            {
                SharedFunctionInfo shared = function.Shared;
                if (!shared.IsClassConstructor)
                {
                    if (!receiver.IsJSReceiver && !shared.Native && shared.LanguageMode == LanguageMode.Sloppy)
                    {
                        receiver = InterpreterCalls.ConvertReceiver(isolate, function, receiver);
                    }
                    if (argc <= code.FastCallArity) return InvokeFastCall(isolate, code, function, receiver, argsStart, argc, argc);
                    return EnterFrame(isolate, function, code, vector, receiver, argsStart, argc, JSValue.Undefined, false);
                }
            }
            else if (function.Shared.BuiltinId != Builtins.Builtin.NoBuiltinId && argc <= 2 &&
                     TryBuiltinFastPath(isolate, callee, receiver, argsStart, argc, out JSValue result))
            {
                // The builtin's fast path on entry (BuiltinFastPaths, as the
                // interpreter's call handlers take it).
                return result;
            }
        }
        // Baseline code and interpreted functions are entered directly (V8's
        // Call builtin jumps to the closure's code, whatever its tier).
        return Baseline.BaselineCalls.CallFromOptimizedCode(isolate, callee, receiver, argsStart, argc, mode);
    }

    // ---- Generic calls with the arguments as values ------------------------------------------------------
    //
    // The Call builtin's dispatch (Generate_CallFunction) for a call whose
    // target is not known at compile time, taking the receiver and up to three
    // arguments as values: a callee with Maglev code is entered through its
    // direct entry (the arguments never go through the register stack),
    // anything else through the register window CallValuesN builds.

    [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.AggressiveOptimization)]
    public static JSValue CallWithValues0(Isolate isolate, JSValue callee, JSValue receiver, int mode)
    {
        if (TryGetFastCallee(isolate, callee, ref receiver, 0, out JSFunction function, out MaglevCode code))
        {
            Delegate fast = code.FastCall!;
            return code.FastCallArity switch
            {
                0 => Unsafe.As<MaglevFastCall0>(fast)(isolate, function, 0, receiver),
                1 => Unsafe.As<MaglevFastCall1>(fast)(isolate, function, 0, receiver, default),
                2 => Unsafe.As<MaglevFastCall2>(fast)(isolate, function, 0, receiver, default, default),
                3 => Unsafe.As<MaglevFastCall3>(fast)(isolate, function, 0, receiver, default, default, default),
                _ => InvokeFastCallValues(isolate, code, function, receiver, 0, default, default, default),
            };
        }
        // A builtin's fast path on entry (BuiltinFastPaths, as MaglevCalls.Call).
        if (IsBuiltin(callee) && Builtins.BuiltinFastPaths.TryCall0(isolate, callee, receiver, out JSValue builtinResult)) return builtinResult;
        return CallValues0(isolate, callee, receiver, mode);
    }

    [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.AggressiveOptimization)]
    public static JSValue CallWithValues1(Isolate isolate, JSValue callee, JSValue receiver, JSValue a0, int mode)
    {
        if (TryGetFastCallee(isolate, callee, ref receiver, 1, out JSFunction function, out MaglevCode code))
        {
            Delegate fast = code.FastCall!;
            return code.FastCallArity switch
            {
                1 => Unsafe.As<MaglevFastCall1>(fast)(isolate, function, 1, receiver, a0),
                2 => Unsafe.As<MaglevFastCall2>(fast)(isolate, function, 1, receiver, a0, default),
                3 => Unsafe.As<MaglevFastCall3>(fast)(isolate, function, 1, receiver, a0, default, default),
                _ => InvokeFastCallValues(isolate, code, function, receiver, 1, a0, default, default),
            };
        }
        // A builtin's fast path on entry (BuiltinFastPaths, as MaglevCalls.Call).
        if (IsBuiltin(callee) && Builtins.BuiltinFastPaths.TryCall1(isolate, callee, receiver, a0, out JSValue builtinResult)) return builtinResult;
        return CallValues1(isolate, callee, receiver, a0, mode);
    }

    [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.AggressiveOptimization)]
    public static JSValue CallWithValues2(Isolate isolate, JSValue callee, JSValue receiver, JSValue a0, JSValue a1, int mode)
    {
        if (TryGetFastCallee(isolate, callee, ref receiver, 2, out JSFunction function, out MaglevCode code))
        {
            Delegate fast = code.FastCall!;
            return code.FastCallArity switch
            {
                2 => Unsafe.As<MaglevFastCall2>(fast)(isolate, function, 2, receiver, a0, a1),
                3 => Unsafe.As<MaglevFastCall3>(fast)(isolate, function, 2, receiver, a0, a1, default),
                _ => InvokeFastCallValues(isolate, code, function, receiver, 2, a0, a1, default),
            };
        }
        // A builtin's fast path on entry (BuiltinFastPaths, as MaglevCalls.Call).
        if (IsBuiltin(callee) && Builtins.BuiltinFastPaths.TryCall2(isolate, callee, receiver, a0, a1, out JSValue builtinResult)) return builtinResult;
        return CallValues2(isolate, callee, receiver, a0, a1, mode);
    }

    [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.AggressiveOptimization)]
    public static JSValue CallWithValues3(Isolate isolate, JSValue callee, JSValue receiver, JSValue a0, JSValue a1, JSValue a2, int mode)
    {
        if (TryGetFastCallee(isolate, callee, ref receiver, 3, out JSFunction function, out MaglevCode code))
        {
            return InvokeFastCallValues(isolate, code, function, receiver, 3, a0, a1, a2);
        }
        return CallValues3(isolate, callee, receiver, a0, a1, a2, mode);
    }

    [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.AggressiveOptimization)]
    public static JSValue CallWithValues4(Isolate isolate, JSValue callee, JSValue receiver, JSValue a0, JSValue a1, JSValue a2, JSValue a3,
        int mode)
    {
        if (TryGetFastCallee(isolate, callee, ref receiver, 4, out JSFunction function, out MaglevCode code))
        {
            return InvokeFastCallValues(isolate, code, function, receiver, 4, a0, a1, a2, a3, default, default);
        }
        return CallValuesN(isolate, callee, receiver, 4, a0, a1, a2, a3, default, default, mode);
    }

    [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.AggressiveOptimization)]
    public static JSValue CallWithValues5(Isolate isolate, JSValue callee, JSValue receiver, JSValue a0, JSValue a1, JSValue a2, JSValue a3,
        JSValue a4, int mode)
    {
        if (TryGetFastCallee(isolate, callee, ref receiver, 5, out JSFunction function, out MaglevCode code))
        {
            return InvokeFastCallValues(isolate, code, function, receiver, 5, a0, a1, a2, a3, a4, default);
        }
        return CallValuesN(isolate, callee, receiver, 5, a0, a1, a2, a3, a4, default, mode);
    }

    [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.AggressiveOptimization)]
    public static JSValue CallWithValues6(Isolate isolate, JSValue callee, JSValue receiver, JSValue a0, JSValue a1, JSValue a2, JSValue a3,
        JSValue a4, JSValue a5, int mode)
    {
        if (TryGetFastCallee(isolate, callee, ref receiver, 6, out JSFunction function, out MaglevCode code))
        {
            return InvokeFastCallValues(isolate, code, function, receiver, 6, a0, a1, a2, a3, a4, a5);
        }
        return CallValuesN(isolate, callee, receiver, 6, a0, a1, a2, a3, a4, a5, mode);
    }

    /// <summary>The slow path of CallWithValues4..6 (as CallValuesN): a register window above the stack top.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static JSValue CallValuesN(Isolate isolate, JSValue callee, JSValue receiver, int argc, JSValue a0, JSValue a1, JSValue a2,
        JSValue a3, JSValue a4, JSValue a5, int mode)
    {
        int window = isolate.AllocateRegisters(argc);
        StoreWindow(isolate.RegisterStack, window, argc, a0, a1, a2, a3, a4, a5);
        try
        {
            return Call(isolate, callee, receiver, window, argc, (ConvertReceiverMode)mode);
        }
        finally
        {
            isolate.ReleaseRegisters(window);
        }
    }

    static void StoreWindow(JSValue[] stack, int window, int argc, JSValue a0, JSValue a1, JSValue a2, JSValue a3, JSValue a4, JSValue a5)
    {
        if (argc > 0) stack[window] = a0;
        if (argc > 1) stack[window + 1] = a1;
        if (argc > 2) stack[window + 2] = a2;
        if (argc > 3) stack[window + 3] = a3;
        if (argc > 4) stack[window + 4] = a4;
        if (argc > 5) stack[window + 5] = a5;
    }

    /// <summary>
    /// ConstructWithReceiver with the arguments as values (up to three): a
    /// callee with Maglev code takes them in its direct entry, others from a
    /// register window above the stack top.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.AggressiveOptimization)]
    public static JSValue ConstructWithReceiverValues(Isolate isolate, JSValue target, JSValue receiver, JSValue newTarget, int argc,
        JSValue a0, JSValue a1, JSValue a2) =>
        ConstructWithReceiverValues(isolate, target, receiver, newTarget, argc, a0, a1, a2, default, default, default);

    /// <summary>ConstructWithReceiverValues with up to six arguments.</summary>
    [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.AggressiveOptimization)]
    public static JSValue ConstructWithReceiverValues(Isolate isolate, JSValue target, JSValue receiver, JSValue newTarget, int argc,
        JSValue a0, JSValue a1, JSValue a2, JSValue a3, JSValue a4, JSValue a5)
    {
        var function = Unsafe.As<JSFunction>(target._obj!);
        if (function.RawFeedbackCell.Value is FeedbackVector { MaglevCode: { } code } && argc <= code.FastCallArity)
        {
            int stubStart = isolate.AllocateRegisters(InterpreterCalls.kConstructStubFrameSlots);
            // The direct entry (argc's sign bit: a construct, new.target in the isolate).
            isolate.MaglevNewTarget = newTarget;
            JSValue result = InvokeFastCallValues(isolate, code, function, receiver, argc | int.MinValue, a0, a1, a2, a3, a4, a5);
            isolate.RegisterStackTop = stubStart;
            return result.IsJSReceiver ? result : receiver;
        }
        int window = isolate.AllocateRegisters(argc);
        StoreWindow(isolate.RegisterStack, window, argc, a0, a1, a2, a3, a4, a5);
        try
        {
            return ConstructWithReceiver(isolate, target, receiver, newTarget, window, argc);
        }
        finally
        {
            isolate.ReleaseRegisters(window);
        }
    }

    /// <summary>
    /// The callee of a generic call when it has Maglev code whose direct entry
    /// takes <paramref name="argc"/> arguments (CallFunction's checks: not a
    /// class constructor; a sloppy callee's receiver converted).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static bool TryGetFastCallee(Isolate isolate, JSValue callee, ref JSValue receiver, int argc, out JSFunction function,
        out MaglevCode code)
    {
        if (AsJSFunction(callee) is { } f && f.RawFeedbackCell.Value is FeedbackVector { MaglevCode: { } c } && argc <= c.FastCallArity)
        {
            SharedFunctionInfo shared = f.Shared;
            if (!shared.IsClassConstructor)
            {
                if (!receiver.IsJSReceiver && !shared.Native && shared.LanguageMode == LanguageMode.Sloppy)
                {
                    receiver = InterpreterCalls.ConvertReceiver(isolate, f, receiver);
                }
                function = f;
                code = c;
                return true;
            }
        }
        function = null!;
        code = null!;
        return false;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static bool IsBuiltin(JSValue callee) => AsJSFunction(callee) is { } f && f.Shared.BuiltinId != Builtins.Builtin.NoBuiltinId;

    /// <summary>The callee as a JSFunction by its instance type (a type test of the unsealed class is a cast helper call).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static JSFunction? AsJSFunction(JSValue callee) =>
        callee._obj is { } o && InstanceTypeChecks.IsJSFunction(o.InstanceType) ? Unsafe.As<JSFunction>(o) : null;

    /// <summary>A direct entry of any arity called with at most six arguments.</summary>
    static JSValue InvokeFastCallValues(Isolate isolate, MaglevCode code, JSFunction function, JSValue receiver, int argc, JSValue a0,
        JSValue a1, JSValue a2, JSValue a3, JSValue a4, JSValue a5)
    {
        Delegate fast = code.FastCall!;
        return code.FastCallArity switch
        {
            0 => Unsafe.As<MaglevFastCall0>(fast)(isolate, function, argc, receiver),
            1 => Unsafe.As<MaglevFastCall1>(fast)(isolate, function, argc, receiver, a0),
            2 => Unsafe.As<MaglevFastCall2>(fast)(isolate, function, argc, receiver, a0, a1),
            3 => Unsafe.As<MaglevFastCall3>(fast)(isolate, function, argc, receiver, a0, a1, a2),
            4 => Unsafe.As<MaglevFastCall4>(fast)(isolate, function, argc, receiver, a0, a1, a2, a3),
            5 => Unsafe.As<MaglevFastCall5>(fast)(isolate, function, argc, receiver, a0, a1, a2, a3, a4),
            _ => Unsafe.As<MaglevFastCall6>(fast)(isolate, function, argc, receiver, a0, a1, a2, a3, a4, a5),
        };
    }

    /// <summary>A direct entry of any arity called with at most three arguments.</summary>
    static JSValue InvokeFastCallValues(Isolate isolate, MaglevCode code, JSFunction function, JSValue receiver, int argc, JSValue a0,
        JSValue a1, JSValue a2)
    {
        Delegate fast = code.FastCall!;
        return code.FastCallArity switch
        {
            0 => Unsafe.As<MaglevFastCall0>(fast)(isolate, function, argc, receiver),
            1 => Unsafe.As<MaglevFastCall1>(fast)(isolate, function, argc, receiver, a0),
            2 => Unsafe.As<MaglevFastCall2>(fast)(isolate, function, argc, receiver, a0, a1),
            3 => Unsafe.As<MaglevFastCall3>(fast)(isolate, function, argc, receiver, a0, a1, a2),
            4 => Unsafe.As<MaglevFastCall4>(fast)(isolate, function, argc, receiver, a0, a1, a2, default),
            5 => Unsafe.As<MaglevFastCall5>(fast)(isolate, function, argc, receiver, a0, a1, a2, default, default),
            _ => Unsafe.As<MaglevFastCall6>(fast)(isolate, function, argc, receiver, a0, a1, a2, default, default, default),
        };
    }

    /// <summary>A call with the arguments in a span through the callee's direct entry (MaglevCode.FastCall).</summary>
    internal static JSValue InvokeFastCall(Isolate isolate, MaglevCode code, JSFunction function, JSValue receiver, ReadOnlySpan<JSValue> args)
    {
        int argc = args.Length;
        JSValue A(ReadOnlySpan<JSValue> a, int i) => i < a.Length ? a[i] : default;
        Delegate fast = code.FastCall!;
        return code.FastCallArity switch
        {
            0 => Unsafe.As<MaglevFastCall0>(fast)(isolate, function, argc, receiver),
            1 => Unsafe.As<MaglevFastCall1>(fast)(isolate, function, argc, receiver, A(args, 0)),
            2 => Unsafe.As<MaglevFastCall2>(fast)(isolate, function, argc, receiver, A(args, 0), A(args, 1)),
            3 => Unsafe.As<MaglevFastCall3>(fast)(isolate, function, argc, receiver, A(args, 0), A(args, 1), A(args, 2)),
            4 => Unsafe.As<MaglevFastCall4>(fast)(isolate, function, argc, receiver, A(args, 0), A(args, 1), A(args, 2), A(args, 3)),
            5 => Unsafe.As<MaglevFastCall5>(fast)(isolate, function, argc, receiver, A(args, 0), A(args, 1), A(args, 2), A(args, 3),
                A(args, 4)),
            _ => Unsafe.As<MaglevFastCall6>(fast)(isolate, function, argc, receiver, A(args, 0), A(args, 1), A(args, 2), A(args, 3),
                A(args, 4), A(args, 5)),
        };
    }

    /// <summary>A call from a register list through the callee's direct entry (MaglevCode.FastCall).</summary>
    internal static JSValue InvokeFastCall(Isolate isolate, MaglevCode code, JSFunction function, JSValue receiver, int argsStart, int argc,
        int passedArgc)
    {
        JSValue[] stack = isolate.RegisterStack;
        JSValue A(int i) => i < argc ? stack[argsStart + i] : default;
        Delegate fast = code.FastCall!;
        return code.FastCallArity switch
        {
            0 => Unsafe.As<MaglevFastCall0>(fast)(isolate, function, passedArgc, receiver),
            1 => Unsafe.As<MaglevFastCall1>(fast)(isolate, function, passedArgc, receiver, A(0)),
            2 => Unsafe.As<MaglevFastCall2>(fast)(isolate, function, passedArgc, receiver, A(0), A(1)),
            3 => Unsafe.As<MaglevFastCall3>(fast)(isolate, function, passedArgc, receiver, A(0), A(1), A(2)),
            4 => Unsafe.As<MaglevFastCall4>(fast)(isolate, function, passedArgc, receiver, A(0), A(1), A(2), A(3)),
            5 => Unsafe.As<MaglevFastCall5>(fast)(isolate, function, passedArgc, receiver, A(0), A(1), A(2), A(3), A(4)),
            _ => Unsafe.As<MaglevFastCall6>(fast)(isolate, function, passedArgc, receiver, A(0), A(1), A(2), A(3), A(4), A(5)),
        };
    }

    static bool TryBuiltinFastPath(Isolate isolate, JSValue callee, JSValue receiver, int argsStart, int argc, out JSValue result)
    {
        JSValue[] stack = isolate.RegisterStack;
        return argc switch
        {
            0 => Builtins.BuiltinFastPaths.TryCall0(isolate, callee, receiver, out result),
            1 => Builtins.BuiltinFastPaths.TryCall1(isolate, callee, receiver, stack[argsStart], out result),
            _ => Builtins.BuiltinFastPaths.TryCall2(callee, stack[argsStart], stack[argsStart + 1], out result),
        };
    }

    /// <summary>
    /// Construct of a known base constructor whose receiver the code allocated
    /// (FastNewObject): the construct stub's frame, the call, and the result
    /// selection (an object result replaces the receiver).
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.AggressiveOptimization)]
    public static JSValue ConstructWithReceiver(Isolate isolate, JSValue target, JSValue receiver, JSValue newTarget, int argsStart,
        int argc)
    {
        var function = Unsafe.As<JSFunction>(target._obj!);
        int stubStart = isolate.AllocateRegisters(InterpreterCalls.kConstructStubFrameSlots);
        JSValue result;
        if (function.RawFeedbackCell.Value is FeedbackVector { MaglevCode: { } code } vector)
        {
            if (argc <= code.FastCallArity)
            {
                // The direct entry (argc's sign bit: a construct, new.target in the isolate).
                isolate.MaglevNewTarget = newTarget;
                result = InvokeFastCall(isolate, code, function, receiver, argsStart, argc, argc | int.MinValue);
            }
            else
            {
                result = EnterFrame(isolate, function, code, vector, receiver, argsStart, argc, newTarget, true);
            }
        }
        else
        {
            result = InterpreterExecution.InvokeFromRegisters(isolate, function, receiver, argsStart, argc, newTarget, true);
        }
        isolate.RegisterStackTop = stubStart;
        return result.IsJSReceiver ? result : receiver;
    }

    // ---- Direct calls between Maglev code ---------------------------------------------------------------
    //
    // A call of a known JSFunction from Maglev code (CallKnownJSFunction) jumps
    // to the callee's Maglev code directly when it has some: the callee's code
    // type has a second entry, its FastCall method (MaglevCodeGenerator), which
    // takes the receiver and the arguments as values and builds the frame
    // with the callee's constants (parameter and register counts, bytecode,
    // feedback vector) through the helpers below, as V8's Maglev prologue
    // does after the Call builtin's CallFunction. Everything else takes
    // Call above.

    /// <summary>
    /// The first half of a direct call's frame setup: reserves the frame
    /// (parameters, fixed slots, registers) at the register stack top, with
    /// the stack checks of the prologue, and returns its slot at fp.
    /// </summary>
    /// <remarks>
    /// Deviation: the register file is not cleared (V8's prologue fills it
    /// with undefined): Maglev code keeps its values in IL locals and a deopt
    /// writes every live register, so the interpreter never reads a register
    /// of this frame it has not written (bytecode liveness).
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ref JSValue EnterFastFrame(Isolate isolate, int paramSlots, int registerCount, out int start, out int fp, out int depth)
    {
        depth = isolate.InterpreterFrameDepth;
        // The native stack check of the prologue, every 8 frames (as EnterFrame).
        if ((depth & 7) == 0) CheckNativeStack(isolate);
        start = isolate.RegisterStackTop;
        fp = start + paramSlots + InterpreterRuntime.kFixedSlotsAboveParams;
        int end = fp + registerCount;
        if ((uint)end > (uint)isolate.RegisterStackLimit) isolate.StackOverflow();
        isolate.RegisterStackTop = end;
        return ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(isolate.RegisterStack), fp);
    }

    /// <summary>
    /// <see cref="EnterFastFrame"/> with the frame depth and the frame pointer
    /// computed by the caller into locals (out parameters would keep them in
    /// memory): the stack checks, the new stack top, and the slot at fp.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ref JSValue EnterFastFrameAt(Isolate isolate, int depth, int fp, int registerCount)
    {
        if ((depth & 7) == 0) CheckNativeStack(isolate);
        int end = fp + registerCount;
        if ((uint)end > (uint)isolate.RegisterStackLimit) isolate.StackOverflow();
        isolate.RegisterStackTop = end;
        return ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(isolate.RegisterStack), fp);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static void CheckLazyFrame(Isolate isolate, int depth, int end)
    {
        if ((depth & 7) == 0 && !RuntimeHelpers.TryEnsureSufficientExecutionStack()) isolate.StackOverflow();
        if ((uint)end > (uint)isolate.RegisterStackLimit || (uint)depth >= (uint)isolate.InterpreterFrames.Length) isolate.StackOverflow();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static void CheckNativeStack(Isolate isolate)
    {
        if (!RuntimeHelpers.TryEnsureSufficientExecutionStack()) isolate.StackOverflow();
    }

    /// <summary>
    /// The second half: the fixed slots, the current context and the frame
    /// record. Returns the caller's context (restored by <see cref="LeaveFastFrame"/>).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Context? InitializeFastFrame(Isolate isolate, ref JSValue fpRef, int fp, JSFunction function, FeedbackVector vector,
        BytecodeArray bytecode, int argc, int newTargetRegister)
    {
        // A construct (ConstructWithReceiver) sets argc's sign bit and passes
        // new.target in the isolate; a call's new.target is undefined.
        InterpreterFrameFlags flags = InterpreterFrameFlags.Maglev;
        if (argc < 0)
        {
            argc &= int.MaxValue;
            flags |= InterpreterFrameFlags.Constructor;
            if (newTargetRegister != int.MinValue) Unsafe.Add(ref fpRef, newTargetRegister) = isolate.MaglevNewTarget;
            isolate.MaglevNewTarget = default;
        }
        else if (newTargetRegister != int.MinValue)
        {
            Unsafe.Add(ref fpRef, newTargetRegister) = default;
        }
        Context context = function.Context;
        Context? saved = isolate.Context;
        if (!ReferenceEquals(saved, context)) isolate.Context = context;
        JSValue.StoreSlot(ref Unsafe.Add(ref fpRef, InterpreterRuntime.kContextOffset), context);
        JSValue.StoreSlot(ref Unsafe.Add(ref fpRef, InterpreterRuntime.kClosureOffset), function);
        JSValue.StoreSlot(ref Unsafe.Add(ref fpRef, InterpreterRuntime.kFeedbackVectorOffset), vector);
        InterpreterRuntime.InitializeFrameSlots(ref fpRef, bytecode, argc);
        ref InterpreterFrameRecord frame = ref isolate.PushFrame();
        frame.Fp = fp;
        frame.Flags = flags;
        frame.ReturnPc = 0;
        frame.RegisterStart = 0;
        return saved;
    }

    /// <summary>A value into a frame slot (the reference half skipped when unchanged: no write barrier).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void StoreFrameSlot(ref JSValue slot, JSValue value) => JSValue.StoreSlot(ref slot, value);

    /// <summary>
    /// The deopt exit of a frameless direct entry (MaglevCodeGenerator,
    /// "Frameless direct entries"): builds the frame the direct entry would
    /// have built (the receiver and the arguments from the deopt scratch
    /// buffer at <paramref name="argsAt"/>), deoptimizes into it and
    /// continues in the interpreter; returns the call's result.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static JSValue DeoptimizeFrameless(Isolate isolate, MaglevCode code, int index, int reason, JSFunction function, int argc,
        int argsAt)
    {
        var bytecode = (BytecodeArray)function.Shared.FunctionData!;
        int formal = bytecode.ParameterCount - 1;
        JSValue[] scratch = isolate.MaglevDeoptScratch;
        ref JSValue fpRef = ref EnterFastFrame(isolate, formal, bytecode.RegisterCount, out int start, out int fp, out int depth);
        JSValue.StoreSlot(ref Unsafe.Add(ref fpRef, InterpreterRuntime.kReceiverOffset), scratch[argsAt]);
        for (int i = 0; i < formal; i++) JSValue.StoreSlot(ref Unsafe.Add(ref fpRef, InterpreterRuntime.kFirstArgumentOffset - i), scratch[argsAt + 1 + i]);
        Context? saved = InitializeFastFrame(isolate, ref fpRef, fp, function, code.FeedbackVector, bytecode, argc, int.MinValue);
        var state = new InterpreterState
        {
            Isolate = isolate,
            Accumulator = JSValue.Undefined,
            Fp = fp,
            FrameIndex = depth,
            BaseFrameIndex = depth,
        };
        try
        {
            V8Sharp.Deoptimizer.Deoptimizer.Deoptimize(isolate, ref state, code, index, reason);
            return MaglevExecution.ContinueAfterDeopt(isolate, ref state);
        }
        finally
        {
            LeaveFastFrame(isolate, depth, start, saved);
        }
    }

    // ---- Lazy optimized frames --------------------------------------------------------------------------
    //
    // V8's optimized frame is not an interpreter frame: the caller pushes the
    // receiver and the arguments, the callee pushes its closure and context,
    // and stack walks and the deoptimizer read the rest from the deopt data.
    // A lazy direct entry (MaglevCodeGenerator, "Lazy frames") does the same:
    // the receiver, the arguments, the closure and the current bytecode offset
    // live in a MaglevActivation struct on the .NET stack, and the frame record
    // points at it (InterpreterFrameFlags.Lazy). The entry reserves the
    // interpreter frame's register window without writing it, so a deopt can
    // build the interpreter frame in place (MaterializeLazyFrame) below the
    // frames of inlined functions pushed meanwhile.

    /// <summary>
    /// The prologue of a lazy direct entry: reserves the frame's register
    /// window (with the stack checks of EnterFastFrame), pushes the lazy frame
    /// record and switches to the function's context. Returns the caller's
    /// context (restored by <see cref="LeaveFastFrame"/>).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Context? EnterLazyFrame(Isolate isolate, int d, int s, int paramSlots, int registerCount, JSFunction function, nint activation,
        int argc)
    {
        // (d and s are the frame depth and the register stack top, read by the
        // caller into locals: out parameters would keep them in memory.)
        int fp = s + paramSlots + InterpreterRuntime.kFixedSlotsAboveParams;
        int end = fp + registerCount;
        InterpreterFrameRecord[] frames = isolate.InterpreterFrames;
        // The stack checks, one branch to a cold call: the native stack every
        // 8 frames, the register stack, the frame records.
        if (((d & 7) == 0) | (uint)end > (uint)isolate.RegisterStackLimit | (uint)d >= (uint)frames.Length) CheckLazyFrame(isolate, d, end);
        isolate.RegisterStackTop = end;
        InterpreterFrameFlags flags = InterpreterFrameFlags.Maglev | InterpreterFrameFlags.Lazy;
        if (argc < 0)
        {
            // A construct (ConstructWithReceiver): the entry takes no new.target.
            flags |= InterpreterFrameFlags.Constructor;
            isolate.MaglevNewTarget = default;
        }
        ref InterpreterFrameRecord frame = ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(frames), d);
        frame.Fp = fp;
        frame.Flags = flags;
        frame.ReturnPc = 0;
        frame.RegisterStart = 0;
        frame.Activation = activation;
        isolate.InterpreterFrameDepth = d + 1;
        Context context = function.Context;
        Context? saved = isolate.Context;
        if (!ReferenceEquals(saved, context)) isolate.Context = context;
        return saved;
    }

    /// <summary>
    /// EnterInlinedFrame for a lazy inlined frame (MaglevCodeGenerator, lazy
    /// inlined frames): reserves the frame's register window, pushes the lazy
    /// record pointing at the activation the code filled, and switches to the
    /// function's context.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void EnterLazyInlinedFrame(Isolate isolate, int paramSlots, int registerCount, nint activation, bool isConstruct)
    {
        int start = isolate.RegisterStackTop;
        int fp = start + paramSlots + InterpreterRuntime.kFixedSlotsAboveParams;
        int end = fp + registerCount;
        if ((uint)end > (uint)isolate.RegisterStackLimit) isolate.StackOverflow();
        int d = isolate.InterpreterFrameDepth;
        InterpreterFrameRecord[] frames = isolate.InterpreterFrames;
        if ((uint)d >= (uint)frames.Length) isolate.StackOverflow();
        isolate.RegisterStackTop = end;
        ref InterpreterFrameRecord frame = ref frames[d];
        frame.Fp = fp;
        frame.Flags = isConstruct
            ? InterpreterFrameFlags.Maglev | InterpreterFrameFlags.Lazy | InterpreterFrameFlags.Constructor
            : InterpreterFrameFlags.Maglev | InterpreterFrameFlags.Lazy;
        frame.ReturnPc = 0;
        frame.RegisterStart = start;
        frame.Activation = activation;
        isolate.InterpreterFrameDepth = d + 1;
        Context context = MaglevActivation.At(activation).Function.Context;
        if (!ReferenceEquals(isolate.Context, context)) isolate.Context = context;
    }

    /// <summary>
    /// Builds the interpreter frame of a lazy frame in its reserved window from
    /// its activation (what MaglevCalls.InitializeFastFrame and the frameful
    /// direct entry write), so the Deoptimizer can continue it.
    /// </summary>
    internal static void MaterializeLazyFrame(Isolate isolate, ref InterpreterFrameRecord record, FeedbackVector? vector)
    {
        ref MaglevActivation a = ref MaglevActivation.At(record.Activation);
        JSFunction function = a.Function;
        var bytecode = (BytecodeArray)function.Shared.FunctionData!;
        int formal = bytecode.ParameterCount - 1;
        ref JSValue fpRef = ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(isolate.RegisterStack), record.Fp);
        Unsafe.Add(ref fpRef, InterpreterRuntime.kReceiverOffset) = a.Receiver;
        // The parameter slots the entry reserved: max(argc, formal), all in
        // the activation (a direct entry takes no more arguments than its arity).
        int count = Math.Max(a.Argc, formal);
        for (int i = 0; i < count; i++) Unsafe.Add(ref fpRef, InterpreterRuntime.kFirstArgumentOffset - i) = MaglevActivation.Argument(ref a, i);
        Unsafe.Add(ref fpRef, InterpreterRuntime.kContextOffset) = function.Context;
        Unsafe.Add(ref fpRef, InterpreterRuntime.kClosureOffset) = function;
        Unsafe.Add(ref fpRef, InterpreterRuntime.kFeedbackVectorOffset) = vector is null ? JSValue.Undefined : vector;
        InterpreterRuntime.InitializeFrameSlots(ref fpRef, bytecode, a.Argc);
        InterpreterRuntime.SetFramePc(ref fpRef, a.Pc);
        // The registers are undefined, as the frameful entry leaves them.
        MemoryMarshal.CreateSpan(ref fpRef, bytecode.RegisterCount).Clear();
        record.Flags &= ~InterpreterFrameFlags.Lazy;
        record.Activation = 0;
    }

    // ---- Arguments objects of lazy frames ---------------------------------------------------------------
    //
    // The arguments builtins of the outermost frame read the interpreter
    // frame's parameter slots (BaselineBuiltins.CreateMappedArguments and
    // others, through the InterpreterState). A lazy frame writes its
    // activation's receiver and arguments into its reserved window first (the
    // objects copy them, or alias context slots), and the frame stays lazy.

    /// <summary>Writes the receiver and arguments of the lazy frame at <paramref name="depth"/> to its window; returns its fp.</summary>
    static int WriteLazyParameters(Isolate isolate, int depth, out JSFunction function, out int argc)
    {
        ref InterpreterFrameRecord record = ref isolate.InterpreterFrames[depth];
        ref MaglevActivation a = ref MaglevActivation.At(record.Activation);
        function = a.Function;
        argc = a.Argc;
        int formal = ((BytecodeArray)function.Shared.FunctionData!).ParameterCount - 1;
        ref JSValue fpRef = ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(isolate.RegisterStack), record.Fp);
        Unsafe.Add(ref fpRef, InterpreterRuntime.kReceiverOffset) = a.Receiver;
        int count = Math.Max(argc, formal);
        for (int i = 0; i < count; i++) Unsafe.Add(ref fpRef, InterpreterRuntime.kFirstArgumentOffset - i) = MaglevActivation.Argument(ref a, i);
        return record.Fp;
    }

    /// <summary>BaselineBuiltins.CreateMappedArguments of a lazy frame.</summary>
    public static JSValue CreateMappedArgumentsLazy(Isolate isolate, int depth, Context context)
    {
        int fp = WriteLazyParameters(isolate, depth, out JSFunction function, out int argc);
        return InterpreterArguments.NewSloppyArguments(isolate, function, context, fp, argc);
    }

    /// <summary>BaselineBuiltins.CreateUnmappedArguments of a lazy frame.</summary>
    public static JSValue CreateUnmappedArgumentsLazy(Isolate isolate, int depth)
    {
        int fp = WriteLazyParameters(isolate, depth, out JSFunction function, out int argc);
        return InterpreterArguments.NewStrictArguments(isolate, function, fp, argc);
    }

    /// <summary>BaselineBuiltins.CreateRestParameter of a lazy frame.</summary>
    public static JSValue CreateRestParameterLazy(Isolate isolate, int depth)
    {
        int fp = WriteLazyParameters(isolate, depth, out JSFunction function, out int argc);
        return InterpreterArguments.NewRestParameter(isolate, function, fp, argc);
    }

    /// <summary>MaglevBuiltins.CallForwardArguments of a lazy frame.</summary>
    public static JSValue CallForwardArgumentsLazy(Isolate isolate, int depth, JSValue target, JSValue receiver, JSValue argumentsObject)
    {
        if (argumentsObject._obj is not null)
        {
            return Builtins.BuiltinsFunction.CallWithArrayLike(isolate, target, receiver, argumentsObject);
        }
        ref MaglevActivation a = ref MaglevActivation.At(isolate.InterpreterFrames[depth].Activation);
        int argc = a.Argc;
        return CallWithValuesN(isolate, target, receiver, argc, argc > 0 ? a.A0 : default, argc > 1 ? a.A1 : default,
            argc > 2 ? a.A2 : default, argc > 3 ? a.A3 : default, argc > 4 ? a.A4 : default, argc > 5 ? a.A5 : default);
    }

    /// <summary>A call with up to six arguments as values (CallWithValues0..6).</summary>
    static JSValue CallWithValuesN(Isolate isolate, JSValue target, JSValue receiver, int argc, JSValue a0, JSValue a1, JSValue a2,
        JSValue a3, JSValue a4, JSValue a5)
    {
        if (TryGetFastCallee(isolate, target, ref receiver, argc, out JSFunction function, out MaglevCode code))
        {
            return InvokeFastCallValues(isolate, code, function, receiver, argc, a0, a1, a2, a3, a4, a5);
        }
        return CallValuesN(isolate, target, receiver, argc, a0, a1, a2, a3, a4, a5, (int)ConvertReceiverMode.Any);
    }

    /// <summary>
    /// The deopt exits of a lazy direct entry: materializes the frame,
    /// deoptimizes into it and continues in the interpreter; returns the
    /// call's result (the entry's epilogue pops the frame).
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static JSValue DeoptimizeLazyFrame(Isolate isolate, MaglevCode code, int index, int reason, int depth)
    {
        ref InterpreterFrameRecord record = ref isolate.InterpreterFrames[depth];
        // (The Deoptimizer builds the interpreter frames of lazy records.)
        var state = new InterpreterState
        {
            Isolate = isolate,
            Accumulator = JSValue.Undefined,
            Fp = record.Fp,
            FrameIndex = depth,
            BaseFrameIndex = depth,
        };
        V8Sharp.Deoptimizer.Deoptimizer.Deoptimize(isolate, ref state, code, index, reason);
        return MaglevExecution.ContinueAfterDeopt(isolate, ref state);
    }

    /// <summary>The epilogue of a direct call (EnterFrame's finally).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void LeaveFastFrame(Isolate isolate, int depth, int start, Context? saved)
    {
        if (isolate.InterpreterFrameDepth > depth + 1) PopFramesTo(isolate, depth + 1);
        isolate.InterpreterFrameDepth = depth;
        int top = isolate.RegisterStackTop;
        if (top > isolate.RegisterStackDirtyEnd) isolate.RegisterStackDirtyEnd = top;
        isolate.RegisterStackTop = start;
        if (!ReferenceEquals(isolate.Context, saved)) isolate.Context = saved;
    }

    /// <summary>Inlined frames left above a returning frame (an exception leaving them): out of line.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    static void PopFramesTo(Isolate isolate, int depth) => isolate.PopFramesTo(depth);

    /// <summary>After the callee's code returned: the interpreter continues its frames if it deoptimized.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static JSValue FinishFastCall(Isolate isolate, ref InterpreterState state, JSValue result) =>
        isolate.MaglevDeoptPending ? MaglevExecution.ContinueAfterDeopt(isolate, ref state) : result;

    /// <summary>
    /// The slow path of a direct call whose arguments are values (the callee has
    /// no Maglev code, or another receiver conversion): they go into a window
    /// above the stack top, as the interpreter's register list would hold them.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static JSValue CallValues0(Isolate isolate, JSValue callee, JSValue receiver, int mode) =>
        Call(isolate, callee, receiver, 0, 0, (ConvertReceiverMode)mode);

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static JSValue CallValues1(Isolate isolate, JSValue callee, JSValue receiver, JSValue a0, int mode)
    {
        int window = isolate.AllocateRegisters(1);
        isolate.RegisterStack[window] = a0;
        try
        {
            return Call(isolate, callee, receiver, window, 1, (ConvertReceiverMode)mode);
        }
        finally
        {
            isolate.ReleaseRegisters(window);
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static JSValue CallValues2(Isolate isolate, JSValue callee, JSValue receiver, JSValue a0, JSValue a1, int mode)
    {
        int window = isolate.AllocateRegisters(2);
        JSValue[] stack = isolate.RegisterStack;
        stack[window] = a0;
        stack[window + 1] = a1;
        try
        {
            return Call(isolate, callee, receiver, window, 2, (ConvertReceiverMode)mode);
        }
        finally
        {
            isolate.ReleaseRegisters(window);
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static JSValue CallValues3(Isolate isolate, JSValue callee, JSValue receiver, JSValue a0, JSValue a1, JSValue a2, int mode)
    {
        int window = isolate.AllocateRegisters(3);
        JSValue[] stack = isolate.RegisterStack;
        stack[window] = a0;
        stack[window + 1] = a1;
        stack[window + 2] = a2;
        try
        {
            return Call(isolate, callee, receiver, window, 3, (ConvertReceiverMode)mode);
        }
        finally
        {
            isolate.ReleaseRegisters(window);
        }
    }

    /// <summary>
    /// Builds the callee's interpreter frame (as InterpreterExecution.EnterFrame
    /// does) and runs its Maglev code.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.AggressiveOptimization)]
    static JSValue EnterFrame(Isolate isolate, JSFunction function, MaglevCode code, FeedbackVector vector, JSValue receiver,
        int argsStart, int argc, JSValue newTarget, bool isConstruct)
    {
        // The native stack check of the prologue.
        // (Checked every 8 frames: the check reads the thread's stack limit, and
        // 8 frames of these calls stay well within the reserve it guarantees.)
        if ((isolate.InterpreterFrameDepth & 7) == 0 && !RuntimeHelpers.TryEnsureSufficientExecutionStack()) isolate.StackOverflow();

        var bytecode = (BytecodeArray)function.Shared.FunctionData!;
        JSValue[] stack = isolate.RegisterStack;
        int formal = bytecode.ParameterCount - 1;
        int paramSlots = argc > formal ? argc : formal;
        int start = isolate.RegisterStackTop;
        int fp = start + paramSlots + InterpreterRuntime.kFixedSlotsAboveParams;
        int registerCount = bytecode.RegisterCount;
        int end = fp + registerCount;
        if ((uint)end > (uint)isolate.RegisterStackLimit) isolate.StackOverflow();
        isolate.RegisterStackTop = end;

        ref JSValue stack0 = ref MemoryMarshal.GetArrayDataReference(stack);
        ref JSValue fpRef = ref Unsafe.Add(ref stack0, fp);
        Unsafe.Add(ref fpRef, InterpreterRuntime.kReceiverOffset) = receiver;
        ref JSValue src = ref Unsafe.Add(ref stack0, argsStart);
        for (int i = 0; i < argc; i++) Unsafe.Add(ref fpRef, InterpreterRuntime.kFirstArgumentOffset - i) = Unsafe.Add(ref src, i);
        // Missing arguments are undefined (V8's argument adaption).
        for (int i = argc; i < paramSlots; i++) Unsafe.Add(ref fpRef, InterpreterRuntime.kFirstArgumentOffset - i) = default;

        Context context = function.Context;
        Context? savedContext = isolate.Context;
        if (!ReferenceEquals(savedContext, context)) isolate.Context = context;
        // The fixed slots, each compared first: a returned frame at the same
        // position usually leaves the same values (no GC write barrier).
        JSValue.StoreSlot(ref Unsafe.Add(ref fpRef, InterpreterRuntime.kContextOffset), context);
        JSValue.StoreSlot(ref Unsafe.Add(ref fpRef, InterpreterRuntime.kClosureOffset), function);
        JSValue.StoreSlot(ref Unsafe.Add(ref fpRef, InterpreterRuntime.kFeedbackVectorOffset), vector);
        InterpreterRuntime.InitializeFrameSlots(ref fpRef, bytecode, argc);
        // The registers are undefined, as the interpreter's trampoline leaves
        // them: a deopt writes the live ones, the frame walker may read the
        // others. Only the values a popped frame left (dirty) need clearing.
        if (isolate.RegisterStackDirtyEnd > fp) MemoryMarshal.CreateSpan(ref fpRef, registerCount).Clear();
        if (isConstruct)
        {
            Register incoming = bytecode.IncomingNewTargetOrGeneratorRegister;
            if (incoming.IsValid) Unsafe.Add(ref fpRef, incoming.Index) = newTarget;
        }

        int depth = isolate.InterpreterFrameDepth;
        ref InterpreterFrameRecord frame = ref isolate.PushFrame();
        frame.Fp = fp;
        frame.Flags = isConstruct ? InterpreterFrameFlags.Maglev | InterpreterFrameFlags.Constructor : InterpreterFrameFlags.Maglev;
        frame.ReturnPc = 0;
        frame.RegisterStart = 0;

        vector.InvocationCount++;

        var state = new InterpreterState
        {
            Isolate = isolate,
            Accumulator = JSValue.Undefined,
            Pc = 0,
            Fp = fp,
            FrameIndex = depth,
            BaseFrameIndex = depth,
        };
        try
        {
            JSValue result = code.Entry(isolate, ref state);
            if (!isolate.MaglevDeoptPending) return result;
            return MaglevExecution.ContinueAfterDeopt(isolate, ref state);
        }
        finally
        {
            // Pop the frame (and any inner ones an exception left), leaving its
            // values above the stack top dirty, as InterpreterInlineCalls.PopFrame.
            if (isolate.InterpreterFrameDepth > depth + 1) isolate.PopFramesTo(depth + 1);
            isolate.InterpreterFrameDepth = depth;
            int top = isolate.RegisterStackTop;
            if (top > isolate.RegisterStackDirtyEnd) isolate.RegisterStackDirtyEnd = top;
            isolate.RegisterStackTop = start;
            if (!ReferenceEquals(isolate.Context, savedContext)) isolate.Context = savedContext;
        }
    }
}

/// <summary>
/// The activation of a lazy optimized frame (MaglevCalls, "Lazy optimized
/// frames"): what V8's optimized frame holds for the stack walker and the
/// deoptimizer (the closure, the arguments the caller pushed, the receiver)
/// and the bytecode offset of the current call (V8 maps the return address
/// to it). A local of the lazy direct entry, on the .NET stack; its frame
/// record holds its address.
/// </summary>
/// <remarks>
/// Deviation (V8Sharp only): the record refers to a .NET stack location by
/// address. The local is address-exposed, so RyuJIT keeps it in memory and
/// reports its references to the GC for the whole method (which updates them
/// when objects move); the record is popped before the method returns
/// (its epilogue and fault block), so only live activations are read.
/// </remarks>
[StructLayout(LayoutKind.Explicit)]
public struct MaglevActivation
{
    [FieldOffset(0)] public JSFunction Function;
    /// <summary>The bytecode offset of the current call or throwing node.</summary>
    [FieldOffset(8)] public int Pc;
    /// <summary>The actual argument count.</summary>
    [FieldOffset(12)] public int Argc;
    [FieldOffset(16)] public JSValue Receiver;
    [FieldOffset(32)] public JSValue A0;
    [FieldOffset(48)] public JSValue A1;
    [FieldOffset(64)] public JSValue A2;
    [FieldOffset(80)] public JSValue A3;
    [FieldOffset(96)] public JSValue A4;
    [FieldOffset(112)] public JSValue A5;

    /// <summary>
    /// The activation local of a code with <paramref name="arity"/> formal
    /// parameters: the same layout as this struct, cut after its arguments
    /// (the prologue zeroes the local, so it takes no more stack than it uses).
    /// </summary>
    public static Type TypeFor(int arity) => arity switch
    {
        0 => typeof(MaglevActivation0),
        1 => typeof(MaglevActivation1),
        2 => typeof(MaglevActivation2),
        3 => typeof(MaglevActivation3),
        4 => typeof(MaglevActivation4),
        5 => typeof(MaglevActivation5),
        _ => typeof(MaglevActivation),
    };

    /// <summary>The activation at <paramref name="address"/> (a live lazy frame record's).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ref MaglevActivation At(nint address) =>
        ref Unsafe.As<byte, MaglevActivation>(ref Unsafe.AddByteOffset(ref Unsafe.NullRef<byte>(), address));

    /// <summary>Argument <paramref name="i"/> (below MaglevFastCalls.kMaxArity).</summary>
    public static ref JSValue Argument(ref MaglevActivation a, int i)
    {
        switch (i)
        {
            case 0: return ref a.A0;
            case 1: return ref a.A1;
            case 2: return ref a.A2;
            case 3: return ref a.A3;
            case 4: return ref a.A4;
            default: return ref a.A5;
        }
    }

    /// <summary>The arguments of the activation at <paramref name="address"/> (its formal parameters' slots, as passed).</summary>
    internal static JSValue[] GetArguments(nint address)
    {
        ref MaglevActivation a = ref At(address);
        // (A direct entry takes at most its arity's arguments, all in the activation.)
        int argc = Math.Min(a.Argc, MaglevFastCalls.kMaxArity);
        if (argc <= 0) return [];
        var result = new JSValue[argc];
        for (int i = 0; i < argc; i++) result[i] = Argument(ref a, i);
        return result;
    }
}

// The activation locals by arity (MaglevActivation.TypeFor): MaglevActivation's
// layout up to their last argument; the stack walker reads them through
// MaglevActivation, within their arity.
[StructLayout(LayoutKind.Explicit)]
public struct MaglevActivation0
{
    [FieldOffset(0)] public JSFunction Function;
    [FieldOffset(8)] public int Pc;
    [FieldOffset(12)] public int Argc;
    [FieldOffset(16)] public JSValue Receiver;
}

[StructLayout(LayoutKind.Explicit)]
public struct MaglevActivation1
{
    [FieldOffset(0)] public JSFunction Function;
    [FieldOffset(8)] public int Pc;
    [FieldOffset(12)] public int Argc;
    [FieldOffset(16)] public JSValue Receiver;
    [FieldOffset(32)] public JSValue A0;
}

[StructLayout(LayoutKind.Explicit)]
public struct MaglevActivation2
{
    [FieldOffset(0)] public JSFunction Function;
    [FieldOffset(8)] public int Pc;
    [FieldOffset(12)] public int Argc;
    [FieldOffset(16)] public JSValue Receiver;
    [FieldOffset(32)] public JSValue A0;
    [FieldOffset(48)] public JSValue A1;
}

[StructLayout(LayoutKind.Explicit)]
public struct MaglevActivation3
{
    [FieldOffset(0)] public JSFunction Function;
    [FieldOffset(8)] public int Pc;
    [FieldOffset(12)] public int Argc;
    [FieldOffset(16)] public JSValue Receiver;
    [FieldOffset(32)] public JSValue A0;
    [FieldOffset(48)] public JSValue A1;
    [FieldOffset(64)] public JSValue A2;
}

[StructLayout(LayoutKind.Explicit)]
public struct MaglevActivation4
{
    [FieldOffset(0)] public JSFunction Function;
    [FieldOffset(8)] public int Pc;
    [FieldOffset(12)] public int Argc;
    [FieldOffset(16)] public JSValue Receiver;
    [FieldOffset(32)] public JSValue A0;
    [FieldOffset(48)] public JSValue A1;
    [FieldOffset(64)] public JSValue A2;
    [FieldOffset(80)] public JSValue A3;
}

[StructLayout(LayoutKind.Explicit)]
public struct MaglevActivation5
{
    [FieldOffset(0)] public JSFunction Function;
    [FieldOffset(8)] public int Pc;
    [FieldOffset(12)] public int Argc;
    [FieldOffset(16)] public JSValue Receiver;
    [FieldOffset(32)] public JSValue A0;
    [FieldOffset(48)] public JSValue A1;
    [FieldOffset(64)] public JSValue A2;
    [FieldOffset(80)] public JSValue A3;
    [FieldOffset(96)] public JSValue A4;
}
