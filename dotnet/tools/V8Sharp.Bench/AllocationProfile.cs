// An allocation profile of a measurement (V8SHARP_BENCH_ALLOCPROFILE=1 with
// `run`): the runtime's AllocationTick events (one about every 100 KB
// allocated, naming the type of the object that crossed the threshold),
// summed per type and printed at exit, largest first. A sampled profile:
// shares are what it is good for, not exact counts.
using System.Diagnostics.Tracing;
using System.Globalization;

namespace V8Sharp.Bench;

sealed class AllocationProfile : EventListener
{
    readonly Dictionary<string, (long Bytes, long Samples)> _byType = new(StringComparer.Ordinal);
    readonly object _lock = new();

    protected override void OnEventSourceCreated(EventSource source)
    {
        // The GC keyword (0x1) at Verbose enables GCAllocationTick.
        if (source.Name == "Microsoft-Windows-DotNETRuntime") EnableEvents(source, EventLevel.Verbose, (EventKeywords)0x1);
    }

    protected override void OnEventWritten(EventWrittenEventArgs e)
    {
        if (e.EventName is null || !e.EventName.StartsWith("GCAllocationTick", StringComparison.Ordinal) || e.Payload is null) return;
        int nameIndex = e.PayloadNames!.IndexOf("TypeName");
        int amountIndex = e.PayloadNames.IndexOf("AllocationAmount64");
        if (amountIndex < 0) amountIndex = e.PayloadNames.IndexOf("AllocationAmount");
        string type = nameIndex >= 0 ? e.Payload[nameIndex] as string ?? "?" : "?";
        long amount = amountIndex >= 0 ? Convert.ToInt64(e.Payload[amountIndex], CultureInfo.InvariantCulture) : 0;
        lock (_lock)
        {
            _byType.TryGetValue(type, out var t);
            _byType[type] = (t.Bytes + amount, t.Samples + 1);
        }
    }

    public void Print(TextWriter output, int top = 30)
    {
        lock (_lock)
        {
            long total = 0;
            foreach (var t in _byType.Values) total += t.Bytes;
            output.WriteLine($"@alloc-profile total {total / 1048576.0:F1} MB sampled");
            foreach (var (type, t) in _byType.OrderByDescending(kv => kv.Value.Bytes).Take(top))
                output.WriteLine($"@alloc {100.0 * t.Bytes / Math.Max(1, total),6:F1}% {t.Bytes / 1048576.0,9:F1} MB  {type}");
        }
    }
}
