// Port of src/builtins/builtins-date.cc (the Date constructor, Date.now,
// Date.parse, Date.UTC, the setters, the string conversions, getYear/setYear,
// toJSON) and src/builtins/builtins-date-gen.cc (the getters, valueOf,
// getTime and @@toPrimitive). Without V8_INTL_SUPPORT the bootstrapper
// installs toLocaleString/toLocaleDateString/toLocaleTimeString as
// DatePrototypeToString/ToDateString/ToTimeString.
using System.Runtime.CompilerServices;
using V8Sharp.Base.Numbers;
using V8Sharp.Date;
using FieldIndex = V8Sharp.Objects.JSDate.FieldIndex;

namespace V8Sharp.Builtins;

public static partial class BuiltinRegistry
{
    static partial void RegisterDate()
    {
        Register(Builtin.DateConstructor, BuiltinsDate.DateConstructor);
        Register(Builtin.DateNow, BuiltinsDate.DateNow);
        Register(Builtin.DateParse, BuiltinsDate.DateParse);
        Register(Builtin.DateUTC, BuiltinsDate.DateUTC);
        Register(Builtin.DatePrototypeSetDate, BuiltinsDate.DatePrototypeSetDate);
        Register(Builtin.DatePrototypeSetFullYear, BuiltinsDate.DatePrototypeSetFullYear);
        Register(Builtin.DatePrototypeSetHours, BuiltinsDate.DatePrototypeSetHours);
        Register(Builtin.DatePrototypeSetMilliseconds, BuiltinsDate.DatePrototypeSetMilliseconds);
        Register(Builtin.DatePrototypeSetMinutes, BuiltinsDate.DatePrototypeSetMinutes);
        Register(Builtin.DatePrototypeSetMonth, BuiltinsDate.DatePrototypeSetMonth);
        Register(Builtin.DatePrototypeSetSeconds, BuiltinsDate.DatePrototypeSetSeconds);
        Register(Builtin.DatePrototypeSetTime, BuiltinsDate.DatePrototypeSetTime);
        Register(Builtin.DatePrototypeSetUTCDate, BuiltinsDate.DatePrototypeSetUTCDate);
        Register(Builtin.DatePrototypeSetUTCFullYear, BuiltinsDate.DatePrototypeSetUTCFullYear);
        Register(Builtin.DatePrototypeSetUTCHours, BuiltinsDate.DatePrototypeSetUTCHours);
        Register(Builtin.DatePrototypeSetUTCMilliseconds, BuiltinsDate.DatePrototypeSetUTCMilliseconds);
        Register(Builtin.DatePrototypeSetUTCMinutes, BuiltinsDate.DatePrototypeSetUTCMinutes);
        Register(Builtin.DatePrototypeSetUTCMonth, BuiltinsDate.DatePrototypeSetUTCMonth);
        Register(Builtin.DatePrototypeSetUTCSeconds, BuiltinsDate.DatePrototypeSetUTCSeconds);
        Register(Builtin.DatePrototypeToDateString, BuiltinsDate.DatePrototypeToDateString);
        Register(Builtin.DatePrototypeToISOString, BuiltinsDate.DatePrototypeToISOString);
        Register(Builtin.DatePrototypeToString, BuiltinsDate.DatePrototypeToString);
        Register(Builtin.DatePrototypeToTimeString, BuiltinsDate.DatePrototypeToTimeString);
        Register(Builtin.DatePrototypeToUTCString, BuiltinsDate.DatePrototypeToUTCString);
        Register(Builtin.DatePrototypeGetYear, BuiltinsDate.DatePrototypeGetYear);
        Register(Builtin.DatePrototypeSetYear, BuiltinsDate.DatePrototypeSetYear);
        Register(Builtin.DatePrototypeToJson, BuiltinsDate.DatePrototypeToJson);

        Register(Builtin.DatePrototypeGetDate, static (Isolate i, in BuiltinArguments a) => BuiltinsDate.GetField(i, a.Receiver, FieldIndex.kDay));
        Register(Builtin.DatePrototypeGetDay, static (Isolate i, in BuiltinArguments a) => BuiltinsDate.GetField(i, a.Receiver, FieldIndex.kWeekday));
        Register(Builtin.DatePrototypeGetFullYear, static (Isolate i, in BuiltinArguments a) => BuiltinsDate.GetField(i, a.Receiver, FieldIndex.kYear));
        Register(Builtin.DatePrototypeGetHours, static (Isolate i, in BuiltinArguments a) => BuiltinsDate.GetField(i, a.Receiver, FieldIndex.kHour));
        Register(Builtin.DatePrototypeGetMilliseconds, static (Isolate i, in BuiltinArguments a) => BuiltinsDate.GetField(i, a.Receiver, FieldIndex.kMillisecond));
        Register(Builtin.DatePrototypeGetMinutes, static (Isolate i, in BuiltinArguments a) => BuiltinsDate.GetField(i, a.Receiver, FieldIndex.kMinute));
        Register(Builtin.DatePrototypeGetMonth, static (Isolate i, in BuiltinArguments a) => BuiltinsDate.GetField(i, a.Receiver, FieldIndex.kMonth));
        Register(Builtin.DatePrototypeGetSeconds, static (Isolate i, in BuiltinArguments a) => BuiltinsDate.GetField(i, a.Receiver, FieldIndex.kSecond));
        Register(Builtin.DatePrototypeGetTimezoneOffset, static (Isolate i, in BuiltinArguments a) => BuiltinsDate.GetField(i, a.Receiver, FieldIndex.kTimezoneOffset));
        Register(Builtin.DatePrototypeGetUTCDate, static (Isolate i, in BuiltinArguments a) => BuiltinsDate.GetField(i, a.Receiver, FieldIndex.kDayUTC));
        Register(Builtin.DatePrototypeGetUTCDay, static (Isolate i, in BuiltinArguments a) => BuiltinsDate.GetField(i, a.Receiver, FieldIndex.kWeekdayUTC));
        Register(Builtin.DatePrototypeGetUTCFullYear, static (Isolate i, in BuiltinArguments a) => BuiltinsDate.GetField(i, a.Receiver, FieldIndex.kYearUTC));
        Register(Builtin.DatePrototypeGetUTCHours, static (Isolate i, in BuiltinArguments a) => BuiltinsDate.GetField(i, a.Receiver, FieldIndex.kHourUTC));
        Register(Builtin.DatePrototypeGetUTCMilliseconds, static (Isolate i, in BuiltinArguments a) => BuiltinsDate.GetField(i, a.Receiver, FieldIndex.kMillisecondUTC));
        Register(Builtin.DatePrototypeGetUTCMinutes, static (Isolate i, in BuiltinArguments a) => BuiltinsDate.GetField(i, a.Receiver, FieldIndex.kMinuteUTC));
        Register(Builtin.DatePrototypeGetUTCMonth, static (Isolate i, in BuiltinArguments a) => BuiltinsDate.GetField(i, a.Receiver, FieldIndex.kMonthUTC));
        Register(Builtin.DatePrototypeGetUTCSeconds, static (Isolate i, in BuiltinArguments a) => BuiltinsDate.GetField(i, a.Receiver, FieldIndex.kSecondUTC));
        Register(Builtin.DatePrototypeGetTime, BuiltinsDate.DatePrototypeValueOf);
        Register(Builtin.DatePrototypeValueOf, BuiltinsDate.DatePrototypeValueOf);
        Register(Builtin.DatePrototypeToPrimitive, BuiltinsDate.DatePrototypeToPrimitive);
    }
}

/// <summary>The Date builtins.</summary>
public static class BuiltinsDate
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static double Num(Isolate isolate, JSValue value) => ObjectOps.ToNumber(isolate, value).Number;

    /// <summary>CHECK_RECEIVER(JSDate, date, method).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static JSDate CheckReceiver(Isolate isolate, JSValue receiver, string method)
    {
        if (receiver.HeapObjectOrNull is JSDate date) return date;
        ThrowIncompatibleReceiver(isolate, receiver, method);
        return null!;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static void ThrowIncompatibleReceiver(Isolate isolate, JSValue receiver, string method) =>
        isolate.ThrowTypeError(MessageTemplate.IncompatibleMethodReceiver, isolate.Factory.NewStringFromAsciiChecked(method), receiver);

    static JSValue SetLocalDateValue(Isolate isolate, JSDate date, double timeVal)
    {
        if (timeVal >= -DateCache.kMaxTimeBeforeUTCInMs && timeVal <= DateCache.kMaxTimeBeforeUTCInMs)
        {
            timeVal = isolate.DateCache.ToUTC((long)timeVal);
            if (DateCache.TryTimeClip(ref timeVal))
            {
                date.SetValue(isolate, timeVal);
                return JSValue.FromNumber(timeVal);
            }
        }
        date.SetNanValue();
        return JSValue.NaN;
    }

    static JSValue SetDateValue(Isolate isolate, JSDate date, double timeVal)
    {
        if (DateCache.TryTimeClip(ref timeVal))
        {
            date.SetValue(isolate, timeVal);
            return JSValue.FromNumber(timeVal);
        }
        date.SetNanValue();
        return JSValue.NaN;
    }

    /// <summary>https://tc39.es/ecma262/#sec-date-constructor</summary>
    public static JSValue DateConstructor(Isolate isolate, in BuiltinArguments args)
    {
        if (args.NewTarget.IsUndefined)
        {
            double now = JSDate.CurrentTimeValue(isolate);
            return DateStrings.ToDateString(isolate, now, ToDateStringMode.kLocalDateAndTime);
        }
        // [Construct]
        int argc = args.Length - 1;
        JSFunction target = args.Target;
        var newTarget = (JSReceiver)args.NewTarget.Object;
        double timeVal;
        if (argc == 0)
        {
            timeVal = JSDate.CurrentTimeValue(isolate);
        }
        else if (argc == 1)
        {
            JSValue value = args[1];
            if (value.HeapObjectOrNull is JSDate date)
            {
                timeVal = date.Value;
            }
            else
            {
                value = ObjectOps.ToPrimitive(isolate, value);
                if (value.HeapObjectOrNull is JSString s)
                {
                    timeVal = DateStrings.ParseDateTimeString(isolate, s);
                }
                else
                {
                    timeVal = Num(isolate, value);
                }
            }
        }
        else
        {
            double year = Num(isolate, args[1]);
            double month = Num(isolate, args[2]);
            double date = 1.0, hours = 0.0, minutes = 0.0, seconds = 0.0, ms = 0.0;
            if (argc >= 3)
            {
                date = Num(isolate, args[3]);
                if (argc >= 4)
                {
                    hours = Num(isolate, args[4]);
                    if (argc >= 5)
                    {
                        minutes = Num(isolate, args[5]);
                        if (argc >= 6)
                        {
                            seconds = Num(isolate, args[6]);
                            if (argc >= 7) ms = Num(isolate, args[7]);
                        }
                    }
                }
            }
            if (!double.IsNaN(year))
            {
                double y = Conversions.DoubleToInteger(year);
                if (0.0 <= y && y <= 99) year = 1900 + y;
            }
            double day = DateCache.MakeDay(year, month, date);
            double time = DateCache.MakeTime(hours, minutes, seconds, ms);
            timeVal = DateCache.MakeDate(day, time);
            if (timeVal >= -DateCache.kMaxTimeBeforeUTCInMs && timeVal <= DateCache.kMaxTimeBeforeUTCInMs)
            {
                timeVal = isolate.DateCache.ToUTC((long)timeVal);
            }
            else
            {
                timeVal = double.NaN;
            }
        }
        return JSDate.New(isolate, target, newTarget, timeVal);
    }

    /// <summary>ES6 section 20.3.3.1 Date.now ( ).</summary>
    public static JSValue DateNow(Isolate isolate, in BuiltinArguments args) =>
        JSValue.FromNumber(JSDate.CurrentTimeValue(isolate));

    /// <summary>ES6 section 20.3.3.2 Date.parse ( string ).</summary>
    public static JSValue DateParse(Isolate isolate, in BuiltinArguments args)
    {
        JSString str = ObjectOps.ToString(isolate, args.AtOrUndefined(1));
        return JSValue.FromNumber(DateStrings.ParseDateTimeString(isolate, str));
    }

    /// <summary>ES6 section 20.3.3.4 Date.UTC (year,month,date,hours,minutes,seconds,ms).</summary>
    public static JSValue DateUTC(Isolate isolate, in BuiltinArguments args)
    {
        int argc = args.Length - 1;
        double year = double.NaN;
        double month = 0.0, date = 1.0, hours = 0.0, minutes = 0.0, seconds = 0.0, ms = 0.0;
        if (argc >= 1)
        {
            year = Num(isolate, args[1]);
            if (argc >= 2)
            {
                month = Num(isolate, args[2]);
                if (argc >= 3)
                {
                    date = Num(isolate, args[3]);
                    if (argc >= 4)
                    {
                        hours = Num(isolate, args[4]);
                        if (argc >= 5)
                        {
                            minutes = Num(isolate, args[5]);
                            if (argc >= 6)
                            {
                                seconds = Num(isolate, args[6]);
                                if (argc >= 7) ms = Num(isolate, args[7]);
                            }
                        }
                    }
                }
            }
        }
        if (!double.IsNaN(year))
        {
            double y = Conversions.DoubleToInteger(year);
            if (0.0 <= y && y <= 99) year = 1900 + y;
        }
        double day = DateCache.MakeDay(year, month, date);
        double time = DateCache.MakeTime(hours, minutes, seconds, ms);
        double value = DateCache.MakeDate(day, time);
        if (DateCache.TryTimeClip(ref value)) return JSValue.FromNumber(value);
        return JSValue.NaN;
    }

    /// <summary>ES6 section 20.3.4.20 Date.prototype.setDate ( date ).</summary>
    public static JSValue DatePrototypeSetDate(Isolate isolate, in BuiltinArguments args)
    {
        JSDate date = CheckReceiver(isolate, args.Receiver, "Date.prototype.setDate");
        JSValue value = args.AtOrUndefined(1);
        double timeVal = date.Value;
        double v = Num(isolate, value);
        if (double.IsNaN(timeVal)) return JSValue.NaN;

        DateCache dc = isolate.DateCache;
        long timeMs = (long)timeVal;
        long localTimeMs = dc.ToLocal(timeMs);
        int days = DateCache.DaysFromTime(localTimeMs);
        int timeWithinDay = DateCache.TimeInDay(localTimeMs, days);
        dc.YearMonthDayFromDays(days, out int year, out int month, out _);
        timeVal = DateCache.MakeDate(DateCache.MakeDay(year, month, v), timeWithinDay);
        return SetLocalDateValue(isolate, date, timeVal);
    }

    /// <summary>ES6 section 20.3.4.21 Date.prototype.setFullYear (year, month, date).</summary>
    public static JSValue DatePrototypeSetFullYear(Isolate isolate, in BuiltinArguments args)
    {
        JSDate date = CheckReceiver(isolate, args.Receiver, "Date.prototype.setFullYear");
        int argc = args.Length - 1;
        double yearDouble = Num(isolate, args.AtOrUndefined(1)), monthDouble = 0.0, dayDouble = 1.0;
        int timeWithinDay = 0;
        if (!double.IsNaN(date.Value))
        {
            DateCache dc = isolate.DateCache;
            long timeMs = (long)date.Value;
            long localTimeMs = dc.ToLocal(timeMs);
            int days = DateCache.DaysFromTime(localTimeMs);
            timeWithinDay = DateCache.TimeInDay(localTimeMs, days);
            dc.YearMonthDayFromDays(days, out _, out int monthInt, out int dayInt);
            monthDouble = monthInt;
            dayDouble = dayInt;
        }
        if (argc >= 2)
        {
            monthDouble = Num(isolate, args[2]);
            if (argc >= 3) dayDouble = Num(isolate, args[3]);
        }
        double timeVal = DateCache.MakeDate(DateCache.MakeDay(yearDouble, monthDouble, dayDouble), timeWithinDay);
        return SetLocalDateValue(isolate, date, timeVal);
    }

    /// <summary>ES6 section 20.3.4.22 Date.prototype.setHours(hour, min, sec, ms).</summary>
    public static JSValue DatePrototypeSetHours(Isolate isolate, in BuiltinArguments args)
    {
        JSDate date = CheckReceiver(isolate, args.Receiver, "Date.prototype.setHours");
        int argc = args.Length - 1;
        double timeVal = date.Value;
        double h = Num(isolate, args.AtOrUndefined(1));
        double? m = null, s = null, milli = null;
        if (argc >= 2)
        {
            m = Num(isolate, args[2]);
            if (argc >= 3)
            {
                s = Num(isolate, args[3]);
                if (argc >= 4) milli = Num(isolate, args[4]);
            }
        }
        if (double.IsNaN(timeVal)) return JSValue.NaN;

        long timeMs = (long)timeVal;
        long localTimeMs = isolate.DateCache.ToLocal(timeMs);
        int day = DateCache.DaysFromTime(localTimeMs);
        int timeWithinDay = DateCache.TimeInDay(localTimeMs, day);
        timeVal = DateCache.MakeDate(day, DateCache.MakeTime(h, m ?? (timeWithinDay / (60 * 1000)) % 60,
            s ?? (timeWithinDay / 1000) % 60, milli ?? timeWithinDay % 1000));
        return SetLocalDateValue(isolate, date, timeVal);
    }

    /// <summary>ES6 section 20.3.4.23 Date.prototype.setMilliseconds(ms).</summary>
    public static JSValue DatePrototypeSetMilliseconds(Isolate isolate, in BuiltinArguments args)
    {
        JSDate date = CheckReceiver(isolate, args.Receiver, "Date.prototype.setMilliseconds");
        double timeVal = date.Value;
        double ms = Num(isolate, args.AtOrUndefined(1));
        if (double.IsNaN(timeVal)) return JSValue.NaN;
        long timeMs = (long)timeVal;
        long localTimeMs = isolate.DateCache.ToLocal(timeMs);
        int day = DateCache.DaysFromTime(localTimeMs);
        int timeWithinDay = DateCache.TimeInDay(localTimeMs, day);
        int h = timeWithinDay / (60 * 60 * 1000);
        int m = (timeWithinDay / (60 * 1000)) % 60;
        int s = (timeWithinDay / 1000) % 60;
        timeVal = DateCache.MakeDate(day, DateCache.MakeTime(h, m, s, ms));
        return SetLocalDateValue(isolate, date, timeVal);
    }

    /// <summary>ES6 section 20.3.4.24 Date.prototype.setMinutes ( min, sec, ms ).</summary>
    public static JSValue DatePrototypeSetMinutes(Isolate isolate, in BuiltinArguments args)
    {
        JSDate date = CheckReceiver(isolate, args.Receiver, "Date.prototype.setMinutes");
        int argc = args.Length - 1;
        double timeVal = date.Value;
        double m = Num(isolate, args.AtOrUndefined(1));
        double? s = null, milli = null;
        if (argc >= 2)
        {
            s = Num(isolate, args[2]);
            if (argc >= 3) milli = Num(isolate, args[3]);
        }
        if (double.IsNaN(timeVal)) return JSValue.NaN;
        long timeMs = (long)timeVal;
        long localTimeMs = isolate.DateCache.ToLocal(timeMs);
        int day = DateCache.DaysFromTime(localTimeMs);
        int timeWithinDay = DateCache.TimeInDay(localTimeMs, day);
        int h = timeWithinDay / (60 * 60 * 1000);
        timeVal = DateCache.MakeDate(day, DateCache.MakeTime(h, m, s ?? (timeWithinDay / 1000) % 60, milli ?? timeWithinDay % 1000));
        return SetLocalDateValue(isolate, date, timeVal);
    }

    /// <summary>ES6 section 20.3.4.25 Date.prototype.setMonth ( month, date ).</summary>
    public static JSValue DatePrototypeSetMonth(Isolate isolate, in BuiltinArguments args)
    {
        JSDate thisDate = CheckReceiver(isolate, args.Receiver, "Date.prototype.setMonth");
        int argc = args.Length - 1;
        double timeVal = thisDate.Value;
        double m = Num(isolate, args.AtOrUndefined(1));
        double? dt = null;
        if (argc >= 2) dt = Num(isolate, args[2]);
        if (double.IsNaN(timeVal)) return JSValue.NaN;
        DateCache dc = isolate.DateCache;
        long timeMs = (long)timeVal;
        long localTimeMs = dc.ToLocal(timeMs);
        int days = DateCache.DaysFromTime(localTimeMs);
        int timeWithinDay = DateCache.TimeInDay(localTimeMs, days);
        dc.YearMonthDayFromDays(days, out int year, out _, out int day);
        timeVal = DateCache.MakeDate(DateCache.MakeDay(year, m, dt ?? day), timeWithinDay);
        return SetLocalDateValue(isolate, thisDate, timeVal);
    }

    /// <summary>ES6 section 20.3.4.26 Date.prototype.setSeconds ( sec, ms ).</summary>
    public static JSValue DatePrototypeSetSeconds(Isolate isolate, in BuiltinArguments args)
    {
        JSDate date = CheckReceiver(isolate, args.Receiver, "Date.prototype.setSeconds");
        int argc = args.Length - 1;
        double timeVal = date.Value;
        double s = Num(isolate, args.AtOrUndefined(1));
        double? milli = null;
        if (argc >= 2) milli = Num(isolate, args[2]);
        if (double.IsNaN(timeVal)) return JSValue.NaN;
        long timeMs = (long)timeVal;
        long localTimeMs = isolate.DateCache.ToLocal(timeMs);
        int day = DateCache.DaysFromTime(localTimeMs);
        int timeWithinDay = DateCache.TimeInDay(localTimeMs, day);
        int h = timeWithinDay / (60 * 60 * 1000);
        double m = (timeWithinDay / (60 * 1000)) % 60;
        timeVal = DateCache.MakeDate(day, DateCache.MakeTime(h, m, s, milli ?? timeWithinDay % 1000));
        return SetLocalDateValue(isolate, date, timeVal);
    }

    /// <summary>ES6 section 20.3.4.27 Date.prototype.setTime ( time ).</summary>
    public static JSValue DatePrototypeSetTime(Isolate isolate, in BuiltinArguments args)
    {
        JSDate date = CheckReceiver(isolate, args.Receiver, "Date.prototype.setTime");
        JSValue value = ObjectOps.ToNumber(isolate, args.AtOrUndefined(1));
        double valueDouble = value.Number;

        double clippedValue = valueDouble;
        if (DateCache.TryTimeClip(ref clippedValue))
        {
            date.SetValue(isolate, clippedValue);
            // If the clipping didn't change the value (i.e. the value was already an
            // integer), we can reuse the incoming value for the return value.
            // Make sure to use SameNumberValue so that -0 is _not_ treated as equal
            // to the 0.
            if (ObjectOps.SameNumberValue(clippedValue, valueDouble)) return value;
            return JSValue.FromNumber(clippedValue);
        }
        date.SetNanValue();
        return JSValue.NaN;
    }

    /// <summary>ES6 section 20.3.4.28 Date.prototype.setUTCDate ( date ).</summary>
    public static JSValue DatePrototypeSetUTCDate(Isolate isolate, in BuiltinArguments args)
    {
        JSDate date = CheckReceiver(isolate, args.Receiver, "Date.prototype.setUTCDate");
        double timeVal = date.Value;
        double v = Num(isolate, args.AtOrUndefined(1));
        if (double.IsNaN(timeVal)) return JSValue.NaN;
        long timeMs = (long)timeVal;
        int days = DateCache.DaysFromTime(timeMs);
        int timeWithinDay = DateCache.TimeInDay(timeMs, days);
        isolate.DateCache.YearMonthDayFromDays(days, out int year, out int month, out _);
        timeVal = DateCache.MakeDate(DateCache.MakeDay(year, month, v), timeWithinDay);
        return SetDateValue(isolate, date, timeVal);
    }

    /// <summary>ES6 section 20.3.4.29 Date.prototype.setUTCFullYear (year, month, date).</summary>
    public static JSValue DatePrototypeSetUTCFullYear(Isolate isolate, in BuiltinArguments args)
    {
        JSDate date = CheckReceiver(isolate, args.Receiver, "Date.prototype.setUTCFullYear");
        int argc = args.Length - 1;
        double yearDouble = Num(isolate, args.AtOrUndefined(1)), monthDouble = 0.0, dayDouble = 1.0;
        int timeWithinDay = 0;
        if (!double.IsNaN(date.Value))
        {
            long timeMs = (long)date.Value;
            int days = DateCache.DaysFromTime(timeMs);
            timeWithinDay = DateCache.TimeInDay(timeMs, days);
            isolate.DateCache.YearMonthDayFromDays(days, out _, out int monthInt, out int dayInt);
            monthDouble = monthInt;
            dayDouble = dayInt;
        }
        if (argc >= 2)
        {
            monthDouble = Num(isolate, args[2]);
            if (argc >= 3) dayDouble = Num(isolate, args[3]);
        }
        double timeVal = DateCache.MakeDate(DateCache.MakeDay(yearDouble, monthDouble, dayDouble), timeWithinDay);
        return SetDateValue(isolate, date, timeVal);
    }

    /// <summary>ES6 section 20.3.4.30 Date.prototype.setUTCHours(hour, min, sec, ms).</summary>
    public static JSValue DatePrototypeSetUTCHours(Isolate isolate, in BuiltinArguments args)
    {
        JSDate date = CheckReceiver(isolate, args.Receiver, "Date.prototype.setUTCHours");
        int argc = args.Length - 1;
        double timeVal = date.Value;
        double h = Num(isolate, args.AtOrUndefined(1));
        double? m = null, s = null, milli = null;
        if (argc >= 2)
        {
            m = Num(isolate, args[2]);
            if (argc >= 3)
            {
                s = Num(isolate, args[3]);
                if (argc >= 4) milli = Num(isolate, args[4]);
            }
        }
        if (double.IsNaN(timeVal)) return JSValue.NaN;
        long timeMs = (long)timeVal;
        int day = DateCache.DaysFromTime(timeMs);
        int timeWithinDay = DateCache.TimeInDay(timeMs, day);
        timeVal = DateCache.MakeDate(day, DateCache.MakeTime(h, m ?? (timeWithinDay / (60 * 1000)) % 60,
            s ?? (timeWithinDay / 1000) % 60, milli ?? timeWithinDay % 1000));
        return SetDateValue(isolate, date, timeVal);
    }

    /// <summary>ES6 section 20.3.4.31 Date.prototype.setUTCMilliseconds(ms).</summary>
    public static JSValue DatePrototypeSetUTCMilliseconds(Isolate isolate, in BuiltinArguments args)
    {
        JSDate date = CheckReceiver(isolate, args.Receiver, "Date.prototype.setUTCMilliseconds");
        double timeVal = date.Value;
        double ms = Num(isolate, args.AtOrUndefined(1));
        if (double.IsNaN(timeVal)) return JSValue.NaN;
        long timeMs = (long)timeVal;
        int day = DateCache.DaysFromTime(timeMs);
        int timeWithinDay = DateCache.TimeInDay(timeMs, day);
        int h = timeWithinDay / (60 * 60 * 1000);
        int m = (timeWithinDay / (60 * 1000)) % 60;
        int s = (timeWithinDay / 1000) % 60;
        timeVal = DateCache.MakeDate(day, DateCache.MakeTime(h, m, s, ms));
        return SetDateValue(isolate, date, timeVal);
    }

    /// <summary>ES6 section 20.3.4.32 Date.prototype.setUTCMinutes ( min, sec, ms ).</summary>
    public static JSValue DatePrototypeSetUTCMinutes(Isolate isolate, in BuiltinArguments args)
    {
        JSDate date = CheckReceiver(isolate, args.Receiver, "Date.prototype.setUTCMinutes");
        int argc = args.Length - 1;
        double timeVal = date.Value;
        double m = Num(isolate, args.AtOrUndefined(1));
        double? s = null, milli = null;
        if (argc >= 2)
        {
            s = Num(isolate, args[2]);
            if (argc >= 3) milli = Num(isolate, args[3]);
        }
        if (double.IsNaN(timeVal)) return JSValue.NaN;
        long timeMs = (long)timeVal;
        int day = DateCache.DaysFromTime(timeMs);
        int timeWithinDay = DateCache.TimeInDay(timeMs, day);
        int h = timeWithinDay / (60 * 60 * 1000);
        timeVal = DateCache.MakeDate(day, DateCache.MakeTime(h, m, s ?? (timeWithinDay / 1000) % 60, milli ?? timeWithinDay % 1000));
        return SetDateValue(isolate, date, timeVal);
    }

    /// <summary>ES6 section 20.3.4.31 Date.prototype.setUTCMonth ( month, date ).</summary>
    public static JSValue DatePrototypeSetUTCMonth(Isolate isolate, in BuiltinArguments args)
    {
        JSDate thisDate = CheckReceiver(isolate, args.Receiver, "Date.prototype.setUTCMonth");
        int argc = args.Length - 1;
        double timeVal = thisDate.Value;
        double m = Num(isolate, args.AtOrUndefined(1));
        double? dt = null;
        if (argc >= 2) dt = Num(isolate, args[2]);
        if (double.IsNaN(timeVal)) return JSValue.NaN;
        long timeMs = (long)timeVal;
        int days = DateCache.DaysFromTime(timeMs);
        int timeWithinDay = DateCache.TimeInDay(timeMs, days);
        isolate.DateCache.YearMonthDayFromDays(days, out int year, out _, out int day);
        timeVal = DateCache.MakeDate(DateCache.MakeDay(year, m, dt ?? day), timeWithinDay);
        return SetDateValue(isolate, thisDate, timeVal);
    }

    /// <summary>ES6 section 20.3.4.34 Date.prototype.setUTCSeconds ( sec, ms ).</summary>
    public static JSValue DatePrototypeSetUTCSeconds(Isolate isolate, in BuiltinArguments args)
    {
        JSDate date = CheckReceiver(isolate, args.Receiver, "Date.prototype.setUTCSeconds");
        int argc = args.Length - 1;
        double timeVal = date.Value;
        double s = Num(isolate, args.AtOrUndefined(1));
        double? milli = null;
        if (argc >= 2) milli = Num(isolate, args[2]);
        if (double.IsNaN(timeVal)) return JSValue.NaN;
        long timeMs = (long)timeVal;
        int day = DateCache.DaysFromTime(timeMs);
        int timeWithinDay = DateCache.TimeInDay(timeMs, day);
        int h = timeWithinDay / (60 * 60 * 1000);
        double m = (timeWithinDay / (60 * 1000)) % 60;
        timeVal = DateCache.MakeDate(day, DateCache.MakeTime(h, m, s, milli ?? timeWithinDay % 1000));
        return SetDateValue(isolate, date, timeVal);
    }

    /// <summary>ES6 section 20.3.4.35 Date.prototype.toDateString ( ).</summary>
    public static JSValue DatePrototypeToDateString(Isolate isolate, in BuiltinArguments args)
    {
        JSDate date = CheckReceiver(isolate, args.Receiver, "Date.prototype.toDateString");
        return DateStrings.ToDateString(isolate, date.Value, ToDateStringMode.kLocalDate);
    }

    /// <summary>ES6 section 20.3.4.36 Date.prototype.toISOString ( ).</summary>
    public static JSValue DatePrototypeToISOString(Isolate isolate, in BuiltinArguments args)
    {
        JSDate date = CheckReceiver(isolate, args.Receiver, "Date.prototype.toISOString");
        double timeVal = date.Value;
        if (double.IsNaN(timeVal)) isolate.ThrowRangeError(MessageTemplate.InvalidTimeValue);
        return DateStrings.ToDateString(isolate, timeVal, ToDateStringMode.kISODateAndTime);
    }

    /// <summary>ES6 section 20.3.4.41 Date.prototype.toString ( ).</summary>
    public static JSValue DatePrototypeToString(Isolate isolate, in BuiltinArguments args)
    {
        JSDate date = CheckReceiver(isolate, args.Receiver, "Date.prototype.toString");
        return DateStrings.ToDateString(isolate, date.Value, ToDateStringMode.kLocalDateAndTime);
    }

    /// <summary>ES6 section 20.3.4.42 Date.prototype.toTimeString ( ).</summary>
    public static JSValue DatePrototypeToTimeString(Isolate isolate, in BuiltinArguments args)
    {
        JSDate date = CheckReceiver(isolate, args.Receiver, "Date.prototype.toTimeString");
        return DateStrings.ToDateString(isolate, date.Value, ToDateStringMode.kLocalTime);
    }

    /// <summary>ES6 section 20.3.4.43 Date.prototype.toUTCString ( ).</summary>
    public static JSValue DatePrototypeToUTCString(Isolate isolate, in BuiltinArguments args)
    {
        JSDate date = CheckReceiver(isolate, args.Receiver, "Date.prototype.toUTCString");
        return DateStrings.ToDateString(isolate, date.Value, ToDateStringMode.kUTCDateAndTime);
    }

    /// <summary>ES6 section B.2.4.1 Date.prototype.getYear ( ).</summary>
    public static JSValue DatePrototypeGetYear(Isolate isolate, in BuiltinArguments args)
    {
        JSDate date = CheckReceiver(isolate, args.Receiver, "Date.prototype.getYear");
        double timeVal = date.Value;
        if (double.IsNaN(timeVal)) return JSValue.NaN;
        long timeMs = (long)timeVal;
        long localTimeMs = isolate.DateCache.ToLocal(timeMs);
        int days = DateCache.DaysFromTime(localTimeMs);
        isolate.DateCache.YearMonthDayFromDays(days, out int year, out _, out _);
        return JSValue.FromInt(year - 1900);
    }

    /// <summary>ES6 section B.2.4.2 Date.prototype.setYear ( year ).</summary>
    public static JSValue DatePrototypeSetYear(Isolate isolate, in BuiltinArguments args)
    {
        JSDate date = CheckReceiver(isolate, args.Receiver, "Date.prototype.setYear");
        double monthDouble = 0.0, dayDouble = 1.0, yearDouble = Num(isolate, args.AtOrUndefined(1));
        if (!double.IsNaN(yearDouble))
        {
            double yearInt = Conversions.DoubleToInteger(yearDouble);
            if (0.0 <= yearInt && yearInt <= 99.0) yearDouble = 1900.0 + yearInt;
        }
        int timeWithinDay = 0;
        if (!double.IsNaN(date.Value))
        {
            DateCache dc = isolate.DateCache;
            long timeMs = (long)date.Value;
            long localTimeMs = dc.ToLocal(timeMs);
            int days = DateCache.DaysFromTime(localTimeMs);
            timeWithinDay = DateCache.TimeInDay(localTimeMs, days);
            dc.YearMonthDayFromDays(days, out _, out int monthInt, out int dayInt);
            monthDouble = monthInt;
            dayDouble = dayInt;
        }
        double timeVal = DateCache.MakeDate(DateCache.MakeDay(yearDouble, monthDouble, dayDouble), timeWithinDay);
        return SetLocalDateValue(isolate, date, timeVal);
    }

    /// <summary>ES6 section 20.3.4.37 Date.prototype.toJSON ( key ).</summary>
    public static JSValue DatePrototypeToJson(Isolate isolate, in BuiltinArguments args)
    {
        JSReceiver receiverObj = ObjectOps.ToObject(isolate, args.Receiver);
        JSValue primitive = JSReceiver.ToPrimitive(isolate, receiverObj, ToPrimitiveHint.Number);
        if (primitive.IsNumber && !double.IsFinite(primitive.Number)) return JSValue.Null;
        JSString name = isolate.Factory.NewStringFromAsciiChecked("toISOString");
        JSValue function = ObjectOps.GetProperty(isolate, receiverObj, isolate.Factory.InternalizeString(name));
        if (!ObjectOps.IsCallable(function))
        {
            isolate.ThrowTypeError(MessageTemplate.CalledNonCallable, name);
        }
        return Execution.Call(isolate, function, receiverObj, []);
    }

    // ---------------------------------------------------------------------
    // builtins-date-gen.cc

    /// <summary>Generate_IsDateCheck.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static JSDate IsDateCheck(Isolate isolate, JSValue receiver)
    {
        if (receiver.HeapObjectOrNull is JSDate date) return date;
        isolate.ThrowTypeError(MessageTemplate.NotDateObject);
        return null!;
    }

    /// <summary>Generate_DatePrototype_GetField.</summary>
    public static JSValue GetField(Isolate isolate, JSValue receiver, FieldIndex fieldIndex)
    {
        JSDate date = IsDateCheck(isolate, receiver);
        // Load the specified date field, falling back to the runtime as necessary.
        if (fieldIndex < FieldIndex.kFirstUncachedField)
        {
            JSValue stamp = date.CacheStamp;
            if (stamp.IsNumber && stamp.Number == isolate.DateCacheStamp)
            {
                return fieldIndex switch
                {
                    FieldIndex.kYear => date.Year,
                    FieldIndex.kMonth => date.Month,
                    FieldIndex.kDay => date.Day,
                    FieldIndex.kWeekday => date.Weekday,
                    FieldIndex.kHour => date.Hour,
                    FieldIndex.kMinute => date.Min,
                    _ => date.Sec,
                };
            }
        }
        return date.GetField(isolate, fieldIndex);
    }

    /// <summary>DatePrototypeValueOf / DatePrototypeGetTime.</summary>
    public static JSValue DatePrototypeValueOf(Isolate isolate, in BuiltinArguments args) =>
        JSValue.FromNumber(IsDateCheck(isolate, args.Receiver).Value);

    /// <summary>https://tc39.es/ecma262/#sec-date.prototype-@@toprimitive</summary>
    public static JSValue DatePrototypeToPrimitive(Isolate isolate, in BuiltinArguments args)
    {
        JSValue receiver = args.Receiver;
        JSValue hint = args.AtOrUndefined(1);
        // Check if the {receiver} is actually a JSReceiver.
        if (receiver.HeapObjectOrNull is not JSReceiver r)
        {
            isolate.ThrowTypeError(MessageTemplate.IncompatibleMethodReceiver,
                isolate.Factory.NewStringFromAsciiChecked("Date.prototype [ @@toPrimitive ]"), receiver);
            return default;
        }
        // Dispatch to the appropriate OrdinaryToPrimitive builtin.
        if (hint.HeapObjectOrNull is JSString s)
        {
            if (ReferenceEquals(s, ReadOnlyRoots.number_string) || s.IsEqualTo("number"))
            {
                return JSReceiver.OrdinaryToPrimitive(isolate, r, OrdinaryToPrimitiveHint.Number);
            }
            if (ReferenceEquals(s, ReadOnlyRoots.default_string) || ReferenceEquals(s, ReadOnlyRoots.string_string) ||
                s.IsEqualTo("default") || s.IsEqualTo("string"))
            {
                return JSReceiver.OrdinaryToPrimitive(isolate, r, OrdinaryToPrimitiveHint.String);
            }
        }
        // Raise a TypeError if the {hint} is invalid.
        isolate.ThrowTypeError(MessageTemplate.InvalidHint, hint);
        return default;
    }
}
