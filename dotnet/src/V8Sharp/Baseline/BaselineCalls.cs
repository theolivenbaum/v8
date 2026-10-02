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

    // The out-of-line call paths are compiled with full optimization on first
    // use (no RyuJIT tier 0), like the interpreter's dispatch loop: baseline
    // code calls them from its first run, and V8's Call builtins it stands in
    // for are optimized code.
    const MethodImplOptions Outline = MethodImplOptions.NoInlining | MethodImplOptions.AggressiveOptimization;

    // ---- The call bytecodes ----------------------------------------------------------------------
    //
    // NoInlining: the frame setup belongs out of line, as V8's calls into the
    // Call builtins; inlined into a baseline method it would bloat every call
    // site and make RyuJIT spill the caller's values around it.

    [MethodImpl(Outline)]
    public static JSValue CallProperty0(Isolate isolate, FeedbackVector fv, int slot, JSValue callee, JSValue receiver)
    {
        InterpreterCalls.CollectCallFeedback(isolate, fv, slot, callee, receiver);
        if (TryGetBaselineCallee(callee, out JSFunction function, out BaselineCode code, out FeedbackVector vector))
        {
            return Enter(isolate, function, code, vector, ConvertReceiver(isolate, function, code, receiver), new NoArguments());
        }
        return CallSlow0(isolate, callee, receiver, ConvertReceiverMode.NotNullOrUndefined);
    }

    [MethodImpl(Outline)]
    public static JSValue CallProperty1(Isolate isolate, FeedbackVector fv, int slot, JSValue callee, JSValue receiver, JSValue arg0)
    {
        InterpreterCalls.CollectCallFeedback(isolate, fv, slot, callee, receiver);
        if (TryGetBaselineCallee(callee, out JSFunction function, out BaselineCode code, out FeedbackVector vector))
        {
            return Enter(isolate, function, code, vector, ConvertReceiver(isolate, function, code, receiver), new OneArgument(arg0));
        }
        return CallSlow1(isolate, callee, receiver, arg0, ConvertReceiverMode.NotNullOrUndefined);
    }

    [MethodImpl(Outline)]
    public static JSValue CallProperty2(Isolate isolate, FeedbackVector fv, int slot, JSValue callee, JSValue receiver, JSValue arg0,
        JSValue arg1)
    {
        InterpreterCalls.CollectCallFeedback(isolate, fv, slot, callee, receiver);
        if (TryGetBaselineCallee(callee, out JSFunction function, out BaselineCode code, out FeedbackVector vector))
        {
            return Enter(isolate, function, code, vector, ConvertReceiver(isolate, function, code, receiver), new TwoArguments(arg0, arg1));
        }
        return CallSlow2(isolate, callee, receiver, arg0, arg1, ConvertReceiverMode.NotNullOrUndefined);
    }

    [MethodImpl(Outline)]
    public static JSValue CallUndefinedReceiver0(Isolate isolate, FeedbackVector fv, int slot, JSValue callee)
    {
        InterpreterCalls.CollectCallFeedback(isolate, fv, slot, callee);
        if (TryGetBaselineCallee(callee, out JSFunction function, out BaselineCode code, out FeedbackVector vector))
        {
            return Enter(isolate, function, code, vector, UndefinedReceiver(function, code), new NoArguments());
        }
        return CallSlow0(isolate, callee, JSValue.Undefined, ConvertReceiverMode.NullOrUndefined);
    }

    [MethodImpl(Outline)]
    public static JSValue CallUndefinedReceiver1(Isolate isolate, FeedbackVector fv, int slot, JSValue callee, JSValue arg0)
    {
        InterpreterCalls.CollectCallFeedback(isolate, fv, slot, callee);
        if (TryGetBaselineCallee(callee, out JSFunction function, out BaselineCode code, out FeedbackVector vector))
        {
            return Enter(isolate, function, code, vector, UndefinedReceiver(function, code), new OneArgument(arg0));
        }
        return CallSlow1(isolate, callee, JSValue.Undefined, arg0, ConvertReceiverMode.NullOrUndefined);
    }

    [MethodImpl(Outline)]
    public static JSValue CallUndefinedReceiver2(Isolate isolate, FeedbackVector fv, int slot, JSValue callee, JSValue arg0,
        JSValue arg1)
    {
        InterpreterCalls.CollectCallFeedback(isolate, fv, slot, callee);
        if (TryGetBaselineCallee(callee, out JSFunction function, out BaselineCode code, out FeedbackVector vector))
        {
            return Enter(isolate, function, code, vector, UndefinedReceiver(function, code), new TwoArguments(arg0, arg1));
        }
        return CallSlow2(isolate, callee, JSValue.Undefined, arg0, arg1, ConvertReceiverMode.NullOrUndefined);
    }

    /// <summary>CallProperty / CallAnyReceiver: the receiver and the arguments are the register list at <paramref name="first"/>.</summary>
    [MethodImpl(Outline)]
    public static JSValue CallProperty(Isolate isolate, FeedbackVector fv, int slot, JSValue callee, int first, int count)
    {
        JSValue receiver = isolate.RegisterStack[first];
        InterpreterCalls.CollectCallFeedback(isolate, fv, slot, callee, receiver);
        if (TryGetBaselineCallee(callee, out JSFunction function, out BaselineCode code, out FeedbackVector vector))
        {
            return Enter(isolate, function, code, vector, ConvertReceiver(isolate, function, code, receiver), new RegisterArguments(first + 1, count - 1));
        }
        return CallSlowRegisters(isolate, callee, receiver, first + 1, count - 1, ConvertReceiverMode.NotNullOrUndefined);
    }

    [MethodImpl(Outline)]
    public static JSValue CallAnyReceiver(Isolate isolate, FeedbackVector fv, int slot, JSValue callee, int first, int count)
    {
        JSValue receiver = isolate.RegisterStack[first];
        InterpreterCalls.CollectCallFeedback(isolate, fv, slot, callee, receiver);
        if (TryGetBaselineCallee(callee, out JSFunction function, out BaselineCode code, out FeedbackVector vector))
        {
            return Enter(isolate, function, code, vector, ConvertReceiver(isolate, function, code, receiver), new RegisterArguments(first + 1, count - 1));
        }
        return CallSlowRegisters(isolate, callee, receiver, first + 1, count - 1, ConvertReceiverMode.Any);
    }

    [MethodImpl(Outline)]
    public static JSValue CallUndefinedReceiver(Isolate isolate, FeedbackVector fv, int slot, JSValue callee, int first, int count)
    {
        InterpreterCalls.CollectCallFeedback(isolate, fv, slot, callee);
        if (TryGetBaselineCallee(callee, out JSFunction function, out BaselineCode code, out FeedbackVector vector))
        {
            return Enter(isolate, function, code, vector, UndefinedReceiver(function, code), new RegisterArguments(first, count));
        }
        return CallSlowRegisters(isolate, callee, JSValue.Undefined, first, count, ConvertReceiverMode.NullOrUndefined);
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
        // (A closure with Maglev code runs it: the slow path enters through InterpreterExecution.EnterFrame.)
        if (callee._obj is JSFunction f && f.Shared.BaselineCode is { CallableDirectly: true } c &&
            f.RawFeedbackCell.Value is FeedbackVector { MaglevCode: null } v)
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

    [MethodImpl(Outline)]
    static JSValue CallSlow0(Isolate isolate, JSValue callee, JSValue receiver, ConvertReceiverMode mode)
    {
        if (TryGetInterpretedCallee(callee, out JSFunction function, out BytecodeArray bytecode))
        {
            return EnterInterpreted(isolate, function, bytecode, ConvertReceiver(isolate, function, receiver), new NoArguments());
        }
        if (IsFunctionPrototypeCall(callee) && TryEnterTarget(isolate, receiver, default, new NoArguments(), out JSValue called))
        {
            return called;
        }
        if (BuiltinFastPaths.TryCall0(isolate, callee, receiver, out JSValue result)) return result;
        return CallValues(isolate, callee, receiver, 0, default, default, mode);
    }

    [MethodImpl(Outline)]
    static JSValue CallSlow1(Isolate isolate, JSValue callee, JSValue receiver, JSValue arg0, ConvertReceiverMode mode)
    {
        if (TryGetInterpretedCallee(callee, out JSFunction function, out BytecodeArray bytecode))
        {
            return EnterInterpreted(isolate, function, bytecode, ConvertReceiver(isolate, function, receiver), new OneArgument(arg0));
        }
        // f.call(thisArg).
        if (IsFunctionPrototypeCall(callee) && TryEnterTarget(isolate, receiver, arg0, new NoArguments(), out JSValue called))
        {
            return called;
        }
        // a.push(x), Math.floor(x), s.charCodeAt(i) ...: the builtins' CSA fast paths.
        if (BuiltinFastPaths.TryCall1(isolate, callee, receiver, arg0, out JSValue result)) return result;
        return CallValues(isolate, callee, receiver, 1, arg0, default, mode);
    }

    [MethodImpl(Outline)]
    static JSValue CallSlow2(Isolate isolate, JSValue callee, JSValue receiver, JSValue arg0, JSValue arg1, ConvertReceiverMode mode)
    {
        if (TryGetInterpretedCallee(callee, out JSFunction function, out BytecodeArray bytecode))
        {
            return EnterInterpreted(isolate, function, bytecode, ConvertReceiver(isolate, function, receiver), new TwoArguments(arg0, arg1));
        }
        if (callee._obj is JSFunction { Shared.BuiltinId: var builtin })
        {
            // f.call(thisArg, x).
            if (builtin == Builtin.FunctionPrototypeCall &&
                TryEnterTarget(isolate, receiver, arg0, new OneArgument(arg1), out JSValue called))
            {
                return called;
            }
            // f.apply(thisArg, arguments) and f.apply(thisArg, array).
            if (builtin == Builtin.FunctionPrototypeApply && TryApply(isolate, receiver, arg0, arg1, out called)) return called;
        }
        if (BuiltinFastPaths.TryCall2(callee, arg0, arg1, out JSValue result)) return result;
        return CallValues(isolate, callee, receiver, 2, arg0, arg1, mode);
    }

    /// <summary>A call with the arguments in registers to a callee without baseline code.</summary>
    [MethodImpl(Outline)]
    static JSValue CallSlowRegisters(Isolate isolate, JSValue callee, JSValue receiver, int argsStart, int argc, ConvertReceiverMode mode)
    {
        if (TryGetInterpretedCallee(callee, out JSFunction function, out BytecodeArray bytecode))
        {
            return EnterInterpreted(isolate, function, bytecode, ConvertReceiver(isolate, function, receiver),
                new RegisterArguments(argsStart, argc));
        }
        // f.call(thisArg, x, y ...).
        if (argc > 0 && IsFunctionPrototypeCall(callee) &&
            TryEnterTarget(isolate, receiver, isolate.RegisterStack[argsStart], new RegisterArguments(argsStart + 1, argc - 1),
                out JSValue called))
        {
            return called;
        }
        return InterpreterCalls.Call(isolate, callee, receiver, argsStart, argc, mode);
    }

    // ---- Function.prototype.call and apply -------------------------------------------------------------
    //
    // V8's Function.prototype.call and apply are ASM builtins without a frame of
    // their own (Generate_FunctionPrototypeCall/Apply): the target is called as
    // if directly, which is what these do for a target that runs baseline or
    // interpreted code (InterpreterInlineCalls.TryPushFunctionCallFrame and
    // TryPushApplyFrame do the same for the interpreter).

    [MethodImpl(Inline)]
    static bool IsFunctionPrototypeCall(JSValue callee) =>
        callee._obj is JSFunction function && function.Shared.BuiltinId == Builtin.FunctionPrototypeCall;

    /// <summary>Calls <paramref name="target"/> with <paramref name="thisArg"/> when it runs baseline or interpreted code.</summary>
    static bool TryEnterTarget<TArgs>(Isolate isolate, JSValue target, JSValue thisArg, TArgs args, out JSValue result)
        where TArgs : struct, ICallArguments
    {
        if (TryGetBaselineCallee(target, out JSFunction function, out BaselineCode code, out FeedbackVector vector))
        {
            result = Enter(isolate, function, code, vector, ConvertReceiver(isolate, function, code, thisArg), args);
            return true;
        }
        if (TryGetInterpretedCallee(target, out function, out BytecodeArray bytecode))
        {
            result = EnterInterpreted(isolate, function, bytecode, ConvertReceiver(isolate, function, thisArg), args);
            return true;
        }
        result = default;
        return false;
    }

    /// <summary>
    /// target.apply(thisArg, argumentsList) for an unmodified arguments object
    /// or a fast array (CallWithArrayLike's fast paths): the elements go into a
    /// window on the register stack, as V8 pushes them on the machine stack.
    /// </summary>
    static bool TryApply(Isolate isolate, JSValue target, JSValue thisArg, JSValue argumentsList, out JSValue result)
    {
        result = default;
        bool baseline = TryGetBaselineCallee(target, out _, out _, out _);
        if (!baseline && !TryGetInterpretedCallee(target, out _, out _)) return false;
        FixedArrayBase? elements = null;
        int length = 0;
        if (!argumentsList.IsNullOrUndefined && !BuiltinsFunction.TryGetFastElements(isolate, argumentsList, out elements, out length))
        {
            return false;
        }
        int window = isolate.RegisterStackTop;
        if (window + length + 64 > isolate.RegisterStackLimit) return false;
        JSValue[] stack = isolate.RegisterStack;
        if (elements is FixedArray fixedArray)
        {
            JSValue[] data = fixedArray.Data;
            for (int i = 0; i < length; i++)
            {
                // Holes (holey arrays with intact protectors) read as undefined.
                JSValue value = data[i];
                stack[window + i] = ReferenceEquals(value._obj, Oddball.TheHole) ? default : value;
            }
        }
        else if (elements is FixedDoubleArray doubles)
        {
            for (int i = 0; i < length; i++) stack[window + i] = doubles.IsTheHole(i) ? default : JSValue.FromNumber(doubles.GetScalar(i));
        }
        isolate.RegisterStackTop = window + length;
        bool entered = TryEnterTarget(isolate, target, thisArg, new RegisterArguments(window, length), out result);
        // The window's values stay above the top, in the dirty range.
        if (window + length > isolate.RegisterStackDirtyEnd) isolate.RegisterStackDirtyEnd = window + length;
        isolate.RegisterStackTop = window;
        return entered;
    }

    /// <summary>
    /// A callee that runs in the interpreter (InterpreterCalls.Call's bytecode
    /// case): a compiled JSFunction that is not a builtin or a class
    /// constructor and has no baseline code (a function with baseline code but
    /// no feedback vector yet takes InterpreterCalls.Call, which installs it).
    /// </summary>
    [MethodImpl(Inline)]
    static bool TryGetInterpretedCallee(JSValue callee, out JSFunction function, out BytecodeArray bytecode)
    {
        if (callee._obj is JSFunction f && f.Shared is { FunctionData: BytecodeArray b, HasBuiltinId: false, IsClassConstructor: false,
                BaselineCode: null })
        {
            function = f;
            bytecode = b;
            return true;
        }
        function = null!;
        bytecode = null!;
        return false;
    }

    /// <summary>CallFunction's receiver conversion for a sloppy, non-native callee.</summary>
    [MethodImpl(Inline)]
    static JSValue ConvertReceiver(Isolate isolate, JSFunction function, JSValue receiver)
    {
        SharedFunctionInfo shared = function.Shared;
        return !shared.Native && shared.LanguageMode == LanguageMode.Sloppy && !receiver.IsJSReceiver
            ? InterpreterCalls.ConvertReceiver(isolate, function, receiver)
            : receiver;
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
    [MethodImpl(Outline)]
    public static JSValue Construct(Isolate isolate, FeedbackVector? fv, int slot, JSValue constructor, JSValue newTarget, int argsStart,
        int argc)
    {
        if (constructor._obj is JSFunction function && function.Shared.BaselineCode is { } code &&
            function.RawFeedbackCell.Value is FeedbackVector { MaglevCode: null } vector && function.Map.IsConstructor &&
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
            JSValue result = Enter(isolate, function, code, vector, implicitReceiver, new RegisterArguments(argsStart, argc), newTarget, true);
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

    [MethodImpl(Outline)]
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

    // ---- Entering the callee ---------------------------------------------------------------------

    /// <summary>
    /// The arguments of a call, as a type: Enter is compiled once per form
    /// (none, one or two values, or a register list), each with the argument
    /// pushes straight-line.
    /// </summary>
    internal interface ICallArguments
    {
        int Count { get; }

        /// <summary>Stores the arguments from the first argument's slot downwards (V8's order: the last deepest).</summary>
        void Store(ref JSValue stack0, ref JSValue firstArgumentSlot);
    }

    internal readonly struct NoArguments : ICallArguments
    {
        public int Count => 0;
        public void Store(ref JSValue stack0, ref JSValue firstArgumentSlot) { }
    }

    internal readonly struct OneArgument(JSValue arg0) : ICallArguments
    {
        public int Count => 1;

        [MethodImpl(Inline)]
        public void Store(ref JSValue stack0, ref JSValue firstArgumentSlot) => StoreSlot(ref firstArgumentSlot, arg0);
    }

    internal readonly struct TwoArguments(JSValue arg0, JSValue arg1) : ICallArguments
    {
        public int Count => 2;

        [MethodImpl(Inline)]
        public void Store(ref JSValue stack0, ref JSValue firstArgumentSlot)
        {
            StoreSlot(ref firstArgumentSlot, arg0);
            StoreSlot(ref Unsafe.Subtract(ref firstArgumentSlot, 1), arg1);
        }
    }

    /// <summary>The <paramref name="count"/> registers at register stack index <paramref name="start"/>.</summary>
    internal readonly struct RegisterArguments(int start, int count) : ICallArguments
    {
        public int Count => count;

        [MethodImpl(Inline)]
        public void Store(ref JSValue stack0, ref JSValue firstArgumentSlot)
        {
            ref JSValue src = ref Unsafe.Add(ref stack0, start);
            for (int i = 0; i < count; i++) StoreSlot(ref Unsafe.Subtract(ref firstArgumentSlot, i), Unsafe.Add(ref src, i));
        }
    }

    /// <summary>
    /// Builds the callee's interpreter frame (the layout InterpreterExecution.EnterFrame
    /// builds) and runs its baseline code.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    static JSValue Enter<TArgs>(Isolate isolate, JSFunction function, BaselineCode code, FeedbackVector vector, JSValue receiver,
        TArgs args, JSValue newTarget = default, bool isConstruct = false) where TArgs : struct, ICallArguments
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
        BytecodeArray bytecode = code.Bytecode;
        Context? savedContext = isolate.Context;
        int start = isolate.RegisterStackTop;
        int fp = PushFrame(isolate, function, bytecode, code.FormalParameterCount, code.RegisterCount, code.IncomingNewTargetRegister,
            receiver, args, newTarget, isConstruct, true, vector);
        vector.InvocationCount++;

        var state = new InterpreterState
        {
            Isolate = isolate,
            Function = function,
            Bytecode = bytecode,
            FeedbackVector = vector,
            Context = function.Context,
            Fp = fp,
            FrameIndex = depth,
            BaseFrameIndex = depth,
            Argc = args.Count,
        };
        JSValue result = code.HasHandlers ? BaselineExecution.Run(isolate, ref state, code) : code.EntryFor(vector)(isolate, ref state);
        LeaveFrame(isolate, depth, start, savedContext);
        return result;
    }

    /// <summary>
    /// A call from baseline code to a function that runs in the interpreter:
    /// the frame of InterpreterExecution.EnterFrame (built as <see cref="Enter"/>
    /// builds it) and the interpreter's dispatch loop (InterpreterExecution.Run),
    /// without EnterFrame's try/finally.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    static JSValue EnterInterpreted<TArgs>(Isolate isolate, JSFunction function, BytecodeArray bytecode, JSValue receiver, TArgs args)
        where TArgs : struct, ICallArguments
    {
        if (isolate.StackGuard.HasPendingInterrupts) isolate.StackGuard.HandleInterrupts();
        // The interpreter's own calls do not recurse on the .NET stack.
        if (!RuntimeHelpers.TryEnsureSufficientExecutionStack()) isolate.StackOverflow();
        int depth = isolate.InterpreterFrameDepth;
        Context? savedContext = isolate.Context;
        int start = isolate.RegisterStackTop;
        Register incoming = bytecode.IncomingNewTargetOrGeneratorRegister;
        int fp = PushFrame(isolate, function, bytecode, bytecode.ParameterCount - 1, bytecode.RegisterCount,
            incoming.IsValid ? incoming.Index : int.MinValue, receiver, args, default, false, false, null);
        FeedbackVector? vector = InterpreterExecution.FeedbackVectorOnEntry(isolate, function);
        StoreSlot(ref isolate.RegisterStack[fp + InterpreterRuntime.kFeedbackVectorOffset],
            vector is null ? JSValue.Undefined : vector);
        if (bytecode.ConstantPoolValues is null) InterpreterRuntime.MaterializeConstantPool(isolate, bytecode);

        var state = new InterpreterState
        {
            Isolate = isolate,
            Function = function,
            Bytecode = bytecode,
            FeedbackVector = vector,
            Context = function.Context,
            Accumulator = JSValue.Undefined,
            Pc = 0,
            Fp = fp,
            FrameIndex = depth,
            BaseFrameIndex = depth,
            Argc = args.Count,
        };
        JSValue result = InterpreterExecution.Run(isolate, ref state);
        // The frames the loop ran inline are gone (they returned to this one).
        LeaveFrame(isolate, depth, start, savedContext);
        return result;
    }

    /// <summary>
    /// The frame setup shared by <see cref="Enter"/> and <see cref="EnterInterpreted"/>:
    /// reserves the parameters, fixed slots and register file on the register
    /// stack, stores the receiver, arguments, context, closure and feedback
    /// vector (when known), pushes the frame record and enters the function's
    /// context. Returns the frame pointer.
    /// </summary>
    [MethodImpl(Inline)]
    static int PushFrame<TArgs>(Isolate isolate, JSFunction function, BytecodeArray bytecode, int formal, int registerCount,
        int incomingNewTargetRegister, JSValue receiver, TArgs args, JSValue newTarget, bool isConstruct, bool isBaseline,
        FeedbackVector? vector) where TArgs : struct, ICallArguments
    {
        int argc = args.Count;
        int paramSlots = argc > formal ? argc : formal;
        int start = isolate.RegisterStackTop;
        int fp = start + paramSlots + InterpreterRuntime.kFixedSlotsAboveParams;
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
        args.Store(ref stack0, ref Unsafe.Add(ref fpRef, InterpreterRuntime.kFirstArgumentOffset));
        for (int i = argc; i < paramSlots; i++) StoreSlot(ref Unsafe.Add(ref fpRef, InterpreterRuntime.kFirstArgumentOffset - i), default);

        Context context = function.Context;
        if (!ReferenceEquals(isolate.Context, context)) isolate.Context = context;
        StoreSlot(ref Unsafe.Add(ref fpRef, InterpreterRuntime.kContextOffset), context);
        StoreSlot(ref Unsafe.Add(ref fpRef, InterpreterRuntime.kClosureOffset), function);
        if (vector is not null) StoreSlot(ref Unsafe.Add(ref fpRef, InterpreterRuntime.kFeedbackVectorOffset), vector);
        // The argument count slot (fp - 4) is not read in V8Sharp (frames keep
        // the count in their record), as for the interpreter's inline calls.
        if (isConstruct && incomingNewTargetRegister != int.MinValue) Unsafe.Add(ref fpRef, incomingNewTargetRegister) = newTarget;

        ref InterpreterFrameRecord frame = ref isolate.PushFrame();
        // A returned frame leaves Function and Bytecode in its record.
        if (!ReferenceEquals(frame.Function, function)) frame.Function = function;
        if (!ReferenceEquals(frame.Bytecode, bytecode)) frame.Bytecode = bytecode;
        frame.Fp = fp;
        frame.Pc = 0;
        frame.Argc = argc;
        frame.Kind = InterpreterFrameKind.Interpreted;
        frame.IsConstructor = isConstruct;
        frame.IsBaseline = isBaseline;
        frame.IsMaglev = false;
        frame.InlineCall = false;
        frame.ReturnPc = 0;
        frame.RegisterStart = 0;
        return fp;
    }

    /// <summary>
    /// BaselineLeaveFrame: pops the frame record and the frame's register
    /// stack and restores the caller's context. The frame's slots stay below
    /// RegisterStackDirtyEnd for the next call at this depth; the record keeps
    /// Function and Bytecode.
    /// </summary>
    [MethodImpl(Inline)]
    static void LeaveFrame(Isolate isolate, int depth, int start, Context? savedContext)
    {
        isolate.InterpreterFrameDepth = depth;
        int top = isolate.RegisterStackTop;
        if (top > isolate.RegisterStackDirtyEnd) isolate.RegisterStackDirtyEnd = top;
        isolate.RegisterStackTop = start;
        if (!ReferenceEquals(isolate.Context, savedContext)) isolate.Context = savedContext;
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
        Unsafe.AsRef(in slot._bits) = value._bits;
    }
}
