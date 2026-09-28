// ISO 8601 date and time records and their arithmetic (the spec's ISO Date
// Records, Time Records, ISO Date-Time Records and the operations on them:
// BalanceISODate, BalanceTime, AddTime, DifferenceTime, RoundTime,
// ISODateTimeWithinLimits, GetUTCEpochNanoseconds, week numbering ...).
// See TemporalCore.cs for where this sits (temporal_rs's iso module).
using System.Runtime.CompilerServices;

namespace V8Sharp.Temporal;

/// <summary>An ISO Date Record. The year is a long so intermediate results cannot overflow.</summary>
public readonly record struct IsoDate(long Year, int Month, int Day) : IComparable<IsoDate>
{
    public int CompareTo(IsoDate other) => IsoCalendar.CompareISODate(this, other);
}

/// <summary>A Time Record (without the [[Days]] field).</summary>
public readonly record struct IsoTime(int Hour, int Minute, int Second, int Millisecond, int Microsecond, int Nanosecond)
{
    public static readonly IsoTime Midnight = default;

    /// <summary>Nanoseconds since midnight.</summary>
    public long TotalNanoseconds =>
        ((((Hour * 60L + Minute) * 60L + Second) * 1000L + Millisecond) * 1000L + Microsecond) * 1000L + Nanosecond;

    public static IsoTime FromNanosecondsOfDay(long ns)
    {
        int nanosecond = (int)(ns % 1000); ns /= 1000;
        int microsecond = (int)(ns % 1000); ns /= 1000;
        int millisecond = (int)(ns % 1000); ns /= 1000;
        int second = (int)(ns % 60); ns /= 60;
        int minute = (int)(ns % 60); ns /= 60;
        return new IsoTime((int)ns, minute, second, millisecond, microsecond, nanosecond);
    }

    public int CompareTo(IsoTime other) => TotalNanoseconds.CompareTo(other.TotalNanoseconds);
}

/// <summary>An ISO Date-Time Record.</summary>
public readonly record struct IsoDateTime(IsoDate Date, IsoTime Time)
{
    public int CompareTo(IsoDateTime other)
    {
        int c = IsoCalendar.CompareISODate(Date, other.Date);
        return c != 0 ? c : Time.CompareTo(other.Time);
    }
}

/// <summary>The ISO 8601 calendar arithmetic of the spec.</summary>
public static class IsoCalendar
{
    public static readonly Int128 NsMaxInstant = (Int128)8_640_000_000_000_000_000_000m;
    public static readonly Int128 NsMinInstant = -(Int128)8_640_000_000_000_000_000_000m;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsLeapYear(long year) => (year & 3) == 0 && (year % 100 != 0 || year % 400 == 0);

    /// <summary>ISODaysInMonth.</summary>
    public static int DaysInMonth(long year, int month) => month switch
    {
        1 or 3 or 5 or 7 or 8 or 10 or 12 => 31,
        2 => IsLeapYear(year) ? 29 : 28,
        _ => 30,
    };

    public static int DaysInYear(long year) => IsLeapYear(year) ? 366 : 365;

    /// <summary>IsValidISODate.</summary>
    public static bool IsValidISODate(long year, long month, long day) =>
        month >= 1 && month <= 12 && day >= 1 && day <= DaysInMonth(year, (int)month);

    /// <summary>IsValidTime.</summary>
    public static bool IsValidTime(long h, long m, long s, long ms, long us, long ns) =>
        h >= 0 && h <= 23 && m >= 0 && m <= 59 && s >= 0 && s <= 59 && ms >= 0 && ms <= 999 && us >= 0 && us <= 999 &&
        ns >= 0 && ns <= 999;

    /// <summary>ISODateToEpochDays(year, month - 1, day): days since 1970-01-01 (proleptic Gregorian).</summary>
    public static long EpochDays(long year, long month, long day)
    {
        // Balance the month first (callers may pass month outside 1..12).
        year += Rounding.FloorDiv(month - 1, 12);
        month = Rounding.FloorMod(month - 1, 12) + 1;
        // Howard Hinnant's days_from_civil.
        long y = month <= 2 ? year - 1 : year;
        long era = Rounding.FloorDiv(y, 400);
        long yoe = y - era * 400;
        long mp = (month + 9) % 12;
        long doy = (153 * mp + 2) / 5 + 1 - 1;
        long doe = yoe * 365 + yoe / 4 - yoe / 100 + doy;
        return era * 146097 + doe - 719468 + (day - 1);
    }

    public static long EpochDays(in IsoDate date) => EpochDays(date.Year, date.Month, date.Day);

    /// <summary>The ISO date of a day number (civil_from_days).</summary>
    public static IsoDate FromEpochDays(long days)
    {
        long z = days + 719468;
        long era = Rounding.FloorDiv(z, 146097);
        long doe = z - era * 146097;
        long yoe = (doe - doe / 1460 + doe / 36524 - doe / 146096) / 365;
        long y = yoe + era * 400;
        long doy = doe - (365 * yoe + yoe / 4 - yoe / 100);
        long mp = (5 * doy + 2) / 153;
        long d = doy - (153 * mp + 2) / 5 + 1;
        long m = mp < 10 ? mp + 3 : mp - 9;
        return new IsoDate(m <= 2 ? y + 1 : y, (int)m, (int)d);
    }

    /// <summary>BalanceISODate.</summary>
    public static IsoDate BalanceISODate(long year, long month, long day) => FromEpochDays(EpochDays(year, month, 1) + day - 1);

    /// <summary>AddDaysToISODate.</summary>
    public static IsoDate AddDays(in IsoDate date, long days) => days == 0 ? date : FromEpochDays(EpochDays(date) + days);

    /// <summary>BalanceISOYearMonth.</summary>
    public static (long year, int month) BalanceISOYearMonth(long year, long month) =>
        (year + Rounding.FloorDiv(month - 1, 12), (int)(Rounding.FloorMod(month - 1, 12) + 1));

    /// <summary>CompareISODate.</summary>
    public static int CompareISODate(in IsoDate a, in IsoDate b)
    {
        if (a.Year != b.Year) return a.Year > b.Year ? 1 : -1;
        if (a.Month != b.Month) return a.Month > b.Month ? 1 : -1;
        if (a.Day != b.Day) return a.Day > b.Day ? 1 : -1;
        return 0;
    }

    /// <summary>RegulateISODate.</summary>
    public static IsoDate RegulateISODate(long year, long month, long day, Overflow overflow)
    {
        if (overflow == Overflow.Constrain)
        {
            month = Math.Clamp(month, 1, 12);
            day = Math.Clamp(day, 1, DaysInMonth(year, (int)month));
            return new IsoDate(year, (int)month, (int)day);
        }
        if (!IsValidISODate(year, month, day)) throw TemporalError.Range("Invalid ISO date.");
        return new IsoDate(year, (int)month, (int)day);
    }

    /// <summary>RegulateTime.</summary>
    public static IsoTime RegulateTime(long h, long m, long s, long ms, long us, long ns, Overflow overflow)
    {
        if (overflow == Overflow.Constrain)
        {
            return new IsoTime((int)Math.Clamp(h, 0, 23), (int)Math.Clamp(m, 0, 59), (int)Math.Clamp(s, 0, 59),
                (int)Math.Clamp(ms, 0, 999), (int)Math.Clamp(us, 0, 999), (int)Math.Clamp(ns, 0, 999));
        }
        if (!IsValidTime(h, m, s, ms, us, ns)) throw TemporalError.Range("Invalid time.");
        return new IsoTime((int)h, (int)m, (int)s, (int)ms, (int)us, (int)ns);
    }

    /// <summary>DayOfWeek (1 = Monday ... 7 = Sunday).</summary>
    public static int DayOfWeek(in IsoDate date) => (int)Rounding.FloorMod(EpochDays(date) + 3, 7) + 1;

    /// <summary>DayOfYear.</summary>
    public static int DayOfYear(in IsoDate date) => (int)(EpochDays(date) - EpochDays(date.Year, 1, 1)) + 1;

    static int WeeksInYear(long year)
    {
        static long P(long y) => Rounding.FloorMod(y + Rounding.FloorDiv(y, 4) - Rounding.FloorDiv(y, 100) + Rounding.FloorDiv(y, 400), 7);
        return P(year) == 4 || P(year - 1) == 3 ? 53 : 52;
    }

    /// <summary>ISO week number and week-based year of a date.</summary>
    public static (int week, long year) WeekOfYear(in IsoDate date)
    {
        int wd = DayOfWeek(date);
        int doy = DayOfYear(date);
        long week = Rounding.FloorDiv(doy - wd + 10, 7);
        if (week < 1) return (WeeksInYear(date.Year - 1), date.Year - 1);
        if (week > WeeksInYear(date.Year)) return (1, date.Year + 1);
        return ((int)week, date.Year);
    }

    /// <summary>The month code "M01" ... "M12".</summary>
    public static string MonthCode(int month) => month < 10 ? "M0" + (char)('0' + month) : "M1" + (char)('0' + month - 10);

    // ---- Limits -----------------------------------------------------------------------------

    /// <summary>IsValidEpochNanoseconds.</summary>
    public static bool IsValidEpochNanoseconds(Int128 ns) => ns >= NsMinInstant && ns <= NsMaxInstant;

    /// <summary>GetUTCEpochNanoseconds.</summary>
    public static Int128 GetUTCEpochNanoseconds(in IsoDateTime dt) =>
        (Int128)EpochDays(dt.Date) * Units.NsPerDay + dt.Time.TotalNanoseconds;

    /// <summary>ISODateTimeWithinLimits.</summary>
    public static bool ISODateTimeWithinLimits(in IsoDateTime dt)
    {
        long days = EpochDays(dt.Date);
        if (Math.Abs(days) > 100_000_001) return false;
        Int128 ns = (Int128)days * Units.NsPerDay + dt.Time.TotalNanoseconds;
        if (ns <= NsMinInstant - Units.NsPerDay) return false;
        if (ns >= NsMaxInstant + Units.NsPerDay) return false;
        return true;
    }

    /// <summary>ISODateWithinLimits.</summary>
    public static bool ISODateWithinLimits(in IsoDate date) =>
        ISODateTimeWithinLimits(new IsoDateTime(date, new IsoTime(12, 0, 0, 0, 0, 0)));

    /// <summary>ISOYearMonthWithinLimits.</summary>
    public static bool ISOYearMonthWithinLimits(in IsoDate date)
    {
        if (date.Year < -271821 || date.Year > 275760) return false;
        if (date.Year == -271821 && date.Month < 4) return false;
        if (date.Year == 275760 && date.Month > 9) return false;
        return true;
    }

    /// <summary>CheckISODaysRange.</summary>
    public static void CheckISODaysRange(in IsoDate date)
    {
        if (Math.Abs(EpochDays(date)) > 100_000_000) throw TemporalError.Range("Date out of range.");
    }

    /// <summary>GetISOPartsFromEpoch.</summary>
    public static IsoDateTime FromEpochNanoseconds(Int128 epochNs)
    {
        Int128 days = Rounding.Int128Floor(epochNs, Units.NsPerDay);
        long remainder = (long)(epochNs - days * Units.NsPerDay);
        return new IsoDateTime(FromEpochDays((long)days), IsoTime.FromNanosecondsOfDay(remainder));
    }

    /// <summary>BalanceTime: time fields (possibly out of range) plus extra nanoseconds; returns the day overflow.</summary>
    public static (long days, IsoTime time) BalanceTime(Int128 totalNs)
    {
        Int128 days = Rounding.Int128Floor(totalNs, Units.NsPerDay);
        long rem = (long)(totalNs - days * Units.NsPerDay);
        return ((long)days, IsoTime.FromNanosecondsOfDay(rem));
    }

    /// <summary>BalanceISODateTime with the time given as nanoseconds relative to midnight of the date.</summary>
    public static IsoDateTime BalanceISODateTime(in IsoDate date, Int128 timeNs)
    {
        (long days, IsoTime time) = BalanceTime(timeNs);
        return new IsoDateTime(AddDays(date, days), time);
    }

    /// <summary>AddTime: time + a time duration; returns the day overflow.</summary>
    public static (long days, IsoTime time) AddTime(in IsoTime time, Int128 timeDuration) =>
        BalanceTime(time.TotalNanoseconds + timeDuration);

    /// <summary>DifferenceTime as a time duration in nanoseconds.</summary>
    public static Int128 DifferenceTime(in IsoTime one, in IsoTime two) => two.TotalNanoseconds - one.TotalNanoseconds;

    /// <summary>RoundTime: returns (days, time) where days is the carry into the date.</summary>
    public static (long days, IsoTime time) RoundTime(in IsoTime time, long increment, Unit unit, RoundingMode mode)
    {
        long quantity = unit switch
        {
            Unit.Day or Unit.Hour => time.TotalNanoseconds,
            Unit.Minute => time.TotalNanoseconds - time.Hour * Units.NsPerHour,
            Unit.Second => time.TotalNanoseconds - time.Hour * Units.NsPerHour - time.Minute * Units.NsPerMinute,
            Unit.Millisecond => (time.Millisecond * 1000L + time.Microsecond) * 1000L + time.Nanosecond,
            Unit.Microsecond => time.Microsecond * 1000L + time.Nanosecond,
            _ => time.Nanosecond,
        };
        long unitLength = Units.Length(unit);
        long result = (long)(Rounding.RoundToIncrement(quantity, (Int128)increment * unitLength, mode) / unitLength);
        Int128 total = unit switch
        {
            Unit.Day => (Int128)result * Units.NsPerDay,
            Unit.Hour => (Int128)result * Units.NsPerHour,
            Unit.Minute => time.Hour * Units.NsPerHour + (Int128)result * Units.NsPerMinute,
            Unit.Second => time.Hour * Units.NsPerHour + time.Minute * Units.NsPerMinute + (Int128)result * Units.NsPerSecond,
            Unit.Millisecond => time.TotalNanoseconds - ((time.Millisecond * 1000L + time.Microsecond) * 1000L + time.Nanosecond) +
                                (Int128)result * Units.NsPerMillisecond,
            Unit.Microsecond => time.TotalNanoseconds - (time.Microsecond * 1000L + time.Nanosecond) + (Int128)result * 1000,
            _ => time.TotalNanoseconds - time.Nanosecond + result,
        };
        return BalanceTime(total);
    }

    /// <summary>RoundISODateTime.</summary>
    public static IsoDateTime RoundISODateTime(in IsoDateTime dt, long increment, Unit unit, RoundingMode mode)
    {
        (long days, IsoTime time) = RoundTime(dt.Time, increment, unit, mode);
        return new IsoDateTime(AddDays(dt.Date, days), time);
    }

    /// <summary>ISODateSurpasses.</summary>
    static bool ISODateSurpasses(int sign, long y1, long m1, long d1, in IsoDate date2)
    {
        if (y1 != date2.Year) return sign * (y1 - date2.Year) > 0;
        if (m1 != date2.Month) return sign * (m1 - date2.Month) > 0;
        if (d1 != date2.Day) return sign * (d1 - date2.Day) > 0;
        return false;
    }

    /// <summary>CalendarDateUntil for the ISO 8601 calendar.</summary>
    public static DateDuration DateUntil(in IsoDate one, in IsoDate two, Unit largestUnit)
    {
        int sign = -CompareISODate(one, two);
        if (sign == 0) return default;
        long years = 0;
        if (largestUnit == Unit.Year)
        {
            long candidateYears = two.Year - one.Year;
            if (candidateYears != 0) candidateYears -= sign;
            while (!ISODateSurpasses(sign, one.Year + candidateYears, one.Month, one.Day, two))
            {
                years = candidateYears;
                candidateYears += sign;
            }
        }
        long months = 0;
        if (largestUnit is Unit.Year or Unit.Month)
        {
            // Start close to the answer instead of stepping month by month from zero.
            long candidateMonths = sign;
            if (largestUnit == Unit.Month)
            {
                long estimate = (two.Year - one.Year) * 12 + (two.Month - one.Month);
                estimate -= 2 * sign;
                if (sign * estimate > 1) candidateMonths = estimate;
            }
            var intermediate = BalanceISOYearMonth(one.Year + years, one.Month + candidateMonths);
            while (!ISODateSurpasses(sign, intermediate.year, intermediate.month, one.Day, two))
            {
                months = candidateMonths;
                candidateMonths += sign;
                intermediate = BalanceISOYearMonth(intermediate.year, intermediate.month + sign);
            }
        }
        var ym = BalanceISOYearMonth(one.Year + years, one.Month + months);
        IsoDate constrained = RegulateISODate(ym.year, ym.month, one.Day, Overflow.Constrain);
        long diffDays = EpochDays(two) - EpochDays(constrained);
        long weeks = 0;
        if (largestUnit == Unit.Week)
        {
            weeks = diffDays / 7;
            diffDays -= weeks * 7;
        }
        return new DateDuration(years, months, weeks, diffDays);
    }

    /// <summary>CalendarDateAdd for the ISO 8601 calendar.</summary>
    public static IsoDate DateAdd(in IsoDate date, in DateDuration duration, Overflow overflow)
    {
        var intermediate = BalanceISOYearMonth(date.Year + (long)duration.Years, date.Month + (long)duration.Months);
        IsoDate regulated = RegulateISODate(intermediate.year, intermediate.month, date.Day, overflow);
        long days = (long)duration.Days + 7 * (long)duration.Weeks;
        IsoDate result = AddDays(regulated, days);
        if (!ISODateWithinLimits(result)) throw TemporalError.Range("Date out of range.");
        return result;
    }

    /// <summary>PadISOYear.</summary>
    public static void AppendPaddedYear(System.Text.StringBuilder sb, long year)
    {
        if (year >= 0 && year <= 9999)
        {
            sb.Append(year.ToString("D4", System.Globalization.CultureInfo.InvariantCulture));
            return;
        }
        sb.Append(year < 0 ? '-' : '+');
        sb.Append(Math.Abs(year).ToString("D6", System.Globalization.CultureInfo.InvariantCulture));
    }

    public static void AppendTwoDigits(System.Text.StringBuilder sb, int value)
    {
        sb.Append((char)('0' + value / 10));
        sb.Append((char)('0' + value % 10));
    }
}
