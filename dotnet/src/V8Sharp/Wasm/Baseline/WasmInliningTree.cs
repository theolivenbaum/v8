// Port of src/wasm/inlining-tree.h (InliningTree: the inlining decisions of
// one function, made before its code is emitted) and of the feedback
// processing of src/wasm/module-compiler.cc (FeedbackMaker,
// TransitiveTypeFeedbackProcessor) that it reads.
//
// V8 inlines in its optimizing tier only, guided by the call counts and call
// targets Liftoff collected. V8Sharp's one compiler stands in for both tiers
// (RyuJIT optimizes the IL), so its first compile of a function already
// inlines: a direct call counts as made once per call of its caller (there
// is no Liftoff run to count it; the budget, not the count, then limits
// inlining, which is V8's rule for tiny callees anyway). The call_indirect
// and call_ref sites of a function collect V8's feedback (up to four targets
// with counts, else megamorphic) in their inline caches; when the function
// has been hot long enough (its tiering budget, as Liftoff's) it is compiled
// again, and the targets the feedback names are inlined speculatively behind
// a target check (deviations.md, "WebAssembly").
using System.Globalization;
using Wacs.Core.Runtime;
using Wacs.Core.Runtime.Types;

namespace V8Sharp.Wasm.Baseline;

/// <summary>
/// The feedback of one call_indirect or call_ref site (V8: a CallSiteFeedback
/// slot of the function's feedback vector) and its inline cache.
/// </summary>
public sealed class WasmCallSite
{
    /// <summary>V8: kMaxPolymorphism.</summary>
    public const int kMaxPolymorphism = 4;

    /// <summary>The site's number among the function's calls (V8: the feedback slot, "call #N").</summary>
    public readonly int CallIndex;

    /// <summary>The table's elements (call_indirect), read by the cache's fast path.</summary>
    public readonly List<Value>? Elements;

    /// <summary>The constant index of the expected type (call_indirect's signature check).</summary>
    public readonly int ExpectedConstant;

    /// <summary>The function address the cache holds (-1: none; a null reference never matches).</summary>
    public long CachedPtr = -1;
    public WasmCode? CachedTarget;
    /// <summary>The case <see cref="CachedTarget"/> counts for (-1: none).</summary>
    public int CachedCase = -1;
    /// <summary>Calls through the cache since it was filled (added to the case's count when it changes).</summary>
    public int Hits;

    /// <summary>The function indices of the targets seen (same-instance functions only).</summary>
    public readonly int[] Targets = new int[kMaxPolymorphism];
    public readonly int[] Counts = new int[kMaxPolymorphism];
    public int Cases;
    public bool Megamorphic;
    /// <summary>A target was seen that cannot be inlined (an import, another instance's function).</summary>
    public bool HasNonInlineableTargets;

    public WasmCallSite(int callIndex, List<Value>? elements, int expectedConstant)
    {
        CallIndex = callIndex;
        Elements = elements;
        ExpectedConstant = expectedConstant;
    }

    /// <summary>Records a call to <paramref name="target"/> and caches it (the cache missed).</summary>
    internal void Record(long ptr, WasmCode target, WasmInstanceData caller)
    {
        Flush();
        CachedPtr = ptr;
        CachedTarget = target;
        CachedCase = -1;
        if (target.Instance != caller || target.FunctionIndex < caller.ImportedFunctionCount)
        {
            HasNonInlineableTargets = true;
            return;
        }
        if (Megamorphic) return;
        for (int i = 0; i < Cases; i++)
        {
            if (Targets[i] == target.FunctionIndex)
            {
                CachedCase = i;
                Counts[i]++;
                return;
            }
        }
        if (Cases == kMaxPolymorphism)
        {
            Megamorphic = true;
            return;
        }
        Targets[Cases] = target.FunctionIndex;
        Counts[Cases] = 1;
        CachedCase = Cases++;
    }

    /// <summary>Adds the cache's hits to its case's count.</summary>
    internal void Flush()
    {
        if (CachedCase >= 0 && Hits > 0)
        {
            Counts[CachedCase] = (int)Math.Min((long)Counts[CachedCase] + Hits, int.MaxValue);
        }
        Hits = 0;
    }
}

/// <summary>
/// The feedback of one function (V8: FunctionTypeFeedback): its invocations
/// and its call_indirect/call_ref sites by instruction index.
/// </summary>
public sealed class WasmFunctionFeedback
{
    public int Invocations;
    public readonly Dictionary<int, WasmCallSite> Sites = [];
}

/// <summary>InliningTree: a function frame and the candidates of its calls.</summary>
internal sealed class WasmInliningTree
{
    /// <summary>V8: kMaxInlinedCount.</summary>
    public const int kMaxInlinedCount = 60;
    /// <summary>V8: kMaxInliningNestingDepth.</summary>
    public const int kMaxInliningNestingDepth = 7;

    sealed class Data
    {
        public required WasmInstanceData Instance;
        public required Isolate Isolate;
        public double MaxGrowthFactor;
        public long BudgetCap;
        public int TopmostCallerIndex;
        public bool Trace;
    }

    readonly Data _data;
    public readonly int FunctionIndex;
    readonly double _relativeCallCount;
    readonly int _wireByteSize;
    readonly int _depth;
    readonly int _callerIndex;
    readonly int _feedbackSlot;
    readonly int _case;
    bool _isInlined;
    bool _feedbackFound;
    /// <summary>The number of calls in the function (V8: the feedback vector's length).</summary>
    int _numCalls;

    /// <summary>The candidates of each call, by the call's instruction index (V8: function_calls_).</summary>
    readonly Dictionary<int, WasmInliningTree[]> _calls = [];
    /// <summary>The call sites with targets that cannot be inlined (V8: has_non_inlineable_targets_).</summary>
    readonly HashSet<int> _nonInlineable = [];

    public bool IsInlined => _isInlined;
    /// <summary>The case of the call this candidate is (V8: case_).</summary>
    public int Case => _case;
    public int Depth => _depth;

    WasmInliningTree(Data data, int functionIndex, double relativeCallCount, int wireByteSize, int callerIndex,
        int feedbackSlot, int theCase, int depth)
    {
        _data = data;
        FunctionIndex = functionIndex;
        _relativeCallCount = relativeCallCount;
        _wireByteSize = wireByteSize;
        _callerIndex = callerIndex;
        _feedbackSlot = feedbackSlot;
        _case = theCase;
        _depth = depth;
    }

    /// <summary>The inlined callee of the direct call at <paramref name="instIndex"/>, or null.</summary>
    public WasmInliningTree? InlinedDirectCall(int instIndex) =>
        _calls.TryGetValue(instIndex, out WasmInliningTree[]? cases) && cases.Length == 1 && cases[0]._isInlined ? cases[0] : null;

    /// <summary>The inlined cases of the call_indirect/call_ref at <paramref name="instIndex"/> (empty if none).</summary>
    public List<WasmInliningTree> InlinedCases(int instIndex)
    {
        var result = new List<WasmInliningTree>();
        if (_calls.TryGetValue(instIndex, out WasmInliningTree[]? cases))
        {
            foreach (WasmInliningTree c in cases)
            {
                if (c._isInlined) result.Add(c);
            }
        }
        return result;
    }

    /// <summary>The inlined frames of the tree, this one included.</summary>
    public IEnumerable<WasmInliningTree> InlinedNodes()
    {
        if (!_isInlined) yield break;
        yield return this;
        foreach (WasmInliningTree[] cases in _calls.Values)
        {
            foreach (WasmInliningTree c in cases)
            {
                foreach (WasmInliningTree n in c.InlinedNodes()) yield return n;
            }
        }
    }

    /// <summary>The number of inlined callees.</summary>
    public int InlinedCount
    {
        get
        {
            int n = -1;
            foreach (WasmInliningTree _ in InlinedNodes()) n++;
            return n;
        }
    }

    /// <summary>InliningTree::CreateRoot: the decisions for compiling <paramref name="functionIndex"/>.</summary>
    public static WasmInliningTree CreateRoot(WasmInstanceData instance, int functionIndex, bool trace)
    {
        Isolate isolate = instance.Engine.Isolate;
        var data = new Data
        {
            Instance = instance,
            Isolate = isolate,
            TopmostCallerIndex = functionIndex,
            Trace = trace,
        };
        double scaled = BudgetScaleFactor(instance);
        // See inlining-tree.h for the Turboshaft adjustments.
        const int kTurboshaftAdjustment = 2;
        int highGrowth = (int)isolate.Flags.wasm_inlining_factor + kTurboshaftAdjustment;
        const int kLowestUsefulValue = 2;
        int lowGrowth = Math.Max(kLowestUsefulValue, highGrowth - 3);
        data.MaxGrowthFactor = lowGrowth * (1 - scaled) + highGrowth * scaled;
        const double kTurboshaftCorrectionFactor = 1.2;
        double highCap = isolate.Flags.wasm_inlining_budget * kTurboshaftCorrectionFactor;
        double lowCap = highCap / 10;
        data.BudgetCap = (long)(lowCap * (1 - scaled) + highCap * scaled);
        if (trace) TraceFeedback(instance, functionIndex);
        var tree = new WasmInliningTree(data, functionIndex, 1.0,
            0, // The root is always expanded.
            -1, -1, -1, 0);
        tree.FullyExpand();
        return tree;
    }

    /// <summary>
    /// The trace of TransitiveTypeFeedbackProcessor: the feedback of the
    /// function and of the functions its feedback names, call by call.
    /// </summary>
    static void TraceFeedback(WasmInstanceData instance, int functionIndex)
    {
        var queue = new Queue<int>();
        var seen = new HashSet<int> { functionIndex };
        queue.Enqueue(functionIndex);
        while (queue.Count > 0)
        {
            int index = queue.Dequeue();
            if (instance.Code[index].Feedback is not { } feedback) continue;
            var sites = new List<WasmCallSite>(feedback.Sites.Values);
            sites.Sort((a, b) => a.CallIndex.CompareTo(b.CallIndex));
            foreach (WasmCallSite site in sites)
            {
                site.Flush();
                if (site.Megamorphic)
                {
                    Print($"[function {index}: call #{site.CallIndex}: megamorphic]\n");
                    continue;
                }
                if (site.Cases == 1) Print($"[function {index}: call #{site.CallIndex} inlineable (monomorphic)]\n");
                else if (site.Cases > 1) Print($"[function {index}: call #{site.CallIndex} inlineable (polymorphic {site.Cases})]\n");
                for (int c = 0; c < site.Cases; c++)
                {
                    if (seen.Add(site.Targets[c])) queue.Enqueue(site.Targets[c]);
                }
            }
        }
    }

    /// <summary>The wire byte size of a declared function's body (V8: WasmFunction::code.length()).</summary>
    public static int WireByteSize(FunctionInstance function)
    {
        uint[] offsets = function.Definition.InstructionOffsets;
        return offsets.Length == 0 ? 0 : (int)(offsets[^1] + 1 - function.Definition.BodyOffset);
    }

    /// <summary>InliningTree::BudgetScaleFactor (num_small_functions: bodies under 50 bytes).</summary>
    static double BudgetScaleFactor(WasmInstanceData instance)
    {
        double smallFunctionPercentage = instance.SmallFunctionPercentage;
        if (smallFunctionPercentage <= 25) return 0;
        if (smallFunctionPercentage >= 50) return 1;
        return (smallFunctionPercentage - 25) / 25;
    }

    double Score() => _wireByteSize == 0 ? 0.0 : _relativeCallCount / _wireByteSize;

    static void Print(string s) => Console.Out.Write(s);

    static string F(double d) => d.ToString("F6", CultureInfo.InvariantCulture);

    /// <summary>
    /// InliningTree::Inline: marks the call inlined and collects its calls:
    /// direct calls from the body, call_indirect/call_ref targets from the
    /// function's feedback.
    /// </summary>
    void Inline()
    {
        _isInlined = true;
        WasmInstanceData instance = _data.Instance;
        WasmCode code = instance.Code[FunctionIndex];
        var function = (FunctionInstance)code.Function;
        WasmFunctionFeedback? feedback = code.Feedback;
        byte[] bytes = instance.WireBytes;
        uint[] offsets = function.Definition.InstructionOffsets;
        int invocations = feedback?.Invocations ?? 0;
        int callIndex = 0;
        bool sawDirectCall = false;
        for (int i = 0; i < offsets.Length; i++)
        {
            int pos = (int)offsets[i];
            byte op = bytes[pos];
            if (op is 0x10 or 0x12)
            {
                int callee = (int)ReadU32(bytes, pos + 1);
                int slot = callIndex++;
                sawDirectCall = true;
                if (!IsInlineable(instance, callee))
                {
                    _nonInlineable.Add(i);
                    continue;
                }
                var calleeFunction = (FunctionInstance)instance.Code[callee].Function;
                _calls[i] =
                [
                    new WasmInliningTree(_data, callee, _relativeCallCount, WireByteSize(calleeFunction),
                        FunctionIndex, slot, 0, _depth + 1),
                ];
            }
            else if (op is 0x11 or 0x13 or 0x14 or 0x15)
            {
                int slot = callIndex++;
                if (feedback is null || !feedback.Sites.TryGetValue(i, out WasmCallSite? site)) continue;
                site.Flush();
                if (site.HasNonInlineableTargets) _nonInlineable.Add(i);
                if (site.Megamorphic || site.Cases == 0) continue;
                // FeedbackMaker::AddCall: the cases by count, highest first.
                var order = new int[site.Cases];
                for (int c = 0; c < order.Length; c++) order[c] = c;
                Array.Sort(order, (a, b) => site.Counts[b].CompareTo(site.Counts[a]));
                var cases = new List<WasmInliningTree>(order.Length);
                for (int c = 0; c < order.Length; c++)
                {
                    int callee = site.Targets[order[c]];
                    if (!IsInlineable(instance, callee)) continue;
                    double relative = invocations != 0 ? (double)site.Counts[order[c]] / invocations : 0.0;
                    var calleeFunction = (FunctionInstance)instance.Code[callee].Function;
                    cases.Add(new WasmInliningTree(_data, callee, relative * _relativeCallCount,
                        WireByteSize(calleeFunction), FunctionIndex, slot, c, _depth + 1));
                }
                if (cases.Count > 0) _calls[i] = [.. cases];
            }
        }
        _numCalls = callIndex;
        // V8 has feedback for a function that ran in Liftoff; direct calls
        // need none here.
        _feedbackFound = callIndex > 0 && (sawDirectCall || invocations > 0);
    }

    /// <summary>
    /// Whether a function can be inlined at all: V8 inlines only declared
    /// functions of the same instance; V8Sharp also leaves out functions with
    /// exception handlers (try, try_table, delegate, rethrow) and functions
    /// kept in the interpreter.
    /// </summary>
    static bool IsInlineable(WasmInstanceData instance, int functionIndex)
    {
        if (functionIndex < instance.ImportedFunctionCount || functionIndex >= instance.Code.Length) return false;
        WasmCode code = instance.Code[functionIndex];
        if (code.Instance != instance || code.Function is not FunctionInstance function) return false;
        if (code.State == WasmCodeState.Interpreted || code.State == WasmCodeState.Host) return false;
        if (WasmModuleCompiler.IsForcedInterpreted(functionIndex)) return false;
        return !instance.HasExceptionHandlers(function);
    }

    static uint ReadU32(byte[] bytes, int pos)
    {
        uint result = 0;
        int shift = 0;
        while (true)
        {
            byte b = bytes[pos++];
            result |= (uint)(b & 0x7f) << shift;
            if ((b & 0x80) == 0) return result;
            shift += 7;
        }
    }

    /// <summary>
    /// InliningTree::FullyExpand: candidates by score, highest first, until
    /// <see cref="kMaxInlinedCount"/> are inlined or the budget is used up.
    /// </summary>
    void FullyExpand()
    {
        int initialWireByteSize = WireByteSize((FunctionInstance)_data.Instance.Code[FunctionIndex].Function);
        long inlinedWireByteCount = 0;
        // Score descending, then function index ascending (TreeNodeOrdering).
        var queue = new PriorityQueue<WasmInliningTree, (double, int)>(
            Comparer<(double, int)>.Create((a, b) => a.Item1 != b.Item1 ? b.Item1.CompareTo(a.Item1) : a.Item2.CompareTo(b.Item2)));
        queue.Enqueue(this, (Score(), FunctionIndex));
        int inlinedCount = 0;
        bool trace = _data.Trace;
        bool ignoreCallCounts = _data.Isolate.Flags.wasm_inlining_ignore_call_counts;
        while (queue.Count > 0 && inlinedCount < kMaxInlinedCount)
        {
            WasmInliningTree top = queue.Dequeue();
            if (trace)
            {
                if (top != this)
                {
                    Print($"[function {_data.TopmostCallerIndex}: in function {top._callerIndex}, considering call #{top._feedbackSlot}, case #{top._case}, to function {top.FunctionIndex} (relative_call_count={F(top._relativeCallCount)}, size={top._wireByteSize}, score={F(top.Score())})... ");
                }
                else
                {
                    Print($"[function {_data.TopmostCallerIndex}: expanding topmost caller... ");
                }
            }
            // Inline when a candidate is hotter than it is big; tiny ones regardless.
            if (top._wireByteSize >= 12 && !ignoreCallCounts && top != this)
            {
                if (top.Score() < 0.0001)
                {
                    if (trace) Print("not called often enough]\n");
                    continue;
                }
            }
            if (top != this && !top.SmallEnoughToInline(initialWireByteSize, inlinedWireByteCount))
            {
                if (trace) Print("not enough inlining budget]\n");
                continue;
            }
            if (trace && top != this) Print("decided to inline! ");
            top.Inline();
            inlinedCount++;
            const int kOneLessCall = 6;
            inlinedWireByteCount += Math.Max(top._wireByteSize - kOneLessCall, 0);
            if (!top._feedbackFound)
            {
                if (trace) Print("no feedback yet or no callees]\n");
            }
            else if (top._depth < kMaxInliningNestingDepth)
            {
                if (trace) Print($"queueing {top._numCalls} callee(s)]\n");
                foreach (WasmInliningTree[] cases in top._calls.Values)
                {
                    foreach (WasmInliningTree call in cases) queue.Enqueue(call, (call.Score(), call.FunctionIndex));
                }
            }
            else if (trace)
            {
                Print("max inlining depth reached]\n");
            }
        }
        if (trace && queue.Count > 0)
        {
            Print($"[function {_data.TopmostCallerIndex}: too many inlining candidates, stopping...]\n");
        }
    }

    /// <summary>InliningTree::SmallEnoughToInline.</summary>
    bool SmallEnoughToInline(long initialWireByteSize, long inlinedWireByteCount)
    {
        if ((ulong)_wireByteSize > _data.Isolate.Flags.wasm_inlining_max_size) return false;
        // For tiny functions, be a bit more generous.
        if (_wireByteSize < 12)
        {
            inlinedWireByteCount = inlinedWireByteCount > 100 ? inlinedWireByteCount - 100 : 0;
        }
        long budgetSmallFunction = Math.Max((long)_data.Isolate.Flags.wasm_inlining_min_budget,
            (long)(_data.MaxGrowthFactor * initialWireByteSize));
        long budgetLargeFunction = Math.Max(_data.BudgetCap, (long)(initialWireByteSize * 1.1));
        long totalSize = initialWireByteSize + inlinedWireByteCount + _wireByteSize;
        if (_data.Trace)
        {
            Print($"budget=min({budgetSmallFunction}, {budgetLargeFunction}), size {initialWireByteSize + inlinedWireByteCount}->{totalSize} ");
        }
        return totalSize < Math.Min(budgetSmallFunction, budgetLargeFunction);
    }
}
