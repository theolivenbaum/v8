// Port of src/flags/flags.cc: FlagList::SetFlagsFromCommandLine /
// SetFlagsFromString. The flag fields themselves are generated from
// flag-definitions.h into FlagList.Generated.cs.
//
// V8 keeps one process-global v8_flags. V8Sharp gives each Isolate its own
// FlagList (Isolate.Flags) so tests can run isolates with different flags in
// one process; Isolate.CurrentFlags reads the current isolate's.
using System.Globalization;

namespace V8Sharp.Common;

/// <summary>V8's FlagList: the engine flags, with V8's names (snake_case) and defaults.</summary>
public sealed partial class FlagList
{
    HashSet<string> _explicitlySet = new(StringComparer.Ordinal);

    /// <summary>
    /// V8Sharp only (no V8 flag): Maglev code of more IL bytes than this is
    /// split into regions of about half of it (MaglevCodeGenerator.Regions.cs);
    /// 0 splits only code over RyuJIT's optimization limits. Tests and
    /// experiments set it (V8SHARP_MAGLEV_SPLIT_IL).
    /// </summary>
    public int MaglevSplitILBytes = int.TryParse(Environment.GetEnvironmentVariable("V8SHARP_MAGLEV_SPLIT_IL"), out int split) ? split : 0;

    /// <summary>
    /// V8Sharp only (no V8 flag): Maglev graphs the native backend supports
    /// are compiled to x64 machine code (MaglevNativeCodeGenerator, Linux on
    /// x64); --no-maglev-native compiles every graph to IL for RyuJIT
    /// (MaglevCodeGenerator), the backend before it. V8SHARP_MAGLEV_NATIVE=0
    /// sets the default.
    /// </summary>
    public bool maglev_native = Environment.GetEnvironmentVariable("V8SHARP_MAGLEV_NATIVE") != "0";

    /// <summary>V8Sharp only: prints which graphs the native Maglev backend compiles, and why it declines the others.</summary>
    public bool trace_maglev_native;

    /// <summary>
    /// V8Sharp only: the native Maglev backend takes only graphs with a loop.
    /// Entering native code costs a transition (the context, the pinned
    /// arrays, the exit's decoding) that a leaf without a loop does not earn
    /// back against RyuJIT's code, which callers enter directly; tests turn it
    /// off to run straight-line code natively.
    /// </summary>
    public bool maglev_native_loops_only = true;

    /// <summary>The V8Sharp-only flags (not in flag-definitions.h): name, then setter.</summary>
    bool SetV8SharpFlag(string name, string? value, bool negated)
    {
        switch (name)
        {
            case "maglev_native":
                if (!ParseBool(value, negated, out bool native)) return false;
                maglev_native = native;
                break;
            case "trace_maglev_native":
                if (!ParseBool(value, negated, out bool trace)) return false;
                trace_maglev_native = trace;
                break;
            case "maglev_native_loops_only":
                if (!ParseBool(value, negated, out bool loops)) return false;
                maglev_native_loops_only = loops;
                break;
            default:
                return false;
        }
        _explicitlySet.Add(name);
        return true;
    }

    static bool IsV8SharpFlag(string name) => name is "maglev_native" or "trace_maglev_native" or "maglev_native_loops_only";

    /// <summary>The process default flags (V8's v8_flags); new isolates copy them.</summary>
    public static FlagList Default { get; } = new();

    /// <summary>A copy of these flags.</summary>
    public FlagList Clone()
    {
        var copy = (FlagList)MemberwiseClone();
        copy._explicitlySet = new HashSet<string>(_explicitlySet, StringComparer.Ordinal);
        return copy;
    }

    /// <summary>True if the flag was set on the command line (V8's IsDefault() == false).</summary>
    public bool IsExplicitlySet(string name) => _explicitlySet.Contains(name);

    /// <summary>
    /// FlagList::SetFlagsFromString: parses "--flag --no-flag --flag=value --flag value".
    /// Dashes and underscores in names are equivalent. Returns the arguments that were not flags.
    /// </summary>
    public List<string> SetFlagsFromString(string flags)
    {
        var args = new List<string>();
        int i = 0;
        while (i < flags.Length)
        {
            while (i < flags.Length && char.IsWhiteSpace(flags[i])) i++;
            if (i >= flags.Length) break;
            int start = i;
            if (flags[i] == '"' || flags[i] == '\'')
            {
                char q = flags[i++];
                start = i;
                while (i < flags.Length && flags[i] != q) i++;
                args.Add(flags.Substring(start, i - start));
                if (i < flags.Length) i++;
                continue;
            }
            while (i < flags.Length && !char.IsWhiteSpace(flags[i])) i++;
            args.Add(flags.Substring(start, i - start));
        }
        return SetFlagsFromCommandLine(args);
    }

    /// <summary>FlagList::SetFlagsFromCommandLine. Unknown flags and non-flag arguments are returned.</summary>
    public List<string> SetFlagsFromCommandLine(IReadOnlyList<string> argv)
    {
        var rest = new List<string>();
        for (int i = 0; i < argv.Count; i++)
        {
            string arg = argv[i];
            if (!arg.StartsWith('-') || arg == "-" || arg == "--")
            {
                rest.Add(arg);
                continue;
            }
            string body = arg.TrimStart('-');
            string? value = null;
            int eq = body.IndexOf('=');
            if (eq >= 0)
            {
                value = body[(eq + 1)..];
                body = body[..eq];
            }
            string name = body.Replace('-', '_');
            bool negated = false;
            if (name.StartsWith("no", StringComparison.Ordinal) && !IsKnown(name) && IsKnown(name[2..].TrimStart('_')))
            {
                negated = true;
                name = name[2..].TrimStart('_');
            }
            if (value is null && !IsBoolFlag(name) && !IsV8SharpFlag(name) && IsKnown(name) && i + 1 < argv.Count)
            {
                value = argv[++i];
            }
            if (!SetFlag(name, value, negated) && !SetV8SharpFlag(name, value, negated)) rest.Add(arg);
        }
        EnforceFlagImplications();
        return rest;
    }

    static bool IsKnown(string name) => IsKnownFlag(name) || IsV8SharpFlag(name);

    static bool ParseBool(string? value, bool negated, out bool result)
    {
        if (value is null)
        {
            result = !negated;
            return true;
        }
        if (negated)
        {
            result = false;
            return false;
        }
        switch (value)
        {
            case "true": case "1": result = true; return true;
            case "false": case "0": result = false; return true;
            default: result = false; return false;
        }
    }

    static bool ParseBool(string? value, bool negated, out bool? result)
    {
        bool ok = ParseBool(value, negated, out bool b);
        result = b;
        return ok;
    }

    static bool ParseInt(string? value, out int result) =>
        int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out result);

    static bool ParseUInt(string? value, out uint result) =>
        uint.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out result);

    static bool ParseULong(string? value, out ulong result) =>
        ulong.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out result);

    static bool ParseDouble(string? value, out double result) =>
        double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out result);
}
