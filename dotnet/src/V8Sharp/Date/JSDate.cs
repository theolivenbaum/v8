// Port of the JSDate members of src/objects/js-objects.cc (New,
// CurrentTimeValue, GetField/DoGetField, GetUTCField, SetValue, SetNanValue,
// SetCachedFields). The fields are declared in Objects/JSObjectShapes.cs.
using V8Sharp.Date;

namespace V8Sharp.Objects;

public sealed partial class JSDate
{
    /// <summary>JSDate::New: a date with the time-clipped value tv.</summary>
    public static JSDate New(Isolate isolate, JSFunction constructor, JSReceiver newTarget, double tv)
    {
        var result = (JSDate)JSObject.New(isolate, constructor, newTarget);
        if (DateCache.TryTimeClip(ref tv))
        {
            result.SetValue(isolate, tv);
        }
        else
        {
            result.SetNanValue();
        }
        return result;
    }

    /// <summary>
    /// JSDate::CurrentTimeValue. According to ECMA-262, section 15.9.1, page
    /// 117, the precision of the number in a Date object representing a
    /// particular instant in time is milliseconds. Therefore, we floor the
    /// result of getting the OS time.
    /// </summary>
    public static long CurrentTimeValue(Isolate isolate)
    {
        if (isolate.Flags.correctness_fuzzer_suppressions) return 4;
        return DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    }

    /// <summary>JSDate::GetField: the value of a (cached or computed) date field.</summary>
    public JSValue GetField(Isolate isolate, FieldIndex index)
    {
        DateCache dateCache = isolate.DateCache;

        if (index < FieldIndex.kFirstUncachedField)
        {
            JSValue stamp = CacheStamp;
            if (!(stamp.IsNumber && stamp.Number == isolate.DateCacheStamp) && stamp.IsSmi)
            {
                // Since the stamp is not NaN, the value is also not NaN.
                long localTimeMs = dateCache.ToLocal((long)Value);
                SetCachedFields(isolate, localTimeMs, dateCache);
            }
            return index switch
            {
                FieldIndex.kYear => Year,
                FieldIndex.kMonth => Month,
                FieldIndex.kDay => Day,
                FieldIndex.kWeekday => Weekday,
                FieldIndex.kHour => Hour,
                FieldIndex.kMinute => Min,
                _ => Sec,
            };
        }

        if (index >= FieldIndex.kFirstUTCField) return GetUTCField(index, Value, dateCache);

        double time = Value;
        if (double.IsNaN(time)) return JSValue.NaN;

        long localTime = dateCache.ToLocal((long)time);
        int days = DateCache.DaysFromTime(localTime);

        if (index == FieldIndex.kDays) return JSValue.FromInt(days);

        int timeInDayMs = DateCache.TimeInDay(localTime, days);
        if (index == FieldIndex.kMillisecond) return JSValue.FromInt(timeInDayMs % 1000);
        Debug.Assert(index == FieldIndex.kTimeInDay);
        return JSValue.FromInt(timeInDayMs);
    }

    /// <summary>JSDate::GetUTCField.</summary>
    public static JSValue GetUTCField(FieldIndex index, double value, DateCache dateCache)
    {
        Debug.Assert(index >= FieldIndex.kFirstUTCField);

        if (double.IsNaN(value)) return JSValue.NaN;

        long timeMs = (long)value;

        if (index == FieldIndex.kTimezoneOffset) return JSValue.FromInt(dateCache.TimezoneOffset(timeMs));

        int days = DateCache.DaysFromTime(timeMs);

        if (index == FieldIndex.kWeekdayUTC) return JSValue.FromInt(dateCache.Weekday(days));

        if (index <= FieldIndex.kDayUTC)
        {
            dateCache.YearMonthDayFromDays(days, out int year, out int month, out int day);
            if (index == FieldIndex.kYearUTC) return JSValue.FromInt(year);
            if (index == FieldIndex.kMonthUTC) return JSValue.FromInt(month);
            Debug.Assert(index == FieldIndex.kDayUTC);
            return JSValue.FromInt(day);
        }

        int timeInDayMs = DateCache.TimeInDay(timeMs, days);
        return index switch
        {
            FieldIndex.kHourUTC => JSValue.FromInt(timeInDayMs / (60 * 60 * 1000)),
            FieldIndex.kMinuteUTC => JSValue.FromInt((timeInDayMs / (60 * 1000)) % 60),
            FieldIndex.kSecondUTC => JSValue.FromInt((timeInDayMs / 1000) % 60),
            FieldIndex.kMillisecondUTC => JSValue.FromInt(timeInDayMs % 1000),
            FieldIndex.kDaysUTC => JSValue.FromInt(days),
            FieldIndex.kTimeInDayUTC => JSValue.FromInt(timeInDayMs),
            _ => throw new InvalidOperationException("unreachable"),
        };
    }

    /// <summary>JSDate::SetValue: {value} must be a time-clipped number.</summary>
    public void SetValue(Isolate isolate, double value)
    {
        Debug.Assert(!double.IsNaN(value));
        Value = value;
        DateCache dateCache = isolate.DateCache;
        // Since the stamp is not NaN, the value is also not NaN.
        long localTimeMs = dateCache.ToLocal((long)value);
        SetCachedFields(isolate, localTimeMs, dateCache);
    }

    /// <summary>JSDate::SetCachedFields.</summary>
    void SetCachedFields(Isolate isolate, long localTimeMs, DateCache dateCache)
    {
        int days = DateCache.DaysFromTime(localTimeMs);
        int timeInDayMs = DateCache.TimeInDay(localTimeMs, days);
        dateCache.YearMonthDayFromDays(days, out int year, out int month, out int day);
        int weekday = dateCache.Weekday(days);
        int hour = timeInDayMs / (60 * 60 * 1000);
        int min = (timeInDayMs / (60 * 1000)) % 60;
        int sec = (timeInDayMs / 1000) % 60;
        CacheStamp = JSValue.FromInt(isolate.GetDateCacheStampAndRecordUsage());
        Year = JSValue.FromInt(year);
        Month = JSValue.FromInt(month);
        Day = JSValue.FromInt(day);
        Weekday = JSValue.FromInt(weekday);
        Hour = JSValue.FromInt(hour);
        Min = JSValue.FromInt(min);
        Sec = JSValue.FromInt(sec);
    }
}
