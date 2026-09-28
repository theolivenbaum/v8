// Port of src/d8/d8-console.cc: d8's debug::ConsoleDelegate. log, warn,
// info, debug and assert print their arguments to stdout, error to stderr;
// time, timeLog, timeEnd and timeStamp print timers. The profiler methods
// and trace are not ported (no CPU profiler; trace prints to stderr only).
using System.Diagnostics;
using System.Globalization;
using V8Sharp.Builtins;
using V8Sharp.Objects;

namespace V8Sharp.D8;

/// <summary>
/// D8Console. Output goes through <paramref name="stdout"/> and
/// <paramref name="stderr"/> (d8's printf/fprintf), so an in-process shell can
/// capture it.
/// </summary>
public sealed class D8Console(Action<string> stdout, Action<string> stderr) : ConsoleDelegate
{
    readonly long _origin = Stopwatch.GetTimestamp();
    readonly Dictionary<string, long> _timers = new(StringComparer.Ordinal);

    /// <summary>A console on the process's stdout and stderr (d8sharp).</summary>
    public D8Console() : this(static s => Console.Out.Write(s), static s => { Console.Out.Flush(); Console.Error.Write(s); })
    {
    }

    /// <summary>
    /// WriteToFile: the prefix, then each argument as it is converted, so a
    /// ToString that throws leaves "prefix: " and the arguments printed so far
    /// in the output (the exception then propagates out of the console call).
    /// </summary>
    static void WriteToFile(string? prefix, Action<string> file, Isolate isolate, ReadOnlySpan<JSValue> args)
    {
        if (prefix is not null) file(prefix + ": ");
        for (int i = 0; i < args.Length; i++)
        {
            if (i > 0) file(" ");
            JSValue arg = args[i];
            if (arg.HeapObjectOrNull is Symbol symbol) arg = symbol.Description;
            file(ObjectOps.ToString(isolate, arg).ToString());
        }
        file("\n");
    }

    public override void Assert(Isolate isolate, ReadOnlySpan<JSValue> args, ConsoleContext context)
    {
        // If no arguments given, the "first" argument is undefined which is
        // false-ish.
        if (args.Length > 0 && ObjectOps.BooleanValue(args[0])) return;
        WriteToFile("console.assert", stdout, isolate, args);
        isolate.Throw(isolate.Factory.NewError(isolate.NativeContext.ErrorFunction,
            isolate.Factory.NewStringFromUtf16("console.assert failed")));
    }

    public override void Log(Isolate isolate, ReadOnlySpan<JSValue> args, ConsoleContext context) =>
        WriteToFile(null, stdout, isolate, args);

    public override void Error(Isolate isolate, ReadOnlySpan<JSValue> args, ConsoleContext context) =>
        WriteToFile("console.error", stderr, isolate, args);

    public override void Warn(Isolate isolate, ReadOnlySpan<JSValue> args, ConsoleContext context) =>
        WriteToFile("console.warn", stdout, isolate, args);

    public override void Info(Isolate isolate, ReadOnlySpan<JSValue> args, ConsoleContext context) =>
        WriteToFile("console.info", stdout, isolate, args);

    public override void Debug(Isolate isolate, ReadOnlySpan<JSValue> args, ConsoleContext context) =>
        WriteToFile("console.debug", stdout, isolate, args);

    /// <summary>GetTimerLabel: "default" without arguments, else ToString of the first (which may throw).</summary>
    static string GetTimerLabel(Isolate isolate, ReadOnlySpan<JSValue> args) =>
        args.Length == 0 ? "default" : ObjectOps.ToString(isolate, args[0]).ToString();

    static string Milliseconds(long from) =>
        Stopwatch.GetElapsedTime(from).TotalMilliseconds.ToString("F6", CultureInfo.InvariantCulture);

    public override void Time(Isolate isolate, ReadOnlySpan<JSValue> args, ConsoleContext context)
    {
        if (isolate.Flags.correctness_fuzzer_suppressions) return;
        string label = GetTimerLabel(isolate, args);
        if (!_timers.TryAdd(label, Stopwatch.GetTimestamp()))
        {
            stdout("console.time: Timer '" + label + "' already exists\n");
        }
    }

    public override void TimeLog(Isolate isolate, ReadOnlySpan<JSValue> args, ConsoleContext context)
    {
        if (isolate.Flags.correctness_fuzzer_suppressions) return;
        string label = GetTimerLabel(isolate, args);
        if (!_timers.TryGetValue(label, out long start))
        {
            stdout("console.timeLog: Timer '" + label + "' does not exist\n");
            return;
        }
        stdout("console.timeLog: " + label + ", " + Milliseconds(start) + "\n");
    }

    public override void TimeEnd(Isolate isolate, ReadOnlySpan<JSValue> args, ConsoleContext context)
    {
        if (isolate.Flags.correctness_fuzzer_suppressions) return;
        string label = GetTimerLabel(isolate, args);
        if (!_timers.Remove(label, out long start))
        {
            stdout("console.timeEnd: Timer '" + label + "' does not exist\n");
            return;
        }
        stdout("console.timeEnd: " + label + ", " + Milliseconds(start) + "\n");
    }

    public override void TimeStamp(Isolate isolate, ReadOnlySpan<JSValue> args, ConsoleContext context)
    {
        if (isolate.Flags.correctness_fuzzer_suppressions) return;
        string label = GetTimerLabel(isolate, args);
        stdout("console.timeStamp: " + label + ", " + Milliseconds(_origin) + "\n");
    }
}
