// Port of the interpreter entry and exit of V8: InterpreterEntryTrampoline
// and ResumeGeneratorTrampoline (src/builtins/x64/builtins-x64.cc), the frame
// setup they do, and the exception unwinding of Isolate::UnwindAndFindHandler
// for interpreted frames (src/execution/isolate.cc).
//
// A call into bytecode reserves a frame on the isolate's register stack
// (InterpreterFrames.cs), pushes a frame record, and runs the dispatch loop.
// An exception thrown while the frame runs is looked up in the frame's handler
// table; if it has a handler, execution continues there with the exception in
// the accumulator, otherwise it leaves the frame as a JavaScriptException.
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using V8Sharp.Codegen;

namespace V8Sharp.Interpreter;

public static partial class InterpreterExecution
{
    /// <summary>Installs the interpreter on an isolate: the entry trampoline and the frame walker.</summary>
    public static void Install(Isolate isolate)
    {
        isolate.InterpreterEntry = Entry;
        isolate.Frames = new InterpreterFrames(isolate);
        isolate.InterpreterData ??= new InterpreterIsolateData();
    }

    /// <summary>
    /// The InterpreterEntryTrampoline as seen by Execution.Call/New: the
    /// receiver is already converted and the function's context entered.
    /// </summary>
    public static JSValue Entry(Isolate isolate, JSFunction function, JSValue receiver, ReadOnlySpan<JSValue> arguments,
        JSValue newTarget) =>
        Invoke(isolate, function, receiver, arguments, newTarget, !newTarget.IsUndefined);

    /// <summary>
    /// Runs the bytecode of <paramref name="function"/> (compiled) with the given
    /// receiver and arguments. <paramref name="newTargetOrGenerator"/> goes to the
    /// bytecode's incoming new.target / generator register.
    /// </summary>
    public static JSValue Invoke(Isolate isolate, JSFunction function, JSValue receiver, ReadOnlySpan<JSValue> arguments,
        JSValue newTargetOrGenerator, bool isConstruct)
    {
        // The native stack check of the trampoline (V8's StackOverflow on entry).
        if (!RuntimeHelpers.TryEnsureSufficientExecutionStack()) isolate.StackOverflow();

        var bytecode = (BytecodeArray)function.Shared.FunctionData!;
        JSValue[] stack = isolate.RegisterStack;
        int argc = arguments.Length;
        int formal = bytecode.ParameterCount - 1;
        int paramSlots = argc > formal ? argc : formal;
        int start = isolate.RegisterStackTop;
        int fp = start + paramSlots + InterpreterRuntime.kFixedSlotsAboveParams;
        int end = fp + bytecode.RegisterCount;
        if ((uint)end > (uint)isolate.RegisterStackLimit) isolate.StackOverflow();
        isolate.RegisterStackTop = end;

        // Push the arguments in V8's order (the last argument deepest).
        ref JSValue fpRef = ref stack[fp];
        Unsafe.Add(ref fpRef, InterpreterRuntime.kReceiverOffset) = receiver;
        for (int i = 0; i < argc; i++) Unsafe.Add(ref fpRef, InterpreterRuntime.kFirstArgumentOffset - i) = arguments[i];
        // Missing arguments are undefined (V8's argument adaption).
        for (int i = argc; i < paramSlots; i++) Unsafe.Add(ref fpRef, InterpreterRuntime.kFirstArgumentOffset - i) = default;

        return EnterFrame(isolate, function, bytecode, fp, start, argc, newTargetOrGenerator, isConstruct);
    }

    /// <summary>
    /// Invoke for arguments that already sit in the caller's register file
    /// (<paramref name="argsStart"/> is a register stack index), avoiding the span.
    /// </summary>
    internal static JSValue InvokeFromRegisters(Isolate isolate, JSFunction function, JSValue receiver, int argsStart, int argc,
        JSValue newTargetOrGenerator, bool isConstruct)
    {
        if (!RuntimeHelpers.TryEnsureSufficientExecutionStack()) isolate.StackOverflow();

        var bytecode = (BytecodeArray)function.Shared.FunctionData!;
        JSValue[] stack = isolate.RegisterStack;
        int formal = bytecode.ParameterCount - 1;
        int paramSlots = argc > formal ? argc : formal;
        int start = isolate.RegisterStackTop;
        int fp = start + paramSlots + InterpreterRuntime.kFixedSlotsAboveParams;
        int end = fp + bytecode.RegisterCount;
        if ((uint)end > (uint)isolate.RegisterStackLimit) isolate.StackOverflow();
        isolate.RegisterStackTop = end;

        ref JSValue stack0 = ref MemoryMarshal.GetArrayDataReference(stack);
        ref JSValue fpRef = ref Unsafe.Add(ref stack0, fp);
        Unsafe.Add(ref fpRef, InterpreterRuntime.kReceiverOffset) = receiver;
        ref JSValue src = ref Unsafe.Add(ref stack0, argsStart);
        for (int i = 0; i < argc; i++) Unsafe.Add(ref fpRef, InterpreterRuntime.kFirstArgumentOffset - i) = Unsafe.Add(ref src, i);
        // Missing arguments are undefined (V8's argument adaption).
        for (int i = argc; i < paramSlots; i++) Unsafe.Add(ref fpRef, InterpreterRuntime.kFirstArgumentOffset - i) = default;

        return EnterFrame(isolate, function, bytecode, fp, start, argc, newTargetOrGenerator, isConstruct);
    }

    static JSValue EnterFrame(Isolate isolate, JSFunction function, BytecodeArray bytecode, int fp, int start, int argc,
        JSValue newTargetOrGenerator, bool isConstruct)
    {
        JSValue[] stack = isolate.RegisterStack;
        Context context = function.Context;
        Context? savedContext = isolate.Context;
        isolate.Context = context;

        ref JSValue fpRef = ref stack[fp];
        Unsafe.Add(ref fpRef, InterpreterRuntime.kContextOffset) = context;
        Unsafe.Add(ref fpRef, InterpreterRuntime.kClosureOffset) = function;
        Unsafe.Add(ref fpRef, InterpreterRuntime.kArgcOffset) = JSValue.FromInt(argc);

        // The trampoline fills the register file with undefined.
        stack.AsSpan(fp, bytecode.RegisterCount).Clear();

        Register incoming = bytecode.IncomingNewTargetOrGeneratorRegister;
        if (incoming.IsValid) Unsafe.Add(ref fpRef, incoming.Index) = newTargetOrGenerator;

        int depth = isolate.InterpreterFrameDepth;
        ref InterpreterFrameRecord frame = ref isolate.PushFrame();
        frame.Function = function;
        frame.Bytecode = bytecode;
        frame.Fp = fp;
        frame.Pc = 0;
        frame.Argc = argc;
        frame.Kind = InterpreterFrameKind.Interpreted;
        frame.IsConstructor = isConstruct;
        frame.IsBaseline = false;

        // BaselineOrInterpreterEntry: a function whose SharedFunctionInfo has
        // baseline code runs it (Runtime_InstallBaselineCode gives it the
        // feedback vector baseline code needs).
        Baseline.BaselineCode? baselineCode = function.Shared.BaselineCode;
        FeedbackVector? feedbackVector = baselineCode is not null && function.RawFeedbackCell.Value is not FeedbackVector
            ? Baseline.BaselineExecution.InstallBaselineCode(isolate, function)
            : FeedbackVectorOnEntry(isolate, function);
        Unsafe.Add(ref fpRef, InterpreterRuntime.kFeedbackVectorOffset) =
            feedbackVector is null ? JSValue.Undefined : feedbackVector;

        // The loop reads the constants through the bytecode (ConstantPoolValues).
        if (bytecode.ConstantPoolValues is null) InterpreterRuntime.MaterializeConstantPool(isolate, bytecode);
        var state = new InterpreterState
        {
            Function = function,
            Bytecode = bytecode,
            FeedbackVector = feedbackVector,
            Context = context,
            Accumulator = JSValue.Undefined,
            Pc = 0,
            Fp = fp,
            FrameIndex = depth,
            BaseFrameIndex = depth,
            Argc = argc,
        };
        try
        {
            return baselineCode is null ? Run(isolate, ref state) : Baseline.BaselineExecution.Run(isolate, ref state, baselineCode);
        }
        finally
        {
            isolate.PopFramesTo(depth);
            isolate.ReleaseRegisters(start);
            isolate.Context = savedContext;
        }
    }

    /// <summary>
    /// The feedback vector of a function being entered: JSFunction::InitializeFeedbackCell
    /// on first entry (V8 does it when the function is compiled / its code
    /// installed), and the invocation count.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static FeedbackVector? FeedbackVectorOnEntry(Isolate isolate, JSFunction function)
    {
        if (function.RawFeedbackCell.Value is FeedbackVector feedbackVector)
        {
            feedbackVector.InvocationCount++;
            return feedbackVector;
        }
        return InitializeFeedbackOnEntry(isolate, function);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static FeedbackVector? InitializeFeedbackOnEntry(Isolate isolate, JSFunction function)
    {
        if (function.RawFeedbackCell.Value is ClosureFeedbackCellArray) return null;
        JSFunctionFeedback.InitializeFeedbackCell(isolate, function, false);
        return function.RawFeedbackCell.Value as FeedbackVector;
    }

    /// <summary>
    /// Runs the dispatch loop, catching exceptions and dispatching them to the
    /// handler tables of its frames (Isolate::UnwindAndFindHandler for this
    /// frame and the calls it runs inline, InterpreterInlineCalls).
    /// </summary>
    internal static JSValue Run(Isolate isolate, ref InterpreterState state)
    {
        while (true)
        {
            try
            {
                JSValue result = Loop<SingleScale>(isolate, ref state);
                if (!state.OsrToBaseline) return result;

                // InterpreterOnStackReplacement_ToBaseline: JumpLoop found baseline
                // code; the frame continues in it at the loop header (state.Pc).
                state.OsrToBaseline = false;
                Baseline.BaselineCode code = state.Function.Shared.BaselineCode!;
                if (state.FrameIndex == state.BaseFrameIndex) return Baseline.BaselineExecution.Run(isolate, ref state, code);
                // An inline frame (InterpreterInlineCalls): it finishes in baseline
                // code, then its caller continues in this loop.
                JSValue value = Baseline.BaselineExecution.Run(isolate, ref state, code);
                state.Accumulator = InterpreterInlineCalls.Return(isolate, ref state, value);
            }
            // The filter only looks for a handler: an exception these frames do
            // not handle keeps propagating without a catch-and-rethrow, which
            // would run every outer frame's handler nested on the .NET stack.
            catch (JavaScriptException e) when (InterpreterInlineCalls.AnyFrameHasHandler(isolate, ref state))
            {
                InterpreterInlineCalls.UnwindToHandler(isolate, ref state, e.Value, e.MessageObject);
            }
        }
    }

    /// <summary>Whether this frame's handler table covers the current bytecode offset.</summary>
    internal static bool HasHandler(Isolate isolate, ref InterpreterState state)
    {
        byte[] handlerTableBytes = state.Bytecode.HandlerTable;
        if (handlerTableBytes.Length == 0) return false;
        int pc = isolate.InterpreterFrames[state.FrameIndex].Pc;
        return new HandlerTable(handlerTableBytes).LookupHandlerIndexForRange(pc) >= 0;
    }

    /// <summary>
    /// Looks up the handler for the current bytecode offset; if there is one,
    /// sets up the frame to continue there (context from the handler's context
    /// register, the exception in the accumulator, the pending message set).
    /// </summary>
    internal static bool TryDispatchToHandler(Isolate isolate, ref InterpreterState state, JSValue exception,
        JSMessageObject? message)
    {
        // The current offset is kept in the frame record (the loop stores it before each bytecode).
        int pc = isolate.InterpreterFrames[state.FrameIndex].Pc;
        byte[] handlerTableBytes = state.Bytecode.HandlerTable;
        if (handlerTableBytes.Length == 0) return false;
        var table = new HandlerTable(handlerTableBytes);
        int index = table.LookupHandlerIndexForRange(pc);
        if (index < 0) return false;

        // Termination is not catchable (it is a different .NET exception type),
        // and neither is the stack-overflow RangeError while the stack is still
        // exhausted: V8 unwinds it to a handler like any other exception.
        int contextRegister = table.GetRangeData((uint)index);
        int handlerOffset = table.GetRangeHandler((uint)index);

        JSValue[] stack = isolate.RegisterStack;
        // The context register is a register index (Register(context_register)).
        Context context = stack[state.Fp + contextRegister].As<Context>();
        state.Context = context;
        isolate.Context = context;
        stack[state.Fp + InterpreterRuntime.kContextOffset] = context;
        state.Accumulator = exception;
        state.Pc = handlerOffset;
        isolate.PendingMessage = message is null ? JSValue.TheHole : message;
        // Frames above this one are gone (their finally blocks popped them), and
        // so is any stack space reserved above this frame's register file.
        isolate.InterpreterFrameDepth = state.FrameIndex + 1;
        // Released slots are cleared: the register stack above its top is
        // always undefined (InterpreterInlineCalls relies on it).
        int top = state.Fp + state.Bytecode.RegisterCount;
        if (isolate.RegisterStackTop > top) isolate.ReleaseRegisters(top);
        else isolate.RegisterStackTop = top;
        return true;
    }

    /// <summary>
    /// The Throw bytecode: creates the message like Isolate::Throw and, when the
    /// current frame has a handler, continues there without a .NET exception.
    /// Returns false when the exception must leave the frame (the caller throws).
    /// </summary>
    internal static JSMessageObject? CreateMessageForThrow(Isolate isolate, JSValue exception)
    {
        if (isolate.BootstrapperActive) return null;
        MessageLocation? location = null;
        if (isolate.ComputeLocation(out MessageLocation computed)) location = computed;
        return isolate.CreateMessageOrAbort(exception, location);
    }
}
