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
            if (value is null && !IsBoolFlag(name) && IsKnown(name) && i + 1 < argv.Count)
            {
                value = argv[++i];
            }
            if (!SetFlag(name, value, negated)) rest.Add(arg);
        }
        EnforceFlagImplications();
        return rest;
    }

    static bool IsKnown(string name) => IsKnownFlag(name);

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
