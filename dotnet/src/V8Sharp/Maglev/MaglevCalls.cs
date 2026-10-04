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

    /// <summary>A call from a register list through the callee's direct entry (MaglevCode.FastCall).</summary>
    static JSValue InvokeFastCall(Isolate isolate, MaglevCode code, JSFunction function, JSValue receiver, int argsStart, int argc,
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
