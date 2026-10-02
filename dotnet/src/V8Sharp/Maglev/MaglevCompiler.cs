// Port of src/maglev/maglev-compiler.{h,cc}, maglev.{h,cc} and the
// synchronous parts of maglev-concurrent-dispatcher.cc: the pipeline
// (graph building, phi representation selection, dead code removal, code
// generation), installing the code on the function's feedback vector with
// its dependencies, and invalidating it.
//
// Deviation: compilation is synchronous on the main thread (V8 builds the
// graph and the code on a background thread by default,
// --concurrent-recompilation); the code space and RyuJIT compile the IL
// lazily on the first call.
using System.Runtime.CompilerServices;
using V8Sharp.Deoptimizer;
using V8Sharp.Interpreter;

namespace V8Sharp.Maglev;

public static class MaglevCompiler
{
    /// <summary>The deopts after which a function is not optimized again (V8Sharp's guard against deopt loops).</summary>
    public const int kMaxDeoptCount = 8;

    sealed class SharedState
    {
        public int DeoptCount;
        public bool CompilationFailed;
        public string? FailureReason;
    }

    static readonly ConditionalWeakTable<SharedFunctionInfo, SharedState> s_sharedState = new();

    static SharedState StateOf(SharedFunctionInfo shared) => s_sharedState.GetValue(shared, static _ => new SharedState());

    /// <summary>SharedFunctionInfo::maglev_compilation_failed (and too many deopts).</summary>
    public static bool OptimizationDisabled(SharedFunctionInfo shared)
    {
        if (!s_sharedState.TryGetValue(shared, out SharedState? state)) return false;
        return state.CompilationFailed || state.DeoptCount >= kMaxDeoptCount;
    }

    public static string? DisabledReason(SharedFunctionInfo shared) =>
        s_sharedState.TryGetValue(shared, out SharedState? state) ? state.FailureReason : null;

    /// <summary>%NeverOptimizeFunction.</summary>
    public static void DisableOptimization(SharedFunctionInfo shared, string reason)
    {
        SharedState state = StateOf(shared);
        state.CompilationFailed = true;
        state.FailureReason = reason;
    }

    /// <summary>
    /// Maglev::Compile: compiles <paramref name="function"/> (for OSR at the
    /// JumpLoop at <paramref name="osrOffset"/>, or -1). Returns null when the
    /// function cannot be compiled (the reason is recorded; not retried).
    /// </summary>
    public static MaglevCode? Compile(Isolate isolate, JSFunction function, int osrOffset = -1)
    {
        SharedFunctionInfo shared = function.Shared;
        if (OptimizationDisabled(shared)) return null;
        if (function.RawFeedbackCell.Value is not FeedbackVector) return null;
        if (shared.FunctionData is not BytecodeArray bytecode) return null;
        if (bytecode.Length > isolate.Flags.max_optimized_bytecode_size) return Fail(isolate, shared, "function too big");
        if (MaglevGraphBuilder.UnsupportedReason(shared, bytecode) is { } unsupported) return Fail(isolate, shared, unsupported);
        if (!shared.IsUserJavaScript()) return Fail(isolate, shared, "not user JavaScript");

        var info = new MaglevCompilationInfo(isolate, function, osrOffset);
        long start = System.Diagnostics.Stopwatch.GetTimestamp();
        try
        {
            var builder = new MaglevGraphBuilder(info, info.Toplevel);
            builder.Build();
            FinalizeGraph(info.Graph);
            if (isolate.Flags.maglev_untagged_phis) MaglevPhiRepresentationSelector.Run(info.Graph);
            ComputeUseCounts(info.Graph);
            if (isolate.Flags.print_maglev_graph) MaglevGraphPrinter.Print(info, Console.Out);
            CheckCodeGenerationLimits(info.Graph);

            var code = new MaglevCode(function, info.Toplevel.Feedback, osrOffset)
            {
                OsrEntryPoint = osrOffset >= 0 ? info.Toplevel.BytecodeAnalysis.OsrEntryPoint : -1,
            };
            var generator = new MaglevCodeGenerator(info, code);
            (MaglevCodeEntry entry, int ilSize) = generator.Generate();
            code.Entry = entry;
            code.ILSize = ilSize;
            code.NodeCount = info.Graph.NodeCount;
            code.Dependencies = info.Dependencies.ToArray();
            EnsureScratch(isolate, code.MaxScratchSize);
            // CompilationDependencies::Commit: the code depends on maps and cells.
            foreach (CompilationDependency dependency in code.Dependencies)
            {
                Objects.DependentCode.InstallDependency(isolate, code, dependency.Object, dependency.Groups);
            }
            if (isolate.Flags.trace_opt)
            {
                double ms = System.Diagnostics.Stopwatch.GetElapsedTime(start).TotalMilliseconds;
                Console.WriteLine($"[compiling method {shared.Name()} (target MAGLEV){(osrOffset >= 0 ? " OSR" : "")}, " +
                                  $"{info.Graph.NodeCount} nodes, {ilSize} bytes IL, {ms:F3} ms]");
            }
            return code;
        }
        catch (MaglevBailoutException e)
        {
            return Fail(isolate, shared, e.Message);
        }
    }

    static MaglevCode? Fail(Isolate isolate, SharedFunctionInfo shared, string reason)
    {
        DisableOptimization(shared, reason);
        if (isolate.Flags.trace_opt) Console.WriteLine($"[aborted optimizing {shared.Name()} (target MAGLEV) because: {reason}]");
        return null;
    }

    static void EnsureScratch(Isolate isolate, int size)
    {
        if (isolate.MaglevDeoptScratch.Length < size) isolate.MaglevDeoptScratch = new JSValue[Math.Max(size, 2 * isolate.MaglevDeoptScratch.Length)];
    }

    /// <summary>Phi inputs from their lists; unreachable blocks marked dead.</summary>
    static void FinalizeGraph(Graph graph)
    {
        foreach (BasicBlock block in graph.Blocks)
        {
            foreach (Phi phi in block.Phis) phi.Inputs = phi.InputList.ToArray();
        }
        // Reachability from the entry (the first block).
        var reachable = new HashSet<BasicBlock>();
        var work = new Stack<BasicBlock>();
        work.Push(graph.Blocks[0]);
        while (work.Count > 0)
        {
            BasicBlock b = work.Pop();
            if (!reachable.Add(b)) continue;
            foreach (BasicBlock s in b.Successors()) work.Push(s);
        }
        foreach (BasicBlock block in graph.Blocks)
        {
            block.IsDead = !reachable.Contains(block);
            if (block.Control is null && !block.IsDead) throw new MaglevBailoutException($"block b{block.Id} without control node");
        }
        // Remove dead predecessors (their phi inputs with them).
        foreach (BasicBlock block in graph.Blocks)
        {
            if (block.IsDead) continue;
            for (int i = block.Predecessors.Count - 1; i >= 0; i--)
            {
                if (reachable.Contains(block.Predecessors[i])) continue;
                block.Predecessors.RemoveAt(i);
                foreach (Phi phi in block.Phis)
                {
                    if (i < phi.InputList.Count) phi.InputList.RemoveAt(i);
                    phi.Inputs = phi.InputList.ToArray();
                }
            }
            foreach (Phi phi in block.Phis)
            {
                if (phi.Inputs.Length != block.Predecessors.Count)
                {
                    throw new MaglevBailoutException($"phi n{phi.Id} has {phi.Inputs.Length} inputs for {block.Predecessors.Count} predecessors");
                }
            }
        }
    }

    /// <summary>
    /// The uses of every value (inputs, phis, control nodes, deopt frames,
    /// lazy deopt results), removing unused nodes without effects (V8's
    /// DeadNodeSweepingProcessor) until nothing changes.
    /// </summary>
    internal static void ComputeUseCounts(Graph graph)
    {
        foreach (ValueNode c in graph.Constants) c.UseCount = 0;
        foreach (BasicBlock block in graph.Blocks)
        {
            foreach (Phi phi in block.Phis) phi.UseCount = 0;
            foreach (Node node in block.Nodes) if (node is ValueNode v) v.UseCount = 0;
        }
        var visitedFrames = new HashSet<DeoptFrame>(ReferenceEqualityComparer.Instance);
        void UseFrame(DeoptFrame? frame)
        {
            for (DeoptFrame? f = frame; f is not null; f = f.Parent)
            {
                // Each deopt exit stores the frame's values: one use per exit is
                // enough to keep the value alive.
                if (!visitedFrames.Add(f)) continue;
                var i = (InterpretedDeoptFrame)f;
                foreach ((Register _, ValueNode value) in i.Values) value.UseCount++;
                i.Closure.UseCount++;
            }
        }
        foreach (BasicBlock block in graph.Blocks)
        {
            if (block.IsDead) continue;
            foreach (Phi phi in block.Phis) foreach (ValueNode input in phi.Inputs) input.UseCount++;
            foreach (Node node in block.Nodes)
            {
                foreach (ValueNode input in node.Inputs) input.UseCount++;
                UseFrame(node.EagerDeoptInfo?.TopFrame);
                UseFrame(node.LazyDeoptInfo?.TopFrame);
                if (node.LazyDeoptInfo is not null && node is ValueNode v) v.UseCount++;
            }
            ControlNode c = block.Control!;
            foreach (ValueNode input in c.Inputs) input.UseCount++;
            UseFrame(c.EagerDeoptInfo?.TopFrame);
        }
        // Sweep dead pure nodes (and the uses they hold) to a fixed point.
        bool changed = true;
        while (changed)
        {
            changed = false;
            foreach (BasicBlock block in graph.Blocks)
            {
                if (block.IsDead) continue;
                for (int i = block.Nodes.Count - 1; i >= 0; i--)
                {
                    Node node = block.Nodes[i];
                    if (node is not ValueNode { UseCount: 0 } || !IsPure(node)) continue;
                    block.Nodes.RemoveAt(i);
                    foreach (ValueNode input in node.Inputs) input.UseCount--;
                    changed = true;
                }
                for (int i = block.Phis.Count - 1; i >= 0; i--)
                {
                    Phi phi = block.Phis[i];
                    if (phi.UseCount != 0) continue;
                    block.Phis.RemoveAt(i);
                    foreach (ValueNode input in phi.Inputs) input.UseCount--;
                    changed = true;
                }
            }
        }
    }

    static bool IsPure(Node node) =>
        (node.Properties & (OpProperties.kEagerDeopt | OpProperties.kCanWrite | OpProperties.kCall | OpProperties.kNotIdempotent |
                            OpProperties.kCanThrow | OpProperties.kLazyDeopt)) == 0;

    /// <summary>
    /// RyuJIT compiles methods over its MinOpts thresholds without
    /// optimization (2000 basic blocks or locals, 60000 bytes of IL): such
    /// graphs are not compiled.
    /// </summary>
    static void CheckCodeGenerationLimits(Graph graph)
    {
        int nodes = 0, blocks = 0;
        foreach (BasicBlock block in graph.Blocks)
        {
            if (block.IsDead) continue;
            blocks++;
            nodes += block.Nodes.Count + block.Phis.Count;
        }
        if (nodes > 1800 || blocks > 600) throw new MaglevBailoutException($"graph too big ({nodes} nodes, {blocks} blocks)");
    }

    // ---- Installing and invalidating code --------------------------------------------------------------

    /// <summary>JSFunction::UpdateOptimizedCode: the code runs on the next call of any closure sharing the feedback vector.</summary>
    public static void InstallCode(Isolate isolate, MaglevCode code)
    {
        FeedbackVector vector = code.FeedbackVector;
        if (code.OsrOffset >= 0)
        {
            (vector.MaglevOsrCode ??= new Dictionary<int, MaglevCode>())[code.OsrOffset] = code;
        }
        else
        {
            vector.MaglevCode = code;
        }
        code.SharedFunctionInfo.MayHaveMaglevCode = true;
        isolate.MayHaveMaglevCode = true;
    }

    /// <summary>
    /// Deoptimizer::DeoptimizeFunction / Code::SetMarkedForDeoptimization:
    /// the code is not entered again; activations deoptimize lazily when
    /// control returns to them.
    /// </summary>
    public static void InvalidateCode(Isolate isolate, MaglevCode code, LazyDeoptimizeReason reason)
    {
        if (code.MarkedForDeoptimization) return;
        code.MarkedForDeoptimization = true;
        FeedbackVector vector = code.FeedbackVector;
        if (ReferenceEquals(vector.MaglevCode, code)) vector.MaglevCode = null;
        if (vector.MaglevOsrCode is { } osr && code.OsrOffset >= 0 && osr.TryGetValue(code.OsrOffset, out MaglevCode? c) &&
            ReferenceEquals(c, code))
        {
            osr.Remove(code.OsrOffset);
        }
        if (reason == LazyDeoptimizeReason.kEagerDeopt) StateOf(code.SharedFunctionInfo).DeoptCount++;
        // The function tiers up again later (TieringManager budget), with new feedback.
        vector.ResetOsrUrgency();
        if (isolate.Flags.trace_deopt && reason != LazyDeoptimizeReason.kEagerDeopt)
        {
            Console.WriteLine($"[marking dependent code {code} for deoptimization, reason: {DeoptimizeReasons.ToString(reason)}]");
        }
    }

    // ---- ManualOptimizationTable (%PrepareFunctionForOptimization) -----------------------------------

    static readonly ConditionalWeakTable<SharedFunctionInfo, object> s_manualOptimization = new();

    /// <summary>ManualOptimizationTable::MarkFunctionForManualOptimization.</summary>
    public static void MarkForManualOptimization(JSFunction function) =>
        s_manualOptimization.AddOrUpdate(function.Shared, function.Shared);

    /// <summary>ManualOptimizationTable::IsMarkedForManualOptimization: heuristic tiering leaves these alone.</summary>
    public static bool IsMarkedForManualOptimization(JSFunction function) => s_manualOptimization.TryGetValue(function.Shared, out _);

    /// <summary>The optimized code a call of <paramref name="function"/> runs, if any.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static MaglevCode? CodeFor(FeedbackVector? vector) => vector?.MaglevCode;
}
