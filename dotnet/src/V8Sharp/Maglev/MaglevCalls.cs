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
    public static JSValue Call(Isolate isolate, JSValue callee, JSValue receiver, int argsStart, int argc, ConvertReceiverMode mode)
    {
        if (callee._obj is JSFunction function)
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
        return Baseline.BaselineCalls.Call(isolate, callee, receiver, argsStart, argc, mode);
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
    public static JSValue ConstructWithReceiver(Isolate isolate, JSValue target, JSValue receiver, JSValue newTarget, int argsStart,
        int argc)
    {
        var function = Unsafe.As<JSFunction>(target._obj!);
        int stubStart = isolate.AllocateRegisters(InterpreterCalls.kConstructStubFrameSlots);
        JSValue result = function.RawFeedbackCell.Value is FeedbackVector { MaglevCode: { } code } vector
            ? EnterFrame(isolate, function, code, vector, receiver, argsStart, argc, newTarget, true)
            : InterpreterExecution.InvokeFromRegisters(isolate, function, receiver, argsStart, argc, newTarget, true);
        isolate.RegisterStackTop = stubStart;
        return result.IsJSReceiver ? result : receiver;
    }

    /// <summary>
    /// Builds the callee's interpreter frame (as InterpreterExecution.EnterFrame
    /// does) and runs its Maglev code.
    /// </summary>
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
        // The context, closure and feedback vector slots are not written: the
        // code reads them from the state, and the Deoptimizer writes them when
        // the frame continues in the interpreter (each is a GC write barrier).
        Unsafe.Add(ref fpRef, InterpreterRuntime.kArgcOffset) = JSValue.FromInt(argc);
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
        // The record of a popped frame keeps Function and Bytecode (as
        // InterpreterInlineCalls does): a call at the same depth skips the stores.
        if (!ReferenceEquals(frame.Function, function)) frame.Function = function;
        if (!ReferenceEquals(frame.Bytecode, bytecode)) frame.Bytecode = bytecode;
        frame.Fp = fp;
        frame.Pc = 0;
        frame.Argc = argc;
        frame.Kind = InterpreterFrameKind.Interpreted;
        frame.IsConstructor = isConstruct;
        frame.IsBaseline = false;
        frame.IsMaglev = true;
        frame.InlineCall = false;
        frame.ReturnPc = 0;
        frame.RegisterStart = 0;
        frame.Receiver = default;

        vector.InvocationCount++;

        var state = new InterpreterState
        {
            Isolate = isolate,
            Function = function,
            Bytecode = bytecode,
            FeedbackVector = vector,
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
