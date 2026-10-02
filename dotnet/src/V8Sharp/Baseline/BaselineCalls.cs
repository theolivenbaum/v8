// Calls from baseline code: the Call_ReceiverIs*_Baseline and
// Construct_Baseline builtins (src/builtins/builtins-call-gen.cc,
// Builtins::Generate_CallFunction, JSConstructStubGeneric) followed, for a
// callee with baseline code, by its baseline prologue
// (BaselineOutOfLinePrologue: frame setup, stack check, invocation count).
//
// A call to a function that has baseline code and a feedback vector builds
// the callee's interpreter frame here and calls its code directly: this is
// V8's machine-level call from Sparkplug code into Sparkplug code. The frame
// is the one InterpreterExecution.EnterFrame builds, set up with the same
// economies as the interpreter's inline calls (InterpreterInlineCalls): the
// slots and the frame record a returned frame left at this depth are compared
// before each reference store (no GC write barrier for an unchanged closure,
// context, feedback vector, receiver or argument tag), the register file is
// cleared only where such a frame left values, and nothing is cleared on
// return.
//
// Deviation: there is no try/finally around the callee. An exception leaving
// it leaves the frame record, the register stack top and the current context
// as the callee had them; whoever catches it restores all three: the handler
// dispatch of an interpreted or baseline frame (TryDispatchToHandler) and the
// finally of every frame entered from C# (InterpreterExecution.EnterFrame),
// below which every C# catch sits. A .NET try/finally per JavaScript call
// costs more than the rest of the frame setup.
//
// Every other callee (builtins, bound functions, proxies, interpreted or
// uncompiled functions) takes InterpreterCalls.Call, after the CSA fast paths
// of the hottest builtins (BuiltinFastPaths), as the interpreter's call
// handlers do.
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using V8Sharp.Interpreter;
using V8Sharp.Runtime;

namespace V8Sharp.Baseline;

public static class BaselineCalls
{
    const MethodImplOptions Inline = MethodImplOptions.AggressiveInlining;

    // ---- The call bytecodes ----------------------------------------------------------------------
    //
    // NoInlining: the frame setup belongs out of line, as V8's calls into the
    // Call builtins; inlined into a baseline method it would bloat every call
    // site and make RyuJIT spill the caller's values around it.

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static JSValue CallProperty0(Isolate isolate, FeedbackVector fv, int slot, JSValue callee, JSValue receiver)
    {
        InterpreterCalls.CollectCallFeedback(isolate, fv, slot, callee, receiver);
        if (TryGetBaselineCallee(callee, out JSFunction function, out BaselineCode code, out FeedbackVector vector))
        {
            return EnterValues(isolate, function, code, vector, ConvertReceiver(isolate, function, code, receiver), 0, default, default);
        }
        return CallSlow0(isolate, callee, receiver, ConvertReceiverMode.NotNullOrUndefined);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static JSValue CallProperty1(Isolate isolate, FeedbackVector fv, int slot, JSValue callee, JSValue receiver, JSValue arg0)
    {
        InterpreterCalls.CollectCallFeedback(isolate, fv, slot, callee, receiver);
        if (TryGetBaselineCallee(callee, out JSFunction function, out BaselineCode code, out FeedbackVector vector))
        {
            return EnterValues(isolate, function, code, vector, ConvertReceiver(isolate, function, code, receiver), 1, arg0, default);
        }
        return CallSlow1(isolate, callee, receiver, arg0, ConvertReceiverMode.NotNullOrUndefined);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static JSValue CallProperty2(Isolate isolate, FeedbackVector fv, int slot, JSValue callee, JSValue receiver, JSValue arg0,
        JSValue arg1)
    {
        InterpreterCalls.CollectCallFeedback(isolate, fv, slot, callee, receiver);
        if (TryGetBaselineCallee(callee, out JSFunction function, out BaselineCode code, out FeedbackVector vector))
        {
            return EnterValues(isolate, function, code, vector, ConvertReceiver(isolate, function, code, receiver), 2, arg0, arg1);
        }
        return CallSlow2(isolate, callee, receiver, arg0, arg1, ConvertReceiverMode.NotNullOrUndefined);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static JSValue CallUndefinedReceiver0(Isolate isolate, FeedbackVector fv, int slot, JSValue callee)
    {
        InterpreterCalls.CollectCallFeedback(isolate, fv, slot, callee);
        if (TryGetBaselineCallee(callee, out JSFunction function, out BaselineCode code, out FeedbackVector vector))
        {
            return EnterValues(isolate, function, code, vector, UndefinedReceiver(function, code), 0, default, default);
        }
        return CallSlow0(isolate, callee, JSValue.Undefined, ConvertReceiverMode.NullOrUndefined);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static JSValue CallUndefinedReceiver1(Isolate isolate, FeedbackVector fv, int slot, JSValue callee, JSValue arg0)
    {
        InterpreterCalls.CollectCallFeedback(isolate, fv, slot, callee);
        if (TryGetBaselineCallee(callee, out JSFunction function, out BaselineCode code, out FeedbackVector vector))
        {
            return EnterValues(isolate, function, code, vector, UndefinedReceiver(function, code), 1, arg0, default);
        }
        return CallSlow1(isolate, callee, JSValue.Undefined, arg0, ConvertReceiverMode.NullOrUndefined);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static JSValue CallUndefinedReceiver2(Isolate isolate, FeedbackVector fv, int slot, JSValue callee, JSValue arg0,
        JSValue arg1)
    {
        InterpreterCalls.CollectCallFeedback(isolate, fv, slot, callee);
        if (TryGetBaselineCallee(callee, out JSFunction function, out BaselineCode code, out FeedbackVector vector))
        {
            return EnterValues(isolate, function, code, vector, UndefinedReceiver(function, code), 2, arg0, arg1);
        }
        return CallSlow2(isolate, callee, JSValue.Undefined, arg0, arg1, ConvertReceiverMode.NullOrUndefined);
    }

    /// <summary>CallProperty / CallAnyReceiver: the receiver and the arguments are the register list at <paramref name="first"/>.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static JSValue CallProperty(Isolate isolate, FeedbackVector fv, int slot, JSValue callee, int first, int count)
    {
        JSValue receiver = isolate.RegisterStack[first];
        InterpreterCalls.CollectCallFeedback(isolate, fv, slot, callee, receiver);
        if (TryGetBaselineCallee(callee, out JSFunction function, out BaselineCode code, out FeedbackVector vector))
        {
            return EnterRegisters(isolate, function, code, vector, ConvertReceiver(isolate, function, code, receiver), first + 1, count - 1);
        }
        return InterpreterCalls.Call(isolate, callee, receiver, first + 1, count - 1, ConvertReceiverMode.NotNullOrUndefined);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static JSValue CallAnyReceiver(Isolate isolate, FeedbackVector fv, int slot, JSValue callee, int first, int count)
    {
        JSValue receiver = isolate.RegisterStack[first];
        InterpreterCalls.CollectCallFeedback(isolate, fv, slot, callee, receiver);
        if (TryGetBaselineCallee(callee, out JSFunction function, out BaselineCode code, out FeedbackVector vector))
        {
            return EnterRegisters(isolate, function, code, vector, ConvertReceiver(isolate, function, code, receiver), first + 1, count - 1);
        }
        return InterpreterCalls.Call(isolate, callee, receiver, first + 1, count - 1, ConvertReceiverMode.Any);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static JSValue CallUndefinedReceiver(Isolate isolate, FeedbackVector fv, int slot, JSValue callee, int first, int count)
    {
        InterpreterCalls.CollectCallFeedback(isolate, fv, slot, callee);
        if (TryGetBaselineCallee(callee, out JSFunction function, out BaselineCode code, out FeedbackVector vector))
        {
            return EnterRegisters(isolate, function, code, vector, UndefinedReceiver(function, code), first, count);
        }
        return InterpreterCalls.Call(isolate, callee, JSValue.Undefined, first, count, ConvertReceiverMode.NullOrUndefined);
    }

    // ---- Callee classification -------------------------------------------------------------------

    /// <summary>
    /// A callee whose code is baseline code that [[Call]] can run directly: a
    /// JSFunction (not a class constructor) whose SharedFunctionInfo has
    /// baseline code and that has a feedback vector (Runtime_InstallBaselineCode
    /// gives one to a closure without; that call takes the slow path once).
    /// </summary>
    [MethodImpl(Inline)]
    static bool TryGetBaselineCallee(JSValue callee, out JSFunction function, out BaselineCode code, out FeedbackVector vector)
    {
        if (callee._obj is JSFunction f && f.Shared.BaselineCode is { CallableDirectly: true } c &&
            f.RawFeedbackCell.Value is FeedbackVector v)
        {
            function = f;
            code = c;
            vector = v;
            return true;
        }
        function = null!;
        code = null!;
        vector = null!;
        return false;
    }

    /// <summary>The receiver of a call with an undefined receiver (CallFunction's ConvertReceiverMode::kNullOrUndefined).</summary>
    [MethodImpl(Inline)]
    static JSValue UndefinedReceiver(JSFunction function, BaselineCode code) =>
        code.ConvertsReceiver ? function.Context.NativeContext.GlobalProxyObject : JSValue.Undefined;

    [MethodImpl(Inline)]
    static JSValue ConvertReceiver(Isolate isolate, JSFunction function, BaselineCode code, JSValue receiver) =>
        code.ConvertsReceiver && !receiver.IsJSReceiver ? InterpreterCalls.ConvertReceiver(isolate, function, receiver) : receiver;

    // ---- The slow paths ----------------------------------------------------------------------------

    [MethodImpl(MethodImplOptions.NoInlining)]
    static JSValue CallSlow0(Isolate isolate, JSValue callee, JSValue receiver, ConvertReceiverMode mode)
    {
        if (BuiltinFastPaths.TryCall0(isolate, callee, receiver, out JSValue result)) return result;
        return CallValues(isolate, callee, receiver, 0, default, default, mode);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static JSValue CallSlow1(Isolate isolate, JSValue callee, JSValue receiver, JSValue arg0, ConvertReceiverMode mode)
    {
        // a.push(x), Math.floor(x), s.charCodeAt(i) ...: the builtins' CSA fast paths.
        if (BuiltinFastPaths.TryCall1(isolate, callee, receiver, arg0, out JSValue result)) return result;
        return CallValues(isolate, callee, receiver, 1, arg0, default, mode);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static JSValue CallSlow2(Isolate isolate, JSValue callee, JSValue receiver, JSValue arg0, JSValue arg1, ConvertReceiverMode mode)
    {
        if (BuiltinFastPaths.TryCall2(callee, arg0, arg1, out JSValue result)) return result;
        return CallValues(isolate, callee, receiver, 2, arg0, arg1, mode);
    }

    /// <summary>InterpreterCalls.Call with up to two arguments passed as values.</summary>
    static JSValue CallValues(Isolate isolate, JSValue callee, JSValue receiver, int argc, JSValue arg0, JSValue arg1,
        ConvertReceiverMode mode)
    {
        // The arguments go into a window above the stack top, as the
        // interpreter's register list would hold them.
        int window = isolate.AllocateRegisters(2);
        JSValue[] stack = isolate.RegisterStack;
        stack[window] = arg0;
        stack[window + 1] = arg1;
        try
        {
            return InterpreterCalls.Call(isolate, callee, receiver, window, argc, mode);
        }
        finally
        {
            isolate.ReleaseRegisters(window);
        }
    }

    // ---- Construct ---------------------------------------------------------------------------------

    /// <summary>The construct stub's frame slots (InterpreterCalls.kConstructStubFrameSlots).</summary>
    const int kConstructStubFrameSlots = InterpreterCalls.kConstructStubFrameSlots;

    /// <summary>
    /// Construct from baseline code: InterpreterCalls.Construct (Construct_Baseline,
    /// JSConstructStubGeneric) for a constructor with baseline code.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static JSValue Construct(Isolate isolate, FeedbackVector? fv, int slot, JSValue constructor, JSValue newTarget, int argsStart,
        int argc)
    {
        if (constructor._obj is JSFunction function && function.Shared.BaselineCode is { } code &&
            function.RawFeedbackCell.Value is FeedbackVector vector && function.Map.IsConstructor &&
            newTarget._obj is JSReceiver newTargetReceiver)
        {
            InterpreterCalls.CollectConstructFeedback(isolate, fv, slot, constructor, newTarget);
            JSValue implicitReceiver;
            if (Globals.IsDerivedConstructor(function.Shared.Kind))
            {
                // Else: use TdzHoleValue as receiver for constructor call
                implicitReceiver = JSValue.TheHole;
            }
            else if (ReferenceEquals(newTargetReceiver, function) && function.PrototypeOrInitialMap is Map initialMap &&
                     !initialMap.IsDictionaryMap)
            {
                // FastNewObject: new.target is the constructor and its initial map
                // exists, so the allocation needs nothing from the context.
                implicitReceiver = isolate.Factory.NewJSObjectFromMap(initialMap);
            }
            else
            {
                // If not derived class constructor: Allocate the new receiver object.
                implicitReceiver = AllocateReceiver(isolate, function, newTargetReceiver);
            }
            int stubStart = isolate.AllocateRegisters(kConstructStubFrameSlots);
            JSValue result = Enter(isolate, function, code, vector, implicitReceiver, argsStart, argc, default, default, newTarget, true);
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

    [MethodImpl(MethodImplOptions.NoInlining)]
    static JSValue AllocateReceiver(Isolate isolate, JSFunction function, JSReceiver newTarget)
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

    // ---- Entering baseline code ------------------------------------------------------------------

    /// <summary>A call whose arguments are values (at most two).</summary>
    [MethodImpl(Inline)]
    static JSValue EnterValues(Isolate isolate, JSFunction function, BaselineCode code, FeedbackVector vector, JSValue receiver,
        int argc, JSValue arg0, JSValue arg1) =>
        Enter(isolate, function, code, vector, receiver, -1, argc, arg0, arg1, default, false);

    /// <summary>A call whose arguments are the registers at <paramref name="argsStart"/>.</summary>
    [MethodImpl(Inline)]
    static JSValue EnterRegisters(Isolate isolate, JSFunction function, BaselineCode code, FeedbackVector vector, JSValue receiver,
        int argsStart, int argc) =>
        Enter(isolate, function, code, vector, receiver, argsStart, argc, default, default, default, false);

    /// <summary>
    /// Builds the callee's interpreter frame (the layout InterpreterExecution.EnterFrame
    /// builds) and runs its baseline code. The arguments are the
    /// <paramref name="argc"/> registers at <paramref name="argsStart"/>, or
    /// <paramref name="arg0"/> / <paramref name="arg1"/> when argsStart is negative.
    /// </summary>
    static JSValue Enter(Isolate isolate, JSFunction function, BaselineCode code, FeedbackVector vector, JSValue receiver,
        int argsStart, int argc, JSValue arg0, JSValue arg1, JSValue newTarget, bool isConstruct)
    {
        // The interrupt check of the prologue's stack check, as on the
        // interpreter's entry (InterpreterInlineCalls.PushFrameCore).
        if (isolate.StackGuard.HasPendingInterrupts) isolate.StackGuard.HandleInterrupts();
        int depth = isolate.InterpreterFrameDepth;
        // The native stack check of the prologue (V8's StackOverflow on entry).
        // Each level costs two .NET frames (this one and the callee's code);
        // checking every fourth level stays well inside the 128 KB the check
        // guarantees, except for large functions, which check on every call.
        if (((depth & 3) == 0 || code.CheckStackOnEveryCall) && !RuntimeHelpers.TryEnsureSufficientExecutionStack())
        {
            isolate.StackOverflow();
        }

        int formal = code.FormalParameterCount;
        int paramSlots = argc > formal ? argc : formal;
        int start = isolate.RegisterStackTop;
        int fp = start + paramSlots + InterpreterRuntime.kFixedSlotsAboveParams;
        int registerCount = code.RegisterCount;
        int end = fp + registerCount;
        if ((uint)end > (uint)isolate.RegisterStackLimit) isolate.StackOverflow();
        isolate.RegisterStackTop = end;

        ref JSValue stack0 = ref MemoryMarshal.GetArrayDataReference(isolate.RegisterStack);
        ref JSValue fpRef = ref Unsafe.Add(ref stack0, fp);
        // The prologue fills the register file with undefined. The stack above
        // its top is undefined except below RegisterStackDirtyEnd, where
        // returned frames left their values.
        int dirtyEnd = isolate.RegisterStackDirtyEnd;
        if (fp < dirtyEnd) ClearSlots(ref fpRef, (end < dirtyEnd ? end : dirtyEnd) - fp);

        // Push the arguments in V8's order (the last argument deepest); missing
        // arguments are undefined (V8's argument adaption).
        StoreSlot(ref Unsafe.Add(ref fpRef, InterpreterRuntime.kReceiverOffset), receiver);
        if (argsStart >= 0)
        {
            ref JSValue src = ref Unsafe.Add(ref stack0, argsStart);
            for (int i = 0; i < argc; i++) StoreSlot(ref Unsafe.Add(ref fpRef, InterpreterRuntime.kFirstArgumentOffset - i), Unsafe.Add(ref src, i));
        }
        else if (argc > 0)
        {
            StoreSlot(ref Unsafe.Add(ref fpRef, InterpreterRuntime.kFirstArgumentOffset), arg0);
            if (argc > 1) StoreSlot(ref Unsafe.Add(ref fpRef, InterpreterRuntime.kFirstArgumentOffset - 1), arg1);
        }
        for (int i = argc; i < paramSlots; i++) StoreSlot(ref Unsafe.Add(ref fpRef, InterpreterRuntime.kFirstArgumentOffset - i), default);

        Context context = function.Context;
        Context? savedContext = isolate.Context;
        if (!ReferenceEquals(savedContext, context)) isolate.Context = context;
        StoreSlot(ref Unsafe.Add(ref fpRef, InterpreterRuntime.kContextOffset), context);
        StoreSlot(ref Unsafe.Add(ref fpRef, InterpreterRuntime.kClosureOffset), function);
        // The argument count slot (fp - 4) is not read in V8Sharp (frames keep
        // the count in their record), as for the interpreter's inline calls.
        StoreSlot(ref Unsafe.Add(ref fpRef, InterpreterRuntime.kFeedbackVectorOffset), vector);
        if (isConstruct && code.IncomingNewTargetRegister != int.MinValue)
        {
            Unsafe.Add(ref fpRef, code.IncomingNewTargetRegister) = newTarget;
        }

        BytecodeArray bytecode = code.Bytecode;
        ref InterpreterFrameRecord frame = ref isolate.PushFrame();
        // A returned frame leaves Function and Bytecode in its record.
        if (!ReferenceEquals(frame.Function, function)) frame.Function = function;
        if (!ReferenceEquals(frame.Bytecode, bytecode)) frame.Bytecode = bytecode;
        frame.Fp = fp;
        frame.Pc = 0;
        frame.Argc = argc;
        frame.Kind = InterpreterFrameKind.Interpreted;
        frame.IsConstructor = isConstruct;
        frame.IsBaseline = true;
        frame.InlineCall = false;
        frame.ReturnPc = 0;
        frame.RegisterStart = 0;

        vector.InvocationCount++;

        var state = new InterpreterState
        {
            Isolate = isolate,
            Function = function,
            Bytecode = bytecode,
            FeedbackVector = vector,
            Context = context,
            Pc = 0,
            Fp = fp,
            FrameIndex = depth,
            BaseFrameIndex = depth,
            Argc = argc,
        };
        JSValue result = code.HasHandlers ? BaselineExecution.Run(isolate, ref state, code) : code.Entry(isolate, ref state);

        // BaselineLeaveFrame. The frame's slots stay below RegisterStackDirtyEnd
        // for the next call at this depth; the record keeps Function and Bytecode.
        isolate.InterpreterFrameDepth = depth;
        int top = isolate.RegisterStackTop;
        if (top > isolate.RegisterStackDirtyEnd) isolate.RegisterStackDirtyEnd = top;
        isolate.RegisterStackTop = start;
        if (!ReferenceEquals(isolate.Context, savedContext)) isolate.Context = savedContext;
        return result;
    }

    /// <summary>Sets <paramref name="count"/> slots to undefined (a loop for the usual small register files).</summary>
    [MethodImpl(Inline)]
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

    /// <summary>A register stack store that skips the reference part when the slot already holds it (no write barrier).</summary>
    [MethodImpl(Inline)]
    static void StoreSlot(ref JSValue slot, JSValue value)
    {
        if (!ReferenceEquals(slot._obj, value._obj)) Unsafe.AsRef(in slot._obj) = value._obj;
        Unsafe.AsRef(in slot._num) = value._num;
    }
}
