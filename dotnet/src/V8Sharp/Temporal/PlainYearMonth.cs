// temporal_rs::PlainYearMonth and temporal_rs::PlainMonthDay.
namespace V8Sharp.Temporal;

public sealed class PlainYearMonth(IsoDate isoDate, Calendar calendar)
{
    public IsoDate IsoDate { get; } = isoDate;
    public Calendar Calendar { get; } = calendar;

    /// <summary>CreateTemporalYearMonth: checks ISOYearMonthWithinLimits.</summary>
    public static PlainYearMonth Create(in IsoDate date, Calendar calendar)
    {
        if (!IsoCalendar.ISOYearMonthWithinLimits(date)) throw TemporalError.Range("Year-month out of range.");
        return new PlainYearMonth(date, calendar);
    }

    /// <summary>PlainYearMonth::try_new_with_overflow (reject): the constructor.</summary>
    public static PlainYearMonth TryNew(long year, long month, long referenceDay, Calendar calendar) =>
        Create(IsoCalendar.RegulateISODate(year, month, referenceDay, Overflow.Reject), calendar);

    /// <summary>PlainYearMonth::from_partial: CalendarYearMonthFromFields.</summary>
    public static PlainYearMonth FromPartial(in PartialDate partial, Overflow overflow) =>
        Create(CalendarFields.YearMonthFromFields(partial, overflow), partial.Calendar);

    /// <summary>ToTemporalYearMonth's string case.</summary>
    public static PlainYearMonth FromString(ReadOnlySpan<char> s)
    {
        ParsedIso result = IsoParser.Parse(s, IsoGoal.YearMonth);
        Calendar calendar = PlainDate.ResolveCalendar(result.Calendar);
        var isoDate = new IsoDate(result.Year!.Value, result.Month, result.Day);
        if (!IsoCalendar.ISOYearMonthWithinLimits(isoDate)) throw TemporalError.Range("Year-month out of range.");
        PartialDate fields = CalendarFields.ToFields(calendar, isoDate, CalendarFieldsType.YearMonth);
        return Create(CalendarFields.YearMonthFromFields(fields, Overflow.Constrain), calendar);
    }

    public static int Compare(PlainYearMonth a, PlainYearMonth b) => IsoCalendar.CompareISODate(a.IsoDate, b.IsoDate);

    public bool Equals(PlainYearMonth other) => IsoDate == other.IsoDate && Calendar == other.Calendar;

    public PlainYearMonth With(in PartialDate partial, Overflow overflow)
    {
        PartialDate fields = CalendarFields.ToFields(Calendar, IsoDate, CalendarFieldsType.YearMonth);
        PartialDate merged = CalendarFields.Merge(fields, partial);
        return Create(CalendarFields.YearMonthFromFields(merged, overflow), Calendar);
    }

    /// <summary>AddDurationToYearMonth.</summary>
    public PlainYearMonth Add(Duration duration, Overflow? overflowOption)
    {
        Overflow overflow = overflowOption ?? Overflow.Constrain;
        int sign = duration.Sign;
        PartialDate fields = CalendarFields.ToFields(Calendar, IsoDate, CalendarFieldsType.YearMonth);
        fields.Day = 1;
        IsoDate intermediateDate = CalendarFields.DateFromFields(fields, Overflow.Constrain);
        IsoDate date;
        if (sign < 0)
        {
            IsoDate nextMonth = IsoCalendar.DateAdd(intermediateDate, new DateDuration(0, 1, 0, 0), Overflow.Constrain);
            date = IsoCalendar.AddDays(nextMonth, -1);
        }
        else
        {
            date = intermediateDate;
        }
        DateDuration durationToAdd = duration.ToDateDurationWithoutTime();
        IsoDate addedDate = IsoCalendar.DateAdd(date, durationToAdd, overflow);
        PartialDate addedDateFields = CalendarFields.ToFields(Calendar, addedDate, CalendarFieldsType.YearMonth);
        return Create(CalendarFields.YearMonthFromFields(addedDateFields, overflow), Calendar);
    }

    public PlainYearMonth Subtract(Duration duration, Overflow? overflow) => Add(duration.Negated(), overflow);

    /// <summary>DifferenceTemporalPlainYearMonth after ToTemporalYearMonth and the calendar check.</summary>
    public Duration Difference(PlainYearMonth other, in DifferenceSettings settings, bool isSince)
    {
        var (largestUnit, smallestUnit, mode, increment) = Units.ResolveDifferenceSettings(settings, isSince, UnitGroup.Date,
            Unit.Week, Unit.Day, true, Unit.Month, Unit.Year);
        if (IsoCalendar.CompareISODate(IsoDate, other.IsoDate) == 0) return Duration.Zero;
        PartialDate thisFields = CalendarFields.ToFields(Calendar, IsoDate, CalendarFieldsType.YearMonth);
        thisFields.Day = 1;
        IsoDate thisDate = CalendarFields.DateFromFields(thisFields, Overflow.Constrain);
        PartialDate otherFields = CalendarFields.ToFields(Calendar, other.IsoDate, CalendarFieldsType.YearMonth);
        otherFields.Day = 1;
        IsoDate otherDate = CalendarFields.DateFromFields(otherFields, Overflow.Constrain);
        DateDuration dateDifference = IsoCalendar.DateUntil(thisDate, otherDate, largestUnit);
        DateDuration yearsMonthsDifference = dateDifference.Adjust(0, 0);
        var duration = new InternalDuration(yearsMonthsDifference, 0);
        if (smallestUnit != Unit.Month || increment != 1)
        {
            var isoDateTime = new IsoDateTime(thisDate, IsoTime.Midnight);
            Int128 originEpochNs = IsoCalendar.GetUTCEpochNanoseconds(isoDateTime);
            var isoDateTimeOther = new IsoDateTime(otherDate, IsoTime.Midnight);
            Int128 destEpochNs = IsoCalendar.GetUTCEpochNanoseconds(isoDateTimeOther);
            duration = RelativeRounding.RoundRelativeDuration(duration, originEpochNs, destEpochNs, isoDateTime, null, Calendar,
                largestUnit, increment, smallestUnit, mode);
        }
        Duration result = Duration.FromInternal(duration, Unit.Day);
        return isSince ? result.Negated() : result;
    }

    public Duration Until(PlainYearMonth other, in DifferenceSettings settings) => Difference(other, settings, false);

    public Duration Since(PlainYearMonth other, in DifferenceSettings settings) => Difference(other, settings, true);

    /// <summary>Temporal.PlainYearMonth.prototype.toPlainDate after PrepareCalendarFields.</summary>
    public PlainDate ToPlainDate(in PartialDate input)
    {
        PartialDate fields = CalendarFields.ToFields(Calendar, IsoDate, CalendarFieldsType.YearMonth);
        PartialDate merged = CalendarFields.Merge(fields, input);
        return PlainDate.Create(CalendarFields.DateFromFields(merged, Overflow.Constrain), Calendar);
    }

    public string ToIxdtfString(DisplayCalendar showCalendar) => Formatting.YearMonthToString(IsoDate, Calendar, showCalendar);
}

public sealed class PlainMonthDay(IsoDate isoDate, Calendar calendar)
{
    public IsoDate IsoDate { get; } = isoDate;
    public Calendar Calendar { get; } = calendar;

    /// <summary>CreateTemporalMonthDay: checks ISODateWithinLimits.</summary>
    public static PlainMonthDay Create(in IsoDate date, Calendar calendar)
    {
        if (!IsoCalendar.ISODateWithinLimits(date)) throw TemporalError.Range("Date out of range.");
        return new PlainMonthDay(date, calendar);
    }

    /// <summary>PlainMonthDay::try_new_with_overflow (reject): the constructor.</summary>
    public static PlainMonthDay TryNew(long month, long day, Calendar calendar, long referenceYear) =>
        Create(IsoCalendar.RegulateISODate(referenceYear, month, day, Overflow.Reject), calendar);

    public static PlainMonthDay FromPartial(in PartialDate partial, Overflow overflow) =>
        Create(CalendarFields.MonthDayFromFields(partial, overflow), partial.Calendar);

    /// <summary>ToTemporalMonthDay's string case.</summary>
    public static PlainMonthDay FromString(ReadOnlySpan<char> s)
    {
        ParsedIso result = IsoParser.Parse(s, IsoGoal.MonthDay);
        Calendar calendar = PlainDate.ResolveCalendar(result.Calendar);
        if (result.Year is null) return Create(new IsoDate(1972, result.Month, result.Day), calendar);
        var isoDate = new IsoDate(result.Year.Value, result.Month, result.Day);
        if (!IsoCalendar.ISODateWithinLimits(isoDate)) throw TemporalError.Range("Date out of range.");
        PartialDate fields = CalendarFields.ToFields(calendar, isoDate, CalendarFieldsType.MonthDay);
        return Create(CalendarFields.MonthDayFromFields(fields, Overflow.Constrain), calendar);
    }

    public bool Equals(PlainMonthDay other) => IsoDate == other.IsoDate && Calendar == other.Calendar;

    public PlainMonthDay With(in PartialDate partial, Overflow overflow)
    {
        PartialDate fields = CalendarFields.ToFields(Calendar, IsoDate, CalendarFieldsType.MonthDay);
        PartialDate merged = CalendarFields.Merge(fields, partial);
        return Create(CalendarFields.MonthDayFromFields(merged, overflow), Calendar);
    }

    /// <summary>Temporal.PlainMonthDay.prototype.toPlainDate after PrepareCalendarFields.</summary>
    public PlainDate ToPlainDate(in PartialDate input)
    {
        PartialDate fields = CalendarFields.ToFields(Calendar, IsoDate, CalendarFieldsType.MonthDay);
        PartialDate merged = CalendarFields.Merge(fields, input);
        return PlainDate.Create(CalendarFields.DateFromFields(merged, Overflow.Constrain), Calendar);
    }

    public string MonthCode => IsoCalendar.MonthCode(IsoDate.Month);

    public string ToIxdtfString(DisplayCalendar showCalendar) => Formatting.MonthDayToString(IsoDate, Calendar, showCalendar);
}
