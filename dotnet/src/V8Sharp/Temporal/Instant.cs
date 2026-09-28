// temporal_rs::Instant and temporal_rs::ZonedDateTime.
namespace V8Sharp.Temporal;

public sealed class Instant(Int128 epochNanoseconds)
{
    public Int128 EpochNanoseconds { get; } = epochNanoseconds;

    /// <summary>Instant::try_new: IsValidEpochNanoseconds.</summary>
    public static Instant TryNew(Int128 ns)
    {
        if (!IsoCalendar.IsValidEpochNanoseconds(ns)) throw TemporalError.Range("Instant out of range.");
        return new Instant(ns);
    }

    public static Instant FromEpochMilliseconds(long ms) => TryNew((Int128)ms * 1_000_000);

    /// <summary>ParseTemporalInstantString.</summary>
    public static Instant FromString(ReadOnlySpan<char> s)
    {
        ParsedIso result = IsoParser.Parse(s, IsoGoal.Instant);
        long offsetNs = result.HasZ ? 0 : IsoParser.ParseDateTimeUTCOffset(result.OffsetString);
        var date = new IsoDate(result.Year!.Value, result.Month, result.Day);
        IsoDateTime balanced = IsoCalendar.BalanceISODateTime(date, result.Time.TotalNanoseconds - (Int128)offsetNs);
        IsoCalendar.CheckISODaysRange(balanced.Date);
        return TryNew(IsoCalendar.GetUTCEpochNanoseconds(balanced));
    }

    public long EpochMilliseconds => (long)Rounding.Int128Floor(EpochNanoseconds, 1_000_000);

    public int Compare(Instant other) => EpochNanoseconds.CompareTo(other.EpochNanoseconds);

    /// <summary>AddDurationToInstant.</summary>
    public Instant Add(Duration duration)
    {
        Unit largestUnit = duration.DefaultLargestUnit();
        if (Units.IsDateUnit(largestUnit))
            throw TemporalError.Range("Cannot add years, months, weeks or days to an Instant.");
        InternalDuration internalDuration = duration.ToInternal();
        return new Instant(RelativeRounding.AddInstant(EpochNanoseconds, internalDuration.Time));
    }

    public Instant Subtract(Duration duration) => Add(duration.Negated());

    /// <summary>DifferenceTemporalInstant after ToTemporalInstant.</summary>
    public Duration Difference(Instant other, in DifferenceSettings settings, bool isSince)
    {
        var (largestUnit, smallestUnit, mode, increment) = Units.ResolveDifferenceSettings(settings, isSince, UnitGroup.Time,
            default, default, false, Unit.Nanosecond, Unit.Second);
        Int128 timeDuration = TimeDurations.Round(other.EpochNanoseconds - EpochNanoseconds, increment, smallestUnit, mode);
        Duration result = Duration.FromInternal(new InternalDuration(default, timeDuration), largestUnit);
        return isSince ? result.Negated() : result;
    }

    public Duration Until(Instant other, in DifferenceSettings settings) => Difference(other, settings, false);

    public Duration Since(Instant other, in DifferenceSettings settings) => Difference(other, settings, true);

    /// <summary>RoundTemporalInstant.</summary>
    public static Int128 RoundTemporalInstant(Int128 ns, long increment, Unit unit, RoundingMode mode) =>
        Rounding.RoundToIncrementAsIfPositive(ns, (Int128)Units.Length(unit) * increment, mode);

    /// <summary>Temporal.Instant.prototype.round after option processing.</summary>
    public Instant Round(in RoundingOptions options)
    {
        Unit smallestUnit = options.SmallestUnit!.Value;
        long maximum = smallestUnit switch
        {
            Unit.Hour => 24,
            Unit.Minute => 1440,
            Unit.Second => 86400,
            Unit.Millisecond => 86_400_000,
            Unit.Microsecond => 86_400_000_000,
            _ => 86_400_000_000_000,
        };
        long increment = options.Increment ?? 1;
        Units.ValidateRoundingIncrement(increment, maximum, true);
        return new Instant(RoundTemporalInstant(EpochNanoseconds, increment, smallestUnit,
            options.RoundingMode ?? RoundingMode.HalfExpand));
    }

    /// <summary>Temporal.Instant.prototype.toString after option processing.</summary>
    public string ToIxdtfString(TimeZone? timeZone, in ToStringRoundingOptions options)
    {
        SecondsStringPrecision precision = Units.ToSecondsStringPrecision(options.SmallestUnit, options.Precision);
        Int128 rounded = RoundTemporalInstant(EpochNanoseconds, precision.Increment, precision.Unit,
            options.RoundingMode ?? RoundingMode.Trunc);
        return TemporalInstantToString(rounded, timeZone, precision.Precision);
    }

    /// <summary>TemporalInstantToString.</summary>
    public static string TemporalInstantToString(Int128 ns, TimeZone? timeZone, Precision precision)
    {
        TimeZone outputTimeZone = timeZone ?? TimeZone.Utc;
        long offsetNs = outputTimeZone.GetOffsetNanosecondsFor(ns);
        IsoDateTime dateTime = IsoCalendar.FromEpochNanoseconds(ns + offsetNs);
        string dateTimeString = Formatting.DateTimeToString(dateTime, Calendar.Iso, precision, DisplayCalendar.Never);
        return timeZone is null ? dateTimeString + "Z" : dateTimeString + Formatting.FormatDateTimeUTCOffsetRounded(offsetNs);
    }

    public ZonedDateTime ToZonedDateTimeISO(TimeZone timeZone) => ZonedDateTime.Create(EpochNanoseconds, timeZone, Calendar.Iso);
}

/// <summary>temporal_rs::PartialZonedDateTime.</summary>
public struct PartialZonedDateTime
{
    public PartialDate Date;
    public PartialTime Time;
    public string? Offset;
    public TimeZone? TimeZone;
}

public sealed class ZonedDateTime(Int128 epochNanoseconds, TimeZone timeZone, Calendar calendar)
{
    public Int128 EpochNanoseconds { get; } = epochNanoseconds;
    public TimeZone TimeZone { get; } = timeZone;
    public Calendar Calendar { get; } = calendar;

    IsoDateTime? _isoDateTime;

    /// <summary>GetISODateTimeFor(timeZone, epochNanoseconds), cached.</summary>
    public IsoDateTime IsoDateTime => _isoDateTime ??= TimeZone.GetISODateTimeFor(EpochNanoseconds);

    public IsoDate IsoDate => IsoDateTime.Date;
    public IsoTime Time => IsoDateTime.Time;

    /// <summary>CreateTemporalZonedDateTime (with the validity check of the constructor).</summary>
    public static ZonedDateTime Create(Int128 ns, TimeZone timeZone, Calendar calendar)
    {
        if (!IsoCalendar.IsValidEpochNanoseconds(ns)) throw TemporalError.Range("Instant out of range.");
        return new ZonedDateTime(ns, timeZone, calendar);
    }

    public long OffsetNanoseconds => TimeZone.GetOffsetNanosecondsFor(EpochNanoseconds);

    public string Offset => Formatting.FormatUTCOffsetNanoseconds(OffsetNanoseconds);

    public long EpochMilliseconds => (long)Rounding.Int128Floor(EpochNanoseconds, 1_000_000);

    /// <summary>InterpretISODateTimeOffset.</summary>
    public static Int128 InterpretISODateTimeOffset(in IsoDate isoDate, IsoTime? time, OffsetBehaviour offsetBehaviour,
        long offsetNanoseconds, TimeZone timeZone, Disambiguation disambiguation, OffsetDisambiguation offsetOption,
        bool matchMinutes)
    {
        if (time is null) return timeZone.GetStartOfDay(isoDate);
        var isoDateTime = new IsoDateTime(isoDate, time.Value);
        if (offsetBehaviour == OffsetBehaviour.Wall ||
            (offsetBehaviour == OffsetBehaviour.Option && offsetOption == OffsetDisambiguation.Ignore))
        {
            return timeZone.GetEpochNanosecondsFor(isoDateTime, disambiguation);
        }
        if (offsetBehaviour == OffsetBehaviour.Exact ||
            (offsetBehaviour == OffsetBehaviour.Option && offsetOption == OffsetDisambiguation.Use))
        {
            IsoDateTime balanced = IsoCalendar.BalanceISODateTime(isoDate, time.Value.TotalNanoseconds - (Int128)offsetNanoseconds);
            IsoCalendar.CheckISODaysRange(balanced.Date);
            Int128 epochNs = IsoCalendar.GetUTCEpochNanoseconds(balanced);
            if (!IsoCalendar.IsValidEpochNanoseconds(epochNs)) throw TemporalError.Range("Instant out of range.");
            return epochNs;
        }
        IsoCalendar.CheckISODaysRange(isoDate);
        Int128 utcEpochNs = IsoCalendar.GetUTCEpochNanoseconds(isoDateTime);
        List<Int128> possible = timeZone.GetPossibleEpochNanoseconds(isoDateTime);
        foreach (Int128 candidate in possible)
        {
            Int128 candidateOffset = utcEpochNs - candidate;
            if (candidateOffset == offsetNanoseconds) return candidate;
            if (matchMinutes)
            {
                Int128 roundedCandidateNs = Rounding.RoundToIncrement(candidateOffset, Units.NsPerMinute, RoundingMode.HalfExpand);
                if (roundedCandidateNs == offsetNanoseconds) return candidate;
            }
        }
        if (offsetOption == OffsetDisambiguation.Reject) throw TemporalError.Range("Offset does not match the time zone.");
        return timeZone.Disambiguate(possible, isoDateTime, disambiguation);
    }

    /// <summary>ToTemporalZonedDateTime's object case after PrepareCalendarFields and the options.</summary>
    public static ZonedDateTime FromPartial(in PartialZonedDateTime partial, Overflow overflow, Disambiguation disambiguation,
        OffsetDisambiguation offsetOption)
    {
        TimeZone timeZone = partial.TimeZone!;
        IsoDate date = CalendarFields.DateFromFields(partial.Date, overflow);
        IsoTime time = partial.Time.ToTime(IsoTime.Midnight, overflow);
        OffsetBehaviour offsetBehaviour = partial.Offset is null ? OffsetBehaviour.Wall : OffsetBehaviour.Option;
        long offsetNs = partial.Offset is null ? 0 : IsoParser.ParseDateTimeUTCOffset(partial.Offset);
        Int128 epochNs = InterpretISODateTimeOffset(date, time, offsetBehaviour, offsetNs, timeZone, disambiguation, offsetOption,
            false);
        return Create(epochNs, timeZone, partial.Date.Calendar);
    }

    /// <summary>A parsed TemporalDateTimeString[+Zoned] (ParsedZonedDateTime).</summary>
    public static ParsedIso Parse(ReadOnlySpan<char> s)
    {
        ParsedIso result = IsoParser.Parse(s, IsoGoal.ZonedDateTime);
        TimeZone.FromIdentifier(result.TimeZoneAnnotation);
        PlainDate.ResolveCalendar(result.Calendar);
        return result;
    }

    /// <summary>ToTemporalZonedDateTime's string case after the options.</summary>
    public static ZonedDateTime FromParsed(ParsedIso result, Disambiguation disambiguation, OffsetDisambiguation offsetOption)
    {
        TimeZone timeZone = TimeZone.FromIdentifier(result.TimeZoneAnnotation);
        Calendar calendar = PlainDate.ResolveCalendar(result.Calendar);
        OffsetBehaviour offsetBehaviour = result.HasZ ? OffsetBehaviour.Exact :
            result.OffsetString is null ? OffsetBehaviour.Wall : OffsetBehaviour.Option;
        bool matchMinutes = true;
        long offsetNs = 0;
        if (offsetBehaviour == OffsetBehaviour.Option)
        {
            offsetNs = IsoParser.ParseDateTimeUTCOffset(result.OffsetString);
            if (IsoParser.OffsetHasSubMinutePrecision(result.OffsetString)) matchMinutes = false;
        }
        var isoDate = new IsoDate(result.Year!.Value, result.Month, result.Day);
        IsoTime? time = result.HasTime ? result.Time : null;
        Int128 epochNs = InterpretISODateTimeOffset(isoDate, time, offsetBehaviour, offsetNs, timeZone, disambiguation,
            offsetOption, matchMinutes);
        return Create(epochNs, timeZone, calendar);
    }

    public int CompareInstant(ZonedDateTime other) => EpochNanoseconds.CompareTo(other.EpochNanoseconds);

    public bool Equals(ZonedDateTime other) =>
        EpochNanoseconds == other.EpochNanoseconds && TimeZone.Equals(TimeZone, other.TimeZone) && Calendar == other.Calendar;

    /// <summary>Temporal.ZonedDateTime.prototype.with after PrepareCalendarFields and the options.</summary>
    public ZonedDateTime With(in PartialZonedDateTime partial, Disambiguation disambiguation, OffsetDisambiguation offsetOption,
        Overflow overflow)
    {
        long offsetNanoseconds = OffsetNanoseconds;
        IsoDateTime isoDateTime = IsoDateTime;
        PartialDate fields = CalendarFields.ToFields(Calendar, isoDateTime.Date, CalendarFieldsType.Date);
        PartialDate mergedDate = CalendarFields.Merge(fields, partial.Date);
        string offset = partial.Offset ?? Formatting.FormatUTCOffsetNanoseconds(offsetNanoseconds);
        IsoDate date = CalendarFields.DateFromFields(mergedDate, overflow);
        IsoTime time = partial.Time.ToTime(isoDateTime.Time, overflow);
        long newOffsetNanoseconds = IsoParser.ParseDateTimeUTCOffset(offset);
        Int128 epochNs = InterpretISODateTimeOffset(date, time, OffsetBehaviour.Option, newOffsetNanoseconds, TimeZone,
            disambiguation, offsetOption, false);
        return Create(epochNs, TimeZone, Calendar);
    }

    public ZonedDateTime WithCalendar(Calendar calendar) => Create(EpochNanoseconds, TimeZone, calendar);

    public ZonedDateTime WithTimeZone(TimeZone timeZone) => Create(EpochNanoseconds, timeZone, Calendar);

    /// <summary>Temporal.ZonedDateTime.prototype.withPlainTime.</summary>
    public ZonedDateTime WithPlainTime(PlainTime? time)
    {
        Int128 epochNs = time is null
            ? TimeZone.GetStartOfDay(IsoDate)
            : TimeZone.GetEpochNanosecondsFor(new IsoDateTime(IsoDate, time.Time), Disambiguation.Compatible);
        return Create(epochNs, TimeZone, Calendar);
    }

    /// <summary>AddDurationToZonedDateTime.</summary>
    public ZonedDateTime Add(Duration duration, Overflow? overflow)
    {
        InternalDuration internalDuration = duration.ToInternal();
        Int128 epochNs = RelativeRounding.AddZonedDateTime(EpochNanoseconds, TimeZone, Calendar, internalDuration,
            overflow ?? Overflow.Constrain);
        return Create(epochNs, TimeZone, Calendar);
    }

    public ZonedDateTime Subtract(Duration duration, Overflow? overflow) => Add(duration.Negated(), overflow);

    /// <summary>DifferenceTemporalZonedDateTime after ToTemporalZonedDateTime and the calendar check.</summary>
    public Duration Difference(ZonedDateTime other, in DifferenceSettings settings, bool isSince)
    {
        var (largestUnit, smallestUnit, mode, increment) = Units.ResolveDifferenceSettings(settings, isSince, UnitGroup.DateTime,
            default, default, false, Unit.Nanosecond, Unit.Hour);
        Duration result;
        if (!Units.IsDateUnit(largestUnit))
        {
            Int128 timeDuration = TimeDurations.Round(other.EpochNanoseconds - EpochNanoseconds, increment, smallestUnit, mode);
            result = Duration.FromInternal(new InternalDuration(default, timeDuration), largestUnit);
            return isSince ? result.Negated() : result;
        }
        if (!TimeZone.Equals(TimeZone, other.TimeZone))
            throw TemporalError.Range("Cannot compute a difference in date units between different time zones.");
        if (EpochNanoseconds == other.EpochNanoseconds) return Duration.Zero;
        InternalDuration internalDuration = RelativeRounding.DifferenceZonedDateTimeWithRounding(EpochNanoseconds,
            other.EpochNanoseconds, TimeZone, Calendar, largestUnit, increment, smallestUnit, mode);
        result = Duration.FromInternal(internalDuration, Unit.Hour);
        return isSince ? result.Negated() : result;
    }

    public Duration Until(ZonedDateTime other, in DifferenceSettings settings) => Difference(other, settings, false);

    public Duration Since(ZonedDateTime other, in DifferenceSettings settings) => Difference(other, settings, true);

    /// <summary>Temporal.ZonedDateTime.prototype.round after option processing.</summary>
    public ZonedDateTime Round(in RoundingOptions options)
    {
        Unit smallestUnit = options.SmallestUnit!.Value;
        long increment = options.Increment ?? 1;
        RoundingMode mode = options.RoundingMode ?? RoundingMode.HalfExpand;
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
        Int128 thisNs = EpochNanoseconds;
        IsoDateTime isoDateTime = IsoDateTime;
        Int128 epochNanoseconds;
        if (smallestUnit == Unit.Day)
        {
            IsoDate dateStart = isoDateTime.Date;
            IsoDate dateEnd = IsoCalendar.AddDays(dateStart, 1);
            Int128 startNs = TimeZone.GetStartOfDay(dateStart);
            Int128 endNs = TimeZone.GetStartOfDay(dateEnd);
            Int128 dayLengthNs = endNs - startNs;
            Int128 dayProgressNs = thisNs - startNs;
            Int128 roundedDayNs = Rounding.RoundToIncrement(dayProgressNs, dayLengthNs, mode);
            epochNanoseconds = roundedDayNs + startNs;
        }
        else
        {
            IsoDateTime roundResult = IsoCalendar.RoundISODateTime(isoDateTime, increment, smallestUnit, mode);
            long offsetNanoseconds = TimeZone.GetOffsetNanosecondsFor(thisNs);
            epochNanoseconds = InterpretISODateTimeOffset(roundResult.Date, roundResult.Time, OffsetBehaviour.Option,
                offsetNanoseconds, TimeZone, Disambiguation.Compatible, OffsetDisambiguation.Prefer, false);
        }
        return Create(epochNanoseconds, TimeZone, Calendar);
    }

    /// <summary>Temporal.ZonedDateTime.prototype.hoursInDay.</summary>
    public double HoursInDay()
    {
        IsoDate today = IsoDate;
        IsoDate tomorrow = IsoCalendar.AddDays(today, 1);
        Int128 todayNs = TimeZone.GetStartOfDay(today);
        Int128 tomorrowNs = TimeZone.GetStartOfDay(tomorrow);
        return TimeDurations.Total(tomorrowNs - todayNs, Unit.Hour);
    }

    public ZonedDateTime StartOfDay() => Create(TimeZone.GetStartOfDay(IsoDate), TimeZone, Calendar);

    /// <summary>Temporal.ZonedDateTime.prototype.getTimeZoneTransition after option processing.</summary>
    public ZonedDateTime? GetTimeZoneTransition(TransitionDirection direction)
    {
        if (TimeZone.IsOffset) return null;
        Int128? transition = TimeZone.GetNamedTransition(EpochNanoseconds, direction);
        return transition is { } t ? Create(t, TimeZone, Calendar) : null;
    }

    public Instant ToInstant() => new(EpochNanoseconds);

    public PlainDate ToPlainDate() => new(IsoDate, Calendar);

    public PlainTime ToPlainTime() => new(Time);

    public PlainDateTime ToPlainDateTime() => new(IsoDateTime, Calendar);

    /// <summary>Temporal.ZonedDateTime.prototype.toString after option processing.</summary>
    public string ToIxdtfString(DisplayOffset showOffset, DisplayTimeZone showTimeZone, DisplayCalendar showCalendar,
        in ToStringRoundingOptions options)
    {
        SecondsStringPrecision precision = Units.ToSecondsStringPrecision(options.SmallestUnit, options.Precision);
        Int128 epochNs = Instant.RoundTemporalInstant(EpochNanoseconds, precision.Increment, precision.Unit,
            options.RoundingMode ?? RoundingMode.Trunc);
        long offsetNs = TimeZone.GetOffsetNanosecondsFor(epochNs);
        IsoDateTime isoDateTime = IsoCalendar.FromEpochNanoseconds(epochNs + offsetNs);
        var sb = new System.Text.StringBuilder(Formatting.DateTimeToString(isoDateTime, Calendar.Iso, precision.Precision,
            DisplayCalendar.Never));
        if (showOffset != DisplayOffset.Never) sb.Append(Formatting.FormatDateTimeUTCOffsetRounded(offsetNs));
        if (showTimeZone != DisplayTimeZone.Never)
        {
            sb.Append('[');
            if (showTimeZone == DisplayTimeZone.Critical) sb.Append('!');
            sb.Append(TimeZone.Identifier);
            sb.Append(']');
        }
        Formatting.AppendCalendarAnnotation(sb, Calendar.Identifier, showCalendar);
        return sb.ToString();
    }
}

/// <summary>The offsetBehaviour of InterpretISODateTimeOffset.</summary>
public enum OffsetBehaviour : byte { Option, Exact, Wall }
