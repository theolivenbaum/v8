// Time zones (temporal_rs::TimeZone and its provider): offset time zones,
// "UTC", and named IANA time zones, with the spec's GetOffsetNanosecondsFor,
// GetPossibleEpochNanoseconds, DisambiguatePossibleEpochNanoseconds,
// GetEpochNanosecondsFor, GetStartOfDay, GetNamedTimeZoneNextTransition /
// PreviousTransition and TimeZoneEquals.
//
// Deviation (deviations.md "Temporal"): named time zones come from .NET's
// TimeZoneInfo (the IANA database of the host on Linux). V8 reads ICU's
// zoneinfo64 through temporal_rs's provider (js-temporal-zoneinfo64.cc).
// TimeZoneInfo covers years 1-9999; later instants reuse the rules of the
// equivalent year in the 400-year Gregorian cycle, earlier ones the offset at
// year 1. Link names are not resolved to their primary identifier except for
// the aliases of UTC.
using System.Collections.Concurrent;

namespace V8Sharp.Temporal;

public sealed class TimeZone
{
    /// <summary>The identifier as stored in a ZonedDateTime (case-normalized, or an offset "+HH:MM").</summary>
    public string Identifier { get; }

    /// <summary>For offset time zones, the offset in minutes.</summary>
    public int? OffsetMinutes { get; }

    readonly TimeZoneInfo? _info;

    /// <summary>The primary identifier (TimeZoneEquals compares these).</summary>
    public string PrimaryIdentifier { get; }

    TimeZone(string identifier, int? offsetMinutes, TimeZoneInfo? info, string primary)
    {
        Identifier = identifier;
        OffsetMinutes = offsetMinutes;
        _info = info;
        PrimaryIdentifier = primary;
    }

    public static readonly TimeZone Utc = new("UTC", null, null, "UTC");

    public bool IsOffset => OffsetMinutes is not null;

    public static TimeZone FromOffsetMinutes(int minutes) =>
        new(Formatting.FormatOffsetTimeZoneIdentifier(minutes), minutes, null, Formatting.FormatOffsetTimeZoneIdentifier(minutes));

    public override string ToString() => Identifier;

    // ---- Identifiers --------------------------------------------------------------------------

    static readonly string[] s_utcAliases =
    [
        "Etc/UTC", "Etc/GMT", "GMT", "Etc/Universal", "Universal", "Etc/Zulu", "Zulu", "Etc/UCT", "UCT",
        "Etc/Greenwich", "Greenwich", "GMT0", "GMT+0", "GMT-0", "Etc/GMT0", "Etc/GMT+0", "Etc/GMT-0",
    ];

    static Dictionary<string, string>? s_available;
    static readonly ConcurrentDictionary<string, TimeZone> s_named = new(StringComparer.Ordinal);

    /// <summary>The available named time zone identifiers, keyed by their ASCII-lowercase form.</summary>
    static Dictionary<string, string> Available
    {
        get
        {
            if (s_available is { } a) return a;
            var map = new Dictionary<string, string>(StringComparer.Ordinal);
            try
            {
                foreach (TimeZoneInfo tz in TimeZoneInfo.GetSystemTimeZones())
                {
                    map.TryAdd(tz.Id.ToLowerInvariant(), tz.Id);
                }
                // The zoneinfo directory also holds links and backward-compatible names.
                string root = "/usr/share/zoneinfo";
                if (Directory.Exists(root))
                {
                    foreach (string file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
                    {
                        string rel = file[(root.Length + 1)..];
                        if (rel.StartsWith("posix/", StringComparison.Ordinal) ||
                            rel.StartsWith("right/", StringComparison.Ordinal) || rel.Contains('.') ||
                            rel is "posixrules" or "localtime" or "Factory" or "leapseconds" or "tzdata.zi" or "SECURITY")
                            continue;
                        if (rel.Length == 0 || !char.IsAsciiLetterUpper(rel[0])) continue;
                        map.TryAdd(rel.ToLowerInvariant(), rel);
                    }
                }
            }
            catch (Exception)
            {
                // No time zone data: only UTC and offsets are available.
            }
            foreach (string alias in s_utcAliases) map.TryAdd(alias.ToLowerInvariant(), alias);
            map["utc"] = "UTC";
            s_available = map;
            return map;
        }
    }

    /// <summary>
    /// GetAvailableNamedTimeZoneIdentifier: the case-normalized identifier, or null.
    /// </summary>
    public static TimeZone? GetAvailableNamed(ReadOnlySpan<char> name)
    {
        string lower = name.ToString().ToLowerInvariant();
        if (!Available.TryGetValue(lower, out string? identifier)) return null;
        if (s_named.TryGetValue(identifier, out TimeZone? cached)) return cached;
        TimeZone tz;
        if (identifier == "UTC" || Array.IndexOf(s_utcAliases, identifier) >= 0)
        {
            tz = identifier == "UTC" ? Utc : new TimeZone(identifier, null, null, "UTC");
        }
        else
        {
            TimeZoneInfo? info = null;
            try
            {
                info = TimeZoneInfo.FindSystemTimeZoneById(identifier);
            }
            catch (Exception)
            {
                return null;
            }
            tz = new TimeZone(identifier, null, info, identifier);
        }
        return s_named.GetOrAdd(identifier, tz);
    }

    /// <summary>ParseTimeZoneIdentifier followed by the availability check (TimeZone::try_from_identifier_str).</summary>
    public static TimeZone FromIdentifier(ReadOnlySpan<char> id)
    {
        if (id.Length > 0 && (id[0] == '+' || id[0] == '-'))
        {
            var parser = new IsoParser(id);
            if (!parser.TryParseUtcOffsetIdentifier(out int minutes) || !parser.AtEnd)
                throw TemporalError.Range("Invalid time zone identifier.");
            return FromOffsetMinutes(minutes);
        }
        if (!IsoParser.IsTimeZoneIANAName(id)) throw TemporalError.Range("Invalid time zone identifier.");
        return GetAvailableNamed(id) ?? throw TemporalError.Range("Unknown time zone " + id.ToString() + ".");
    }

    /// <summary>
    /// ToTemporalTimeZoneIdentifier for a string (TimeZone::try_from_str): a time zone
    /// identifier or an ISO string with a time zone annotation, Z or an offset.
    /// </summary>
    public static TimeZone FromString(ReadOnlySpan<char> s)
    {
        // ParseTemporalTimeZoneString: a TimeZoneIdentifier first.
        if (s.Length > 0 && (s[0] == '+' || s[0] == '-'))
        {
            var p = new IsoParser(s);
            if (p.TryParseUtcOffsetIdentifier(out int minutes) && p.AtEnd) return FromOffsetMinutes(minutes);
        }
        else if (IsoParser.IsTimeZoneIANAName(s))
        {
            return GetAvailableNamed(s) ?? throw TemporalError.Range("Unknown time zone " + s.ToString() + ".");
        }
        ParsedIso result = IsoParser.ParseAny(s);
        if (result.TimeZoneAnnotation is { } annotation) return FromIdentifier(annotation);
        if (result.HasZ) return Utc;
        if (result.OffsetString is { } offset)
        {
            var p = new IsoParser(offset);
            if (!p.TryParseUtcOffsetIdentifier(out int minutes) || !p.AtEnd)
                throw TemporalError.Range("Offset with sub-minute precision is not a time zone.");
            return FromOffsetMinutes(minutes);
        }
        throw TemporalError.Range("String does not contain a time zone.");
    }

    /// <summary>TimeZoneEquals.</summary>
    public static bool Equals(TimeZone one, TimeZone two)
    {
        if (ReferenceEquals(one, two) || one.Identifier == two.Identifier) return true;
        if (one.IsOffset != two.IsOffset) return false;
        if (one.IsOffset) return one.OffsetMinutes == two.OffsetMinutes;
        return one.PrimaryIdentifier == two.PrimaryIdentifier;
    }

    // ---- Offsets --------------------------------------------------------------------------------

    const long kTicksPerSecond = 10_000_000;
    static readonly long s_unixEpochTicks = DateTime.UnixEpoch.Ticks;
    const long kSecondsPer400Years = 146097L * 86400;

    /// <summary>The UTC offset in seconds of a named zone at an epoch second.</summary>
    long OffsetSecondsAt(long epochSeconds)
    {
        TimeZoneInfo info = _info!;
        // Map into DateTime's range using the 400-year cycle of the Gregorian calendar.
        long minSeconds = (DateTime.MinValue.Ticks - s_unixEpochTicks) / kTicksPerSecond + 2 * 86400;
        long maxSeconds = (DateTime.MaxValue.Ticks - s_unixEpochTicks) / kTicksPerSecond - 2 * 86400;
        if (epochSeconds > maxSeconds)
        {
            long cycles = (epochSeconds - maxSeconds) / kSecondsPer400Years + 1;
            epochSeconds -= cycles * kSecondsPer400Years;
        }
        else if (epochSeconds < minSeconds)
        {
            epochSeconds = minSeconds;
        }
        var utc = new DateTime(s_unixEpochTicks + epochSeconds * kTicksPerSecond, DateTimeKind.Utc);
        return (long)Math.Round(info.GetUtcOffset(utc).TotalSeconds);
    }

    /// <summary>GetOffsetNanosecondsFor.</summary>
    public long GetOffsetNanosecondsFor(Int128 epochNs)
    {
        if (OffsetMinutes is int m) return m * Units.NsPerMinute;
        if (_info is null) return 0;
        long seconds = (long)Rounding.Int128Floor(epochNs, Units.NsPerSecond);
        return OffsetSecondsAt(seconds) * Units.NsPerSecond;
    }

    /// <summary>GetISODateTimeFor.</summary>
    public IsoDateTime GetISODateTimeFor(Int128 epochNs)
    {
        long offsetNs = GetOffsetNanosecondsFor(epochNs);
        return IsoCalendar.FromEpochNanoseconds(epochNs + offsetNs);
    }

    /// <summary>GetPossibleEpochNanoseconds (sorted ascending).</summary>
    public List<Int128> GetPossibleEpochNanoseconds(in IsoDateTime isoDateTime)
    {
        var result = new List<Int128>(2);
        if (OffsetMinutes is int minutes)
        {
            IsoDateTime balanced = IsoCalendar.BalanceISODateTime(isoDateTime.Date,
                isoDateTime.Time.TotalNanoseconds - (Int128)minutes * Units.NsPerMinute);
            IsoCalendar.CheckISODaysRange(balanced.Date);
            result.Add(IsoCalendar.GetUTCEpochNanoseconds(balanced));
        }
        else
        {
            IsoCalendar.CheckISODaysRange(isoDateTime.Date);
            Int128 local = IsoCalendar.GetUTCEpochNanoseconds(isoDateTime);
            if (_info is null)
            {
                result.Add(local);
            }
            else
            {
                long before = GetOffsetNanosecondsFor(local - Units.NsPerDay);
                long after = GetOffsetNanosecondsFor(local + Units.NsPerDay);
                Span<long> candidates = [before, after];
                foreach (long offset in candidates)
                {
                    Int128 candidate = local - offset;
                    if (GetOffsetNanosecondsFor(candidate) == offset && !result.Contains(candidate)) result.Add(candidate);
                }
                if (result.Count == 0)
                {
                    long at = GetOffsetNanosecondsFor(local);
                    Int128 candidate = local - at;
                    if (GetOffsetNanosecondsFor(candidate) == at) result.Add(candidate);
                }
                result.Sort();
            }
        }
        foreach (Int128 ns in result)
        {
            if (!IsoCalendar.IsValidEpochNanoseconds(ns)) throw TemporalError.Range("Instant out of range.");
        }
        return result;
    }

    /// <summary>DisambiguatePossibleEpochNanoseconds.</summary>
    public Int128 Disambiguate(List<Int128> possible, in IsoDateTime isoDateTime, Disambiguation disambiguation)
    {
        int n = possible.Count;
        if (n == 1) return possible[0];
        if (n != 0)
        {
            return disambiguation switch
            {
                Disambiguation.Earlier or Disambiguation.Compatible => possible[0],
                Disambiguation.Later => possible[n - 1],
                _ => throw TemporalError.Range("Ambiguous local time rejected."),
            };
        }
        if (disambiguation == Disambiguation.Reject) throw TemporalError.Range("Nonexistent local time rejected.");
        Int128 epochNs = IsoCalendar.GetUTCEpochNanoseconds(isoDateTime);
        Int128 dayBefore = epochNs - Units.NsPerDay;
        if (!IsoCalendar.IsValidEpochNanoseconds(dayBefore)) throw TemporalError.Range("Instant out of range.");
        long offsetBefore = GetOffsetNanosecondsFor(dayBefore);
        Int128 dayAfter = epochNs + Units.NsPerDay;
        if (!IsoCalendar.IsValidEpochNanoseconds(dayAfter)) throw TemporalError.Range("Instant out of range.");
        long offsetAfter = GetOffsetNanosecondsFor(dayAfter);
        long nanoseconds = offsetAfter - offsetBefore;
        if (disambiguation == Disambiguation.Earlier)
        {
            IsoDateTime earlier = IsoCalendar.BalanceISODateTime(isoDateTime.Date, isoDateTime.Time.TotalNanoseconds - nanoseconds);
            List<Int128> p = GetPossibleEpochNanoseconds(earlier);
            if (p.Count == 0) throw TemporalError.Range("Nonexistent local time.");
            return p[0];
        }
        IsoDateTime later = IsoCalendar.BalanceISODateTime(isoDateTime.Date, isoDateTime.Time.TotalNanoseconds + nanoseconds);
        List<Int128> q = GetPossibleEpochNanoseconds(later);
        if (q.Count == 0) throw TemporalError.Range("Nonexistent local time.");
        return q[^1];
    }

    /// <summary>GetEpochNanosecondsFor.</summary>
    public Int128 GetEpochNanosecondsFor(in IsoDateTime isoDateTime, Disambiguation disambiguation) =>
        Disambiguate(GetPossibleEpochNanoseconds(isoDateTime), isoDateTime, disambiguation);

    /// <summary>GetStartOfDay.</summary>
    public Int128 GetStartOfDay(in IsoDate date)
    {
        var isoDateTime = new IsoDateTime(date, IsoTime.Midnight);
        List<Int128> possible = GetPossibleEpochNanoseconds(isoDateTime);
        if (possible.Count > 0) return possible[0];
        Int128 utcns = IsoCalendar.GetUTCEpochNanoseconds(isoDateTime);
        Int128 dayBefore = utcns - Units.NsPerDay;
        if (!IsoCalendar.IsValidEpochNanoseconds(dayBefore)) throw TemporalError.Range("Instant out of range.");
        return GetNamedTransition(dayBefore, TransitionDirection.Next) ??
               throw TemporalError.Range("No start of day.");
    }

    // ---- Transitions -----------------------------------------------------------------------------

    static readonly long s_scanStart = (new DateTime(1800, 1, 1, 0, 0, 0, DateTimeKind.Utc).Ticks - s_unixEpochTicks) / kTicksPerSecond;

    /// <summary>
    /// GetNamedTimeZoneNextTransition / GetNamedTimeZonePreviousTransition: the next (or
    /// previous) instant at which the UTC offset changes, or null. Offset time zones have none.
    /// </summary>
    public Int128? GetNamedTransition(Int128 epochNs, TransitionDirection direction)
    {
        if (_info is null) return null;
        long now = (DateTime.UtcNow.Ticks - s_unixEpochTicks) / kTicksPerSecond;
        long horizon = Math.Max(now, 253402300799L) + 450L * 365 * 86400;
        if (direction == TransitionDirection.Next)
        {
            long t = (long)Rounding.Int128Floor(epochNs, Units.NsPerSecond);
            if (t < s_scanStart) t = s_scanStart - 1;
            long offset = OffsetSecondsAt(t);
            long step = 86400;
            for (long cursor = t; cursor < horizon; cursor += step)
            {
                long nextCursor = cursor + step;
                if (OffsetSecondsAt(nextCursor) != offset)
                {
                    long lo = cursor, hi = nextCursor;
                    while (hi - lo > 1)
                    {
                        long mid = lo + (hi - lo) / 2;
                        if (OffsetSecondsAt(mid) != offset) hi = mid; else lo = mid;
                    }
                    Int128 result = (Int128)hi * Units.NsPerSecond;
                    if (result <= epochNs) { offset = OffsetSecondsAt(hi); continue; }
                    return IsoCalendar.IsValidEpochNanoseconds(result) ? result : null;
                }
            }
            return null;
        }
        else
        {
            Int128 limit = epochNs - 1;
            long t = (long)Rounding.Int128Floor(limit, Units.NsPerSecond);
            if (t > horizon) t = horizon;
            long offset = OffsetSecondsAt(t);
            long step = 86400;
            for (long cursor = t; cursor > s_scanStart; cursor -= step)
            {
                long prevCursor = cursor - step;
                if (OffsetSecondsAt(prevCursor) != offset)
                {
                    long lo = prevCursor, hi = cursor;
                    while (hi - lo > 1)
                    {
                        long mid = lo + (hi - lo) / 2;
                        if (OffsetSecondsAt(mid) != offset) lo = mid; else hi = mid;
                    }
                    Int128 result = (Int128)hi * Units.NsPerSecond;
                    if (result > limit) { offset = OffsetSecondsAt(lo); continue; }
                    return IsoCalendar.IsValidEpochNanoseconds(result) ? result : null;
                }
            }
            return null;
        }
    }
}
