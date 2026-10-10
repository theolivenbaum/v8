// The string output operations of the spec: FormatFractionalSeconds,
// TimeRecordToString, TemporalDateToString, ISODateTimeToString,
// TemporalYearMonthToString, TemporalMonthDayToString,
// FormatCalendarAnnotation, FormatOffsetTimeZoneIdentifier,
// FormatUTCOffsetNanoseconds and FormatDateTimeUTCOffsetRounded
// (temporal_rs's ixdtf writers).
using System.Text;

namespace V8Sharp.Temporal;

public static class Formatting
{
    /// <summary>FormatFractionalSeconds.</summary>
    public static void AppendFractionalSeconds(StringBuilder sb, long subSecondNanoseconds, Precision precision)
    {
        if (precision.IsMinute) return;
        Span<char> digits = stackalloc char[9];
        long v = subSecondNanoseconds;
        for (int i = 8; i >= 0; i--)
        {
            digits[i] = (char)('0' + v % 10);
            v /= 10;
        }
        int length;
        if (precision.Digits is int p)
        {
            if (p == 0) return;
            length = p;
        }
        else
        {
            if (subSecondNanoseconds == 0) return;
            length = 9;
            while (length > 0 && digits[length - 1] == '0') length--;
        }
        sb.Append('.');
        sb.Append(digits[..length]);
    }

    /// <summary>TimeRecordToString.</summary>
    public static void AppendTime(StringBuilder sb, in IsoTime time, Precision precision)
    {
        IsoCalendar.AppendTwoDigits(sb, time.Hour);
        sb.Append(':');
        IsoCalendar.AppendTwoDigits(sb, time.Minute);
        if (precision.IsMinute) return;
        sb.Append(':');
        IsoCalendar.AppendTwoDigits(sb, time.Second);
        long subSecond = (time.Millisecond * 1000L + time.Microsecond) * 1000L + time.Nanosecond;
        AppendFractionalSeconds(sb, subSecond, precision);
    }

    /// <summary>The date part of TemporalDateToString (without the calendar annotation).</summary>
    public static void AppendDate(StringBuilder sb, in IsoDate date)
    {
        IsoCalendar.AppendPaddedYear(sb, date.Year);
        sb.Append('-');
        IsoCalendar.AppendTwoDigits(sb, date.Month);
        sb.Append('-');
        IsoCalendar.AppendTwoDigits(sb, date.Day);
    }

    /// <summary>FormatCalendarAnnotation.</summary>
    public static void AppendCalendarAnnotation(StringBuilder sb, string calendarId, DisplayCalendar showCalendar)
    {
        if (showCalendar == DisplayCalendar.Never) return;
        if (showCalendar == DisplayCalendar.Auto && calendarId == Calendar.Iso8601Id) return;
        sb.Append('[');
        if (showCalendar == DisplayCalendar.Critical) sb.Append('!');
        sb.Append("u-ca=");
        sb.Append(calendarId);
        sb.Append(']');
    }

    /// <summary>TemporalDateToString.</summary>
    public static string DateToString(in IsoDate date, Calendar calendar, DisplayCalendar showCalendar)
    {
        var sb = new StringBuilder();
        AppendDate(sb, date);
        AppendCalendarAnnotation(sb, calendar.Identifier, showCalendar);
        return sb.ToString();
    }

    /// <summary>ISODateTimeToString.</summary>
    public static string DateTimeToString(in IsoDateTime dt, Calendar calendar, Precision precision, DisplayCalendar showCalendar)
    {
        var sb = new StringBuilder();
        AppendDate(sb, dt.Date);
        sb.Append('T');
        AppendTime(sb, dt.Time, precision);
        AppendCalendarAnnotation(sb, calendar.Identifier, showCalendar);
        return sb.ToString();
    }

    /// <summary>TemporalYearMonthToString.</summary>
    public static string YearMonthToString(in IsoDate date, Calendar calendar, DisplayCalendar showCalendar)
    {
        var sb = new StringBuilder();
        IsoCalendar.AppendPaddedYear(sb, date.Year);
        sb.Append('-');
        IsoCalendar.AppendTwoDigits(sb, date.Month);
        if (showCalendar is DisplayCalendar.Always or DisplayCalendar.Critical || calendar.Identifier != Calendar.Iso8601Id)
        {
            sb.Append('-');
            IsoCalendar.AppendTwoDigits(sb, date.Day);
        }
        AppendCalendarAnnotation(sb, calendar.Identifier, showCalendar);
        return sb.ToString();
    }

    /// <summary>TemporalMonthDayToString.</summary>
    public static string MonthDayToString(in IsoDate date, Calendar calendar, DisplayCalendar showCalendar)
    {
        var sb = new StringBuilder();
        if (showCalendar is DisplayCalendar.Always or DisplayCalendar.Critical || calendar.Identifier != Calendar.Iso8601Id)
        {
            IsoCalendar.AppendPaddedYear(sb, date.Year);
            sb.Append('-');
        }
        IsoCalendar.AppendTwoDigits(sb, date.Month);
        sb.Append('-');
        IsoCalendar.AppendTwoDigits(sb, date.Day);
        AppendCalendarAnnotation(sb, calendar.Identifier, showCalendar);
        return sb.ToString();
    }

    /// <summary>FormatOffsetTimeZoneIdentifier.</summary>
    public static string FormatOffsetTimeZoneIdentifier(int offsetMinutes)
    {
        var sb = new StringBuilder(6);
        sb.Append(offsetMinutes >= 0 ? '+' : '-');
        int abs = Math.Abs(offsetMinutes);
        IsoCalendar.AppendTwoDigits(sb, abs / 60);
        sb.Append(':');
        IsoCalendar.AppendTwoDigits(sb, abs % 60);
        return sb.ToString();
    }

    /// <summary>FormatUTCOffsetNanoseconds.</summary>
    public static string FormatUTCOffsetNanoseconds(long offsetNs)
    {
        var sb = new StringBuilder();
        sb.Append(offsetNs >= 0 ? '+' : '-');
        long abs = Math.Abs(offsetNs);
        long hours = abs / Units.NsPerHour;
        long minutes = abs / Units.NsPerMinute % 60;
        long seconds = abs / Units.NsPerSecond % 60;
        long subSecond = abs % Units.NsPerSecond;
        IsoCalendar.AppendTwoDigits(sb, (int)hours);
        sb.Append(':');
        IsoCalendar.AppendTwoDigits(sb, (int)minutes);
        if (seconds != 0 || subSecond != 0)
        {
            sb.Append(':');
            IsoCalendar.AppendTwoDigits(sb, (int)seconds);
            AppendFractionalSeconds(sb, subSecond, Precision.Auto);
        }
        return sb.ToString();
    }

    /// <summary>FormatDateTimeUTCOffsetRounded.</summary>
    public static string FormatDateTimeUTCOffsetRounded(long offsetNs)
    {
        long rounded = (long)Rounding.RoundToIncrement(offsetNs, Units.NsPerMinute, RoundingMode.HalfExpand);
        return FormatOffsetTimeZoneIdentifier((int)(rounded / Units.NsPerMinute));
    }
}
