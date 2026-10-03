// Entering and running baseline code: the baseline half of V8's
// BaselineOrInterpreterEntry / InterpreterOnStackReplacement_ToBaseline
// (src/builtins/x64/builtins-x64.cc), Runtime_InstallBaselineCode
// (src/runtime/runtime-compiler.cc), the budget interrupts of baseline code
// (Runtime_BytecodeBudgetInterrupt{,WithStackCheck}_Sparkplug,
// src/runtime/runtime-internal.cc) and the exception unwinding into a baseline
// frame (Isolate::UnwindAndFindHandler for BASELINE frames).
//
// A baseline frame is an interpreter frame (InterpreterExecution.EnterFrame
// builds it); only the code running it differs. So a function switches tiers
// without a frame conversion: at entry, when its SharedFunctionInfo has
// baseline code, and in the middle of a loop (OSR), when the interpreter's
// JumpLoop finds baseline code installed.
using System.Runtime.CompilerServices;
using V8Sharp.Interpreter;

namespace V8Sharp.Baseline;

public static class BaselineExecution
{
    /// <summary>
    /// Runs the baseline code of the frame described by <paramref name="state"/>
    /// from state.Pc, dispatching exceptions to the frame's handlers like the
    /// interpreter's Run loop does.
    /// </summary>
    public static JSValue Run(Isolate isolate, ref InterpreterState state, BaselineCode code)
    {
        isolate.InterpreterFrames[state.FrameIndex].IsBaseline = true;
        BaselineCodeEntry entry = code.EntryFor(state.FeedbackVector);
        while (true)
        {
            try
            {
                return entry(isolate, ref state);
            }
            // As in the interpreter, the filter only looks for a handler: an
            // exception this frame does not handle keeps propagating.
            catch (JavaScriptException e) when (InterpreterExecution.HasHandler(isolate, ref state))
            {
                InterpreterExecution.TryDispatchToHandler(isolate, ref state, e.Value, e.MessageObject);
            }
        }
    }

    /// <summary>
    /// Runtime_InstallBaselineCode: a call to a function whose
    /// SharedFunctionInfo has baseline code but that has no feedback vector yet
    /// (baseline code needs one). Returns the vector.
    /// </summary>
    public static FeedbackVector InstallBaselineCode(Isolate isolate, JSFunction function)
    {
        FeedbackVector vector = JSFunctionFeedback.EnsureFeedbackVector(isolate, function);
        // (The interrupt budget was set by CreateAndAttachFeedbackVector.)
        return vector;
    }

    /// <summary>
    /// The interrupt budget update of baseline JumpLoop
    /// (UpdateInterruptBudgetAndJumpToLabel with kEnableStackCheck), which
    /// also serves pending interrupts, like the interpreter's back edge.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void UpdateInterruptBudgetOnJumpLoop(Isolate isolate, JSFunction function, int weight)
    {
        FeedbackCell cell = function.RawFeedbackCell;
        if ((cell.InterruptBudget -= weight) < 0 || isolate.StackGuard.HasPendingInterrupts)
        {
            BytecodeBudgetInterruptWithStackCheck(isolate, function);
        }
    }

    /// <summary>The interrupt budget update of BaselineLeaveFrame (Return).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void UpdateInterruptBudgetOnReturn(Isolate isolate, JSFunction function, int weight)
    {
        FeedbackCell cell = function.RawFeedbackCell;
        if ((cell.InterruptBudget -= weight) < 0) BytecodeBudgetInterrupt(isolate, function);
    }

    /// <summary>The runtime call of baseline JumpLoop when the budget ran out or an interrupt is pending.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void BudgetInterruptOnJumpLoop(Isolate isolate, JSFunction function) =>
        BytecodeBudgetInterruptWithStackCheck(isolate, function);

    /// <summary>
    /// The runtime call of a baseline back edge with on-stack replacement into
    /// Maglev code (OSR from Sparkplug: the JumpLoop's
    /// Runtime_BytecodeBudgetInterruptWithStackCheck_Sparkplug, then V8's
    /// OnStackReplacement as from Ignition). True when this frame continues
    /// in OSR code for its JumpLoop (the offset in the frame, an unprefixed
    /// JumpLoop): <see cref="OsrToMaglev"/> runs it.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static bool BudgetInterruptOnJumpLoopOsr(Isolate isolate, ref InterpreterState state, JSFunction function)
    {
        BytecodeBudgetInterruptWithStackCheck(isolate, function);
        if (!isolate.UseOptimizer || function.RawFeedbackCell.Value is not FeedbackVector vector) return false;
        ref JSValue fp = ref isolate.RegisterStack[state.Fp];
        int pc = InterpreterRuntime.FramePc(ref fp);
        var bytecode = (BytecodeArray)function.Shared.FunctionData!;
        if ((uint)pc >= (uint)bytecode.Length || bytecode.Bytecodes[pc] != (byte)Bytecode.JumpLoop) return false;
        if (Maglev.MaglevExecution.TryGetOsrCode(isolate, function, vector, bytecode, pc) is not { } code) return false;
        state.OsrCode = code;
        return true;
    }

    /// <summary>
    /// Continues the baseline frame in the OSR code <see cref="BudgetInterruptOnJumpLoopOsr"/>
    /// found, at the loop header (the registers are in the frame); returns the
    /// function's result, which the baseline code returns.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static JSValue OsrToMaglev(Isolate isolate, ref InterpreterState state)
    {
        ref JSValue fp = ref isolate.RegisterStack[state.Fp];
        int pc = InterpreterRuntime.FramePc(ref fp);
        BytecodeArray bytecode = InterpreterRuntime.FrameBytecode(ref fp);
        // JumpLoop's first operand: the unsigned distance back to the loop header.
        state.Pc = pc - bytecode.Bytecodes[pc + 1];
        state.Accumulator = default;
        Maglev.MaglevCode code = state.OsrCode!;
        state.OsrCode = null;
        return Maglev.MaglevExecution.RunOsr(isolate, ref state, code);
    }

    /// <summary>The runtime call of baseline Return when the budget ran out.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void BudgetInterruptOnReturn(Isolate isolate, JSFunction function) => BytecodeBudgetInterrupt(isolate, function);

    /// <summary>Runtime_BytecodeBudgetInterruptWithStackCheck_Sparkplug.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    static void BytecodeBudgetInterruptWithStackCheck(Isolate isolate, JSFunction function) =>
        InterpreterTiering.OnBudgetInterrupt(isolate, function, withStackCheck: true, CodeKind.BASELINE);

    /// <summary>Runtime_BytecodeBudgetInterrupt_Sparkplug.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    static void BytecodeBudgetInterrupt(Isolate isolate, JSFunction function) =>
        InterpreterTiering.OnBudgetInterrupt(isolate, function, withStackCheck: false, CodeKind.BASELINE);
}
