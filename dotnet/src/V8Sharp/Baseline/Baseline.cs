// Port of src/baseline/baseline.{h,cc}: CanCompileWithBaseline and
// GenerateBaselineCode, plus SharedFunctionInfo::PassesFilter
// (src/objects/shared-function-info.cc, src/utils/utils.cc PassesFilter) for
// --sparkplug-filter.
using V8Sharp.Interpreter;

namespace V8Sharp.Baseline;

public static class BaselineSupport
{
    /// <summary>The largest bytecode array that tiers up to baseline code by itself (a V8Sharp limit; see TiersUpToBaseline).</summary>
    public const int kMaxBytecodeLength = 5000;

    /// <summary>CanCompileWithBaseline.</summary>
    public static bool CanCompileWithBaseline(Isolate isolate, SharedFunctionInfo shared)
    {
        // Check that baseline compiler is enabled.
        if (!isolate.Flags.sparkplug) return false;

        // Check that short builtin calls are enabled if needed. (V8Sharp has no
        // builtin call distance: short builtin calls are never enabled.)
        if (isolate.Flags.sparkplug_needs_short_builtins) return false;

        // Check if we actually have bytecode.
        if (shared.FunctionData is not BytecodeArray) return false;

        // (No debugger: no break points and no instrumented bytecode.)

        // Do not baseline compile if function doesn't pass sparkplug_filter.
        if (!PassesFilter(shared, isolate.Flags.sparkplug_filter)) return false;

        return true;
    }

    /// <summary>
    /// Whether the tiering paths (batch compilation, --always-sparkplug)
    /// compile the function: CanCompileWithBaseline, and not beyond the size
    /// limit.
    /// </summary>
    /// <remarks>
    /// Deviation: V8 tiers up functions of any size to Sparkplug. A function
    /// with more than <see cref="kMaxBytecodeLength"/> bytes of bytecode would
    /// be one IL method beyond RyuJIT's optimization limits even in the
    /// compact form (BaselineILEmitter: about 3 IL instructions and one local
    /// reference per bytecode byte), which RyuJIT compiles with minimal
    /// optimization only: slower than the interpreter, slow to compile, and
    /// with a large .NET frame. It stays in the interpreter unless compiled
    /// explicitly (%CompileBaseline).
    /// </remarks>
    public static bool TiersUpToBaseline(Isolate isolate, SharedFunctionInfo shared) =>
        CanCompileWithBaseline(isolate, shared) && ((BytecodeArray)shared.FunctionData!).Length <= kMaxBytecodeLength;

    /// <summary>GenerateBaselineCode.</summary>
    public static BaselineCode GenerateBaselineCode(Isolate isolate, SharedFunctionInfo shared)
    {
        // (The IL is generated on the code's first run; see BaselineCode.)
        return new BaselineCode(isolate, shared, (BytecodeArray)shared.FunctionData!);
    }

    /// <summary>SharedFunctionInfo::PassesFilter over the debug name.</summary>
    public static bool PassesFilter(SharedFunctionInfo shared, string? filter)
    {
        if (filter is null || filter == "*") return true;
        string name = shared.Name().ToString();
        if (name.Length == 0) name = shared.InferredName().ToString();
        return PassesFilter(name, filter);
    }

    /// <summary>PassesFilter (src/utils/utils.cc).</summary>
    public static bool PassesFilter(string name, string filter)
    {
        if (filter.Length == 0) return name.Length == 0;
        int it = 0;
        bool positiveFilter = true;
        if (filter[it] == '-')
        {
            ++it;
            positiveFilter = false;
        }
        if (it == filter.Length) return name.Length != 0;
        if (filter[it] == '*') return positiveFilter;
        if (filter[it] == '~') return !positiveFilter;

        bool prefixMatch = filter[^1] == '*';
        int minMatchLength = filter.Length;
        if (!positiveFilter) minMatchLength--; // Subtract 1 for leading '-'.
        if (prefixMatch) minMatchLength--; // Subtract 1 for trailing '*'.

        if (name.Length < minMatchLength) return !positiveFilter;

        int f = it, n = 0;
        while (f < filter.Length && n < name.Length && filter[f] == name[n])
        {
            f++;
            n++;
        }
        if (f == filter.Length)
        {
            // The strings match, so {name} passes if we have a {positive_filter};
            // {name} is longer than the filter, so it passes if we don't.
            return n == name.Length ? positiveFilter : !positiveFilter;
        }
        // We matched up to the wildcard, so {name} passes if we have a {positive_filter}.
        if (filter[f] == '*') return positiveFilter;
        // We don't match, so {name} passes if we don't have a {positive_filter}.
        return !positiveFilter;
    }
}
