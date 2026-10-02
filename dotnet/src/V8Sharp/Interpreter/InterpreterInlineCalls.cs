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
    /// <summary>Whether a call to <paramref name="callee"/> can run in the caller's dispatch loop.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool CanInline(JSValue callee, out JSFunction function)
    {
        if (callee._obj is JSFunction f)
        {
            SharedFunctionInfo shared = f.Shared;
            // A function with baseline code runs it (through InterpreterCalls.Call
            // and InterpreterExecution.EnterFrame), not inline in the interpreter.
            if (shared.FunctionData is BytecodeArray && !shared.HasBuiltinId && !shared.IsClassConstructor &&
                !Globals.IsResumableFunction(shared.Kind) && shared.BaselineCode is null &&
                // A closure with Maglev code runs it (through InterpreterExecution.EnterFrame).
                (!shared.MayHaveMaglevCode || f.RawFeedbackCell.Value is not FeedbackVector { MaglevCode: not null }))
            {
                function = f;
                return true;
            }
        }
        function = null!;
        return false;
    }

    /// <summary>
    /// Enters <paramref name="function"/> from the current frame of
    /// <paramref name="st"/>: pushes the receiver and the arguments (the
    /// <paramref name="argc"/> registers at stack index <paramref name="argsStart"/>,
    /// or <paramref name="arg0"/> / <paramref name="arg1"/> when argsStart is
    /// negative), the fixed slots and the register file, a frame record, and makes
    /// <paramref name="st"/> describe the callee at offset 0.
    /// </summary>
    public static void PushFrame(Isolate isolate, ref InterpreterState st, JSFunction function, JSValue receiver, int argsStart,
        int argc, JSValue arg0, JSValue arg1, int returnPc)
    {
        SharedFunctionInfo shared = function.Shared;
        if (!shared.Native && shared.LanguageMode == LanguageMode.Sloppy && !receiver.IsJSReceiver)
        {
            receiver = InterpreterCalls.ConvertReceiver(isolate, function, receiver);
        }
        PushFrameCore(isolate, ref st, function, receiver, argsStart, argc, arg0, arg1, returnPc, isolate.RegisterStackTop,
            false, default);
    }

    /// <summary>
    /// A call of Function.prototype.call (<paramref name="callTarget"/> is its
    /// receiver) whose target can run in this loop: the target is entered with
    /// <paramref name="thisArg"/> and the argument window after it, like
    /// <see cref="PushFrame"/> (Function.prototype.call is an ASM builtin in V8,
    /// with no frame of its own).
    /// </summary>
    public static bool TryPushFunctionCallFrame(Isolate isolate, ref InterpreterState st, JSValue callTarget, JSValue thisArg,
        int argsStart, int argc, int returnPc)
    {
        if (!CanInline(callTarget, out JSFunction function)) return false;
        PushFrame(isolate, ref st, function, thisArg, argsStart, argc, default, default, returnPc);
        return true;
    }

    /// <summary>
    /// A call of Function.prototype.apply (<paramref name="applyTarget"/> is its
    /// receiver) whose target can run in this loop and whose argument list is
    /// an unmodified arguments object or a fast array (CallWithArrayLike's fast
    /// paths): the elements are pushed like an argument window and the target
    /// entered like <see cref="PushFrame"/>. Function.prototype.apply is an ASM
    /// builtin in V8, with no frame of its own. False, with nothing done, for
    /// anything else.
    /// </summary>
    public static bool TryPushApplyFrame(Isolate isolate, ref InterpreterState st, JSValue applyTarget, JSValue thisArg,
        JSValue argumentsList, int returnPc)
    {
        if (!CanInline(applyTarget, out JSFunction function)) return false;
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

        SharedFunctionInfo shared = function.Shared;
        if (!shared.Native && shared.LanguageMode == LanguageMode.Sloppy && !thisArg.IsJSReceiver)
        {
            thisArg = InterpreterCalls.ConvertReceiver(isolate, function, thisArg);
        }
        PushFrameCore(isolate, ref st, function, thisArg, windowStart, length, default, default, returnPc, windowStart, false, default);
        return true;
    }

    /// <summary>
    /// The Construct bytecode for an ordinary (not derived) constructor with
    /// bytecode: collects the construct feedback, allocates the receiver
    /// (JSConstructStubGeneric) and enters the constructor like
    /// <see cref="PushFrame"/>. False, with nothing done, for other constructors.
    /// </summary>
    public static bool TryPushConstructFrame(Isolate isolate, ref InterpreterState st, int slot, JSValue constructor,
        JSValue newTarget, int argsStart, int argc, int returnPc)
    {
        if (constructor._obj is not JSFunction function || !function.Map.IsConstructor ||
            newTarget._obj is not JSReceiver newTargetReceiver)
        {
            return false;
        }
        SharedFunctionInfo shared = function.Shared;
        if (shared.FunctionData is not BytecodeArray || shared.HasBuiltinId || Globals.IsDerivedConstructor(shared.Kind) ||
            Globals.IsResumableFunction(shared.Kind) || shared.BaselineCode is not null ||
            shared.MayHaveMaglevCode && function.RawFeedbackCell.Value is FeedbackVector { MaglevCode: not null })
        {
            return false;
        }
        InterpreterCalls.CollectConstructFeedback(isolate, st.FeedbackVector, slot, constructor, newTarget);

        JSValue implicitReceiver;
        if (ReferenceEquals(newTargetReceiver, function) && function.PrototypeOrInitialMap is Map initialMap &&
            !initialMap.IsDictionaryMap)
        {
            // FastNewObject: new.target is the constructor and its initial map
            // exists, so the allocation needs nothing from the context.
            implicitReceiver = isolate.Factory.NewJSObjectFromMap(initialMap);
        }
        else
        {
            // Allocate the new receiver object in the constructor's context.
            Context? saved = isolate.Context;
            isolate.Context = function.Context;
            try
            {
                implicitReceiver = JSObject.New(isolate, function, newTargetReceiver, null);
            }
            finally
            {
                isolate.Context = saved;
            }
        }

        // The construct stub's frame (see InterpreterCalls.ConstructInterpreted).
        int stubStart = isolate.AllocateRegisters(InterpreterCalls.kConstructStubFrameSlots);
        PushFrameCore(isolate, ref st, function, implicitReceiver, argsStart, argc, default, default, returnPc, stubStart, true,
            newTarget);
        return true;
    }

    static void PushFrameCore(Isolate isolate, ref InterpreterState st, JSFunction function, JSValue receiver, int argsStart,
        int argc, JSValue arg0, JSValue arg1, int returnPc, int registerStart, bool isConstruct, JSValue newTarget)
    {
        SharedFunctionInfo shared = function.Shared;
        if (isolate.StackGuard.HasPendingInterrupts) isolate.StackGuard.HandleInterrupts();

        // CanInline established the type.
        var bytecode = Unsafe.As<BytecodeArray>(shared.FunctionData!);
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
        // The trampoline fills the register file with undefined. The stack
        // above its top is undefined except below RegisterStackDirtyEnd, where
        // frames returned from inline left their values (PopFrame).
        int dirtyEnd = isolate.RegisterStackDirtyEnd;
        if (fp < dirtyEnd) ClearSlots(ref fpRef, (end < dirtyEnd ? end : dirtyEnd) - fp);
        Debug.Assert(MemoryMarshal.CreateSpan(ref fpRef, registerCount).IndexOfAnyExcept(default(JSValue)) < 0);

        // Reference stores cost a GC write barrier each. The parameters and
        // fixed slots may still hold the previous frame's values at this depth
        // (often the same closure, context and feedback vector, the same
        // receiver, number arguments): StoreSlot skips an unchanged reference.
        StoreSlot(ref Unsafe.Add(ref fpRef, InterpreterRuntime.kReceiverOffset), receiver);
        if (argsStart >= 0)
        {
            ref JSValue src = ref Unsafe.Add(ref stack0, argsStart);
            for (int i = 0; i < argc; i++) StoreSlot(ref Unsafe.Add(ref fpRef, InterpreterRuntime.kFirstArgumentOffset - i), Unsafe.Add(ref src, i));
        }
        else
        {
            StoreSlot(ref Unsafe.Add(ref fpRef, InterpreterRuntime.kFirstArgumentOffset), arg0);
            StoreSlot(ref Unsafe.Add(ref fpRef, InterpreterRuntime.kFirstArgumentOffset - 1), arg1);
        }
        // Missing arguments are undefined (V8's argument adaption).
        for (int i = argc; i < paramSlots; i++) StoreSlot(ref Unsafe.Add(ref fpRef, InterpreterRuntime.kFirstArgumentOffset - i), default);

        Context context = function.Context;
        if (!ReferenceEquals(isolate.Context, context)) isolate.Context = context;
        StoreSlot(ref Unsafe.Add(ref fpRef, InterpreterRuntime.kContextOffset), context);
        StoreSlot(ref Unsafe.Add(ref fpRef, InterpreterRuntime.kClosureOffset), function);
        // The argument count slot (fp - 4) is not read in V8Sharp: frames keep
        // the count in their record, so it is not written (a reference store).
        if (isConstruct)
        {
            Register incoming = bytecode.IncomingNewTargetOrGeneratorRegister;
            if (incoming.IsValid) Unsafe.Add(ref fpRef, incoming.Index) = newTarget;
        }

        FeedbackVector? feedbackVector = InterpreterExecution.FeedbackVectorOnEntry(isolate, function);
        StoreSlot(ref Unsafe.Add(ref fpRef, InterpreterRuntime.kFeedbackVectorOffset),
            feedbackVector is null ? JSValue.Undefined : feedbackVector);

        int depth = isolate.InterpreterFrameDepth;
        ref InterpreterFrameRecord frame = ref isolate.PushFrame();
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
        frame.RegisterStart = registerStart;
        // The caller resumes after the call bytecode.
        isolate.InterpreterFrames[st.FrameIndex].ReturnPc = returnPc;

        st.Function = function;
        st.Bytecode = bytecode;
        if (bytecode.ConstantPoolValues is null) InterpreterRuntime.MaterializeConstantPool(isolate, bytecode);
        st.FeedbackVector = feedbackVector;
        if (!ReferenceEquals(st.Context, context)) st.Context = context;
        st.Accumulator = JSValue.Undefined;
        st.Pc = 0;
        st.Fp = fp;
        st.FrameIndex = depth;
        st.Argc = argc;
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
        ref InterpreterFrameRecord frame = ref frames[st.FrameIndex];
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
        // RegisterStackDirtyEnd for the next call at this depth (PushFrameCore).
        int top = isolate.RegisterStackTop;
        if (top > isolate.RegisterStackDirtyEnd) isolate.RegisterStackDirtyEnd = top;
        isolate.RegisterStackTop = start;
        JSValue[] stack = isolate.RegisterStack;

        int callerIndex = st.FrameIndex - 1;
        ref InterpreterFrameRecord caller = ref frames[callerIndex];
        BytecodeArray bytecode = caller.Bytecode!;
        int fp = caller.Fp;
        Context context = stack[fp + InterpreterRuntime.kContextOffset].UncheckedAs<Context>();
        if (!ReferenceEquals(isolate.Context, context)) isolate.Context = context;

        st.Function = caller.Function;
        st.Bytecode = bytecode;
        // The slot holds the frame's FeedbackVector or undefined.
        st.FeedbackVector = Unsafe.As<FeedbackVector?>(stack[fp + InterpreterRuntime.kFeedbackVectorOffset]._obj);
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
