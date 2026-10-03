// Temporal.Duration's engine (temporal_rs::Duration): the Duration value, the
// spec's Date Duration Records, time durations (a nanosecond count below
// 2^53 seconds) and Internal Duration Records, and the duration operations
// that do not need a relative-to point (add, round/total without relativeTo,
// toString). The relative-to machinery is in DurationRounding.cs.
using System.Globalization;
using System.Numerics;
using System.Text;

namespace V8Sharp.Temporal;

/// <summary>A Date Duration Record.</summary>
public readonly record struct DateDuration(long Years, long Months, long Weeks, long Days)
{
    /// <summary>DateDurationSign.</summary>
    public int Sign =>
        Years != 0 ? Math.Sign(Years) : Months != 0 ? Math.Sign(Months) : Weeks != 0 ? Math.Sign(Weeks) : Math.Sign(Days);

    /// <summary>CreateDateDurationRecord (validates).</summary>
    public static DateDuration Create(long years, long months, long weeks, long days)
    {
        if (!Duration.IsValid(years, months, weeks, days, 0, 0, 0, 0, 0, 0))
            throw TemporalError.Range("Invalid duration.");
        return new DateDuration(years, months, weeks, days);
    }

    /// <summary>AdjustDateDurationRecord.</summary>
    public DateDuration Adjust(long days, long? weeks = null, long? months = null) =>
        Create(Years, months ?? Months, weeks ?? Weeks, days);
}

/// <summary>An Internal Duration Record: a date duration and a time duration in nanoseconds.</summary>
public readonly record struct InternalDuration(DateDuration Date, Int128 Time)
{
    /// <summary>InternalDurationSign.</summary>
    public int Sign
    {
        get
        {
            int s = Date.Sign;
            return s != 0 ? s : Time.CompareTo(Int128.Zero);
        }
    }
}

/// <summary>Time duration helpers (the spec's Time Duration operations).</summary>
public static class TimeDurations
{
    /// <summary>maxTimeDuration = 2^53 × 10^9 - 1.</summary>
    public static readonly Int128 Max = ((Int128)1 << 53) * 1_000_000_000 - 1;

    public static Int128 Validate(Int128 d)
    {
        if (Int128.Abs(d) > Max) throw TemporalError.Range("Time duration out of range.");
        return d;
    }

    /// <summary>AddTimeDuration.</summary>
    public static Int128 Add(Int128 one, Int128 two) => Validate(one + two);

    /// <summary>Add24HourDaysToTimeDuration.</summary>
    public static Int128 Add24HourDays(Int128 d, long days) => Validate(d + (Int128)days * Units.NsPerDay);

    /// <summary>RoundTimeDurationToIncrement.</summary>
    public static Int128 RoundToIncrement(Int128 d, Int128 increment, RoundingMode mode) =>
        Validate(Rounding.RoundToIncrement(d, increment, mode));

    /// <summary>RoundTimeDuration.</summary>
    public static Int128 Round(Int128 d, long increment, Unit unit, RoundingMode mode) =>
        RoundToIncrement(d, (Int128)Units.Length(unit) * increment, mode);

    /// <summary>TotalTimeDuration: 𝔽(d / unitLength).</summary>
    public static double Total(Int128 d, Unit unit) =>
        Rounding.RationalToDouble((BigInteger)d, Units.Length(unit));

    public static int Sign(Int128 d) => d.CompareTo(Int128.Zero);
}

/// <summary>The value of a Temporal.Duration (temporal_rs::Duration). Fields hold integral float64 values.</summary>
public sealed class Duration
{
    public readonly double Years, Months, Weeks, Days, Hours, Minutes, Seconds, Milliseconds, Microseconds, Nanoseconds;

    Duration(double y, double mo, double w, double d, double h, double mi, double s, double ms, double us, double ns)
    {
        // CreateTemporalDuration stores ℝ(𝔽(x)); -0 is normalized to +0.
        Years = y + 0.0; Months = mo + 0.0; Weeks = w + 0.0; Days = d + 0.0; Hours = h + 0.0; Minutes = mi + 0.0;
        Seconds = s + 0.0; Milliseconds = ms + 0.0; Microseconds = us + 0.0; Nanoseconds = ns + 0.0;
    }

    public static readonly Duration Zero = new(0, 0, 0, 0, 0, 0, 0, 0, 0, 0);

    /// <summary>CreateTemporalDuration: validates and creates.</summary>
    public static Duration Create(double y, double mo, double w, double d, double h, double mi, double s, double ms,
        double us, double ns)
    {
        if (!IsValid(y, mo, w, d, h, mi, s, ms, us, ns)) throw TemporalError.Range("Invalid duration.");
        return new Duration(y, mo, w, d, h, mi, s, ms, us, ns);
    }

    /// <summary>IsValidDuration.</summary>
    public static bool IsValid(double y, double mo, double w, double d, double h, double mi, double s, double ms, double us,
        double ns)
    {
        int sign = 0;
        Span<double> fields = [y, mo, w, d, h, mi, s, ms, us, ns];
        foreach (double v in fields)
        {
            if (!double.IsFinite(v)) return false;
            if (v < 0)
            {
                if (sign > 0) return false;
                sign = -1;
            }
            else if (v > 0)
            {
                if (sign < 0) return false;
                sign = 1;
            }
        }
        const double TwoTo32 = 4294967296.0;
        if (Math.Abs(y) >= TwoTo32 || Math.Abs(mo) >= TwoTo32 || Math.Abs(w) >= TwoTo32) return false;
        const double TwoTo53 = 9007199254740992.0;
        if (Math.Abs(d) >= TwoTo53 || Math.Abs(h) >= TwoTo53 || Math.Abs(mi) >= TwoTo53 || Math.Abs(s) >= TwoTo53) return false;
        if (Math.Abs(ms) >= 1e19 || Math.Abs(us) >= 1e22 || Math.Abs(ns) >= 1e25) return false;
        Int128 total = (Int128)d * Units.NsPerDay + (Int128)h * Units.NsPerHour + (Int128)mi * Units.NsPerMinute +
                       (Int128)s * Units.NsPerSecond + (Int128)ms * Units.NsPerMillisecond + (Int128)us * 1000 + (Int128)ns;
        return Int128.Abs(total) <= TimeDurations.Max;
    }

    /// <summary>DurationSign.</summary>
    public int Sign
    {
        get
        {
            if (Years != 0) return Math.Sign(Years);
            if (Months != 0) return Math.Sign(Months);
            if (Weeks != 0) return Math.Sign(Weeks);
            if (Days != 0) return Math.Sign(Days);
            if (Hours != 0) return Math.Sign(Hours);
            if (Minutes != 0) return Math.Sign(Minutes);
            if (Seconds != 0) return Math.Sign(Seconds);
            if (Milliseconds != 0) return Math.Sign(Milliseconds);
            if (Microseconds != 0) return Math.Sign(Microseconds);
            return Math.Sign(Nanoseconds);
        }
    }

    public bool IsZero => Sign == 0;

    /// <summary>CreateNegatedTemporalDuration.</summary>
    public Duration Negated() => new(-Years, -Months, -Weeks, -Days, -Hours, -Minutes, -Seconds, -Milliseconds,
        -Microseconds, -Nanoseconds);

    public Duration Abs() => new(Math.Abs(Years), Math.Abs(Months), Math.Abs(Weeks), Math.Abs(Days), Math.Abs(Hours),
        Math.Abs(Minutes), Math.Abs(Seconds), Math.Abs(Milliseconds), Math.Abs(Microseconds), Math.Abs(Nanoseconds));

    public bool FieldsEqual(Duration o) =>
        Years == o.Years && Months == o.Months && Weeks == o.Weeks && Days == o.Days && Hours == o.Hours &&
        Minutes == o.Minutes && Seconds == o.Seconds && Milliseconds == o.Milliseconds &&
        Microseconds == o.Microseconds && Nanoseconds == o.Nanoseconds;

    /// <summary>DefaultTemporalLargestUnit.</summary>
    public Unit DefaultLargestUnit()
    {
        if (Years != 0) return Unit.Year;
        if (Months != 0) return Unit.Month;
        if (Weeks != 0) return Unit.Week;
        if (Days != 0) return Unit.Day;
        if (Hours != 0) return Unit.Hour;
        if (Minutes != 0) return Unit.Minute;
        if (Seconds != 0) return Unit.Second;
        if (Milliseconds != 0) return Unit.Millisecond;
        if (Microseconds != 0) return Unit.Microsecond;
        return Unit.Nanosecond;
    }

    /// <summary>TimeDurationFromComponents for the time fields.</summary>
    public Int128 TimeDuration =>
        (Int128)Hours * Units.NsPerHour + (Int128)Minutes * Units.NsPerMinute + (Int128)Seconds * Units.NsPerSecond +
        (Int128)Milliseconds * Units.NsPerMillisecond + (Int128)Microseconds * 1000 + (Int128)Nanoseconds;

    public DateDuration DateDuration => new((long)Years, (long)Months, (long)Weeks, (long)Days);

    /// <summary>ToInternalDurationRecord.</summary>
    public InternalDuration ToInternal() => new(DateDuration, TimeDuration);

    /// <summary>ToInternalDurationRecordWith24HourDays.</summary>
    public InternalDuration ToInternalWith24HourDays() =>
        new(new DateDuration((long)Years, (long)Months, (long)Weeks, 0), TimeDuration + (Int128)Days * Units.NsPerDay);

    /// <summary>ToDateDurationRecordWithoutTime.</summary>
    public DateDuration ToDateDurationWithoutTime()
    {
        InternalDuration internalDuration = ToInternalWith24HourDays();
        long days = (long)(internalDuration.Time / Units.NsPerDay);
        return DateDuration.Create(internalDuration.Date.Years, internalDuration.Date.Months, internalDuration.Date.Weeks, days);
    }

    /// <summary>TemporalDurationFromInternal.</summary>
    public static Duration FromInternal(in InternalDuration internalDuration, Unit largestUnit)
    {
        Int128 time = internalDuration.Time;
        int sign = TimeDurations.Sign(time);
        Int128 ns = Int128.Abs(time);
        Int128 days = 0, hours = 0, minutes = 0, seconds = 0, ms = 0, us = 0;
        switch (largestUnit)
        {
            case Unit.Year or Unit.Month or Unit.Week or Unit.Day:
                us = ns / 1000; ns %= 1000;
                ms = us / 1000; us %= 1000;
                seconds = ms / 1000; ms %= 1000;
                minutes = seconds / 60; seconds %= 60;
                hours = minutes / 60; minutes %= 60;
                days = hours / 24; hours %= 24;
                break;
            case Unit.Hour:
                us = ns / 1000; ns %= 1000;
                ms = us / 1000; us %= 1000;
                seconds = ms / 1000; ms %= 1000;
                minutes = seconds / 60; seconds %= 60;
                hours = minutes / 60; minutes %= 60;
                break;
            case Unit.Minute:
                us = ns / 1000; ns %= 1000;
                ms = us / 1000; us %= 1000;
                seconds = ms / 1000; ms %= 1000;
                minutes = seconds / 60; seconds %= 60;
                break;
            case Unit.Second:
                us = ns / 1000; ns %= 1000;
                ms = us / 1000; us %= 1000;
                seconds = ms / 1000; ms %= 1000;
                break;
            case Unit.Millisecond:
                us = ns / 1000; ns %= 1000;
                ms = us / 1000; us %= 1000;
                break;
            case Unit.Microsecond:
                us = ns / 1000; ns %= 1000;
                break;
        }
        DateDuration date = internalDuration.Date;
        return Create(date.Years, date.Months, date.Weeks, (double)(date.Days + days * sign), (double)(hours * sign),
            (double)(minutes * sign), (double)(seconds * sign), (double)(ms * sign), (double)(us * sign),
            (double)(ns * sign));
    }

    public static Duration FromDateDuration(in DateDuration d) => Create(d.Years, d.Months, d.Weeks, d.Days, 0, 0, 0, 0, 0, 0);

    /// <summary>A partial duration record, merged over this duration (Duration.prototype.with).</summary>
    public static Duration FromPartial(in PartialDuration p) => Create(p.Years ?? 0, p.Months ?? 0, p.Weeks ?? 0,
        p.Days ?? 0, p.Hours ?? 0, p.Minutes ?? 0, p.Seconds ?? 0, p.Milliseconds ?? 0, p.Microseconds ?? 0,
        p.Nanoseconds ?? 0);

    // ---- Arithmetic without relativeTo ------------------------------------------------------

    /// <summary>AddDurations (the checks and the time-only arithmetic).</summary>
    public Duration Add(Duration other)
    {
        Unit largestUnit1 = DefaultLargestUnit();
        Unit largestUnit2 = other.DefaultLargestUnit();
        Unit largestUnit = Units.Larger(largestUnit1, largestUnit2);
        if (Units.IsCalendarUnit(largestUnit))
            throw TemporalError.Range("Cannot add durations with years, months or weeks without relativeTo.");
        InternalDuration d1 = ToInternalWith24HourDays();
        InternalDuration d2 = other.ToInternalWith24HourDays();
        Int128 timeResult = TimeDurations.Add(d1.Time, d2.Time);
        return FromInternal(new InternalDuration(default, timeResult), largestUnit);
    }

    public Duration Subtract(Duration other) => Add(other.Negated());

    // ---- Formatting -------------------------------------------------------------------------

    /// <summary>Temporal.Duration.prototype.toString after option processing (steps 8-17).</summary>
    public string ToString(Precision digits, Unit? smallestUnit, RoundingMode roundingMode)
    {
        if (smallestUnit is Unit.Hour or Unit.Minute) throw TemporalError.Range("smallestUnit must be a second or smaller unit.");
        SecondsStringPrecision precision = Units.ToSecondsStringPrecision(smallestUnit, digits);
        if (precision.Unit == Unit.Nanosecond && precision.Increment == 1) return TemporalDurationToString(this, precision.Precision);
        Unit largestUnit = DefaultLargestUnit();
        InternalDuration internalDuration = ToInternal();
        Int128 timeDuration = TimeDurations.Round(internalDuration.Time, precision.Increment, precision.Unit, roundingMode);
        internalDuration = new InternalDuration(internalDuration.Date, timeDuration);
        Unit roundedLargestUnit = Units.Larger(largestUnit, Unit.Second);
        Duration roundedDuration = FromInternal(internalDuration, roundedLargestUnit);
        return TemporalDurationToString(roundedDuration, precision.Precision);
    }

    public override string ToString() => TemporalDurationToString(this, Precision.Auto);

    static void AppendInteger(StringBuilder sb, double value)
    {
        value = Math.Abs(value);
        if (value < 1e15) sb.Append(((long)value).ToString(CultureInfo.InvariantCulture));
        else sb.Append(new BigInteger(value).ToString(CultureInfo.InvariantCulture));
    }

    /// <summary>TemporalDurationToString.</summary>
    public static string TemporalDurationToString(Duration d, Precision precision)
    {
        int sign = d.Sign;
        var sb = new StringBuilder();
        if (sign < 0) sb.Append('-');
        sb.Append('P');
        if (d.Years != 0) { AppendInteger(sb, d.Years); sb.Append('Y'); }
        if (d.Months != 0) { AppendInteger(sb, d.Months); sb.Append('M'); }
        if (d.Weeks != 0) { AppendInteger(sb, d.Weeks); sb.Append('W'); }
        if (d.Days != 0) { AppendInteger(sb, d.Days); sb.Append('D'); }
        var time = new StringBuilder();
        if (d.Hours != 0) { AppendInteger(time, d.Hours); time.Append('H'); }
        if (d.Minutes != 0) { AppendInteger(time, d.Minutes); time.Append('M'); }
        bool zeroMinutesAndHigher = d.DefaultLargestUnit() >= Unit.Second;
        Int128 secondsDuration = (Int128)d.Seconds * Units.NsPerSecond + (Int128)d.Milliseconds * Units.NsPerMillisecond +
                                 (Int128)d.Microseconds * 1000 + (Int128)d.Nanoseconds;
        if (secondsDuration != 0 || zeroMinutesAndHigher || precision.Digits is not null)
        {
            Int128 abs = Int128.Abs(secondsDuration);
            time.Append((abs / Units.NsPerSecond).ToString());
            Formatting.AppendFractionalSeconds(time, (long)(abs % Units.NsPerSecond), precision);
            time.Append('S');
        }
        if (time.Length > 0)
        {
            sb.Append('T');
            sb.Append(time);
        }
        return sb.ToString();
    }
}

/// <summary>temporal_rs::PartialDuration.</summary>
public struct PartialDuration
{
    public double? Years, Months, Weeks, Days, Hours, Minutes, Seconds, Milliseconds, Microseconds, Nanoseconds;
}
