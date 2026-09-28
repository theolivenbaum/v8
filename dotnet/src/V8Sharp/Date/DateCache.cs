// Port of src/date/date.{h,cc}: DateCache (the local time zone offset cache,
// year/month/day cache, time zone names), MakeDay/MakeTime/MakeDate,
// ToDateString and ParseDateTimeString; and of the POSIX time zone cache that
// V8 uses without ICU (src/base/platform/platform-posix.cc,
// platform-posix-time.cc: PosixDefaultTimezoneCache).
using System.Runtime.CompilerServices;
using V8Sharp.Base.Numbers;

namespace V8Sharp.Date;

/// <summary>V8's DateCache (one per isolate, Isolate::date_cache()).</summary>
public class DateCache
{
    public const int kMsPerMin = 60 * 1000;
    public const int kSecPerDay = 24 * 60 * 60;
    public const long kMsPerDay = kSecPerDay * 1000L;
    public const long kMsPerMonth = kMsPerDay * 30;

    // The largest time that can be passed to OS date-time library functions.
    public const int kMaxEpochTimeInSec = int.MaxValue;
    public const long kMaxEpochTimeInMs = (long)int.MaxValue * 1000;

    // The largest time that can be stored in JSDate.
    public const long kMaxTimeInMs = 864000000L * 10000000;

    // Conservative upper bound on time that can be stored in JSDate
    // before UTC conversion.
    public const long kMaxTimeBeforeUTCInMs = kMaxTimeInMs + kMsPerMonth;

    // Sentinel that denotes an invalid local offset.
    public const int kInvalidLocalOffsetInMs = int.MaxValue;

    // The implementation relies on the fact that no time zones have more than one
    // time zone offset change (including DST offset changes) per 19 days. In
    // Egypt in 2010 they decided to suspend DST during Ramadan. This led to a
    // short interval where DST is in effect from September 10 to September 30.
    const int kDefaultTimeZoneOffsetDeltaInMs = 19 * kSecPerDay * 1000;

    const int kCacheSize = 32;

    // Stores a segment of time where time zone offset does not change.
    sealed class CacheItem
    {
        public long StartMs;
        public long EndMs;
        public int OffsetMs;
        public int LastUsed;
    }

    const int kDaysIn4Years = 4 * 365 + 1;
    const int kDaysIn100Years = 25 * kDaysIn4Years - 1;
    const int kDaysIn400Years = 4 * kDaysIn100Years + 1;
    const int kDays1970to2000 = 30 * 365 + 7;
    const int kDaysOffset = 1000 * kDaysIn400Years + 5 * kDaysIn400Years - kDays1970to2000;
    const int kYearsOffset = 400000;
    static ReadOnlySpan<byte> DaysInMonths => [31, 28, 31, 30, 31, 30, 31, 31, 30, 31, 30, 31];

    // Daylight Saving Time cache.
    readonly CacheItem[] _cache = new CacheItem[kCacheSize];
    int _cacheUsageCounter;
    CacheItem _before;
    CacheItem _after;

    int _localOffsetMs;

    // Year/Month/Day cache.
    bool _ymdValid;
    int _ymdDays;
    int _ymdYear;
    int _ymdMonth;
    int _ymdDay;

    // Timezone name cache
    string? _tzName;
    string? _dstTzName;

    readonly TimezoneCache _tzCache;

    public DateCache() : this(new PosixDefaultTimezoneCache()) { }

    public DateCache(TimezoneCache tzCache)
    {
        _tzCache = tzCache;
        for (int i = 0; i < kCacheSize; i++) _cache[i] = new CacheItem();
        _before = _cache[0];
        _after = _cache[1];
        ResetDateCache(TimezoneCache.TimeZoneDetection.kSkip);
    }

    /// <summary>Clears cached timezone information and increments the cache stamp.</summary>
    public void ResetDateCache(TimezoneCache.TimeZoneDetection timeZoneDetection)
    {
        for (int i = 0; i < kCacheSize; ++i) ClearSegment(_cache[i]);
        _cacheUsageCounter = 0;
        _before = _cache[0];
        _after = _cache[1];
        _ymdValid = false;
        _localOffsetMs = kInvalidLocalOffsetInMs;
        _tzCache.Clear(timeZoneDetection);
        _tzName = null;
        _dstTzName = null;
    }

    /// <summary>Computes floor(time_ms / kMsPerDay).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int DaysFromTime(long timeMs)
    {
        if (timeMs < 0) timeMs -= kMsPerDay - 1;
        return (int)(timeMs / kMsPerDay);
    }

    /// <summary>Computes modulo(time_ms, kMsPerDay) given that days = floor(time_ms / kMsPerDay).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int TimeInDay(long timeMs, int days) => (int)(timeMs - days * kMsPerDay);

    /// <summary>
    /// Performs the success path of the ECMA 262 TimeClip operation (when the
    /// value is within the range, truncates it to an integer). Returns false if
    /// the value is outside the range, and should be clipped to NaN.
    /// ECMA 262 - https://tc39.es/ecma262/#sec-timeclip TimeClip (time)
    /// </summary>
    public static bool TryTimeClip(ref double time)
    {
        if (-kMaxTimeInMs <= time && time <= kMaxTimeInMs)
        {
            // Inline the finite part of DoubleToInteger here, since the range check
            // already covers the non-finite checks.
            time = (time > 0 ? Math.Floor(time) : Math.Ceiling(time)) + 0.0;
            return true;
        }
        return false;
    }

    /// <summary>Given the number of days since the epoch, computes the weekday. ECMA 262 - 15.9.1.6.</summary>
    public int Weekday(int days)
    {
        int result = (days + 4) % 7;
        return result >= 0 ? result : result + 7;
    }

    public bool IsLeap(int year) => year % 4 == 0 && (year % 100 != 0 || year % 400 == 0);

    /// <summary>The local time zone name for a time (the short OS name without ICU, e.g. "EST").</summary>
    public string LocalTimezone(long timeMs)
    {
        if (timeMs < 0 || timeMs > kMaxEpochTimeInMs)
        {
            timeMs = EquivalentTime(timeMs);
        }
        bool isDst = DaylightSavingsOffsetInMs(timeMs) != 0;
        if (isDst) return _dstTzName ??= _tzCache.LocalTimezone(timeMs);
        return _tzName ??= _tzCache.LocalTimezone(timeMs);
    }

    /// <summary>ECMA 262 - 15.9.5.26.</summary>
    public int TimezoneOffset(long timeMs)
    {
        long localMs = ToLocal(timeMs);
        return (int)((timeMs - localMs) / kMsPerMin);
    }

    /// <summary>
    /// ECMA 262 - https://tc39.es/ecma262/#sec-localtime-t
    /// LocalTime(t) = t + LocalTZA(t, true)
    /// </summary>
    public long ToLocal(long timeMs) => timeMs + LocalOffsetInMs(timeMs, true);

    /// <summary>
    /// ECMA 262 - https://tc39.es/ecma262/#sec-utc-t
    /// UTC(t) = t - LocalTZA(t, false)
    /// </summary>
    public long ToUTC(long timeMs) => timeMs - LocalOffsetInMs(timeMs, false);

    /// <summary>
    /// Computes a time equivalent to the given time according to ECMA 262 -
    /// 15.9.1.9: maps the time to a year with the same leap-year-ness and the
    /// same starting day for the year.
    /// </summary>
    public long EquivalentTime(long timeMs)
    {
        int days = DaysFromTime(timeMs);
        int timeWithinDayMs = (int)(timeMs - days * kMsPerDay);
        YearMonthDayFromDays(days, out int year, out int month, out int day);
        int newDays = DaysFromYearMonth(EquivalentYear(year), month) + day - 1;
        return (long)newDays * kMsPerDay + timeWithinDayMs;
    }

    /// <summary>
    /// Returns an equivalent year in the range [2008-2035] matching
    /// - leap year,
    /// - week day of first day.
    /// ECMA 262 - 15.9.1.9.
    /// </summary>
    public int EquivalentYear(int year)
    {
        int weekDay = Weekday(DaysFromYearMonth(year, 0));
        int recentYear = (IsLeap(year) ? 1956 : 1967) + (weekDay * 12) % 28;
        // Find the year in the range 2008..2037 that is equivalent mod 28.
        // Add 3*28 to give a positive argument to the modulus operator.
        return 2008 + (recentYear + 3 * 28 - 2008) % 28;
    }

    /// <summary>Given the number of days since the epoch, computes the corresponding year, month, and day.</summary>
    public void YearMonthDayFromDays(int days, out int year, out int month, out int day)
    {
        if (_ymdValid)
        {
            // Check conservatively if the given 'days' has
            // the same year and month as the cached 'days'.
            int newDay = _ymdDay + (days - _ymdDays);
            if (newDay >= 1 && newDay <= 28)
            {
                _ymdDay = newDay;
                _ymdDays = days;
                year = _ymdYear;
                month = _ymdMonth;
                day = newDay;
                return;
            }
        }
        int saveDays = days;

        days += kDaysOffset;
        year = 400 * (days / kDaysIn400Years) - kYearsOffset;
        days %= kDaysIn400Years;

        Debug.Assert(saveDays == DaysFromYearMonth(year, 0) + days);

        days--;
        int yd1 = days / kDaysIn100Years;
        days %= kDaysIn100Years;
        year += 100 * yd1;

        days++;
        int yd2 = days / kDaysIn4Years;
        days %= kDaysIn4Years;
        year += 4 * yd2;

        days--;
        int yd3 = days / 365;
        days %= 365;
        year += yd3;

        bool isLeap = (yd1 == 0 || yd2 != 0) && yd3 == 0;

        days += isLeap ? 1 : 0;

        month = 0;
        day = 0;
        // Check if the date is after February.
        if (days >= 31 + 28 + (isLeap ? 1 : 0))
        {
            days -= 31 + 28 + (isLeap ? 1 : 0);
            // Find the date starting from March.
            for (int i = 2; i < 12; i++)
            {
                if (days < DaysInMonths[i])
                {
                    month = i;
                    day = days + 1;
                    break;
                }
                days -= DaysInMonths[i];
            }
        }
        else
        {
            // Check January and February.
            if (days < 31)
            {
                month = 0;
                day = days + 1;
            }
            else
            {
                month = 1;
                day = days - 31 + 1;
            }
        }
        Debug.Assert(DaysFromYearMonth(year, month) + day - 1 == saveDays);
        _ymdValid = true;
        _ymdYear = year;
        _ymdMonth = month;
        _ymdDay = day;
        _ymdDays = saveDays;
    }

    /// <summary>Computes the number of days since the epoch for the first day of the given month in the given year.</summary>
    public int DaysFromYearMonth(int year, int month)
    {
        year += month / 12;
        month %= 12;
        if (month < 0)
        {
            year--;
            month += 12;
        }

        // year_delta is an arbitrary number such that:
        // a) year_delta = -1 (mod 400)
        // b) year + year_delta > 0 for years in the range defined by
        //    ECMA 262 - 15.9.1.1, i.e. upto 100,000,000 days on either side of
        //    Jan 1 1970. This is required so that we don't run into integer
        //    division of negative numbers.
        // c) there shouldn't be an overflow for 32-bit integers in the following
        //    operations.
        const int yearDelta = 399999;
        const int baseDay = 365 * (1970 + yearDelta) + (1970 + yearDelta) / 4 - (1970 + yearDelta) / 100 + (1970 + yearDelta) / 400;

        int year1 = year + yearDelta;
        int dayFromYear = 365 * year1 + year1 / 4 - year1 / 100 + year1 / 400 - baseDay;

        if (year % 4 != 0 || (year % 100 == 0 && year % 400 != 0))
        {
            return dayFromYear + DayFromMonth[month];
        }
        return dayFromYear + DayFromMonthLeap[month];
    }

    static ReadOnlySpan<short> DayFromMonth => [0, 31, 59, 90, 120, 151, 181, 212, 243, 273, 304, 334];
    static ReadOnlySpan<short> DayFromMonthLeap => [0, 31, 60, 91, 121, 152, 182, 213, 244, 274, 305, 335];

    /// <summary>Breaks down the time value.</summary>
    public void BreakDownTime(long timeMs, out int year, out int month, out int day, out int weekday, out int hour,
        out int min, out int sec, out int ms)
    {
        int days = DaysFromTime(timeMs);
        int timeInDayMs = TimeInDay(timeMs, days);
        YearMonthDayFromDays(days, out year, out month, out day);
        weekday = Weekday(days);
        hour = timeInDayMs / (60 * 60 * 1000);
        min = (timeInDayMs / (60 * 1000)) % 60;
        sec = (timeInDayMs / 1000) % 60;
        ms = timeInDayMs % 1000;
    }

    // These functions are virtual so that we can override them when testing.
    public virtual int GetDaylightSavingsOffsetFromOS(long timeSec)
    {
        double timeMs = timeSec * 1000.0;
        return (int)_tzCache.DaylightSavingsOffset(timeMs);
    }

    /// <summary>
    /// Implements LocalTimeZonedjustment(t, isUTC)
    /// ECMA 262 - https://tc39.es/ecma262/#sec-local-time-zone-adjustment
    /// </summary>
    public virtual int GetLocalOffsetFromOS(long timeMs, bool isUtc)
    {
        // When ICU timezone data is not used, we need to compute the timezone
        // offset for a given local time.
        //
        // The following shows that using DST for (t - LocalTZA - hour) produces
        // correct conversion where LocalTZA is the timezone offset in winter (no
        // DST) and the timezone offset is assumed to have no historical change.
        // Note that it does not work for the past and the future if LocalTZA (no
        // DST) is different from the current LocalTZA (no DST). For instance,
        // this will break for Europe/Moscow in 2012 ~ 2013 because LocalTZA was
        // 4h instead of the current 3h (as of 2018).
        //
        // Consider transition to DST at local time L1.
        // Let L0 = L1 - hour, L2 = L1 + hour,
        //     U1 = UTC time that corresponds to L1,
        //     U0 = U1 - hour.
        // Transitioning to DST moves local clock one hour forward L1 => L2, so
        // U0 = UTC time that corresponds to L0 = L0 - LocalTZA,
        // U1 = UTC time that corresponds to L1 = L1 - LocalTZA,
        // U1 = UTC time that corresponds to L2 = L2 - LocalTZA - hour.
        // Note that DST(U0 - hour) = 0, DST(U0) = 0, DST(U1) = 1.
        // U0 = L0 - LocalTZA - DST(L0 - LocalTZA - hour),
        // U1 = L1 - LocalTZA - DST(L1 - LocalTZA - hour),
        // U1 = L2 - LocalTZA - DST(L2 - LocalTZA - hour).
        //
        // Consider transition from DST at local time L1.
        // Let L0 = L1 - hour,
        //     U1 = UTC time that corresponds to L1,
        //     U0 = U1 - hour, U2 = U1 + hour.
        // Transitioning from DST moves local clock one hour back L1 => L0, so
        // U0 = UTC time that corresponds to L0 (before transition)
        //    = L0 - LocalTZA - hour.
        // U1 = UTC time that corresponds to L0 (after transition)
        //    = L0 - LocalTZA = L1 - LocalTZA - hour
        // U2 = UTC time that corresponds to L1 = L1 - LocalTZA.
        // Note that DST(U0) = 1, DST(U1) = 0, DST(U2) = 0.
        // U0 = L0 - LocalTZA - DST(L0 - LocalTZA - hour) = L0 - LocalTZA - DST(U0).
        // U2 = L1 - LocalTZA - DST(L1 - LocalTZA - hour) = L1 - LocalTZA - DST(U1).
        // It is impossible to get U1 from local time.
        if (_localOffsetMs == kInvalidLocalOffsetInMs)
        {
            // This gets the constant LocalTZA (arguments are ignored).
            _localOffsetMs = (int)_tzCache.LocalTimeOffset(timeMs, isUtc);
        }
        double offset = _localOffsetMs;
        if (!isUtc)
        {
            const int kMsPerHour = 3600 * 1000;
            timeMs -= (long)(offset + kMsPerHour);
        }
        offset += DaylightSavingsOffsetInMs(timeMs);
        Debug.Assert(offset < kInvalidLocalOffsetInMs);
        return (int)offset;
    }

    /// <summary>Computes the daylight savings offset for the given time. ECMA 262 - 15.9.1.8.</summary>
    int DaylightSavingsOffsetInMs(long timeMs)
    {
        int timeSec = timeMs >= 0 && timeMs <= kMaxEpochTimeInMs
            ? (int)(timeMs / 1000)
            : (int)(EquivalentTime(timeMs) / 1000);
        return GetDaylightSavingsOffsetFromOS(timeSec);
    }

    static void ClearSegment(CacheItem segment)
    {
        segment.StartMs = 0;
        segment.EndMs = -1;
        segment.OffsetMs = 0;
        segment.LastUsed = 0;
    }

    static bool InvalidSegment(CacheItem segment) => segment.StartMs > segment.EndMs;

    /// <summary>
    /// Extends the after_ segment with the given point or resets it
    /// if it starts later than the given time + kDefaultDSTDeltaInMs.
    /// </summary>
    void ExtendTheAfterSegment(long timeMs, int offsetMs)
    {
        if (!InvalidSegment(_after) && _after.OffsetMs == offsetMs &&
            _after.StartMs - kDefaultTimeZoneOffsetDeltaInMs <= timeMs && timeMs <= _after.EndMs)
        {
            // Extend the after_ segment.
            _after.StartMs = timeMs;
        }
        else
        {
            // The after_ segment is either invalid or starts too late.
            if (!InvalidSegment(_after))
            {
                // If the after_ segment is valid, replace it with a new segment.
                _after = LeastRecentlyUsedCacheItem(_before);
            }
            _after.StartMs = timeMs;
            _after.EndMs = timeMs;
            _after.OffsetMs = offsetMs;
            _after.LastUsed = ++_cacheUsageCounter;
        }
    }

    /// <summary>ECMA 262 - https://tc39.es/ecma262/#sec-local-time-zone-adjustment</summary>
    public int LocalOffsetInMs(long timeMs, bool isUtc)
    {
        if (!isUtc) return GetLocalOffsetFromOS(timeMs, isUtc);

        // Invalidate cache if the usage counter is close to overflow.
        // Note that cache_usage_counter is incremented less than ten times
        // in this function.
        if (_cacheUsageCounter >= int.MaxValue - 10)
        {
            _cacheUsageCounter = 0;
            for (int i = 0; i < kCacheSize; ++i) ClearSegment(_cache[i]);
        }

        // Optimistic fast check.
        if (_before.StartMs <= timeMs && timeMs <= _before.EndMs)
        {
            // Cache hit.
            _before.LastUsed = ++_cacheUsageCounter;
            return _before.OffsetMs;
        }

        ProbeCache(timeMs);

        Debug.Assert(InvalidSegment(_before) || _before.StartMs <= timeMs);
        Debug.Assert(InvalidSegment(_after) || timeMs < _after.StartMs);

        if (InvalidSegment(_before))
        {
            // Cache miss.
            _before.StartMs = timeMs;
            _before.EndMs = timeMs;
            _before.OffsetMs = GetLocalOffsetFromOS(timeMs, isUtc);
            _before.LastUsed = ++_cacheUsageCounter;
            return _before.OffsetMs;
        }

        if (timeMs <= _before.EndMs)
        {
            // Cache hit.
            _before.LastUsed = ++_cacheUsageCounter;
            return _before.OffsetMs;
        }

        if (timeMs - kDefaultTimeZoneOffsetDeltaInMs > _before.EndMs)
        {
            // If the before_ segment ends too early, then just
            // query for the offset of the time_ms
            int offsetMs = GetLocalOffsetFromOS(timeMs, isUtc);
            ExtendTheAfterSegment(timeMs, offsetMs);
            // This swap helps the optimistic fast check in subsequent invocations.
            (_before, _after) = (_after, _before);
            return offsetMs;
        }

        // Now the time_ms is between
        // before_->end_ms and before_->end_ms + default time zone offset delta.
        // Update the usage counter of before_ since it is going to be used.
        _before.LastUsed = ++_cacheUsageCounter;

        // Check if after_ segment is invalid or starts too late.
        long newAfterStartMs = _before.EndMs + kDefaultTimeZoneOffsetDeltaInMs;
        if (InvalidSegment(_after) || newAfterStartMs <= _after.StartMs)
        {
            int newOffsetMs = GetLocalOffsetFromOS(newAfterStartMs, isUtc);
            ExtendTheAfterSegment(newAfterStartMs, newOffsetMs);
        }
        else
        {
            Debug.Assert(!InvalidSegment(_after));
            // Update the usage counter of after_ since it is going to be used.
            _after.LastUsed = ++_cacheUsageCounter;
        }

        // Now the time_ms is between before_->end_ms and after_->start_ms.
        // Only one daylight savings offset change can occur in this interval.

        if (_before.OffsetMs == _after.OffsetMs)
        {
            // Merge two segments if they have the same offset.
            _before.EndMs = _after.EndMs;
            ClearSegment(_after);
            return _before.OffsetMs;
        }

        // Binary search for time zone offset change point,
        // but give up if we don't find it in five iterations.
        for (int i = 4; i >= 0; --i)
        {
            long delta = _after.StartMs - _before.EndMs;
            long middleSec = i == 0 ? timeMs : _before.EndMs + delta / 2;
            int offsetMs = GetLocalOffsetFromOS(middleSec, isUtc);
            if (_before.OffsetMs == offsetMs)
            {
                _before.EndMs = middleSec;
                if (timeMs <= _before.EndMs) return offsetMs;
                // If we didn't return, we can't be in the last iteration.
                Debug.Assert(i > 0);
            }
            else
            {
                Debug.Assert(_after.OffsetMs == offsetMs);
                _after.StartMs = middleSec;
                if (timeMs >= _after.StartMs)
                {
                    // This swap helps the optimistic fast check in subsequent invocations.
                    (_before, _after) = (_after, _before);
                    return offsetMs;
                }
                // If we didn't return, we can't be in the last iteration.
                Debug.Assert(i > 0);
            }
        }
        // During the last iteration, we set middle_sec = time_ms and return via one
        // of the two return statements above. Thus, we never end up here.
        throw new InvalidOperationException("unreachable");
    }

    /// <summary>
    /// Sets the before_ and the after_ segments from the timezone offset cache
    /// such that the before_ segment starts earlier than the given time and the
    /// after_ segment start later than the given time. Both segments might be
    /// invalid. The last_used counters of the before_ and after_ are updated.
    /// </summary>
    void ProbeCache(long timeMs)
    {
        CacheItem? before = null;
        CacheItem? after = null;
        Debug.Assert(_before != _after);

        for (int i = 0; i < kCacheSize; ++i)
        {
            CacheItem c = _cache[i];
            if (InvalidSegment(c)) continue;
            if (c.StartMs <= timeMs)
            {
                if (before == null || before.StartMs < c.StartMs) before = c;
            }
            else if (timeMs < c.EndMs)
            {
                if (after == null || after.EndMs > c.EndMs) after = c;
            }
        }

        // If before or after segments were not found,
        // then set them to any invalid segment.
        before ??= InvalidSegment(_before) ? _before : LeastRecentlyUsedCacheItem(after);
        after ??= InvalidSegment(_after) && before != _after ? _after : LeastRecentlyUsedCacheItem(before);

        Debug.Assert(before != after);
        _before = before;
        _after = after;
    }

    /// <summary>Finds the least recently used segment that is not equal to the given 'skip' segment.</summary>
    CacheItem LeastRecentlyUsedCacheItem(CacheItem? skip)
    {
        CacheItem? result = null;
        for (int i = 0; i < kCacheSize; ++i)
        {
            if (_cache[i] == skip) continue;
            if (result == null || result.LastUsed > _cache[i].LastUsed) result = _cache[i];
        }
        ClearSegment(result!);
        return result!;
    }

    // -------------------------------------------------------------------------
    // Routines shared between Date and Temporal.

    // ES6 section 20.3.1.1 Time Values and Time Range
    const double kMinYear = -1000000.0;
    const double kMaxYear = -kMinYear;
    const double kMinMonth = -10000000.0;
    const double kMaxMonth = -kMinMonth;

    const double kMsPerDayD = 86400000.0;
    const double kMsPerSecond = 1000.0;
    const double kMsPerMinute = 60000.0;
    const double kMsPerHour = 3600000.0;

    /// <summary>ES6 section 20.3.1.14 MakeDate (day, time).</summary>
    public static double MakeDate(double day, double time)
    {
        if (double.IsFinite(day) && double.IsFinite(time)) return time + day * kMsPerDayD;
        return double.NaN;
    }

    /// <summary>ES6 section 20.3.1.13 MakeDay (year, month, date).</summary>
    public static double MakeDay(double year, double month, double date)
    {
        if (kMinYear <= year && year <= kMaxYear && kMinMonth <= month && month <= kMaxMonth && double.IsFinite(date))
        {
            int y = Conversions.FastD2I(year);
            int m = Conversions.FastD2I(month);
            y += m / 12;
            m %= 12;
            if (m < 0)
            {
                m += 12;
                y -= 1;
            }
            Debug.Assert(0 <= m && m < 12);

            // kYearDelta is an arbitrary number such that:
            // a) kYearDelta = -1 (mod 400)
            // b) year + kYearDelta > 0 for years in the range defined by
            //    ECMA 262 - 15.9.1.1, i.e. upto 100,000,000 days on either side of
            //    Jan 1 1970. This is required so that we don't run into integer
            //    division of negative numbers.
            // c) there shouldn't be an overflow for 32-bit integers in the following
            //    operations.
            const int kYearDelta = 399999;
            const int kBaseDay = 365 * (1970 + kYearDelta) + (1970 + kYearDelta) / 4 - (1970 + kYearDelta) / 100 +
                                 (1970 + kYearDelta) / 400;
            int dayFromYear = 365 * (y + kYearDelta) + (y + kYearDelta) / 4 - (y + kYearDelta) / 100 +
                              (y + kYearDelta) / 400 - kBaseDay;
            if (y % 4 != 0 || (y % 100 == 0 && y % 400 != 0))
            {
                dayFromYear += DayFromMonth[m];
            }
            else
            {
                dayFromYear += DayFromMonthLeap[m];
            }
            return (dayFromYear - 1) + Conversions.DoubleToInteger(date);
        }
        return double.NaN;
    }

    /// <summary>ES6 section 20.3.1.12 MakeTime (hour, min, sec, ms).</summary>
    public static double MakeTime(double hour, double min, double sec, double ms)
    {
        if (double.IsFinite(hour) && double.IsFinite(min) && double.IsFinite(sec) && double.IsFinite(ms))
        {
            double h = Conversions.DoubleToInteger(hour);
            double m = Conversions.DoubleToInteger(min);
            double s = Conversions.DoubleToInteger(sec);
            double milli = Conversions.DoubleToInteger(ms);
            return h * kMsPerHour + m * kMsPerMinute + s * kMsPerSecond + milli;
        }
        return double.NaN;
    }
}

/// <summary>ToDateStringMode (date.h).</summary>
public enum ToDateStringMode
{
    kLocalDate,
    kLocalTime,
    kLocalDateAndTime,
    kUTCDateAndTime,
    kISODateAndTime,
}

/// <summary>ToDateString and ParseDateTimeString (date.cc).</summary>
public static class DateStrings
{
    static readonly string[] kShortWeekDays = ["Sun", "Mon", "Tue", "Wed", "Thu", "Fri", "Sat"];
    static readonly string[] kShortMonths = ["Jan", "Feb", "Mar", "Apr", "May", "Jun", "Jul", "Aug", "Sep", "Oct", "Nov", "Dec"];

    /// <summary>The largest ToDateString result (a long time zone name aside).</summary>
    public const int kMaxDateStringLength = 128;

    /// <summary>
    /// ES6 section 20.3.4.41.1 ToDateString(tv): writes the formatted date
    /// into <paramref name="buffer"/> and returns its length. The buffer must
    /// hold kMaxDateStringLength characters plus the time zone name.
    /// </summary>
    public static int ToDateString(double timeVal, DateCache dateCache, ToDateStringMode mode, Span<char> buffer)
    {
        var w = new Writer(buffer);
        if (double.IsNaN(timeVal))
        {
            w.Append("Invalid Date");
            return w.Length;
        }
        long timeMs = (long)timeVal;
        long localTimeMs = mode is ToDateStringMode.kUTCDateAndTime or ToDateStringMode.kISODateAndTime
            ? timeMs
            : dateCache.ToLocal(timeMs);
        dateCache.BreakDownTime(localTimeMs, out int year, out int month, out int day, out int weekday, out int hour,
            out int min, out int sec, out int ms);
        int timezoneOffset = -dateCache.TimezoneOffset(timeMs);
        int timezoneHour = Math.Abs(timezoneOffset) / 60;
        int timezoneMin = Math.Abs(timezoneOffset) % 60;
        string localTimezone = dateCache.LocalTimezone(timeMs);
        switch (mode)
        {
            case ToDateStringMode.kLocalDate:
                // (year < 0) ? "%s %s %02d %05d" : "%s %s %02d %04d"
                w.Append(kShortWeekDays[weekday]).Append(' ').Append(kShortMonths[month]).Append(' ').Pad(day, 2).Append(' ')
                    .Pad(year, year < 0 ? 5 : 4);
                break;
            case ToDateStringMode.kLocalTime:
                // "%02d:%02d:%02d GMT%c%02d%02d (%s)"
                w.Pad(hour, 2).Append(':').Pad(min, 2).Append(':').Pad(sec, 2).Append(" GMT")
                    .Append(timezoneOffset < 0 ? '-' : '+').Pad(timezoneHour, 2).Pad(timezoneMin, 2)
                    .Append(" (").Append(localTimezone).Append(')');
                break;
            case ToDateStringMode.kLocalDateAndTime:
                // "%s %s %02d %04d %02d:%02d:%02d GMT%c%02d%02d (%s)" (%05d for negative years)
                w.Append(kShortWeekDays[weekday]).Append(' ').Append(kShortMonths[month]).Append(' ').Pad(day, 2).Append(' ')
                    .Pad(year, year < 0 ? 5 : 4).Append(' ')
                    .Pad(hour, 2).Append(':').Pad(min, 2).Append(':').Pad(sec, 2).Append(" GMT")
                    .Append(timezoneOffset < 0 ? '-' : '+').Pad(timezoneHour, 2).Pad(timezoneMin, 2)
                    .Append(" (").Append(localTimezone).Append(')');
                break;
            case ToDateStringMode.kUTCDateAndTime:
                // "%s, %02d %s %04d %02d:%02d:%02d GMT" (%05d for negative years)
                w.Append(kShortWeekDays[weekday]).Append(", ").Pad(day, 2).Append(' ').Append(kShortMonths[month]).Append(' ')
                    .Pad(year, year < 0 ? 5 : 4).Append(' ')
                    .Pad(hour, 2).Append(':').Pad(min, 2).Append(':').Pad(sec, 2).Append(" GMT");
                break;
            case ToDateStringMode.kISODateAndTime:
                if (year >= 0 && year <= 9999)
                {
                    // "%04d-%02d-%02dT%02d:%02d:%02d.%03dZ"
                    w.Pad(year, 4);
                }
                else if (year < 0)
                {
                    // "-%06d-..."
                    w.Append('-').Pad(-year, 6);
                }
                else
                {
                    // "+%06d-..."
                    w.Append('+').Pad(year, 6);
                }
                w.Append('-').Pad(month + 1, 2).Append('-').Pad(day, 2).Append('T').Pad(hour, 2).Append(':').Pad(min, 2)
                    .Append(':').Pad(sec, 2).Append('.').Pad(ms, 3).Append('Z');
                break;
        }
        return w.Length;
    }

    /// <summary>ToDateString as a JS string.</summary>
    public static JSString ToDateString(Isolate isolate, double timeVal, ToDateStringMode mode)
    {
        DateCache dateCache = isolate.DateCache;
        Span<char> buffer = stackalloc char[kMaxDateStringLength + 64];
        if (mode is ToDateStringMode.kLocalTime or ToDateStringMode.kLocalDateAndTime && !double.IsNaN(timeVal) &&
            dateCache.LocalTimezone((long)timeVal).Length > 64)
        {
            buffer = new char[kMaxDateStringLength + dateCache.LocalTimezone((long)timeVal).Length];
        }
        int length = ToDateString(timeVal, dateCache, mode, buffer);
        return isolate.Factory.NewStringFromUtf16(buffer[..length]);
    }

    /// <summary>A printf-like writer for the fixed formats of ToDateString (C's %0Nd).</summary>
    ref struct Writer(Span<char> buffer)
    {
        readonly Span<char> _buffer = buffer;
        int _pos;

        public readonly int Length => _pos;

        [System.Diagnostics.CodeAnalysis.UnscopedRef]
        public ref Writer Append(char c)
        {
            _buffer[_pos++] = c;
            return ref this;
        }

        [System.Diagnostics.CodeAnalysis.UnscopedRef]
        public ref Writer Append(string s)
        {
            s.AsSpan().CopyTo(_buffer[_pos..]);
            _pos += s.Length;
            return ref this;
        }

        /// <summary>printf("%0{width}d", value): zero-padded to width, the sign counting toward it.</summary>
        [System.Diagnostics.CodeAnalysis.UnscopedRef]
        public ref Writer Pad(int value, int width)
        {
            Span<char> digits = stackalloc char[12];
            int n = 0;
            long v = value;
            bool negative = v < 0;
            if (negative) v = -v;
            do
            {
                digits[n++] = (char)('0' + (int)(v % 10));
                v /= 10;
            } while (v != 0);
            int length = n + (negative ? 1 : 0);
            if (negative) _buffer[_pos++] = '-';
            for (int i = length; i < width; i++) _buffer[_pos++] = '0';
            while (n > 0) _buffer[_pos++] = digits[--n];
            return ref this;
        }
    }

    /// <summary>ES6 section 20.3.1.16 Date Time String Format.</summary>
    public static double ParseDateTimeString(Isolate isolate, JSString str)
    {
        Span<double> @out = stackalloc double[DateParser.OUTPUT_SIZE];
        bool result = DateParser.Parse(isolate, str.FlatSpan(), @out);
        if (!result) return double.NaN;
        double day = DateCache.MakeDay(@out[DateParser.YEAR], @out[DateParser.MONTH], @out[DateParser.DAY]);
        double time = DateCache.MakeTime(@out[DateParser.HOUR], @out[DateParser.MINUTE], @out[DateParser.SECOND],
            @out[DateParser.MILLISECOND]);
        double date = DateCache.MakeDate(day, time);
        if (double.IsNaN(@out[DateParser.UTC_OFFSET]))
        {
            if (date >= -DateCache.kMaxTimeBeforeUTCInMs && date <= DateCache.kMaxTimeBeforeUTCInMs)
            {
                date = isolate.DateCache.ToUTC((long)date);
            }
            else
            {
                return double.NaN;
            }
        }
        else
        {
            date -= @out[DateParser.UTC_OFFSET] * 1000.0;
        }
        if (!DateCache.TryTimeClip(ref date)) return double.NaN;
        return date;
    }
}
