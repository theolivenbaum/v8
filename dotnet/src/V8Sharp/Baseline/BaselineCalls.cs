// Calls from baseline code to functions that run baseline code: the
// Call_ReceiverIs* builtins (src/builtins/builtins-call-gen.cc,
// Builtins::Generate_CallFunction) followed by the callee's baseline
// prologue (BaselineOutOfLinePrologue: frame setup, stack check, invocation
// count), without the interpreter's generic entry.
//
// This is InterpreterCalls.Call + InterpreterExecution.InvokeFromRegisters +
// EnterFrame specialized for a callee with baseline code and a feedback
// vector: the frame it builds is the same interpreter frame. Every other
// callee (builtins, bound functions, proxies, interpreted or uncompiled
// functions) takes InterpreterCalls.Call.
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using V8Sharp.Interpreter;
using V8Sharp.Runtime;

namespace V8Sharp.Baseline;

public static class BaselineCalls
{
    /// <summary>
    /// Calls <paramref name="callee"/> with the arguments in the register stack at
    /// <paramref name="argsStart"/> (the caller's register list).
    /// </summary>
    public static JSValue Call(Isolate isolate, JSValue callee, JSValue receiver, int argsStart, int argc, ConvertReceiverMode mode)
    {
        if (callee._obj is JSFunction function)
        {
            SharedFunctionInfo shared = function.Shared;
            BaselineCode? code = shared.BaselineCode;
            if (code is not null && function.RawFeedbackCell.Value is FeedbackVector vector)
            {
                if (shared.IsClassConstructor) return RuntimeClasses.ThrowConstructorNonCallableError(isolate, function);
                if (!receiver.IsJSReceiver && !shared.Native && shared.LanguageMode == LanguageMode.Sloppy)
                {
                    receiver = InterpreterCalls.ConvertReceiver(isolate, function, receiver);
                }
                return EnterFrame(isolate, function, code, vector, receiver, argsStart, argc, JSValue.Undefined, false);
            }
        }
        return InterpreterCalls.Call(isolate, callee, receiver, argsStart, argc, mode);
    }

    /// <summary>The construct stub's frame slots (InterpreterCalls.kConstructStubFrameSlots).</summary>
    const int kConstructStubFrameSlots = 16;

    /// <summary>
    /// Construct from baseline code: InterpreterCalls.Construct (Construct_Baseline,
    /// JSConstructStubGeneric) for a constructor with baseline code.
    /// </summary>
    public static JSValue Construct(Isolate isolate, FeedbackVector? fv, int slot, JSValue constructor, JSValue newTarget, int argsStart,
        int argc)
    {
        if (constructor._obj is JSFunction function && function.Shared.BaselineCode is { } code &&
            function.RawFeedbackCell.Value is FeedbackVector vector && function.Map.IsConstructor)
        {
            InterpreterCalls.CollectConstructFeedback(isolate, fv, slot, constructor, newTarget);
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
            int stubStart = isolate.AllocateRegisters(kConstructStubFrameSlots);
            JSValue result = EnterFrame(isolate, function, code, vector, implicitReceiver, argsStart, argc, newTarget, true);
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
        return InterpreterCalls.Construct(isolate, fv, slot, constructor, newTarget, argsStart, argc);
    }

    /// <summary>
    /// Builds the callee's interpreter frame (as InterpreterExecution.EnterFrame
    /// does) and runs its baseline code.
    /// </summary>
    static JSValue EnterFrame(Isolate isolate, JSFunction function, BaselineCode code, FeedbackVector vector, JSValue receiver,
        int argsStart, int argc, JSValue newTarget, bool isConstruct)
    {
        // The native stack check of the prologue (V8's StackOverflow on entry).
        if (!RuntimeHelpers.TryEnsureSufficientExecutionStack()) isolate.StackOverflow();

        BytecodeArray bytecode = code.Bytecode;
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
        // Push the arguments in V8's order (the last argument deepest); missing
        // arguments are undefined (V8's argument adaption).
        Unsafe.Add(ref fpRef, InterpreterRuntime.kReceiverOffset) = receiver;
        ref JSValue src = ref Unsafe.Add(ref stack0, argsStart);
        for (int i = 0; i < argc; i++) Unsafe.Add(ref fpRef, InterpreterRuntime.kFirstArgumentOffset - i) = Unsafe.Add(ref src, i);
        for (int i = argc; i < paramSlots; i++) Unsafe.Add(ref fpRef, InterpreterRuntime.kFirstArgumentOffset - i) = default;

        Context context = function.Context;
        Context? savedContext = isolate.Context;
        isolate.Context = context;
        Unsafe.Add(ref fpRef, InterpreterRuntime.kContextOffset) = context;
        Unsafe.Add(ref fpRef, InterpreterRuntime.kClosureOffset) = function;
        Unsafe.Add(ref fpRef, InterpreterRuntime.kArgcOffset) = JSValue.FromInt(argc);
        Unsafe.Add(ref fpRef, InterpreterRuntime.kFeedbackVectorOffset) = vector;
        // The prologue fills the register file with undefined.
        MemoryMarshal.CreateSpan(ref fpRef, registerCount).Clear();
        if (isConstruct)
        {
            Register incoming = bytecode.IncomingNewTargetOrGeneratorRegister;
            if (incoming.IsValid) Unsafe.Add(ref fpRef, incoming.Index) = newTarget;
        }

        int depth = isolate.InterpreterFrameDepth;
        ref InterpreterFrameRecord frame = ref isolate.PushFrame();
        frame.Function = function;
        frame.Bytecode = bytecode;
        frame.Fp = fp;
        frame.Pc = 0;
        frame.Argc = argc;
        frame.Kind = InterpreterFrameKind.Interpreted;
        frame.IsConstructor = isConstruct;
        frame.IsBaseline = true;

        vector.InvocationCount++;

        var state = new InterpreterState
        {
            Function = function,
            Bytecode = bytecode,
            Constants = bytecode.ConstantPoolValues ?? InterpreterRuntime.MaterializeConstantPool(isolate, bytecode),
            FeedbackVector = vector,
            Context = context,
            Accumulator = JSValue.Undefined,
            Pc = 0,
            Fp = fp,
            FrameIndex = depth,
            Argc = argc,
        };
        try
        {
            return code.HasHandlers ? BaselineExecution.Run(isolate, ref state, code) : code.Entry(isolate, ref state);
        }
        finally
        {
            isolate.PopFramesTo(depth);
            isolate.ReleaseRegisters(start);
            isolate.Context = savedContext;
        }
    }
}
