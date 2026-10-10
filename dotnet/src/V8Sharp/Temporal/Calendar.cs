// The calendar (temporal_rs::Calendar / AnyCalendarKind) and the calendar
// field operations of the spec for the ISO 8601 calendar:
// CanonicalizeCalendar, CalendarResolveFields, CalendarDateFromFields,
// CalendarYearMonthFromFields, CalendarMonthDayFromFields, CalendarMergeFields
// and ISODateToFields.
//
// Deviation (deviations.md "Temporal"): only "iso8601" is available. V8 gets
// the other calendars from ICU4X inside temporal_rs; V8Sharp has no calendar
// data (it matches a V8 build without i18n support otherwise).
namespace V8Sharp.Temporal;

/// <summary>A calendar (temporal_rs::Calendar). Only the ISO 8601 calendar exists.</summary>
public sealed class Calendar
{
    public const string Iso8601Id = "iso8601";

    public static readonly Calendar Iso = new(Iso8601Id);

    public string Identifier { get; }

    Calendar(string identifier) => Identifier = identifier;

    /// <summary>AnyCalendarKind::get_for_str on an ASCII-lowercased identifier; null if unknown.</summary>
    public static Calendar? GetForLowercase(ReadOnlySpan<char> lowercase) =>
        lowercase.SequenceEqual(Iso8601Id) ? Iso : null;

    /// <summary>CanonicalizeCalendar: case-insensitive lookup; throws RangeError if unknown.</summary>
    public static Calendar? TryCanonicalize(ReadOnlySpan<char> id)
    {
        if (id.Length != Iso8601Id.Length) return null;
        Span<char> lower = stackalloc char[id.Length];
        for (int i = 0; i < id.Length; i++)
        {
            char c = id[i];
            lower[i] = c is >= 'A' and <= 'Z' ? (char)(c + 32) : c;
        }
        return GetForLowercase(lower);
    }

    /// <summary>Whether the calendar has eras (CalendarSupportsEra): not the ISO calendar.</summary>
    public bool UsesEras => false;

    public override string ToString() => Identifier;
}

/// <summary>temporal_rs::PartialDate: the calendar fields of a date after conversion.</summary>
public struct PartialDate
{
    public long? Year;
    public long? Month;
    public string? MonthCode;
    public long? Day;
    public string? Era;
    public long? EraYear;
    public Calendar Calendar;

    public PartialDate(Calendar calendar) : this() => Calendar = calendar;

    public readonly bool IsEmpty => Year is null && Month is null && MonthCode is null && Day is null && Era is null &&
                                    EraYear is null;
}

/// <summary>temporal_rs::PartialTime.</summary>
public struct PartialTime
{
    public long? Hour, Minute, Second, Millisecond, Microsecond, Nanosecond;

    public readonly bool IsEmpty => Hour is null && Minute is null && Second is null && Millisecond is null &&
                                    Microsecond is null && Nanosecond is null;

    public static PartialTime From(in IsoTime t) => new()
    {
        Hour = t.Hour, Minute = t.Minute, Second = t.Second, Millisecond = t.Millisecond, Microsecond = t.Microsecond,
        Nanosecond = t.Nanosecond,
    };

    /// <summary>Fills the unset fields from <paramref name="t"/> and regulates.</summary>
    public readonly IsoTime ToTime(in IsoTime t, Overflow overflow) => IsoCalendar.RegulateTime(Hour ?? t.Hour,
        Minute ?? t.Minute, Second ?? t.Second, Millisecond ?? t.Millisecond, Microsecond ?? t.Microsecond,
        Nanosecond ?? t.Nanosecond, overflow);
}

/// <summary>The kind of a CalendarResolveFields call.</summary>
public enum CalendarFieldsType { Date, YearMonth, MonthDay }

/// <summary>The ISO 8601 calendar's field operations.</summary>
public static class CalendarFields
{
    /// <summary>ISODateToFields.</summary>
    public static PartialDate ToFields(Calendar calendar, in IsoDate date, CalendarFieldsType type)
    {
        var fields = new PartialDate(calendar) { MonthCode = IsoCalendar.MonthCode(date.Month) };
        if (type is CalendarFieldsType.MonthDay or CalendarFieldsType.Date) fields.Day = date.Day;
        if (type is CalendarFieldsType.YearMonth or CalendarFieldsType.Date) fields.Year = date.Year;
        return fields;
    }

    /// <summary>CalendarMergeFields.</summary>
    public static PartialDate Merge(in PartialDate fields, in PartialDate additional)
    {
        PartialDate merged = fields;
        if (additional.Month is not null || additional.MonthCode is not null)
        {
            merged.Month = null;
            merged.MonthCode = null;
        }
        if (additional.Year is not null) merged.Year = additional.Year;
        if (additional.Month is not null) merged.Month = additional.Month;
        if (additional.MonthCode is not null) merged.MonthCode = additional.MonthCode;
        if (additional.Day is not null) merged.Day = additional.Day;
        if (additional.Era is not null || additional.EraYear is not null)
        {
            merged.Era = additional.Era;
            merged.EraYear = additional.EraYear;
        }
        return merged;
    }

    /// <summary>CalendarResolveFields for iso8601: returns the month.</summary>
    static long Resolve(in PartialDate fields, CalendarFieldsType type)
    {
        if (type is CalendarFieldsType.Date or CalendarFieldsType.YearMonth && fields.Year is null)
            throw TemporalError.Type("Required field year is missing.");
        if (type is CalendarFieldsType.Date or CalendarFieldsType.MonthDay && fields.Day is null)
            throw TemporalError.Type("Required field day is missing.");
        long? month = fields.Month;
        string? monthCode = fields.MonthCode;
        if (monthCode is null)
        {
            if (month is null) throw TemporalError.Type("Required field month or monthCode is missing.");
            return month.Value;
        }
        // The ISO 8601 calendar does not include leap months.
        if (monthCode.Length != 3 || monthCode[0] != 'M') throw TemporalError.Range("Invalid monthCode.");
        char d1 = monthCode[1], d2 = monthCode[2];
        if (d1 is < '0' or > '9' || d2 is < '0' or > '9') throw TemporalError.Range("Invalid monthCode.");
        int monthCodeInteger = (d1 - '0') * 10 + (d2 - '0');
        if (monthCodeInteger < 1 || monthCodeInteger > 12) throw TemporalError.Range("Invalid monthCode.");
        if (month is not null && month.Value != monthCodeInteger) throw TemporalError.Range("month and monthCode conflict.");
        return monthCodeInteger;
    }

    /// <summary>CalendarDateFromFields.</summary>
    public static IsoDate DateFromFields(in PartialDate fields, Overflow overflow)
    {
        long month = Resolve(fields, CalendarFieldsType.Date);
        IsoDate result = IsoCalendar.RegulateISODate(fields.Year!.Value, month, fields.Day!.Value, overflow);
        if (!IsoCalendar.ISODateWithinLimits(result)) throw TemporalError.Range("Date out of range.");
        return result;
    }

    /// <summary>CalendarYearMonthFromFields.</summary>
    public static IsoDate YearMonthFromFields(in PartialDate fields, Overflow overflow)
    {
        long month = Resolve(fields, CalendarFieldsType.YearMonth);
        IsoDate result = IsoCalendar.RegulateISODate(fields.Year!.Value, month, 1, overflow);
        if (!IsoCalendar.ISOYearMonthWithinLimits(result)) throw TemporalError.Range("Year-month out of range.");
        return result;
    }

    /// <summary>CalendarMonthDayFromFields.</summary>
    public static IsoDate MonthDayFromFields(in PartialDate fields, Overflow overflow)
    {
        long month = Resolve(fields, CalendarFieldsType.MonthDay);
        const int referenceISOYear = 1972;
        long year = fields.Year ?? referenceISOYear;
        IsoDate result = IsoCalendar.RegulateISODate(year, month, fields.Day!.Value, overflow);
        return new IsoDate(referenceISOYear, result.Month, result.Day);
    }
}
