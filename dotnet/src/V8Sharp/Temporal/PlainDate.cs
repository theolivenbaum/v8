// temporal_rs::PlainDate: the value of a Temporal.PlainDate and its operations,
// and the calendar-derived getters shared with the other date types.
namespace V8Sharp.Temporal;

public sealed class PlainDate(IsoDate isoDate, Calendar calendar)
{
    public IsoDate IsoDate { get; } = isoDate;
    public Calendar Calendar { get; } = calendar;

    /// <summary>CreateTemporalDate: checks ISODateWithinLimits.</summary>
    public static PlainDate Create(in IsoDate date, Calendar calendar)
    {
        if (!IsoCalendar.ISODateWithinLimits(date)) throw TemporalError.Range("Date out of range.");
        return new PlainDate(date, calendar);
    }

    /// <summary>PlainDate::try_new.</summary>
    public static PlainDate TryNew(long year, long month, long day, Calendar calendar) =>
        Create(IsoCalendar.RegulateISODate(year, month, day, Overflow.Reject), calendar);

    /// <summary>PlainDate::from_partial (CalendarDateFromFields).</summary>
    public static PlainDate FromPartial(in PartialDate partial, Overflow? overflow) =>
        Create(CalendarFields.DateFromFields(partial, overflow ?? Overflow.Constrain), partial.Calendar);

    /// <summary>ParsedDate::from_utf16 + PlainDate::from_parsed: ToTemporalDate's string case.</summary>
    public static PlainDate FromString(ReadOnlySpan<char> s)
    {
        ParsedIso result = IsoParser.Parse(s, IsoGoal.DateTime);
        Calendar calendar = ResolveCalendar(result.Calendar);
        return Create(new IsoDate(result.Year!.Value, result.Month, result.Day), calendar);
    }

    /// <summary>The calendar of a parsed string: iso8601 if absent, else CanonicalizeCalendar.</summary>
    public static Calendar ResolveCalendar(string? calendar)
    {
        if (calendar is null) return Calendar.Iso;
        return Calendar.TryCanonicalize(calendar) ?? throw TemporalError.Range("Unknown calendar " + calendar + ".");
    }

    // ---- Getters (calendar-dependent; ISO 8601 only) ------------------------------------------

    public long Year => IsoDate.Year;
    public int Month => IsoDate.Month;
    public string MonthCode => IsoCalendar.MonthCode(IsoDate.Month);
    public int Day => IsoDate.Day;

    public static string? Era(in IsoDate _, Calendar __) => null;
    public static int? EraYear(in IsoDate _, Calendar __) => null;
    public static int DayOfWeek(in IsoDate d) => IsoCalendar.DayOfWeek(d);
    public static int DayOfYear(in IsoDate d) => IsoCalendar.DayOfYear(d);
    public static int? WeekOfYear(in IsoDate d) => IsoCalendar.WeekOfYear(d).week;
    public static long? YearOfWeek(in IsoDate d) => IsoCalendar.WeekOfYear(d).year;
    public static int DaysInWeek(in IsoDate _) => 7;
    public static int DaysInMonth(in IsoDate d) => IsoCalendar.DaysInMonth(d.Year, d.Month);
    public static int DaysInYear(in IsoDate d) => IsoCalendar.DaysInYear(d.Year);
    public static int MonthsInYear(in IsoDate _) => 12;
    public static bool InLeapYear(in IsoDate d) => IsoCalendar.IsLeapYear(d.Year);

    // ---- Operations ---------------------------------------------------------------------------

    public static int Compare(PlainDate a, PlainDate b) => IsoCalendar.CompareISODate(a.IsoDate, b.IsoDate);

    public bool Equals(PlainDate other) => IsoDate == other.IsoDate && Calendar == other.Calendar;

    /// <summary>Temporal.PlainDate.prototype.with after PrepareCalendarFields.</summary>
    public PlainDate With(in PartialDate partial, Overflow overflow)
    {
        PartialDate fields = CalendarFields.ToFields(Calendar, IsoDate, CalendarFieldsType.Date);
        PartialDate merged = CalendarFields.Merge(fields, partial);
        return Create(CalendarFields.DateFromFields(merged, overflow), Calendar);
    }

    public PlainDate WithCalendar(Calendar calendar) => Create(IsoDate, calendar);

    /// <summary>AddDurationToDate.</summary>
    public PlainDate Add(Duration duration, Overflow? overflow)
    {
        DateDuration dateDuration = duration.ToDateDurationWithoutTime();
        return Create(IsoCalendar.DateAdd(IsoDate, dateDuration, overflow ?? Overflow.Constrain), Calendar);
    }

    public PlainDate Subtract(Duration duration, Overflow? overflow) => Add(duration.Negated(), overflow);

    /// <summary>DifferenceTemporalPlainDate after ToTemporalDate and the calendar check.</summary>
    public Duration Difference(PlainDate other, in DifferenceSettings settings, bool isSince)
    {
        var (largestUnit, smallestUnit, mode, increment) = Units.ResolveDifferenceSettings(settings, isSince, UnitGroup.Date,
            default, default, false, Unit.Day, Unit.Day);
        if (IsoCalendar.CompareISODate(IsoDate, other.IsoDate) == 0) return Duration.Zero;
        DateDuration dateDifference = IsoCalendar.DateUntil(IsoDate, other.IsoDate, largestUnit);
        var duration = new InternalDuration(dateDifference, 0);
        bool roundingGranularityIsNoop = smallestUnit == Unit.Day && increment == 1;
        if (!roundingGranularityIsNoop)
        {
            var isoDateTime = new IsoDateTime(IsoDate, IsoTime.Midnight);
            Int128 originEpochNs = IsoCalendar.GetUTCEpochNanoseconds(isoDateTime);
            var isoDateTimeOther = new IsoDateTime(other.IsoDate, IsoTime.Midnight);
            Int128 destEpochNs = IsoCalendar.GetUTCEpochNanoseconds(isoDateTimeOther);
            duration = RelativeRounding.RoundRelativeDuration(duration, originEpochNs, destEpochNs, isoDateTime, null, Calendar,
                largestUnit, increment, smallestUnit, mode);
        }
        Duration result = Duration.FromInternal(duration, Unit.Day);
        return isSince ? result.Negated() : result;
    }

    public Duration Until(PlainDate other, in DifferenceSettings settings) => Difference(other, settings, false);

    public Duration Since(PlainDate other, in DifferenceSettings settings) => Difference(other, settings, true);

    /// <summary>Temporal.PlainDate.prototype.toPlainDateTime.</summary>
    public PlainDateTime ToPlainDateTime(PlainTime? time) =>
        PlainDateTime.Create(new IsoDateTime(IsoDate, time?.Time ?? IsoTime.Midnight), Calendar);

    /// <summary>Temporal.PlainDate.prototype.toPlainYearMonth.</summary>
    public PlainYearMonth ToPlainYearMonth()
    {
        PartialDate fields = CalendarFields.ToFields(Calendar, IsoDate, CalendarFieldsType.Date);
        return new PlainYearMonth(CalendarFields.YearMonthFromFields(fields, Overflow.Constrain), Calendar);
    }

    /// <summary>Temporal.PlainDate.prototype.toPlainMonthDay.</summary>
    public PlainMonthDay ToPlainMonthDay()
    {
        PartialDate fields = CalendarFields.ToFields(Calendar, IsoDate, CalendarFieldsType.Date);
        return new PlainMonthDay(CalendarFields.MonthDayFromFields(fields, Overflow.Constrain), Calendar);
    }

    /// <summary>Temporal.PlainDate.prototype.toZonedDateTime after argument processing.</summary>
    public ZonedDateTime ToZonedDateTime(TimeZone timeZone, PlainTime? time)
    {
        Int128 epochNs;
        if (time is null)
        {
            epochNs = timeZone.GetStartOfDay(IsoDate);
        }
        else
        {
            var isoDateTime = new IsoDateTime(IsoDate, time.Time);
            if (!IsoCalendar.ISODateTimeWithinLimits(isoDateTime)) throw TemporalError.Range("Date-time out of range.");
            epochNs = timeZone.GetEpochNanosecondsFor(isoDateTime, Disambiguation.Compatible);
        }
        return ZonedDateTime.Create(epochNs, timeZone, Calendar);
    }

    public string ToIxdtfString(DisplayCalendar showCalendar) => Formatting.DateToString(IsoDate, Calendar, showCalendar);
}
