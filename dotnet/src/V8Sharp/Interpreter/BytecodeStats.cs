#if BYTECODE_STATS
namespace V8Sharp.Interpreter;

/// <summary>Temporary: dynamic bytecode and bytecode-pair counts (build with -p:DefineConstants=BYTECODE_STATS).</summary>
public static class BytecodeStats
{
    static readonly long[] s_single = new long[256];
    static readonly long[] s_pairs = new long[256 * 256];
    static int s_prev;

    static BytecodeStats()
    {
        AppDomain.CurrentDomain.ProcessExit += (_, _) => Dump();
    }

    public static void Count(byte b)
    {
        s_single[b]++;
        s_pairs[s_prev * 256 + b]++;
        s_prev = b;
    }

    static void Dump()
    {
        string? path = Environment.GetEnvironmentVariable("V8SHARP_BYTECODE_STATS");
        if (path is null) return;
        using var w = new StreamWriter(path);
        long total = 0;
        foreach (long c in s_single) total += c;
        w.WriteLine("total " + total);
        var singles = Enumerable.Range(0, 256).OrderByDescending(i => s_single[i]).Take(80);
        foreach (int i in singles)
            w.WriteLine($"{(Bytecode)i,-40} {s_single[i],14} {100.0 * s_single[i] / total,6:F2}%");
        w.WriteLine();
        var pairs = Enumerable.Range(0, 256 * 256).OrderByDescending(i => s_pairs[i]).Take(120);
        foreach (int i in pairs)
            w.WriteLine($"{(Bytecode)(i / 256),-30} {(Bytecode)(i % 256),-30} {s_pairs[i],14} {100.0 * s_pairs[i] / total,6:F2}%");
    }
}
#endif
