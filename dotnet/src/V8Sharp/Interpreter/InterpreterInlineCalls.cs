// Calls from bytecode to bytecode without a new .NET frame.
//
// V8's interpreter calls a JavaScript function by pushing the arguments and
// jumping to the InterpreterEntryTrampoline on the machine stack: a call costs a
// few pushes. The natural C# equivalent (the dispatch loop calling itself
// through Invoke/EnterFrame/Run) costs a .NET frame of the dispatch loop, its
// prologue zeroing, a try/finally and a try/catch per JavaScript call. So a
// call bytecode whose target is an ordinary compiled bytecode function sets up
// the callee's frame (the same register-stack layout and frame record
// InterpreterExecution.EnterFrame builds) and the dispatch loop continues with
// the callee; Return in such a frame resumes the caller in the same loop.
// Exceptions unwind these frames in InterpreterExecution.Run.
//
// Only plain calls go this way: constructors, class constructors, generators
// and async functions, builtins and wide-operand calls take the ordinary path.
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using V8Sharp.Codegen;

namespace V8Sharp.Interpreter;

internal static class InterpreterInlineCalls
{
    // SharedFunctionInfo.InterpreterCallMode: how a call from the dispatch loop
    // enters the function (0 is "not computed").
    /// <summary>Not in the loop: builtins, uncompiled, class constructors, generators, baseline code.</summary>
    public const byte kCallModeNotInline = 1;
    /// <summary>In the loop; a strict or native function takes the receiver as it is.</summary>
    public const byte kCallModeInline = 2;
    /// <summary>In the loop; a sloppy function gets a non-receiver receiver converted (ConvertReceiver).</summary>
    public const byte kCallModeInlineSloppy = 3;
    /// <summary>In the loop unless the closure has Maglev code (<see cref="CanInline"/> decides per call).</summary>
    public const byte kCallModeCheckClosure = 4;

    /// <summary>
    /// The call mode of <paramref name="shared"/>: one load and compare per
    /// call instead of CanInline's six fields (V8 decides by the JSFunction's
    /// code field, which the tiers update, in the same way).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int InlineCallMode(SharedFunctionInfo shared)
    {
        int mode = shared.InterpreterCallMode;
        return mode != 0 ? mode : ComputeInlineCallMode(shared);
    }

    /// <summary>
    /// Whether a call from the dispatch loop to <paramref name="callee"/> runs in
    /// the loop (<see cref="EnterInline"/>), and with which mode.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool TryGetInlineMode(JSValue callee, out JSFunction function, out int mode)
    {
        if (callee._obj is JSFunction f)
        {
            function = f;
            mode = InlineCallMode(f.Shared);
            if ((uint)(mode - kCallModeInline) <= kCallModeInlineSloppy - kCallModeInline) return true;
            if (mode == kCallModeCheckClosure)
            {
                mode = CheckClosure(f);
                return mode != 0;
            }
            return false;
        }
        function = null!;
        mode = 0;
        return false;
    }

    /// <summary>
    /// kCallModeCheckClosure: the mode of a call to <paramref name="function"/>,
    /// or 0 when the closure has Maglev code, which it runs (through
    /// InterpreterExecution.EnterFrame).
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    static int CheckClosure(JSFunction function)
    {
        if (function.RawFeedbackCell.Value is FeedbackVector { MaglevCode: not null }) return 0;
        SharedFunctionInfo shared = function.Shared;
        return !shared.Native && shared.LanguageMode == LanguageMode.Sloppy ? kCallModeInlineSloppy : kCallModeInline;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static int ComputeInlineCallMode(SharedFunctionInfo shared)
    {
        byte mode = kCallModeNotInline;
        if (shared.FunctionData is BytecodeArray && !shared.HasBuiltinId && !shared.IsClassConstructor &&
            !Globals.IsResumableFunction(shared.Kind) && shared.BaselineCode is null)
        {
            mode = shared.MayHaveMaglevCode ? kCallModeCheckClosure
                : !shared.Native && shared.LanguageMode == LanguageMode.Sloppy ? kCallModeInlineSloppy
                : kCallModeInline;
        }
        shared.InterpreterCallMode = mode;
        return mode;
    }

    /// <summary>
    /// Enters <paramref name="function"/>, whose <see cref="InlineCallMode"/> is
    /// <paramref name="mode"/> (kCallModeInline or kCallModeInlineSloppy), from
    /// the innermost frame of <paramref name="st"/>: the callee's frame (the
    /// layout and record InterpreterExecution.EnterFrame builds) on the register
    /// stack, and <paramref name="st"/> describing the callee at offset 0. The
    /// caller resumes at <paramref name="returnPc"/>.
    /// </summary>
    // The interpreter's InterpreterPushArgsThenCall + InterpreterEntryTrampoline,
    // straight-line per argument form (the call handlers inline it): the
    // arguments go from the caller's registers into the callee's parameter
    // slots, and every reference store into the register stack or the record
    // is skipped when the slot already holds the value (a returned frame at
    // the same depth leaves its values), so a repeated call pays no GC write
    // barriers.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void EnterInline<TArgs>(Isolate isolate, ref InterpreterState st, JSFunction function, int mode,
        JSValue receiver, TArgs args, int returnPc) where TArgs : struct, Baseline.BaselineCalls.ICallArguments
    {
        if (mode == kCallModeInlineSloppy && !receiver.IsJSReceiver) receiver = InterpreterCalls.ConvertReceiver(isolate, function, receiver);
        EnterInlineCore(isolate, ref st, function, receiver, args, returnPc, -1, false, default);
    }

    /// <summary>
    /// <see cref="EnterInline"/> with the receiver converted. Stack space
    /// reserved below the frame (an apply's arguments, the construct stub's
    /// slots) starts at <paramref name="registerStart"/> and is released with it;
    /// a construct call (<paramref name="isConstruct"/>) passes the implicit
    /// receiver and new.target.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static void EnterInlineCore<TArgs>(Isolate isolate, ref InterpreterState st, JSFunction function, JSValue receiver,
        TArgs args, int returnPc, int registerStart, bool isConstruct, JSValue newTarget)
        where TArgs : struct, Baseline.BaselineCalls.ICallArguments
    {
        // The interrupt check of the entry's stack check.
        if (isolate.StackGuard.HasPendingInterrupts) isolate.StackGuard.HandleInterrupts();

        // The mode established the type.
        var bytecode = Unsafe.As<BytecodeArray>(function.Shared.FunctionData!);
        int argc = args.Count;
        int formal = bytecode.ParameterCount - 1;
        int start = isolate.RegisterStackTop;
        int fp = start + (argc > formal ? argc : formal) + InterpreterRuntime.kFixedSlotsAboveParams;
        int end = fp + bytecode.RegisterCount;
        if ((uint)end > (uint)isolate.RegisterStackLimit) isolate.StackOverflow();
        isolate.RegisterStackTop = end;

        ref JSValue stack0 = ref MemoryMarshal.GetArrayDataReference(isolate.RegisterStack);
        ref JSValue fpRef = ref Unsafe.Add(ref stack0, fp);
        // The register file is undefined: the stack above its top is, except
        // below RegisterStackDirtyEnd, where returned frames left their values.
        int dirtyEnd = isolate.RegisterStackDirtyEnd;
        if (fp < dirtyEnd) ClearSlots(ref fpRef, (end < dirtyEnd ? end : dirtyEnd) - fp);

        StoreSlot(ref Unsafe.Add(ref fpRef, InterpreterRuntime.kReceiverOffset), receiver);
        args.Store(ref stack0, ref Unsafe.Add(ref fpRef, InterpreterRuntime.kFirstArgumentOffset));
        // Missing arguments are undefined (V8's argument adaption).
        for (int i = argc; i < formal; i++) StoreSlot(ref Unsafe.Add(ref fpRef, InterpreterRuntime.kFirstArgumentOffset - i), default);

        Context context = function.Context;
        if (!ReferenceEquals(isolate.Context, context)) isolate.Context = context;
        StoreSlot(ref Unsafe.Add(ref fpRef, InterpreterRuntime.kContextOffset), context);
        StoreSlot(ref Unsafe.Add(ref fpRef, InterpreterRuntime.kClosureOffset), function);
        // The argument count slot (fp - 4) is not written : frames keep the count in their record.
        if (isConstruct)
        {
            Register incoming = bytecode.IncomingNewTargetOrGeneratorRegister;
            if (incoming.IsValid) Unsafe.Add(ref fpRef, incoming.Index) = newTarget;
        }
        FeedbackVector? feedbackVector = InterpreterExecution.FeedbackVectorOnEntry(isolate, function);
        StoreSlot(ref Unsafe.Add(ref fpRef, InterpreterRuntime.kFeedbackVectorOffset),
            feedbackVector is null ? JSValue.Undefined : feedbackVector);

        int depth = isolate.InterpreterFrameDepth;
        if ((uint)depth >= (uint)Isolate.kMaxInterpreterFrames) isolate.StackOverflow();
        isolate.InterpreterFrameDepth = depth + 1;
        ref InterpreterFrameRecord frame = ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(isolate.InterpreterFrames), depth);
        // The caller is the frame below (the loop runs the innermost frame).
        Debug.Assert(st.FrameIndex == depth - 1);
        Unsafe.Subtract(ref frame, 1).ReturnPc = returnPc;
        // PopFrame leaves Function and Bytecode in the record.
        if (!ReferenceEquals(frame.Function, function)) frame.Function = function;
        if (!ReferenceEquals(frame.Bytecode, bytecode)) frame.Bytecode = bytecode;
        frame.Fp = fp;
        frame.Pc = 0;
        frame.Argc = argc;
        frame.Kind = InterpreterFrameKind.Interpreted;
        frame.IsConstructor = isConstruct;
        frame.IsBaseline = false;
        frame.IsMaglev = false;
        frame.InlineCall = true;
        frame.RegisterStart = registerStart >= 0 ? registerStart : start;

        st.Function = function;
        st.Bytecode = bytecode;
        if (bytecode.ConstantPoolValues is null) InterpreterRuntime.MaterializeConstantPool(isolate, bytecode);
        st.FeedbackVector = feedbackVector;
        st.Context = context;
        st.Accumulator = default;
        st.Pc = 0;
        st.Fp = fp;
        st.FrameIndex = depth;
        st.Argc = argc;
    }

    /// <summary>
    /// A call of Function.prototype.call (<paramref name="callTarget"/> is its
    /// receiver) whose target can run in this loop: the target is entered with
    /// <paramref name="thisArg"/> and the argument window after it, like
    /// <see cref="EnterInline"/> (Function.prototype.call is an ASM builtin in V8,
    /// with no frame of its own).
    /// </summary>
    public static bool TryPushFunctionCallFrame(Isolate isolate, ref InterpreterState st, JSValue callTarget, JSValue thisArg,
        int argsStart, int argc, int returnPc)
    {
        if (!TryGetInlineMode(callTarget, out JSFunction function, out int mode)) return false;
        EnterInline(isolate, ref st, function, mode, thisArg, new Baseline.BaselineCalls.RegisterArguments(argsStart, argc), returnPc);
        return true;
    }

    /// <summary>
    /// A call of Function.prototype.apply (<paramref name="applyTarget"/> is its
    /// receiver) whose target can run in this loop and whose argument list is
    /// an unmodified arguments object or a fast array (CallWithArrayLike's fast
    /// paths): the elements are pushed like an argument window and the target
    /// entered like <see cref="EnterInline"/>. Function.prototype.apply is an ASM
    /// builtin in V8, with no frame of its own. False, with nothing done, for
    /// anything else.
    /// </summary>
    public static bool TryPushApplyFrame(Isolate isolate, ref InterpreterState st, JSValue applyTarget, JSValue thisArg,
        JSValue argumentsList, int returnPc)
    {
        if (!TryGetInlineMode(applyTarget, out JSFunction function, out int mode)) return false;
        FixedArrayBase? elements = null;
        int length = 0;
        if (!argumentsList.IsNullOrUndefined &&
            !Builtins.BuiltinsFunction.TryGetFastElements(isolate, argumentsList, out elements, out length))
        {
            return false;
        }
        // The elements go into a window on the register stack below the frame
        // (released with it), as V8 pushes them onto the machine stack.
        int windowStart = isolate.RegisterStackTop;
        if (windowStart + length + 64 > isolate.RegisterStackLimit) return false;
        ref JSValue window = ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(isolate.RegisterStack), windowStart);
        if (elements is FixedArray fixedArray)
        {
            JSValue[] data = fixedArray.Data;
            for (int i = 0; i < length; i++)
            {
                // Holes (holey arrays with intact protectors) read as undefined.
                JSValue value = data[i];
                Unsafe.Add(ref window, i) = ReferenceEquals(value._obj, Oddball.TheHole) ? default : value;
            }
        }
        else if (elements is FixedDoubleArray doubles)
        {
            for (int i = 0; i < length; i++)
            {
                Unsafe.Add(ref window, i) = doubles.IsTheHole(i) ? default : JSValue.FromNumber(doubles.GetScalar(i));
            }
        }
        isolate.RegisterStackTop = windowStart + length;

        if (mode == kCallModeInlineSloppy && !thisArg.IsJSReceiver) thisArg = InterpreterCalls.ConvertReceiver(isolate, function, thisArg);
        EnterInlineCore(isolate, ref st, function, thisArg, new Baseline.BaselineCalls.RegisterArguments(windowStart, length), returnPc,
            windowStart, false, default);
        return true;
    }

    /// <summary>
    /// The Construct bytecode for an ordinary (not derived) constructor with
    /// bytecode: collects the construct feedback, allocates the receiver
    /// (JSConstructStubGeneric) and enters the constructor like
    /// <see cref="EnterInline"/>. False, with nothing done, for other constructors.
    /// </summary>
    public static bool TryPushConstructFrame(Isolate isolate, ref InterpreterState st, int slot, JSValue constructor,
        JSValue newTarget, int argsStart, int argc, int returnPc)
    {
        if (constructor._obj is not JSFunction function) return false;
        // The construct mode, cached on the SharedFunctionInfo like the call
        // mode (V8 decides by the constructor's code: JSConstructStubGeneric
        // with the interpreter entry trampoline).
        int mode = function.Shared.InterpreterConstructMode;
        if (mode != kCallModeInline)
        {
            if (mode == 0) mode = ComputeInlineConstructMode(function.Shared);
            if (mode != kCallModeInline && (mode != kCallModeCheckClosure || CheckClosure(function) == 0)) return false;
        }
        HeapObject? newTargetObject = newTarget._obj;
        if (!function.Map.IsConstructor || newTargetObject is null || newTargetObject.InstanceType < InstanceTypeChecks.FirstJSReceiver)
        {
            return false;
        }

        // CollectConstructFeedback: the call count and the monomorphic hit
        // inline, everything else in the runtime function.
        if (st.FeedbackVector is { } fv)
        {
            JSValue[] slots = fv.Slots;
            if (ReferenceEquals(slots[slot]._obj, newTargetObject))
            {
                ref JSValue count = ref slots[slot + 1];
                Unsafe.AsRef(in count._bits) = BitConverter.DoubleToInt64Bits(count._num + InterpreterCalls.kCallCountIncrement);
            }
            else
            {
                InterpreterCalls.CollectConstructFeedback(isolate, fv, slot, constructor, newTarget);
            }
        }

        // JSConstructStubGeneric: FastNewObject when new.target is the
        // constructor and its initial map exists (the allocation needs nothing
        // from the context), the runtime otherwise.
        JSObject implicitReceiver = ReferenceEquals(newTargetObject, function) && function.PrototypeOrInitialMap is Map initialMap &&
            !initialMap.IsDictionaryMap
            ? isolate.Factory.NewJSObjectFromMap(initialMap)
            : NewImplicitReceiver(isolate, function, Unsafe.As<JSReceiver>(newTargetObject));

        // The construct stub's frame (see InterpreterCalls.ConstructInterpreted).
        int stubStart = isolate.AllocateRegisters(InterpreterCalls.kConstructStubFrameSlots);
        EnterInlineCore(isolate, ref st, function, implicitReceiver, new Baseline.BaselineCalls.RegisterArguments(argsStart, argc),
            returnPc, stubStart, true, newTarget);
        return true;
    }

    /// <summary>The implicit receiver of a construct call allocated in the constructor's context (JSObject::New).</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    static JSObject NewImplicitReceiver(Isolate isolate, JSFunction function, JSReceiver newTarget)
    {
        Context? saved = isolate.Context;
        isolate.Context = function.Context;
        try
        {
            return JSObject.New(isolate, function, newTarget, null);
        }
        finally
        {
            isolate.Context = saved;
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static int ComputeInlineConstructMode(SharedFunctionInfo shared)
    {
        byte mode = kCallModeNotInline;
        if (shared.FunctionData is BytecodeArray && !shared.HasBuiltinId && !Globals.IsDerivedConstructor(shared.Kind) &&
            !Globals.IsResumableFunction(shared.Kind) && shared.BaselineCode is null)
        {
            mode = shared.MayHaveMaglevCode ? kCallModeCheckClosure : kCallModeInline;
        }
        shared.InterpreterConstructMode = mode;
        return mode;
    }

    /// <summary>
    /// Sets <paramref name="count"/> slots to undefined: a loop for the usual
    /// small register files (Span.Clear is a call into SpanHelpers).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static void ClearSlots(ref JSValue start, int count)
    {
        if (count <= 16)
        {
            for (int i = 0; i < count; i++) Unsafe.Add(ref start, i) = default;
        }
        else
        {
            MemoryMarshal.CreateSpan(ref start, count).Clear();
        }
    }

    /// <summary>
    /// A register stack store that skips the reference part when the slot
    /// already holds the same object or tag (InterpreterExecution.StoreRegister).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static void StoreSlot(ref JSValue slot, JSValue value)
    {
        if (!ReferenceEquals(slot._obj, value._obj)) Unsafe.AsRef(in slot._obj) = value._obj;
        Unsafe.AsRef(in slot._bits) = value._bits;
    }

    /// <summary>
    /// Return from the inline frame <paramref name="st"/> describes: the value
    /// the caller gets (a constructor's receiver unless it returned an object),
    /// after <see cref="PopFrame"/>.
    /// </summary>
    public static JSValue Return(Isolate isolate, ref InterpreterState st, JSValue result)
    {
        ref InterpreterFrameRecord frame = ref isolate.InterpreterFrames[st.FrameIndex];
        // If the result is an object (in the ECMA sense), we should get rid
        // of the receiver and use the result; see ECMA-262 section 13.2.2-7
        // on page 74.
        if (frame.IsConstructor && !result.IsJSReceiver)
        {
            result = isolate.RegisterStack[frame.Fp + InterpreterRuntime.kReceiverOffset];
        }
        PopFrame(isolate, ref st);
        return result;
    }

    /// <summary>
    /// Leaves the inline frame <paramref name="st"/> describes: releases its
    /// registers and record and makes <paramref name="st"/> describe the caller
    /// at its return offset.
    /// </summary>
    /// <summary>
    /// The Return bytecode in a frame this loop entered inline: <see cref="Return"/>
    /// with the result in st.Accumulator, true. False, with nothing done, for
    /// the frame the loop was entered for.
    /// </summary>
    public static bool TryReturnInline(Isolate isolate, ref InterpreterState st, JSValue result)
    {
        InterpreterFrameRecord[] frames = isolate.InterpreterFrames;
        ref InterpreterFrameRecord frame = ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(frames), st.FrameIndex);
        if (!frame.InlineCall) return false;
        if (frame.IsConstructor && !result.IsJSReceiver)
        {
            result = isolate.RegisterStack[frame.Fp + InterpreterRuntime.kReceiverOffset];
        }
        PopFrame(isolate, ref st, frames, ref frame);
        st.Accumulator = result;
        return true;
    }

    public static void PopFrame(Isolate isolate, ref InterpreterState st)
    {
        InterpreterFrameRecord[] frames = isolate.InterpreterFrames;
        PopFrame(isolate, ref st, frames, ref frames[st.FrameIndex]);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static void PopFrame(Isolate isolate, ref InterpreterState st, InterpreterFrameRecord[] frames, ref InterpreterFrameRecord record)
    {
        int start = record.RegisterStart;
        // The record is left as it is: every push sets all of its fields but
        // Function and Bytecode, which it compares first (functions and their
        // bytecode are long-lived; keeping them lets the next call at this depth
        // skip the reference stores), and Receiver, which only builtin frames
        // set and clear.
        isolate.InterpreterFrameDepth = st.FrameIndex;
        // The frame's slots are not cleared: they stay below
        // RegisterStackDirtyEnd for the next call at this depth (EnterInlineCore).
        int top = isolate.RegisterStackTop;
        if (top > isolate.RegisterStackDirtyEnd) isolate.RegisterStackDirtyEnd = top;
        isolate.RegisterStackTop = start;
        int callerIndex = st.FrameIndex - 1;
        // The record below a frame entered inline is its caller's.
        ref InterpreterFrameRecord caller = ref Unsafe.Subtract(ref record, 1);
        BytecodeArray bytecode = caller.Bytecode!;
        int fp = caller.Fp;
        ref JSValue fpRef = ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(isolate.RegisterStack), fp);
        Context context = Unsafe.As<Context>(Unsafe.Add(ref fpRef, InterpreterRuntime.kContextOffset)._obj!);
        if (!ReferenceEquals(isolate.Context, context)) isolate.Context = context;

        st.Function = caller.Function;
        st.Bytecode = bytecode;
        // The slot holds the frame's FeedbackVector or undefined.
        st.FeedbackVector = Unsafe.As<FeedbackVector?>(Unsafe.Add(ref fpRef, InterpreterRuntime.kFeedbackVectorOffset)._obj);
        if (!ReferenceEquals(st.Context, context)) st.Context = context;
        st.Pc = caller.ReturnPc;
        st.Fp = fp;
        st.FrameIndex = callerIndex;
        st.Argc = caller.Argc;
    }

    /// <summary>Whether some frame of this loop, from the innermost inline one down, handles the current offset.</summary>
    public static bool AnyFrameHasHandler(Isolate isolate, ref InterpreterState st)
    {
        InterpreterFrameRecord[] frames = isolate.InterpreterFrames;
        for (int index = st.FrameIndex; index >= st.BaseFrameIndex; index--)
        {
            ref InterpreterFrameRecord frame = ref frames[index];
            byte[] handlerTableBytes = frame.Bytecode!.HandlerTable;
            if (handlerTableBytes.Length != 0 && new HandlerTable(handlerTableBytes).LookupHandlerIndexForRange(frame.Pc) >= 0)
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Isolate::UnwindAndFindHandler over the frames of this loop: pops inline
    /// frames without a handler, then dispatches to the handler of the first
    /// frame that has one (AnyFrameHasHandler said there is one).
    /// </summary>
    public static void UnwindToHandler(Isolate isolate, ref InterpreterState st, JSValue exception, JSMessageObject? message)
    {
        while (!InterpreterExecution.TryDispatchToHandler(isolate, ref st, exception, message))
        {
            PopFrame(isolate, ref st);
            // The caller's offset is its call bytecode, where the exception now is.
            st.Pc = isolate.InterpreterFrames[st.FrameIndex].Pc;
        }
    }
}
