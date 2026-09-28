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
                !Globals.IsResumableFunction(shared.Kind) && shared.BaselineCode is null)
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
            Globals.IsResumableFunction(shared.Kind) || shared.BaselineCode is not null)
        {
            return false;
        }
        InterpreterCalls.CollectConstructFeedback(isolate, st.FeedbackVector, slot, constructor, newTarget);

        // Allocate the new receiver object in the constructor's context.
        Context? saved = isolate.Context;
        isolate.Context = function.Context;
        JSValue implicitReceiver;
        try
        {
            implicitReceiver = JSObject.New(isolate, function, newTargetReceiver, null);
        }
        finally
        {
            isolate.Context = saved;
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
        Unsafe.Add(ref fpRef, InterpreterRuntime.kReceiverOffset) = receiver;
        if (argsStart >= 0)
        {
            ref JSValue src = ref Unsafe.Add(ref stack0, argsStart);
            for (int i = 0; i < argc; i++) Unsafe.Add(ref fpRef, InterpreterRuntime.kFirstArgumentOffset - i) = Unsafe.Add(ref src, i);
        }
        else
        {
            Unsafe.Add(ref fpRef, InterpreterRuntime.kFirstArgumentOffset) = arg0;
            Unsafe.Add(ref fpRef, InterpreterRuntime.kFirstArgumentOffset - 1) = arg1;
        }
        // Missing arguments are undefined (V8's argument adaption).
        for (int i = argc; i < paramSlots; i++) Unsafe.Add(ref fpRef, InterpreterRuntime.kFirstArgumentOffset - i) = default;

        Context context = function.Context;
        isolate.Context = context;
        Unsafe.Add(ref fpRef, InterpreterRuntime.kContextOffset) = context;
        Unsafe.Add(ref fpRef, InterpreterRuntime.kClosureOffset) = function;
        // The argument count slot (fp - 4) is not read in V8Sharp: frames keep
        // the count in their record, so it is not written (a reference store).

        // The trampoline fills the register file with undefined: the register
        // stack above its top is always clear (released slots are cleared).
        Debug.Assert(MemoryMarshal.CreateSpan(ref fpRef, registerCount).IndexOfAnyExcept(default(JSValue)) < 0);
        if (isConstruct)
        {
            Register incoming = bytecode.IncomingNewTargetOrGeneratorRegister;
            if (incoming.IsValid) Unsafe.Add(ref fpRef, incoming.Index) = newTarget;
        }

        FeedbackVector? feedbackVector = InterpreterExecution.FeedbackVectorOnEntry(isolate, function);
        Unsafe.Add(ref fpRef, InterpreterRuntime.kFeedbackVectorOffset) =
            feedbackVector is null ? JSValue.Undefined : feedbackVector;

        int depth = isolate.InterpreterFrameDepth;
        ref InterpreterFrameRecord frame = ref isolate.PushFrame();
        frame.Function = function;
        frame.Bytecode = bytecode;
        frame.Receiver = default;
        frame.Fp = fp;
        frame.Pc = 0;
        frame.Argc = argc;
        frame.Kind = InterpreterFrameKind.Interpreted;
        frame.IsConstructor = isConstruct;
        frame.InlineCall = true;
        frame.RegisterStart = registerStart;
        // The caller resumes after the call bytecode.
        isolate.InterpreterFrames[st.FrameIndex].ReturnPc = returnPc;

        st.Function = function;
        st.Bytecode = bytecode;
        if (bytecode.ConstantPoolValues is null) InterpreterRuntime.MaterializeConstantPool(isolate, bytecode);
        st.FeedbackVector = feedbackVector;
        st.Context = context;
        st.Accumulator = JSValue.Undefined;
        st.Pc = 0;
        st.Fp = fp;
        st.FrameIndex = depth;
        st.Argc = argc;
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
    public static void PopFrame(Isolate isolate, ref InterpreterState st)
    {
        InterpreterFrameRecord[] frames = isolate.InterpreterFrames;
        int start = frames[st.FrameIndex].RegisterStart;
        isolate.PopFramesTo(st.FrameIndex);
        isolate.ReleaseRegisters(start);

        int callerIndex = st.FrameIndex - 1;
        ref InterpreterFrameRecord caller = ref frames[callerIndex];
        BytecodeArray bytecode = caller.Bytecode!;
        int fp = caller.Fp;
        JSValue[] stack = isolate.RegisterStack;
        Context context = stack[fp + InterpreterRuntime.kContextOffset].UncheckedAs<Context>();
        isolate.Context = context;

        st.Function = caller.Function;
        st.Bytecode = bytecode;
        st.FeedbackVector = stack[fp + InterpreterRuntime.kFeedbackVectorOffset]._obj as FeedbackVector;
        st.Context = context;
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
