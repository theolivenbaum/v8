// temporal_rs::PlainDateTime: the value of a Temporal.PlainDateTime and its operations.
namespace V8Sharp.Temporal;

/// <summary>temporal_rs::PartialDateTime.</summary>
public struct PartialDateTime
{
    public PartialDate Date;
    public PartialTime Time;
}

public sealed class PlainDateTime(IsoDateTime isoDateTime, Calendar calendar)
{
    public IsoDateTime IsoDateTime { get; } = isoDateTime;
    public Calendar Calendar { get; } = calendar;
    public IsoDate IsoDate => IsoDateTime.Date;
    public IsoTime Time => IsoDateTime.Time;

    /// <summary>CreateTemporalDateTime: checks ISODateTimeWithinLimits.</summary>
    public static PlainDateTime Create(in IsoDateTime dt, Calendar calendar)
    {
        if (!IsoCalendar.ISODateTimeWithinLimits(dt)) throw TemporalError.Range("Date-time out of range.");
        return new PlainDateTime(dt, calendar);
    }

    public static PlainDateTime TryNew(long y, long mo, long d, long h, long mi, long s, long ms, long us, long ns,
        Calendar calendar) =>
        Create(new IsoDateTime(IsoCalendar.RegulateISODate(y, mo, d, Overflow.Reject),
            IsoCalendar.RegulateTime(h, mi, s, ms, us, ns, Overflow.Reject)), calendar);

    /// <summary>PlainDateTime::from_partial: InterpretTemporalDateTimeFields.</summary>
    public static PlainDateTime FromPartial(in PartialDateTime partial, Overflow? overflow)
    {
        Overflow o = overflow ?? Overflow.Constrain;
        IsoDate date = CalendarFields.DateFromFields(partial.Date, o);
        IsoTime time = partial.Time.ToTime(IsoTime.Midnight, o);
        return Create(new IsoDateTime(date, time), partial.Date.Calendar);
    }

    /// <summary>ToTemporalDateTime's string case.</summary>
    public static PlainDateTime FromString(ReadOnlySpan<char> s)
    {
        ParsedIso result = IsoParser.Parse(s, IsoGoal.DateTime);
        Calendar calendar = PlainDate.ResolveCalendar(result.Calendar);
        var date = new IsoDate(result.Year!.Value, result.Month, result.Day);
        return Create(new IsoDateTime(date, result.HasTime ? result.Time : IsoTime.Midnight), calendar);
    }

    public static int Compare(PlainDateTime a, PlainDateTime b) => a.IsoDateTime.CompareTo(b.IsoDateTime);

    public bool Equals(PlainDateTime other) => IsoDateTime == other.IsoDateTime && Calendar == other.Calendar;

    /// <summary>Temporal.PlainDateTime.prototype.with after PrepareCalendarFields.</summary>
    public PlainDateTime With(in PartialDateTime partial, Overflow overflow)
    {
        PartialDate fields = CalendarFields.ToFields(Calendar, IsoDate, CalendarFieldsType.Date);
        PartialDate merged = CalendarFields.Merge(fields, partial.Date);
        IsoDate date = CalendarFields.DateFromFields(merged, overflow);
        IsoTime time = partial.Time.ToTime(Time, overflow);
        return Create(new IsoDateTime(date, time), Calendar);
    }

    public PlainDateTime WithCalendar(Calendar calendar) => Create(IsoDateTime, calendar);

    public PlainDateTime WithTime(PlainTime? time) => Create(new IsoDateTime(IsoDate, time?.Time ?? IsoTime.Midnight), Calendar);

    /// <summary>AddDurationToDateTime.</summary>
    public PlainDateTime Add(Duration duration, Overflow? overflow)
    {
        InternalDuration internalDuration = duration.ToInternalWith24HourDays();
        (long days, IsoTime time) = IsoCalendar.AddTime(Time, internalDuration.Time);
        DateDuration dateDuration = internalDuration.Date.Adjust(days);
        IsoDate addedDate = IsoCalendar.DateAdd(IsoDate, dateDuration, overflow ?? Overflow.Constrain);
        return Create(new IsoDateTime(addedDate, time), Calendar);
    }

    public PlainDateTime Subtract(Duration duration, Overflow? overflow) => Add(duration.Negated(), overflow);

    /// <summary>DifferenceTemporalPlainDateTime after ToTemporalDateTime and the calendar check.</summary>
    public Duration Difference(PlainDateTime other, in DifferenceSettings settings, bool isSince)
    {
        var (largestUnit, smallestUnit, mode, increment) = Units.ResolveDifferenceSettings(settings, isSince, UnitGroup.DateTime,
            default, default, false, Unit.Nanosecond, Unit.Day);
        if (IsoDateTime.CompareTo(other.IsoDateTime) == 0) return Duration.Zero;
        InternalDuration internalDuration = RelativeRounding.DifferencePlainDateTimeWithRounding(IsoDateTime, other.IsoDateTime,
            Calendar, largestUnit, increment, smallestUnit, mode);
        Duration result = Duration.FromInternal(internalDuration, largestUnit);
        return isSince ? result.Negated() : result;
    }

    public Duration Until(PlainDateTime other, in DifferenceSettings settings) => Difference(other, settings, false);

    public Duration Since(PlainDateTime other, in DifferenceSettings settings) => Difference(other, settings, true);

    /// <summary>Temporal.PlainDateTime.prototype.round after option processing.</summary>
    public PlainDateTime Round(in RoundingOptions options)
    {
        Unit smallestUnit = options.SmallestUnit!.Value;
        long increment = options.Increment ?? 1;
        long maximum;
        bool inclusive;
        if (smallestUnit == Unit.Day)
        {
            maximum = 1;
            inclusive = true;
        }
        else
        {
            maximum = Units.MaximumRoundingIncrement(smallestUnit)!.Value;
            inclusive = false;
        }
        Units.ValidateRoundingIncrement(increment, maximum, inclusive);
        if (smallestUnit == Unit.Nanosecond && increment == 1) return this;
        IsoDateTime result = IsoCalendar.RoundISODateTime(IsoDateTime, increment, smallestUnit,
            options.RoundingMode ?? RoundingMode.HalfExpand);
        return Create(result, Calendar);
    }

    /// <summary>Temporal.PlainDateTime.prototype.toString after option processing.</summary>
    public string ToIxdtfString(in ToStringRoundingOptions options, DisplayCalendar showCalendar)
    {
        if (options.SmallestUnit == Unit.Hour) throw TemporalError.Range("smallestUnit cannot be hour.");
        SecondsStringPrecision precision = Units.ToSecondsStringPrecision(options.SmallestUnit, options.Precision);
        IsoDateTime result = IsoCalendar.RoundISODateTime(IsoDateTime, precision.Increment, precision.Unit,
            options.RoundingMode ?? RoundingMode.Trunc);
        if (!IsoCalendar.ISODateTimeWithinLimits(result)) throw TemporalError.Range("Date-time out of range.");
        return Formatting.DateTimeToString(result, Calendar, precision.Precision, showCalendar);
    }

    public PlainDate ToPlainDate() => new(IsoDate, Calendar);

    public PlainTime ToPlainTime() => new(Time);

    /// <summary>Temporal.PlainDateTime.prototype.toZonedDateTime after option processing.</summary>
    public ZonedDateTime ToZonedDateTime(TimeZone timeZone, Disambiguation disambiguation)
    {
        Int128 epochNs = timeZone.GetEpochNanosecondsFor(IsoDateTime, disambiguation);
        return ZonedDateTime.Create(epochNs, timeZone, Calendar);
    }
}
