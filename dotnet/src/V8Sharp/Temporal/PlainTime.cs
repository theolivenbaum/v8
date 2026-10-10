// temporal_rs::PlainTime: the value of a Temporal.PlainTime and its operations.
namespace V8Sharp.Temporal;

public sealed class PlainTime(IsoTime time)
{
    public IsoTime Time { get; } = time;

    public int Hour => Time.Hour;
    public int Minute => Time.Minute;
    public int Second => Time.Second;
    public int Millisecond => Time.Millisecond;
    public int Microsecond => Time.Microsecond;
    public int Nanosecond => Time.Nanosecond;

    /// <summary>PlainTime::try_new (the binding has checked IsValidTime).</summary>
    public static PlainTime TryNew(long h, long m, long s, long ms, long us, long ns) =>
        new(IsoCalendar.RegulateTime(h, m, s, ms, us, ns, Overflow.Reject));

    /// <summary>PlainTime::from_partial: missing fields are 0.</summary>
    public static PlainTime FromPartial(in PartialTime partial, Overflow overflow) =>
        new(partial.ToTime(IsoTime.Midnight, overflow));

    /// <summary>ParseTemporalTimeString and the rest of ToTemporalTime's string case.</summary>
    public static PlainTime FromString(ReadOnlySpan<char> s)
    {
        ParsedIso result = IsoParser.Parse(s, IsoGoal.Time);
        if (result.HasZ) throw TemporalError.Range("UTC designator not allowed in a time string.");
        return new PlainTime(result.Time);
    }

    public PlainTime With(in PartialTime partial, Overflow overflow) => new(partial.ToTime(Time, overflow));

    public static int Compare(PlainTime a, PlainTime b) => a.Time.CompareTo(b.Time);

    public bool Equals(PlainTime other) => Time == other.Time;

    /// <summary>AddDurationToTime.</summary>
    public PlainTime Add(Duration duration)
    {
        InternalDuration internalDuration = duration.ToInternal();
        (_, IsoTime result) = IsoCalendar.AddTime(Time, internalDuration.Time);
        return new PlainTime(result);
    }

    public PlainTime Subtract(Duration duration) => Add(duration.Negated());

    /// <summary>DifferenceTemporalPlainTime after ToTemporalTime.</summary>
    public Duration Difference(PlainTime other, in DifferenceSettings settings, bool isSince)
    {
        var (largestUnit, smallestUnit, mode, increment) = Units.ResolveDifferenceSettings(settings, isSince, UnitGroup.Time,
            default, default, false, Unit.Nanosecond, Unit.Hour);
        Int128 timeDuration = IsoCalendar.DifferenceTime(Time, other.Time);
        timeDuration = TimeDurations.Round(timeDuration, increment, smallestUnit, mode);
        Duration result = Duration.FromInternal(new InternalDuration(default, timeDuration), largestUnit);
        return isSince ? result.Negated() : result;
    }

    public Duration Until(PlainTime other, in DifferenceSettings settings) => Difference(other, settings, false);

    public Duration Since(PlainTime other, in DifferenceSettings settings) => Difference(other, settings, true);

    /// <summary>Temporal.PlainTime.prototype.round after option processing.</summary>
    public PlainTime Round(in RoundingOptions options)
    {
        Unit smallestUnit = options.SmallestUnit!.Value;
        long increment = options.Increment ?? 1;
        long maximum = Units.MaximumRoundingIncrement(smallestUnit) ?? 1;
        Units.ValidateRoundingIncrement(increment, maximum, false);
        (_, IsoTime result) = IsoCalendar.RoundTime(Time, increment, smallestUnit, options.RoundingMode ?? RoundingMode.HalfExpand);
        return new PlainTime(result);
    }

    /// <summary>Temporal.PlainTime.prototype.toString after option processing.</summary>
    public string ToIxdtfString(in ToStringRoundingOptions options)
    {
        if (options.SmallestUnit == Unit.Hour) throw TemporalError.Range("smallestUnit cannot be hour.");
        SecondsStringPrecision precision = Units.ToSecondsStringPrecision(options.SmallestUnit, options.Precision);
        (_, IsoTime rounded) = IsoCalendar.RoundTime(Time, precision.Increment, precision.Unit,
            options.RoundingMode ?? RoundingMode.Trunc);
        var sb = new System.Text.StringBuilder();
        Formatting.AppendTime(sb, rounded, precision.Precision);
        return sb.ToString();
    }
}
