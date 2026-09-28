// Port of Runtime_BytecodeBudgetInterrupt / BytecodeBudgetInterruptWithStackCheck
// (src/runtime/runtime-internal.cc) and their _Sparkplug variants: the stack
// check, then TieringManager::OnInterruptTick (Execution/TieringManager.cs),
// which allocates the feedback vector, tiers up to Sparkplug and resets the
// budget.
namespace V8Sharp.Interpreter;

public static class InterpreterTiering
{
    /// <summary>
    /// The budget interrupt of JumpLoop and Return, in Ignition
    /// (<paramref name="codeKind"/> INTERPRETED_FUNCTION) or baseline code.
    /// Returns the function's feedback vector (allocated here on the first
    /// interrupt).
    /// </summary>
    public static FeedbackVector? OnBudgetInterrupt(Isolate isolate, JSFunction function, bool withStackCheck,
        CodeKind codeKind = CodeKind.INTERPRETED_FUNCTION)
    {
        if (withStackCheck)
        {
            // Runtime_BytecodeBudgetInterruptWithStackCheck: the stack check
            // (and the interrupts it serves) comes first.
            isolate.StackGuard.StackCheck(isolate);
        }

        FeedbackCell cell = function.RawFeedbackCell;
        if (cell.InterruptBudget >= 0) return cell.Value as FeedbackVector;

        if (function.Shared.FunctionData is not BytecodeArray || ReferenceEquals(cell, FeedbackCell.ManyClosuresCell))
        {
            cell.InterruptBudget = JSFunctionFeedback.kMaxInterruptBudget;
            return cell.Value as FeedbackVector;
        }

        isolate.TieringManager.OnInterruptTick(function, codeKind);
        return function.RawFeedbackCell.Value as FeedbackVector;
    }
}
