// Differences and rounding relative to a starting point: DifferenceISODateTime,
// DifferencePlainDateTimeWithRounding / WithTotal, DifferenceZonedDateTime,
// DifferenceZonedDateTimeWithRounding / WithTotal, RoundRelativeDuration,
// NudgeToCalendarUnit, NudgeToZonedTime, NudgeToDayOrTime,
// BubbleRelativeDuration, TotalRelativeDuration, AddZonedDateTime, and
// Temporal.Duration's round, total and compare (temporal_rs's
// duration/normalized and rounding modules).
using System.Numerics;

namespace V8Sharp.Temporal;

/// <summary>temporal_rs::RelativeTo: a PlainDate or a ZonedDateTime (or neither).</summary>
public readonly record struct RelativeTo(PlainDate? Date, ZonedDateTime? Zoned);

public static class RelativeRounding
{
    readonly record struct Nudge(InternalDuration Duration, Int128 NudgedEpochNs, bool DidExpandCalendarUnit);

    /// <summary>AddInstant.</summary>
    public static Int128 AddInstant(Int128 epochNs, Int128 timeDuration)
    {
        Int128 result = epochNs + timeDuration;
        if (!IsoCalendar.IsValidEpochNanoseconds(result)) throw TemporalError.Range("Instant out of range.");
        return result;
    }

    /// <summary>AddZonedDateTime.</summary>
    public static Int128 AddZonedDateTime(Int128 epochNs, TimeZone timeZone, Calendar calendar, in InternalDuration duration,
        Overflow overflow)
    {
        if (duration.Date.Sign == 0) return AddInstant(epochNs, duration.Time);
        IsoDateTime isoDateTime = timeZone.GetISODateTimeFor(epochNs);
        IsoDate addedDate = IsoCalendar.DateAdd(isoDateTime.Date, duration.Date, overflow);
        var intermediate = new IsoDateTime(addedDate, isoDateTime.Time);
        if (!IsoCalendar.ISODateTimeWithinLimits(intermediate)) throw TemporalError.Range("Date-time out of range.");
        Int128 intermediateNs = timeZone.GetEpochNanosecondsFor(intermediate, Disambiguation.Compatible);
        return AddInstant(intermediateNs, duration.Time);
    }

    /// <summary>DifferenceISODateTime.</summary>
    public static InternalDuration DifferenceISODateTime(in IsoDateTime one, in IsoDateTime two, Calendar calendar, Unit largestUnit)
    {
        Int128 timeDuration = IsoCalendar.DifferenceTime(one.Time, two.Time);
        int timeSign = TimeDurations.Sign(timeDuration);
        int dateSign = IsoCalendar.CompareISODate(two.Date, one.Date);
        IsoDate adjustedDate = two.Date;
        if (timeSign == dateSign)
        {
            adjustedDate = IsoCalendar.AddDays(adjustedDate, timeSign);
            timeDuration = TimeDurations.Add24HourDays(timeDuration, -timeSign);
        }
        Unit dateLargestUnit = Units.Larger(Unit.Day, largestUnit);
        DateDuration dateDifference = IsoCalendar.DateUntil(one.Date, adjustedDate, dateLargestUnit);
        if (largestUnit != dateLargestUnit)
        {
            timeDuration = TimeDurations.Add24HourDays(timeDuration, dateDifference.Days);
            dateDifference = dateDifference with { Days = 0 };
        }
        return new InternalDuration(dateDifference, timeDuration);
    }

    /// <summary>DifferencePlainDateTimeWithRounding.</summary>
    public static InternalDuration DifferencePlainDateTimeWithRounding(in IsoDateTime one, in IsoDateTime two, Calendar calendar,
        Unit largestUnit, long increment, Unit smallestUnit, RoundingMode mode)
    {
        if (one.CompareTo(two) == 0) return default;
        if (!IsoCalendar.ISODateTimeWithinLimits(one) || !IsoCalendar.ISODateTimeWithinLimits(two))
            throw TemporalError.Range("Date-time out of range.");
        InternalDuration diff = DifferenceISODateTime(one, two, calendar, largestUnit);
        if (smallestUnit == Unit.Nanosecond && increment == 1) return diff;
        Int128 originEpochNs = IsoCalendar.GetUTCEpochNanoseconds(one);
        Int128 destEpochNs = IsoCalendar.GetUTCEpochNanoseconds(two);
        return RoundRelativeDuration(diff, originEpochNs, destEpochNs, one, null, calendar, largestUnit, increment, smallestUnit,
            mode);
    }

    /// <summary>DifferencePlainDateTimeWithTotal.</summary>
    public static double DifferencePlainDateTimeWithTotal(in IsoDateTime one, in IsoDateTime two, Calendar calendar, Unit unit)
    {
        if (one.CompareTo(two) == 0) return 0;
        if (!IsoCalendar.ISODateTimeWithinLimits(one) || !IsoCalendar.ISODateTimeWithinLimits(two))
            throw TemporalError.Range("Date-time out of range.");
        InternalDuration diff = DifferenceISODateTime(one, two, calendar, unit);
        if (unit == Unit.Nanosecond) return (double)diff.Time;
        Int128 originEpochNs = IsoCalendar.GetUTCEpochNanoseconds(one);
        Int128 destEpochNs = IsoCalendar.GetUTCEpochNanoseconds(two);
        return TotalRelativeDuration(diff, originEpochNs, destEpochNs, one, null, calendar, unit);
    }

    /// <summary>DifferenceZonedDateTime.</summary>
    public static InternalDuration DifferenceZonedDateTime(Int128 ns1, Int128 ns2, TimeZone timeZone, Calendar calendar,
        Unit largestUnit)
    {
        if (ns1 == ns2) return default;
        IsoDateTime startDateTime = timeZone.GetISODateTimeFor(ns1);
        IsoDateTime endDateTime = timeZone.GetISODateTimeFor(ns2);
        if (IsoCalendar.CompareISODate(startDateTime.Date, endDateTime.Date) == 0)
        {
            return new InternalDuration(default, ns2 - ns1);
        }
        int sign = ns2 - ns1 < 0 ? -1 : 1;
        int maxDayCorrection = sign == 1 ? 2 : 1;
        int dayCorrection = 0;
        Int128 timeDuration = IsoCalendar.DifferenceTime(startDateTime.Time, endDateTime.Time);
        if (TimeDurations.Sign(timeDuration) == -sign) dayCorrection++;
        bool success = false;
        IsoDateTime intermediateDateTime = default;
        while (dayCorrection <= maxDayCorrection && !success)
        {
            IsoDate intermediateDate = IsoCalendar.AddDays(endDateTime.Date, -dayCorrection * sign);
            intermediateDateTime = new IsoDateTime(intermediateDate, startDateTime.Time);
            Int128 intermediateNs = timeZone.GetEpochNanosecondsFor(intermediateDateTime, Disambiguation.Compatible);
            timeDuration = ns2 - intermediateNs;
            int timeSign = TimeDurations.Sign(timeDuration);
            if (sign != -timeSign) success = true;
            dayCorrection++;
        }
        if (!success) throw TemporalError.Range("Could not compute the difference.");
        Unit dateLargestUnit = Units.Larger(largestUnit, Unit.Day);
        DateDuration dateDifference = IsoCalendar.DateUntil(startDateTime.Date, intermediateDateTime.Date, dateLargestUnit);
        return new InternalDuration(dateDifference, timeDuration);
    }

    /// <summary>DifferenceZonedDateTimeWithRounding.</summary>
    public static InternalDuration DifferenceZonedDateTimeWithRounding(Int128 ns1, Int128 ns2, TimeZone timeZone, Calendar calendar,
        Unit largestUnit, long increment, Unit smallestUnit, RoundingMode mode)
    {
        InternalDuration diff = DifferenceZonedDateTime(ns1, ns2, timeZone, calendar, largestUnit);
        if (smallestUnit == Unit.Nanosecond && increment == 1) return diff;
        IsoDateTime dateTime = timeZone.GetISODateTimeFor(ns1);
        return RoundRelativeDuration(diff, ns1, ns2, dateTime, timeZone, calendar, largestUnit, increment, smallestUnit, mode);
    }

    /// <summary>DifferenceZonedDateTimeWithTotal.</summary>
    public static double DifferenceZonedDateTimeWithTotal(Int128 ns1, Int128 ns2, TimeZone timeZone, Calendar calendar, Unit unit)
    {
        if (!Units.IsDateUnit(unit)) return TimeDurations.Total(ns2 - ns1, unit);
        InternalDuration difference = DifferenceZonedDateTime(ns1, ns2, timeZone, calendar, unit);
        IsoDateTime dateTime = timeZone.GetISODateTimeFor(ns1);
        return TotalRelativeDuration(difference, ns1, ns2, dateTime, timeZone, calendar, unit);
    }

    static Int128 EpochNsFor(in IsoDateTime dt, TimeZone? timeZone) =>
        timeZone is null ? IsoCalendar.GetUTCEpochNanoseconds(dt) : timeZone.GetEpochNanosecondsFor(dt, Disambiguation.Compatible);

    /// <summary>NudgeToCalendarUnit: returns the nudge and the exact total as a rational.</summary>
    static Nudge NudgeToCalendarUnit(int sign, in InternalDuration duration, Int128 originEpochNs, Int128 destEpochNs,
        in IsoDateTime isoDateTime, TimeZone? timeZone, Calendar calendar, long increment, Unit unit, RoundingMode mode,
        out BigInteger totalNum, out BigInteger totalDen)
    {
        bool didExpandCalendarUnit = false;
        long r1, r2;
        DateDuration startDuration, endDuration;
        DateDuration date = duration.Date;
        switch (unit)
        {
            case Unit.Year:
            {
                long years = (long)Rounding.RoundToIncrement(date.Years, increment, RoundingMode.Trunc);
                r1 = years;
                r2 = years + increment * sign;
                startDuration = DateDuration.Create(r1, 0, 0, 0);
                endDuration = DateDuration.Create(r2, 0, 0, 0);
                break;
            }
            case Unit.Month:
            {
                long months = (long)Rounding.RoundToIncrement(date.Months, increment, RoundingMode.Trunc);
                r1 = months;
                r2 = months + increment * sign;
                startDuration = date.Adjust(0, 0, r1);
                endDuration = date.Adjust(0, 0, r2);
                break;
            }
            case Unit.Week:
            {
                DateDuration yearsMonths = date.Adjust(0, 0);
                IsoDate weeksStart = IsoCalendar.DateAdd(isoDateTime.Date, yearsMonths, Overflow.Constrain);
                IsoDate weeksEnd = IsoCalendar.AddDays(weeksStart, date.Days);
                DateDuration untilResult = IsoCalendar.DateUntil(weeksStart, weeksEnd, Unit.Week);
                long weeks = (long)Rounding.RoundToIncrement(date.Weeks + untilResult.Weeks, increment, RoundingMode.Trunc);
                r1 = weeks;
                r2 = weeks + increment * sign;
                startDuration = date.Adjust(0, r1);
                endDuration = date.Adjust(0, r2);
                break;
            }
            default:
            {
                long days = (long)Rounding.RoundToIncrement(date.Days, increment, RoundingMode.Trunc);
                r1 = days;
                r2 = days + increment * sign;
                startDuration = date.Adjust(r1);
                endDuration = date.Adjust(r2);
                break;
            }
        }
        IsoDate start = IsoCalendar.DateAdd(isoDateTime.Date, startDuration, Overflow.Constrain);
        IsoDate end = IsoCalendar.DateAdd(isoDateTime.Date, endDuration, Overflow.Constrain);
        var startDateTime = new IsoDateTime(start, isoDateTime.Time);
        var endDateTime = new IsoDateTime(end, isoDateTime.Time);
        Int128 startEpochNs = EpochNsFor(startDateTime, timeZone);
        Int128 endEpochNs = EpochNsFor(endDateTime, timeZone);
        if (endEpochNs == startEpochNs) throw TemporalError.Range("Rounding over a zero-length span.");
        Int128 numerator = destEpochNs - startEpochNs;
        Int128 denominator = endEpochNs - startEpochNs;
        // total = r1 + numerator / denominator × increment × sign
        BigInteger den = denominator;
        BigInteger num = (BigInteger)r1 * den + (BigInteger)numerator * increment * sign;
        if (den.Sign < 0)
        {
            den = -den;
            num = -num;
        }
        totalNum = num;
        totalDen = den;
        UnsignedRoundingMode unsignedMode = Rounding.GetUnsigned(mode, sign < 0);
        // |total| lies in [|r1|, |r2|]; progress = |numerator| / |denominator|.
        Int128 absNum = Int128.Abs(numerator);
        Int128 absDen = Int128.Abs(denominator);
        bool roundUp;
        if (absNum == absDen)
        {
            roundUp = true;
        }
        else
        {
            long r1Units = Math.Abs(r1) / increment;
            roundUp = Rounding.RoundsUp(absNum, absDen, unsignedMode, (r1Units & 1) == 0);
        }
        InternalDuration resultDuration;
        Int128 nudgedEpochNs;
        if (roundUp)
        {
            didExpandCalendarUnit = true;
            resultDuration = new InternalDuration(endDuration, 0);
            nudgedEpochNs = endEpochNs;
        }
        else
        {
            resultDuration = new InternalDuration(startDuration, 0);
            nudgedEpochNs = startEpochNs;
        }
        return new Nudge(resultDuration, nudgedEpochNs, didExpandCalendarUnit);
    }

    /// <summary>NudgeToZonedTime.</summary>
    static Nudge NudgeToZonedTime(int sign, in InternalDuration duration, in IsoDateTime isoDateTime, TimeZone timeZone,
        Calendar calendar, long increment, Unit unit, RoundingMode mode)
    {
        IsoDate start = IsoCalendar.DateAdd(isoDateTime.Date, duration.Date, Overflow.Constrain);
        var startDateTime = new IsoDateTime(start, isoDateTime.Time);
        IsoDate endDate = IsoCalendar.AddDays(start, sign);
        var endDateTime = new IsoDateTime(endDate, isoDateTime.Time);
        Int128 startEpochNs = timeZone.GetEpochNanosecondsFor(startDateTime, Disambiguation.Compatible);
        Int128 endEpochNs = timeZone.GetEpochNanosecondsFor(endDateTime, Disambiguation.Compatible);
        Int128 daySpan = endEpochNs - startEpochNs;
        Int128 unitIncrement = (Int128)Units.Length(unit) * increment;
        Int128 roundedTimeDuration = TimeDurations.RoundToIncrement(duration.Time, unitIncrement, mode);
        Int128 beyondDaySpan = TimeDurations.Add(roundedTimeDuration, -daySpan);
        bool didRoundBeyondDay;
        int dayDelta;
        Int128 nudgedEpochNs;
        if (TimeDurations.Sign(beyondDaySpan) != -sign)
        {
            didRoundBeyondDay = true;
            dayDelta = sign;
            roundedTimeDuration = TimeDurations.RoundToIncrement(beyondDaySpan, unitIncrement, mode);
            nudgedEpochNs = roundedTimeDuration + endEpochNs;
        }
        else
        {
            didRoundBeyondDay = false;
            dayDelta = 0;
            nudgedEpochNs = roundedTimeDuration + startEpochNs;
        }
        DateDuration dateDuration = duration.Date.Adjust(duration.Date.Days + dayDelta);
        return new Nudge(new InternalDuration(dateDuration, roundedTimeDuration), nudgedEpochNs, didRoundBeyondDay);
    }

    /// <summary>NudgeToDayOrTime.</summary>
    static Nudge NudgeToDayOrTime(in InternalDuration duration, Int128 destEpochNs, Unit largestUnit, long increment,
        Unit smallestUnit, RoundingMode mode)
    {
        Int128 timeDuration = TimeDurations.Add24HourDays(duration.Time, duration.Date.Days);
        Int128 unitLength = Units.Length(smallestUnit);
        Int128 roundedTime = TimeDurations.RoundToIncrement(timeDuration, unitLength * increment, mode);
        Int128 diffTime = roundedTime - timeDuration;
        Int128 wholeDays = timeDuration / Units.NsPerDay;
        Int128 roundedWholeDays = roundedTime / Units.NsPerDay;
        Int128 dayDelta = roundedWholeDays - wholeDays;
        int dayDeltaSign = dayDelta < 0 ? -1 : dayDelta > 0 ? 1 : 0;
        bool didExpandDays = dayDeltaSign == TimeDurations.Sign(timeDuration);
        Int128 nudgedEpochNs = diffTime + destEpochNs;
        long days = 0;
        Int128 remainder = roundedTime;
        if (Units.IsDateUnit(largestUnit))
        {
            days = (long)roundedWholeDays;
            remainder = roundedTime - roundedWholeDays * Units.NsPerDay;
        }
        DateDuration dateDuration = duration.Date.Adjust(days);
        return new Nudge(new InternalDuration(dateDuration, remainder), nudgedEpochNs, didExpandDays);
    }

    /// <summary>BubbleRelativeDuration.</summary>
    static InternalDuration BubbleRelativeDuration(int sign, InternalDuration duration, Int128 nudgedEpochNs,
        in IsoDateTime isoDateTime, TimeZone? timeZone, Calendar calendar, Unit largestUnit, Unit smallestUnit)
    {
        if (smallestUnit == largestUnit) return duration;
        int largestUnitIndex = (int)largestUnit;
        int smallestUnitIndex = (int)smallestUnit;
        int unitIndex = smallestUnitIndex - 1;
        bool done = false;
        while (unitIndex >= largestUnitIndex && !done)
        {
            var unit = (Unit)unitIndex;
            if (unit != Unit.Week || largestUnit == Unit.Week)
            {
                DateDuration endDuration;
                if (unit == Unit.Year)
                {
                    long years = duration.Date.Years + sign;
                    endDuration = DateDuration.Create(years, 0, 0, 0);
                }
                else if (unit == Unit.Month)
                {
                    long months = duration.Date.Months + sign;
                    endDuration = duration.Date.Adjust(0, 0, months);
                }
                else
                {
                    long weeks = duration.Date.Weeks + sign;
                    endDuration = duration.Date.Adjust(0, weeks);
                }
                IsoDate end = IsoCalendar.DateAdd(isoDateTime.Date, endDuration, Overflow.Constrain);
                var endDateTime = new IsoDateTime(end, isoDateTime.Time);
                Int128 endEpochNs = EpochNsFor(endDateTime, timeZone);
                Int128 beyondEnd = nudgedEpochNs - endEpochNs;
                int beyondEndSign = TimeDurations.Sign(beyondEnd);
                if (beyondEndSign != -sign) duration = new InternalDuration(endDuration, 0);
                else done = true;
            }
            unitIndex--;
        }
        return duration;
    }

    /// <summary>RoundRelativeDuration.</summary>
    public static InternalDuration RoundRelativeDuration(InternalDuration duration, Int128 originEpochNs, Int128 destEpochNs,
        in IsoDateTime isoDateTime, TimeZone? timeZone, Calendar calendar, Unit largestUnit, long increment, Unit smallestUnit,
        RoundingMode mode)
    {
        bool irregularLengthUnit = Units.IsCalendarUnit(smallestUnit) || (timeZone is not null && smallestUnit == Unit.Day);
        int sign = duration.Sign < 0 ? -1 : 1;
        Nudge nudged;
        if (irregularLengthUnit)
        {
            nudged = NudgeToCalendarUnit(sign, duration, originEpochNs, destEpochNs, isoDateTime, timeZone, calendar, increment,
                smallestUnit, mode, out _, out _);
        }
        else if (timeZone is not null)
        {
            nudged = NudgeToZonedTime(sign, duration, isoDateTime, timeZone, calendar, increment, smallestUnit, mode);
        }
        else
        {
            nudged = NudgeToDayOrTime(duration, destEpochNs, largestUnit, increment, smallestUnit, mode);
        }
        duration = nudged.Duration;
        if (nudged.DidExpandCalendarUnit && smallestUnit != Unit.Week)
        {
            Unit startUnit = Units.Larger(smallestUnit, Unit.Day);
            duration = BubbleRelativeDuration(sign, duration, nudged.NudgedEpochNs, isoDateTime, timeZone, calendar, largestUnit,
                startUnit);
        }
        return duration;
    }

    /// <summary>TotalRelativeDuration.</summary>
    public static double TotalRelativeDuration(in InternalDuration duration, Int128 originEpochNs, Int128 destEpochNs,
        in IsoDateTime isoDateTime, TimeZone? timeZone, Calendar calendar, Unit unit)
    {
        if (Units.IsCalendarUnit(unit) || (timeZone is not null && unit == Unit.Day))
        {
            int sign = duration.Sign < 0 ? -1 : 1;
            NudgeToCalendarUnit(sign, duration, originEpochNs, destEpochNs, isoDateTime, timeZone, calendar, 1, unit,
                RoundingMode.Trunc, out BigInteger num, out BigInteger den);
            return Rounding.RationalToDouble(num, den);
        }
        Int128 timeDuration = TimeDurations.Add24HourDays(duration.Time, duration.Date.Days);
        return TimeDurations.Total(timeDuration, unit);
    }

    // ---- Temporal.Duration.prototype.round / total and Temporal.Duration.compare -------------

    /// <summary>Temporal.Duration.prototype.round after option processing.</summary>
    public static Duration Round(Duration duration, in RoundingOptions options, in RelativeTo relativeTo)
    {
        bool smallestUnitPresent = true;
        bool largestUnitPresent = true;
        long increment = options.Increment ?? 1;
        RoundingMode mode = options.RoundingMode ?? RoundingMode.HalfExpand;
        Unit? largestOption = options.LargestUnit;
        if (largestOption is Unit lo && lo != Unit.Auto) Units.ValidateUnitGroup(lo, UnitGroup.DateTime);
        Unit smallestUnit;
        if (options.SmallestUnit is Unit su)
        {
            if (su == Unit.Auto) throw TemporalError.Range("smallestUnit cannot be auto.");
            smallestUnit = su;
        }
        else
        {
            smallestUnitPresent = false;
            smallestUnit = Unit.Nanosecond;
        }
        Unit existingLargestUnit = duration.DefaultLargestUnit();
        Unit defaultLargestUnit = Units.Larger(existingLargestUnit, smallestUnit);
        Unit largestUnit;
        if (largestOption is null)
        {
            largestUnitPresent = false;
            largestUnit = defaultLargestUnit;
        }
        else if (largestOption == Unit.Auto)
        {
            largestUnit = defaultLargestUnit;
        }
        else
        {
            largestUnit = largestOption.Value;
        }
        if (!smallestUnitPresent && !largestUnitPresent) throw TemporalError.Range("One of smallestUnit or largestUnit is required.");
        if (Units.Larger(largestUnit, smallestUnit) != largestUnit) throw TemporalError.Range("smallestUnit is larger than largestUnit.");
        if (Units.MaximumRoundingIncrement(smallestUnit) is long maximum) Units.ValidateRoundingIncrement(increment, maximum, false);
        if (increment > 1 && largestUnit != smallestUnit && Units.IsDateUnit(smallestUnit))
            throw TemporalError.Range("roundingIncrement must be 1 when rounding to a date unit with a larger largestUnit.");
        if (relativeTo.Zoned is { } zoned)
        {
            InternalDuration internalDuration = duration.ToInternal();
            TimeZone timeZone = zoned.TimeZone;
            Calendar calendar = zoned.Calendar;
            Int128 relativeEpochNs = zoned.EpochNanoseconds;
            Int128 targetEpochNs = AddZonedDateTime(relativeEpochNs, timeZone, calendar, internalDuration, Overflow.Constrain);
            internalDuration = DifferenceZonedDateTimeWithRounding(relativeEpochNs, targetEpochNs, timeZone, calendar, largestUnit,
                increment, smallestUnit, mode);
            if (Units.IsDateUnit(largestUnit)) largestUnit = Unit.Hour;
            return Duration.FromInternal(internalDuration, largestUnit);
        }
        if (relativeTo.Date is { } plainRelativeTo)
        {
            InternalDuration internalDuration = duration.ToInternalWith24HourDays();
            (long days, IsoTime targetTime) = IsoCalendar.AddTime(IsoTime.Midnight, internalDuration.Time);
            DateDuration dateDuration = internalDuration.Date.Adjust(days);
            IsoDate targetDate = IsoCalendar.DateAdd(plainRelativeTo.IsoDate, dateDuration, Overflow.Constrain);
            var isoDateTime = new IsoDateTime(plainRelativeTo.IsoDate, IsoTime.Midnight);
            var targetDateTime = new IsoDateTime(targetDate, targetTime);
            internalDuration = DifferencePlainDateTimeWithRounding(isoDateTime, targetDateTime, plainRelativeTo.Calendar,
                largestUnit, increment, smallestUnit, mode);
            return Duration.FromInternal(internalDuration, largestUnit);
        }
        if (Units.IsCalendarUnit(existingLargestUnit) || Units.IsCalendarUnit(largestUnit))
            throw TemporalError.Range("A relativeTo is required to round a duration with calendar units.");
        InternalDuration id = duration.ToInternalWith24HourDays();
        if (smallestUnit == Unit.Day)
        {
            Int128 roundedNs = Rounding.RoundToIncrement(id.Time, (Int128)increment * Units.NsPerDay, mode);
            long days = (long)(roundedNs / Units.NsPerDay);
            DateDuration dateDuration = DateDuration.Create(0, 0, 0, days);
            id = new InternalDuration(dateDuration, 0);
        }
        else
        {
            Int128 timeDuration = TimeDurations.Round(id.Time, increment, smallestUnit, mode);
            id = new InternalDuration(default, timeDuration);
        }
        return Duration.FromInternal(id, largestUnit);
    }

    /// <summary>Temporal.Duration.prototype.total after option processing.</summary>
    public static double Total(Duration duration, Unit unit, in RelativeTo relativeTo)
    {
        if (relativeTo.Zoned is { } zoned)
        {
            InternalDuration internalDuration = duration.ToInternal();
            Int128 relativeEpochNs = zoned.EpochNanoseconds;
            Int128 targetEpochNs = AddZonedDateTime(relativeEpochNs, zoned.TimeZone, zoned.Calendar, internalDuration,
                Overflow.Constrain);
            return DifferenceZonedDateTimeWithTotal(relativeEpochNs, targetEpochNs, zoned.TimeZone, zoned.Calendar, unit);
        }
        if (relativeTo.Date is { } plainRelativeTo)
        {
            InternalDuration internalDuration = duration.ToInternalWith24HourDays();
            (long days, IsoTime targetTime) = IsoCalendar.AddTime(IsoTime.Midnight, internalDuration.Time);
            DateDuration dateDuration = internalDuration.Date.Adjust(days);
            IsoDate targetDate = IsoCalendar.DateAdd(plainRelativeTo.IsoDate, dateDuration, Overflow.Constrain);
            var isoDateTime = new IsoDateTime(plainRelativeTo.IsoDate, IsoTime.Midnight);
            var targetDateTime = new IsoDateTime(targetDate, targetTime);
            return DifferencePlainDateTimeWithTotal(isoDateTime, targetDateTime, plainRelativeTo.Calendar, unit);
        }
        Unit largestUnit = duration.DefaultLargestUnit();
        if (Units.IsCalendarUnit(largestUnit) || Units.IsCalendarUnit(unit))
            throw TemporalError.Range("A relativeTo is required for calendar units.");
        InternalDuration id = duration.ToInternalWith24HourDays();
        return TimeDurations.Total(id.Time, unit);
    }

    /// <summary>DateDurationDays.</summary>
    static long DateDurationDays(in DateDuration dateDuration, PlainDate plainRelativeTo)
    {
        DateDuration yearsMonthsWeeks = dateDuration.Adjust(0);
        if (yearsMonthsWeeks.Sign == 0) return dateDuration.Days;
        IsoDate later = IsoCalendar.DateAdd(plainRelativeTo.IsoDate, yearsMonthsWeeks, Overflow.Constrain);
        long epochDays1 = IsoCalendar.EpochDays(plainRelativeTo.IsoDate);
        long epochDays2 = IsoCalendar.EpochDays(later);
        return dateDuration.Days + (epochDays2 - epochDays1);
    }

    /// <summary>Temporal.Duration.compare after option processing.</summary>
    public static int Compare(Duration one, Duration two, in RelativeTo relativeTo)
    {
        if (one.FieldsEqual(two)) return 0;
        Unit largestUnit1 = one.DefaultLargestUnit();
        Unit largestUnit2 = two.DefaultLargestUnit();
        InternalDuration duration1 = one.ToInternal();
        InternalDuration duration2 = two.ToInternal();
        if (relativeTo.Zoned is { } zoned && (Units.IsDateUnit(largestUnit1) || Units.IsDateUnit(largestUnit2)))
        {
            Int128 after1 = AddZonedDateTime(zoned.EpochNanoseconds, zoned.TimeZone, zoned.Calendar, duration1, Overflow.Constrain);
            Int128 after2 = AddZonedDateTime(zoned.EpochNanoseconds, zoned.TimeZone, zoned.Calendar, duration2, Overflow.Constrain);
            return after1.CompareTo(after2);
        }
        long days1, days2;
        if (Units.IsCalendarUnit(largestUnit1) || Units.IsCalendarUnit(largestUnit2))
        {
            if (relativeTo.Date is not { } plainRelativeTo)
                throw TemporalError.Range("A relativeTo is required to compare durations with calendar units.");
            days1 = DateDurationDays(duration1.Date, plainRelativeTo);
            days2 = DateDurationDays(duration2.Date, plainRelativeTo);
        }
        else
        {
            days1 = (long)one.Days;
            days2 = (long)two.Days;
        }
        Int128 timeDuration1 = TimeDurations.Add24HourDays(duration1.Time, days1);
        Int128 timeDuration2 = TimeDurations.Add24HourDays(duration2.Time, days2);
        return timeDuration1.CompareTo(timeDuration2);
    }
}
