// Port of src/execution/tiering-manager.{h,cc}: the interrupt budget and the
// tiering decisions taken when it runs out (OnInterruptTick).
//
// V8Sharp's tiers are Ignition and Sparkplug (baseline IL, src/V8Sharp/Baseline);
// there is no Maglev or Turbofan yet, so V8Sharp behaves like a V8 built with
// v8_enable_turbofan=false and v8_enable_maglev=false: isolate->use_optimizer()
// is false, the first tick allocates the feedback vector and tiers up to
// Sparkplug, and later ticks only raise the budget. The optimization paths
// (MaybeOptimizeFrame, ShouldOptimize, OSR urgency) are ported for when the
// optimizing tier lands, and are reached only through use_optimizer().
using V8Sharp.Baseline;
using V8Sharp.Codegen;
using V8Sharp.Interpreter;

namespace V8Sharp;

public sealed partial class Isolate
{
    TieringManager? _tieringManager;
    BaselineBatchCompiler? _baselineBatchCompiler;

    /// <summary>The dynamic assembly baseline code is emitted into (Baseline/BaselineCodeSpace.cs).</summary>
    internal BaselineCodeSpace? BaselineCodeSpace;

    /// <summary>Isolate::tiering_manager.</summary>
    public TieringManager TieringManager => _tieringManager ??= new TieringManager(this);

    /// <summary>Isolate::baseline_batch_compiler.</summary>
    public BaselineBatchCompiler BaselineBatchCompiler => _baselineBatchCompiler ??= new BaselineBatchCompiler(this);

    /// <summary>
    /// Isolate::use_optimizer: V8Sharp has no optimizing compiler yet (as a V8
    /// built without Turbofan and Maglev).
    /// </summary>
    public bool UseOptimizer => false;
}

/// <summary>OptimizationReason (tiering-manager.cc).</summary>
public enum OptimizationReason : byte
{
    kDoNotOptimize,
    kHotAndStable,
}

/// <summary>OptimizationDecision (tiering-manager.cc).</summary>
public readonly record struct OptimizationDecision(OptimizationReason OptimizationReason, CodeKind CodeKind, bool Concurrent)
{
    public static OptimizationDecision Maglev() => new(OptimizationReason.kHotAndStable, CodeKind.MAGLEV, true);
    public static OptimizationDecision TurbofanHotAndStable() => new(OptimizationReason.kHotAndStable, CodeKind.TURBOFAN_JS, true);
    public static OptimizationDecision DoNotOptimize() => new(OptimizationReason.kDoNotOptimize, CodeKind.TURBOFAN_JS, true);
    public bool ShouldOptimize => OptimizationReason != OptimizationReason.kDoNotOptimize;
}

/// <summary>TieringManager.</summary>
public sealed class TieringManager(Isolate isolate)
{
    /// <summary>
    /// kMaxInterruptBudget: INT_MAX / 2, so that adding a forward-jump weight
    /// cannot overflow.
    /// </summary>
    public const int kMaxInterruptBudget = int.MaxValue / 2;

    // ---- JSFunction tier queries (js-function.cc) --------------------------------

    /// <summary>
    /// JSFunction::GetActiveTier: the highest tier available. Baseline code
    /// lives on the SharedFunctionInfo, so every closure of a function with
    /// baseline code runs it (V8's GetAvailableCodeKinds adds BASELINE when the
    /// SFI has baseline code).
    /// </summary>
    public static CodeKind? GetActiveTier(JSFunction function)
    {
        SharedFunctionInfo shared = function.Shared;
        if (shared.HasBaselineCode) return CodeKind.BASELINE;
        if (shared.FunctionData is BytecodeArray) return CodeKind.INTERPRETED_FUNCTION;
        return null;
    }

    /// <summary>JSFunction::ActiveTierIsIgnition.</summary>
    public static bool ActiveTierIsIgnition(JSFunction function) => GetActiveTier(function) == CodeKind.INTERPRETED_FUNCTION;

    /// <summary>JSFunction::ActiveTierIsBaseline.</summary>
    public static bool ActiveTierIsBaseline(JSFunction function) => GetActiveTier(function) == CodeKind.BASELINE;

    // ---- Budgets ---------------------------------------------------------------------

    static int ScaleInterruptBudget(long invocations, int bytecodeLength) =>
        (int)Math.Clamp(invocations * bytecodeLength, 0, kMaxInterruptBudget);

    static int ScaleInterruptBudget(double invocations, int bytecodeLength)
    {
        double budget = invocations * bytecodeLength;
        int saturated = double.IsNaN(budget) ? 0 : budget >= int.MaxValue ? int.MaxValue : budget <= int.MinValue ? int.MinValue : (int)budget;
        return Math.Clamp(saturated, 0, kMaxInterruptBudget);
    }

    /// <summary>
    /// FirstTimeTierUpToSparkplug: true when the function should be enqueued
    /// for Sparkplug compilation for the first time.
    /// </summary>
    static bool FirstTimeTierUpToSparkplug(Isolate isolate, JSFunction function) =>
        !JSFunctionFeedback.HasFeedbackVector(function) ||
        // We request sparkplug even in the presence of a fbv, if we are
        // running ignition and haven't enqueued the function for sparkplug
        // batch compilation yet. This ensures we tier-up to sparkplug when the
        // feedback vector is allocated eagerly (e.g. for logging function
        // events; see JSFunction::InitializeFeedbackCell()).
        (ActiveTierIsIgnition(function) && BaselineSupport.CanCompileWithBaseline(isolate, function.Shared) &&
         function.Shared.CachedTieringDecision == CachedTieringDecision.kPending);

    /// <summary>maglev::IsMaglevEnabled: V8Sharp has no Maglev.</summary>
    static bool IsMaglevEnabled() => false;

    static bool TiersUpToMaglev(CodeKind? codeKind) =>
        IsMaglevEnabled() && codeKind is { } kind && CodeKindHelpers.IsUnoptimizedJSFunction(kind);

    /// <summary>The anonymous InterruptBudgetFor of tiering-manager.cc (no tiering is ever in progress).</summary>
    static int InterruptBudgetFor(Isolate isolate, CodeKind? codeKind, JSFunction function,
        CachedTieringDecision cachedTieringDecision, int bytecodeLength)
    {
        FlagList flags = isolate.Flags;
        if (TiersUpToMaglev(codeKind))
        {
            if (flags.profile_guided_optimization)
            {
                switch (cachedTieringDecision)
                {
                    case CachedTieringDecision.kDelayMaglev:
                        return ScaleInterruptBudget(
                            (long)Math.Max(flags.invocation_count_for_maglev, flags.minimum_invocations_after_ic_update) +
                            flags.invocation_count_for_maglev_with_delay, bytecodeLength);
                    case CachedTieringDecision.kEarlyMaglev:
                    case CachedTieringDecision.kEarlyTurbofan:
                        return ScaleInterruptBudget((long)flags.invocation_count_for_early_optimization, bytecodeLength);
                    default:
                        return ScaleInterruptBudget((long)flags.invocation_count_for_maglev, bytecodeLength);
                }
            }
            return ScaleInterruptBudget((long)flags.invocation_count_for_maglev, bytecodeLength);
        }
        return ScaleInterruptBudget((long)flags.invocation_count_for_turbofan, bytecodeLength);
    }

    /// <summary>TieringManager::InterruptBudgetFor.</summary>
    public static int InterruptBudgetFor(Isolate isolate, JSFunction function, CodeKind? overrideActiveTier = null)
    {
        if (function.Shared.FunctionData is not BytecodeArray bytecode) return kMaxInterruptBudget;
        int bytecodeLength = bytecode.Length;

        if (FirstTimeTierUpToSparkplug(isolate, function))
        {
            return ScaleInterruptBudget((long)isolate.Flags.invocation_count_for_feedback_allocation, bytecodeLength);
        }

        if (bytecodeLength > isolate.Flags.max_optimized_bytecode_size) return kMaxInterruptBudget;
        return InterruptBudgetFor(isolate, overrideActiveTier ?? GetActiveTier(function), function,
            function.Shared.CachedTieringDecision, bytecodeLength);
    }

    // ---- OnInterruptTick -----------------------------------------------------------------

    /// <summary>
    /// TieringManager::OnInterruptTick: called when the function's interrupt
    /// budget runs out (JumpLoop, Return) in Ignition or Sparkplug code.
    /// </summary>
    public void OnInterruptTick(JSFunction function, CodeKind codeKind)
    {
        SharedFunctionInfo shared = function.Shared;

        // Remember whether the function had a vector at this point. This is
        // relevant later since the configuration 'Ignition without a vector' can be
        // considered a tier on its own. We begin tiering up to tiers higher than
        // Sparkplug only when reaching this point *with* a feedback vector.
        bool hadFeedbackVector = JSFunctionFeedback.HasFeedbackVector(function);
        bool firstTimeTieredUpToSparkplug = FirstTimeTierUpToSparkplug(isolate, function);
        // (maybe_had_optimized_osr_code is always false: there is no optimized OSR code.)
        bool compileSparkplug = BaselineSupport.CanCompileWithBaseline(isolate, shared) && ActiveTierIsIgnition(function);

        // Ensure that the feedback vector has been allocated.
        if (!hadFeedbackVector)
        {
            if (compileSparkplug && shared.CachedTieringDecision == CachedTieringDecision.kPending)
            {
                // Mark the function as compiled with sparkplug before the feedback
                // vector is created to initialize the interrupt budget for the next
                // tier.
                shared.CachedTieringDecision = CachedTieringDecision.kEarlySparkplug;
            }
            FeedbackVector vector = JSFunctionFeedback.CreateAndAttachFeedbackVector(isolate, function);
            // Also initialize the invocation count here. This is only really needed
            // for OSR. When we OSR functions with lazy feedback allocation we want to
            // have a non zero invocation count so we can inline functions.
            vector.InvocationCount = 1;
        }

        if (compileSparkplug)
        {
            if (isolate.Flags.baseline_batch_compilation)
            {
                isolate.BaselineBatchCompiler.EnqueueFunction(function);
            }
            else
            {
                Compiler.CompileBaseline(isolate, function);
            }
        }

        // We only tier up beyond sparkplug if we already had a feedback vector.
        if (firstTimeTieredUpToSparkplug)
        {
            // If we didn't have a feedback vector, the interrupt budget has already
            // been set by JSFunction::CreateAndAttachFeedbackVector, so no need to
            // set it again.
            if (hadFeedbackVector)
            {
                if (shared.CachedTieringDecision == CachedTieringDecision.kPending)
                {
                    shared.CachedTieringDecision = CachedTieringDecision.kEarlySparkplug;
                }
                JSFunctionFeedback.SetInterruptBudget(isolate, function, raise: true);
            }
            return;
        }

        // Don't tier up if Turbofan is disabled.
        if (!isolate.UseOptimizer)
        {
            JSFunctionFeedback.SetInterruptBudget(isolate, function, raise: true);
            return;
        }

        // --- We've decided to proceed for now. ---
        MaybeOptimizeFrame(function, codeKind);

        // Make sure to set the interrupt budget after maybe starting an optimization,
        // so that the interrupt budget size takes into account tiering state.
        JSFunctionFeedback.SetInterruptBudget(isolate, function, raise: true);
    }

    /// <summary>
    /// TieringManager::MaybeOptimizeFrame. Unreachable until the optimizing
    /// tier exists (use_optimizer() is false); kept so the decision logic has
    /// its place.
    /// </summary>
    void MaybeOptimizeFrame(JSFunction function, CodeKind currentCodeKind)
    {
        if (JSFunctionFeedback.GetFeedbackVector(function) is not { } vector) return;
        OptimizationDecision d = ShouldOptimize(vector, currentCodeKind);
        if (d.ShouldOptimize && isolate.Flags.use_osr && vector.OsrState < FeedbackVector.kMaxOsrUrgency)
        {
            // TryIncrementOsrUrgency for a function stuck in a lower tier.
            vector.OsrState = (byte)Math.Min(vector.OsrState + 1, FeedbackVector.kMaxOsrUrgency);
        }
    }

    /// <summary>TieringManager::ShouldOptimize.</summary>
    OptimizationDecision ShouldOptimize(FeedbackVector feedbackVector, CodeKind currentCodeKind)
    {
        SharedFunctionInfo shared = feedbackVector.SharedFunctionInfo;
        if (currentCodeKind == CodeKind.TURBOFAN_JS) return OptimizationDecision.DoNotOptimize();
        if (TiersUpToMaglev(currentCodeKind)) return OptimizationDecision.Maglev();
        if (!isolate.Flags.turbofan || !isolate.UseOptimizer) return OptimizationDecision.DoNotOptimize();
        if (shared.FunctionData is BytecodeArray bytecode && bytecode.Length > isolate.Flags.max_optimized_bytecode_size)
        {
            return OptimizationDecision.DoNotOptimize();
        }
        return OptimizationDecision.TurbofanHotAndStable();
    }

    /// <summary>
    /// TieringManager::NotifyICChanged. Without an optimizing tier ShouldOptimize
    /// never says yes, so the budget is never reset by an IC change.
    /// </summary>
    public void NotifyICChanged(FeedbackVector vector)
    {
        SharedFunctionInfo shared = vector.SharedFunctionInfo;
        CodeKind codeKind = shared.HasBaselineCode ? CodeKind.BASELINE : CodeKind.INTERPRETED_FUNCTION;
        if (codeKind == CodeKind.INTERPRETED_FUNCTION && BaselineSupport.CanCompileWithBaseline(isolate, shared) &&
            shared.CachedTieringDecision == CachedTieringDecision.kPending)
        {
            // Don't delay tier-up if we haven't tiered up to baseline yet, but will --
            // baseline code is feedback independent.
            return;
        }
        OptimizationDecision decision = ShouldOptimize(vector, codeKind);
        if (!decision.ShouldOptimize) return;
        FeedbackCell cell = vector.ParentFeedbackCell;
        int bytecodeLength = ((BytecodeArray)shared.FunctionData!).Length;
        int invocations = Math.Max(1, isolate.Flags.minimum_invocations_after_ic_update);
        int bytecodes = Math.Max(1, Math.Min(bytecodeLength, kMaxInterruptBudget / invocations));
        int newBudget = ScaleInterruptBudget((long)invocations, bytecodes);
        if (newBudget > cell.InterruptBudget) cell.InterruptBudget = newBudget;
    }
}
