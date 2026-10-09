// Port of src/maglev/maglev-compiler.{h,cc} and maglev.{h,cc}: the pipeline
// (graph building, phi representation selection, dead code removal, code
// generation), the synchronous compile and the concurrent one's entry and
// finalization (with MaglevConcurrentDispatcher.cs), installing the code on
// the function's feedback vector with its dependencies, and invalidating it.
//
// The synchronous path (%OptimizeFunctionOnNextCall, OSR the natives asked
// for) leaves RyuJIT to compile the IL on the first call; concurrent jobs have
// it compiled fully optimized on the worker.
using System.Runtime.CompilerServices;
using V8Sharp.Deoptimizer;
using V8Sharp.Interpreter;

namespace V8Sharp.Maglev;

public static class MaglevCompiler
{
    // Diagnostics: V8SHARP_JIT_STATS=1 prints the RyuJIT totals and the Maglev compiles at exit.
    static int s_compiles;
    static long s_ilBytes;
    static double s_codegenMs;
    static MaglevCompiler()
    {
        if (Environment.GetEnvironmentVariable("V8SHARP_JIT_STATS") == "1")
        {
            AppDomain.CurrentDomain.ProcessExit += static (_, _) => Console.Error.WriteLine(
                $"[jit: {System.Runtime.JitInfo.GetCompiledMethodCount()} methods, {System.Runtime.JitInfo.GetCompiledILBytes()} IL bytes, " +
                $"{System.Runtime.JitInfo.GetCompilationTime().TotalMilliseconds:F0} ms; maglev: {s_compiles} compiles, {s_ilBytes} IL bytes, {s_codegenMs:F0} ms on the main thread, {MaglevCodeGenerator.CreateTypeMs:F0} ms type creation, {MaglevConcurrentDispatcher.BackgroundExceptions} worker exceptions]");
        }
    }

    /// <summary>SharedFunctionInfo::DebugNameCStr: the name, else the inferred name.</summary>
    internal static string DebugName(SharedFunctionInfo shared)
    {
        string name = shared.Name().ToString();
        return name.Length != 0 ? name : shared.InferredName().ToString();
    }

    /// <summary>
    /// The deopts after which the tiering manager does not optimize a function
    /// again: none by default, as in V8 (the deopts' feedback updates and
    /// kDelayMaglev end deopt loops); V8SHARP_MAGLEV_MAX_DEOPTS sets a limit.
    /// </summary>
    public static readonly int kMaxDeoptCount = int.TryParse(Environment.GetEnvironmentVariable("V8SHARP_MAGLEV_MAX_DEOPTS"), out int maxDeopts) ? maxDeopts : int.MaxValue;

    sealed class SharedState
    {
        public int DeoptCount;
        public bool CompilationFailed;
        public string? FailureReason;
        /// <summary>V8Sharp: a hoisted untagging check deoptimized (MaglevPhiRepresentationSelector).</summary>
        public bool NoSpeculativeUntagging;
    }

    /// <summary>V8Sharp: a speculatively hoisted loop entry untagging failed; later compiles do not hoist.</summary>
    public static void DisableSpeculativeUntagging(SharedFunctionInfo shared) => StateOf(shared).NoSpeculativeUntagging = true;

    static bool SpeculativeUntaggingDisabled(SharedFunctionInfo shared) =>
        s_sharedState.TryGetValue(shared, out SharedState? state) && state.NoSpeculativeUntagging;

    static readonly ConditionalWeakTable<SharedFunctionInfo, SharedState> s_sharedState = new();

    static SharedState StateOf(SharedFunctionInfo shared) => s_sharedState.GetValue(shared, static _ => new SharedState());

    /// <summary>SharedFunctionInfo::maglev_compilation_failed (and too many deopts).</summary>
    public static bool OptimizationDisabled(SharedFunctionInfo shared)
    {
        if (!s_sharedState.TryGetValue(shared, out SharedState? state)) return false;
        return state.CompilationFailed || state.DeoptCount >= kMaxDeoptCount;
    }

    /// <summary>
    /// SharedFunctionInfo::maglev_compilation_failed only: explicit requests
    /// (%OptimizeFunctionOnNextCall) compile after any number of deopts, as in
    /// V8, where the deopts' feedback updates end deopt loops.
    /// </summary>
    public static bool CompilationDisabled(SharedFunctionInfo shared) =>
        s_sharedState.TryGetValue(shared, out SharedState? state) && state.CompilationFailed;

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
    /// The largest graph the tiering manager optimizes: none by default, as in
    /// V8 (whose limits are max_maglev_optimized_bytecode_size in PrepareJob
    /// and max_optimized_bytecode_size in the interrupt budget). A method over
    /// RyuJIT's optimization limits is compiled with MinOpts, which is still
    /// well ahead of the lower tiers (zlib's biggest function: cold zlib +76%).
    /// V8SHARP_MAGLEV_MAX_NODES sets a limit, for experiments.
    /// </summary>
    internal static readonly int kMaxTieringGraphNodes = int.TryParse(Environment.GetEnvironmentVariable("V8SHARP_MAGLEV_MAX_NODES"), out int n) ? n : int.MaxValue;

    /// <summary>
    /// Compiler::CompileOptimized(function, kSynchronous, MAGLEV) up to the
    /// install: runs a MaglevCompilationJob's three phases on the main thread
    /// (for OSR at the JumpLoop at <paramref name="osrOffset"/>, or -1).
    /// Returns null when the function cannot be compiled (the reason is
    /// recorded; not retried).
    /// </summary>
    /// <summary>
    /// V8SHARP_MAGLEV_STRESS_CONCURRENT=1: synchronous compiles run their
    /// ExecuteJob on another thread (as a concurrent job, while the main thread
    /// waits), so every forced optimization builds its graph off the main
    /// thread (a test mode for the concurrent graph builder).
    /// </summary>
    static readonly bool s_stressConcurrent = Environment.GetEnvironmentVariable("V8SHARP_MAGLEV_STRESS_CONCURRENT") == "1";

    public static MaglevCode? Compile(Isolate isolate, JSFunction function, int osrOffset = -1, bool byTieringManager = false)
    {
        SharedFunctionInfo shared = function.Shared;
        if (byTieringManager ? OptimizationDisabled(shared) : CompilationDisabled(shared)) return null;
        if (function.RawFeedbackCell.Value is not FeedbackVector) return null;
        long start = System.Diagnostics.Stopwatch.GetTimestamp();
        var job = MaglevCompilationJob.New(isolate, function, osrOffset, concurrent: s_stressConcurrent, byTieringManager);
        if (job.PrepareJob() == MaglevCompilationJob.State.kReadyToExecute)
        {
            if (s_stressConcurrent)
            {
                // The worker's ExecuteJob, while the main thread waits.
                var thread = new Thread(() => job.ExecuteJob(), 16 * 1024 * 1024) { IsBackground = true };
                thread.Start();
                thread.Join();
            }
            else
            {
                job.ExecuteJob();
            }
            job.FinalizeJob();
        }
        if (job.CurrentState != MaglevCompilationJob.State.kSucceeded)
        {
            if (job.DisablesOptimization) return Fail(isolate, shared, job.BailoutReason!);
            if (isolate.Flags.trace_opt) Console.WriteLine($"[aborted optimizing {DebugName(shared)} (target MAGLEV) because: {job.BailoutReason}]");
            return null;
        }
        MaglevCode code = job.Code!;
        s_compiles++;
        s_ilBytes += code.ILSize;
        s_codegenMs += job.ExecuteMs;
        EnsureScratch(isolate, code.MaxScratchSize);
        if (isolate.Flags.trace_opt_verbose)
        {
            Console.WriteLine($"[maglev phases: graph {job.GraphMs:F3} ms, codegen {job.ExecuteMs - job.GraphMs:F3} ms]");
        }
        if (isolate.Flags.trace_opt)
        {
            double ms = System.Diagnostics.Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            Console.WriteLine($"[compiling method {DebugName(shared)} (target MAGLEV){(osrOffset >= 0 ? " OSR" : "")}, " +
                              $"{code.NodeCount} nodes, {code.ILSize} bytes IL, {ms:F3} ms]");
        }
        return code;
    }

    /// <summary>
    /// MaglevCompiler::Compile: builds the graph (MaglevGraphBuilder::Build,
    /// with the restarts of BuildGraph) and runs the passes before code
    /// generation. <paramref name="concurrent"/>: on a worker thread.
    /// </summary>
    internal static MaglevCompilationInfo BuildAndOptimizeGraph(Isolate isolate, JSFunction function, int osrOffset, bool concurrent)
    {
        SharedFunctionInfo shared = function.Shared;
        MaglevCompilationInfo info = BuildGraph(isolate, function, osrOffset, concurrent);
        FinalizeGraph(info.Graph);
        if (isolate.Flags.maglev_untagged_phis) MaglevPhiRepresentationSelector.Run(info.Graph, !SpeculativeUntaggingDisabled(shared));
        if (isolate.Flags.maglev_truncation) MaglevTruncation.Run(info.Graph);
        ComputeUseCounts(info.Graph);
        ElideArgumentsObjects(info.Graph);
        if (MaglevEscapeAnalysis.Run(info.Graph, isolate.Flags.maglev_escape_analysis, info.IsTracing || isolate.Flags.trace_maglev_escape_analysis))
        {
            // The removed stores' values may be dead now.
            ComputeUseCounts(info.Graph);
        }
        MaglevEscapeAnalysis.MarkOverwrittenMapStores(info.Graph);
        CheckStackSlots(info.Graph);
        if (isolate.Flags.print_maglev_graph) MaglevGraphPrinter.Print(info, Console.Out);
        return info;
    }

    /// <summary>
    /// MaglevGraphBuilder::Build, again when a loop's effects contradict what
    /// its header assumed (MaglevRestartException): the next attempt starts
    /// that loop with the effects the previous one saw (V8 peels the loop's
    /// first iteration to learn them instead). After a few attempts, every
    /// loop header forgets what the body could change.
    /// </summary>
    static MaglevCompilationInfo BuildGraph(Isolate isolate, JSFunction function, int osrOffset, bool concurrent)
    {
        Dictionary<(SharedFunctionInfo, int), LoopEffects>? hints = null;
        for (int attempt = 0; ; attempt++)
        {
            var info = new MaglevCompilationInfo(isolate, function, osrOffset) { OptimisticLoops = attempt < 4, IsConcurrent = concurrent };
            if (hints is not null) info.LoopHints = hints;
            try
            {
                new MaglevGraphBuilder(info, info.Toplevel).Build();
                return info;
            }
            catch (MaglevRestartException)
            {
                hints = info.LoopHints;
                if (info.IsTracing) Console.WriteLine("[maglev] restarting the graph with the loop effects learnt");
            }
        }
    }

    // ---- Concurrent compilation (MaglevConcurrentDispatcher) -----------------------------------------------

    /// <summary>
    /// Compiler::CompileOptimized(function, kConcurrent, MAGLEV) for a tiering
    /// request (GetOrCompileOptimized): prepares a MaglevCompilationJob and
    /// enqueues it to the isolate's MaglevConcurrentDispatcher; the graph is
    /// built, the IL generated and compiled by RyuJIT on a worker thread, and
    /// the code is installed at the next INSTALL_MAGLEV_CODE interrupt
    /// (FinalizeMaglevCompilationJob). False when the function cannot be
    /// optimized.
    /// </summary>
    public static bool CompileConcurrently(Isolate isolate, JSFunction function, int osrOffset = -1)
    {
        SharedFunctionInfo shared = function.Shared;
        if (function.RawFeedbackCell.Value is not FeedbackVector vector) return false;
        if (osrOffset < 0 ? vector.TieringInProgress : vector.OsrTieringInProgress) return true;
        if (OptimizationDisabled(shared)) return false;
        var job = MaglevCompilationJob.New(isolate, function, osrOffset, concurrent: true, byTieringManager: true);
        if (job.PrepareJob() != MaglevCompilationJob.State.kReadyToExecute)
        {
            job.Dispose();
            if (job.DisablesOptimization) Fail(isolate, shared, job.BailoutReason!);
            return false;
        }
        // JSFunction::SetTieringInProgress.
        if (osrOffset < 0)
        {
            vector.TieringInProgress = true;
            vector.MaglevJob = job;
        }
        else
        {
            vector.OsrTieringInProgress = true;
            vector.MaglevOsrJob = job;
        }
        isolate.MaglevConcurrentDispatcher.EnqueueJob(job);
        s_codegenMs += job.PrepareMs;
        if (isolate.Flags.trace_opt)
        {
            Console.WriteLine($"[queued concurrent maglev compile of {DebugName(shared)}{(osrOffset >= 0 ? " OSR" : "")}, prepare {job.PrepareMs:F3} ms]");
        }
        return true;
    }

    /// <summary>INSTALL_MAGLEV_CODE: MaglevConcurrentDispatcher::FinalizeFinishedJobs.</summary>
    public static void InstallConcurrentCode(Isolate isolate) => isolate.MaglevConcurrentDispatcher.FinalizeFinishedJobs();

    /// <summary>
    /// Compiler::FinalizeMaglevCompilationJob: commits and installs the code of
    /// a finished concurrent job (unless a dependency was invalidated meanwhile
    /// or the function got other code), or aborts it (AbortMaglevCompilationJob).
    /// </summary>
    internal static void FinalizeMaglevCompilationJob(Isolate isolate, MaglevCompilationJob job)
    {
        JSFunction function = job.Function;
        SharedFunctionInfo shared = function.Shared;
        var vector = (FeedbackVector)function.RawFeedbackCell.Value!;
        job.FinalizeJob();
        // JSFunction::SetTieringInProgress(false): a function's budget is reset
        // (its tier may have changed).
        if (job.IsOsr)
        {
            vector.OsrTieringInProgress = false;
            vector.MaglevOsrJob = null;
        }
        else
        {
            vector.TieringInProgress = false;
            vector.MaglevJob = null;
            JSFunctionFeedback.SetInterruptBudget(isolate, function, raise: false);
        }
        MaglevCode? code = job.Code;
        bool install = job.CurrentState == MaglevCompilationJob.State.kSucceeded &&
                       (job.IsOsr
                           ? vector.MaglevOsrCode?.GetValueOrDefault(job.OsrOffset) is not { MarkedForDeoptimization: false }
                           : vector.MaglevCode is not { MarkedForDeoptimization: false });
        if (install)
        {
            EnsureScratch(isolate, code!.MaxScratchSize);
            InstallCode(isolate, code);
            s_compiles++;
            s_ilBytes += code.ILSize;
            s_codegenMs += job.FinalizeMs;
            if (isolate.Flags.profile_guided_optimization && shared.CachedTieringDecision <= CachedTieringDecision.kEarlySparkplug)
            {
                shared.CachedTieringDecision = CachedTieringDecision.kEarlyMaglev;
            }
            // V8's JumpLoop enters the OSR cache's code at the next back edge
            // (the vector's maybe_has_optimized_osr_code bit is part of the
            // osr_state it compares with the loop depth); V8Sharp's JumpLoop
            // looks at the cache at budget interrupts, so the next back edge
            // takes one.
            if (job.IsOsr) function.RawFeedbackCell.InterruptBudget = 0;
        }
        else if (job.CurrentState == MaglevCompilationJob.State.kFailed)
        {
            // AbortMaglevCompilationJob.
            if (job.DisablesOptimization)
            {
                Fail(isolate, shared, job.BailoutReason!);
            }
            else if (!job.IsOsr)
            {
                // A dependency changed (or a race on the worker): not disabled, the
                // function may be compiled again (BudgetModification::kReduce).
                FeedbackCell cell = function.RawFeedbackCell;
                cell.InterruptBudget = Math.Min(cell.InterruptBudget, TieringManager.InterruptBudgetFor(isolate, function));
            }
            shared.CachedTieringDecision = CachedTieringDecision.kDelayMaglev;
        }
        else if (code is not null)
        {
            // Committed, but the function got code meanwhile (%OptimizeFunctionOnNextCall).
            InvalidateCode(isolate, code, Deoptimizer.LazyDeoptimizeReason.kTesting);
        }
        if (isolate.Flags.trace_opt)
        {
            Console.WriteLine($"[{(install ? "completed" : job.CurrentState == MaglevCompilationJob.State.kFailed ? "aborted" : "discarded")} " +
                              $"concurrent maglev compile of {DebugName(shared)}{(job.IsOsr ? " OSR" : "")}, " +
                              $"{job.NodeCount} nodes, {code?.ILSize ?? 0} bytes IL, queued {job.QueuedMs:F3} ms, background {job.ExecuteMs:F3} ms " +
                              $"(graph {job.GraphMs:F3} ms, RyuJIT {job.JitMs:F3} ms), finalize {job.FinalizeMs:F3} ms" +
                              (job.CurrentState == MaglevCompilationJob.State.kFailed ? ", reason: " + job.BailoutReason : "") +
                              (isolate.Flags.trace_opt_verbose && code is not null
                                  ? $"; {code.ILCounts.Instructions} instructions, {code.ILCounts.BlockBoundaries} block boundaries, " +
                                    $"{code.ILCounts.LocalReferences} local references, {code.ILCounts.Locals} locals"
                                  : "") + "]");
        }
    }

    /// <summary>%WaitForBackgroundOptimization / %FinalizeOptimization: waits for the jobs in flight and installs them.</summary>
    public static void WaitForBackgroundOptimization(Isolate isolate)
    {
        isolate.MaglevConcurrentDispatcher.AwaitCompileJobs();
        InstallConcurrentCode(isolate);
    }

    /// <summary>MaglevCompiler::Compile's kMaxStackSlots bailout (MaglevStackSlots).</summary>
    static void CheckStackSlots(Graph graph)
    {
        int slots = MaglevStackSlots.MaxLiveValues(graph);
        if (slots > MaglevStackSlots.kMaxStackSlots) throw new MaglevBailoutException($"too many stack slots ({slots})");
    }

    static MaglevCode? Fail(Isolate isolate, SharedFunctionInfo shared, string reason)
    {
        DisableOptimization(shared, reason);
        if (isolate.Flags.trace_opt) Console.WriteLine($"[aborted optimizing {MaglevCompiler.DebugName(shared)} (target MAGLEV) because: {reason}]");
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
            // Exception edges: a throwing node continues at its catch block.
            foreach (Node node in b.Nodes)
            {
                if (node.ExceptionHandler?.CatchState.Block is { } catchBlock) work.Push(catchBlock);
            }
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
                if (phi.IsExceptionPhi) continue;
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
    /// <summary>
    /// Elides the arguments objects whose only uses (besides deopt frames) are
    /// CallForwardArguments calls (V8's escape analysis of the arguments
    /// object, for this one pattern).
    /// </summary>
    static void ElideArgumentsObjects(Graph graph)
    {
        List<ValueNode>? candidates = null;
        foreach (BasicBlock block in graph.Blocks)
        {
            if (block.IsDead) continue;
            foreach (Node node in block.Nodes)
            {
                if (node is ValueNode { Opcode: Opcode.CallBuiltin, Obj0: CallBuiltinInfo { ArgumentsKind: not ArgumentsObjectKind.None } } v)
                {
                    (candidates ??= []).Add(v);
                }
            }
        }
        if (candidates is null) return;
        var escaping = new HashSet<ValueNode>(ReferenceEqualityComparer.Instance);
        foreach (BasicBlock block in graph.Blocks)
        {
            if (block.IsDead) continue;
            foreach (Phi phi in block.Phis) foreach (ValueNode input in phi.Inputs) escaping.Add(input);
            foreach (Node node in block.Nodes)
            {
                bool forwards = node.Obj0 is CallBuiltinInfo { ForwardsArguments: true };
                for (int i = 0; i < node.Inputs.Length; i++)
                {
                    if (forwards && i == 2) continue;
                    escaping.Add(node.Inputs[i]);
                }
            }
            foreach (ValueNode input in block.Control!.Inputs) escaping.Add(input);
        }
        foreach (ValueNode candidate in candidates)
        {
            if (!escaping.Contains(candidate)) ((CallBuiltinInfo)candidate.Obj0!).Elided = true;
        }
    }

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
                var i = (InterpretedDeoptFrame)f;
                // Each deopt exit stores the frame's values: one use per exit is
                // enough to keep the value alive. The fields of an elided
                // allocation come from the top frame's virtual objects, which
                // differ between the frames sharing a parent.
                bool visited = !visitedFrames.Add(f);
                foreach ((Register _, ValueNode value) in i.Values)
                {
                    if (value is InlinedAllocation { IsElided: true } allocation)
                    {
                        foreach (ValueNode slot in frame!.VirtualObjects.Find(allocation)!.Slots)
                        {
                            MaglevEscapeAnalysis.UseCaptured(slot, frame.VirtualObjects, static v => v.UseCount++);
                        }
                    }
                    if (!visited) value.UseCount++;
                }
                if (!visited) i.Closure.UseCount++;
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
