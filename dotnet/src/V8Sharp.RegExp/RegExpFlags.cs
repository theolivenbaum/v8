// Port of src/regexp/regexp-flags.h.

namespace V8Sharp.RegExp;

/// <summary>
/// RegExp flags. Bit positions are V8's (<c>REGEXP_FLAG_LIST</c>): the flag
/// characters are alphabetically sorted, which shuffles the bits.
/// </summary>
[Flags]
public enum RegExpFlags
{
    None = 0,
    Global = 1 << 0,      // 'g'
    IgnoreCase = 1 << 1,  // 'i'
    Multiline = 1 << 2,   // 'm'
    Sticky = 1 << 3,      // 'y'
    Unicode = 1 << 4,     // 'u'
    DotAll = 1 << 5,      // 's'
    Linear = 1 << 6,      // 'l'
    HasIndices = 1 << 7,  // 'd'
    UnicodeSets = 1 << 8, // 'v'
}

public static class RegExpFlagsExtensions
{
    public const int kFlagCount = 9;

    public static bool IsGlobal(this RegExpFlags f) => (f & RegExpFlags.Global) != 0;
    public static bool IsIgnoreCase(this RegExpFlags f) => (f & RegExpFlags.IgnoreCase) != 0;
    public static bool IsMultiline(this RegExpFlags f) => (f & RegExpFlags.Multiline) != 0;
    public static bool IsSticky(this RegExpFlags f) => (f & RegExpFlags.Sticky) != 0;
    public static bool IsUnicode(this RegExpFlags f) => (f & RegExpFlags.Unicode) != 0;
    public static bool IsDotAll(this RegExpFlags f) => (f & RegExpFlags.DotAll) != 0;
    public static bool IsLinear(this RegExpFlags f) => (f & RegExpFlags.Linear) != 0;
    public static bool IsHasIndices(this RegExpFlags f) => (f & RegExpFlags.HasIndices) != 0;
    public static bool IsUnicodeSets(this RegExpFlags f) => (f & RegExpFlags.UnicodeSets) != 0;

    public static bool IsEitherUnicode(this RegExpFlags f) => (f & (RegExpFlags.Unicode | RegExpFlags.UnicodeSets)) != 0;

    /// <summary>Whether to rewind the index when it initially points into the
    /// middle of a surrogate pair. See also OptionallyStepBackToLeadSurrogate().</summary>
    public static bool ShouldOptionallyStepBackToLeadSurrogate(this RegExpFlags f) =>
        f.IsEitherUnicode() && (f.IsGlobal() || f.IsSticky());

    /// <summary>TryFlagFromChar.</summary>
    public static RegExpFlags? TryFlagFromChar(char c) => c switch
    {
        'd' => RegExpFlags.HasIndices,
        'g' => RegExpFlags.Global,
        'i' => RegExpFlags.IgnoreCase,
        'l' => RegExpFlags.Linear,
        'm' => RegExpFlags.Multiline,
        's' => RegExpFlags.DotAll,
        'u' => RegExpFlags.Unicode,
        'v' => RegExpFlags.UnicodeSets,
        'y' => RegExpFlags.Sticky,
        _ => null,
    };

    /// <summary>operator&lt;&lt;(std::ostream&amp;, Flags): flag chars in alphabetical order.</summary>
    public static string ToFlagString(this RegExpFlags flags)
    {
        Span<char> buf = stackalloc char[kFlagCount];
        int n = 0;
        if ((flags & RegExpFlags.HasIndices) != 0) buf[n++] = 'd';
        if ((flags & RegExpFlags.Global) != 0) buf[n++] = 'g';
        if ((flags & RegExpFlags.IgnoreCase) != 0) buf[n++] = 'i';
        if ((flags & RegExpFlags.Linear) != 0) buf[n++] = 'l';
        if ((flags & RegExpFlags.Multiline) != 0) buf[n++] = 'm';
        if ((flags & RegExpFlags.DotAll) != 0) buf[n++] = 's';
        if ((flags & RegExpFlags.Unicode) != 0) buf[n++] = 'u';
        if ((flags & RegExpFlags.UnicodeSets) != 0) buf[n++] = 'v';
        if ((flags & RegExpFlags.Sticky) != 0) buf[n++] = 'y';
        return new string(buf[..n]);
    }

    /// <summary>
    /// Parses a flags string the way JSRegExp::FlagsFromString does: every
    /// character must be a known flag and appear at most once. Returns null on
    /// an invalid flags string.
    /// </summary>
    public static RegExpFlags? FromString(string flags)
    {
        RegExpFlags value = RegExpFlags.None;
        foreach (char c in flags)
        {
            RegExpFlags? f = TryFlagFromChar(c);
            if (f is null) return null;
            if ((value & f.Value) != 0) return null;
            value |= f.Value;
        }
        return value;
    }
}
