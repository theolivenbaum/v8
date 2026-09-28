// Port of Runtime_BytecodeBudgetInterrupt / BytecodeBudgetInterruptWithStackCheck
// (src/runtime/runtime-internal.cc) and the part of
// TieringManager::OnInterruptTick (src/execution/tiering-manager.cc) that
// applies without optimizing tiers: lazy feedback vector allocation and the
// budget reset. V8Sharp runs as V8 does with --jitless (no Sparkplug, Maglev
// or Turbofan), so no tier-up is requested.
namespace V8Sharp.Interpreter;

public static class InterpreterTiering
{
    /// <summary>
    /// The budget interrupt of JumpLoop and Return. Returns the function's
    /// feedback vector (allocated here on the first interrupt).
    /// </summary>
    public static FeedbackVector? OnBudgetInterrupt(Isolate isolate, JSFunction function, bool withStackCheck)
    {
        if (withStackCheck)
        {
            // Runtime_BytecodeBudgetInterruptWithStackCheck: the stack check
            // (and the interrupts it serves) comes first.
            isolate.StackGuard.StackCheck(isolate);
        }

        FeedbackCell cell = function.RawFeedbackCell;
        if (cell.InterruptBudget >= 0) return cell.Value as FeedbackVector;

        // TieringManager::OnInterruptTick.
        if (cell.Value is not FeedbackVector)
        {
            if (function.Shared.FunctionData is BytecodeArray && !ReferenceEquals(cell, FeedbackCell.ManyClosuresCell))
            {
                FeedbackVector vector = JSFunctionFeedback.CreateAndAttachFeedbackVector(isolate, function);
                // Also initialize the invocation count here. This is only really needed
                // for OSR. When we OSR functions with lazy feedback allocation we want to
                // have a non zero invocation count so we can inline functions.
                vector.InvocationCount = 1;
                return vector;
            }
            cell.InterruptBudget = JSFunctionFeedback.kMaxInterruptBudget;
            return null;
        }

        JSFunctionFeedback.SetInterruptBudget(isolate, function, raise: false);
        return (FeedbackVector)cell.Value;
    }
}
