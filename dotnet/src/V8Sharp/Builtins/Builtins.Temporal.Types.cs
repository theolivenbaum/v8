// Port of src/builtins/builtins-temporal.cc and js-temporal-objects.cc part 2:
// the builtins of Temporal.Now, PlainDate, PlainTime, PlainDateTime,
// PlainYearMonth and PlainMonthDay (the JSTemporal*::Method bodies are inlined
// into their BUILTIN, as the TEMPORAL_* macros of builtins-temporal.cc do).
using V8Sharp.Temporal;
using Calendar = V8Sharp.Temporal.Calendar;
using Duration = V8Sharp.Temporal.Duration;
using TimeZone = V8Sharp.Temporal.TimeZone;

namespace V8Sharp.Builtins;

public static partial class TemporalBuiltins
{
    /// <summary>CHECK_RECEIVER.</summary>
    static T Receiver<T>(Isolate isolate, in BuiltinArguments args, string method) where T : JSTemporalObject
    {
        if (args.Receiver.HeapObjectOrNull is T t) return t;
        isolate.ThrowTypeError(MessageTemplate.IncompatibleMethodReceiver, Str(isolate, method), args.Receiver);
        return null!;
    }

    static JSValue Arg(in BuiltinArguments args, int i) => args.AtOrUndefined(i);

    static JSValue NewString(Isolate isolate, string s) => isolate.Factory.NewStringFromAsciiChecked(s);

    static JSValue Nullable(int? v) => v is int i ? JSValue.FromInt(i) : JSValue.Undefined;

    static JSValue Nullable(long? v) => v is long l ? JSValue.FromNumber(l) : JSValue.Undefined;

    static JSValue NullableString(Isolate isolate, string? s) => s is null ? JSValue.Undefined : NewString(isolate, s);

    /// <summary>TEMPORAL_VALUE_OF.</summary>
    static JSValue ThrowValueOf(Isolate isolate, string type) =>
        isolate.ThrowTypeError(MessageTemplate.DoNotUse, Str(isolate, "Temporal." + type + ".prototype.valueOf"),
            Str(isolate, "use Temporal." + type + ".prototype.compare for comparison."));

    static JSValue ThrowNewTargetUndefined(Isolate isolate, string type) =>
        isolate.ThrowTypeError(MessageTemplate.MethodInvokedOnWrongType, Str(isolate, type));

    // ======== Now ========================================================================================

    public static JSValue TemporalNowInstant(Isolate isolate, in BuiltinArguments args) =>
        Wrap(isolate, Instant.TryNew(SystemUTCEpochNanoseconds(isolate)));

    /// <summary>JSTemporalNowTimeZoneId: without V8_INTL_SUPPORT, "UTC".</summary>
    public static JSValue TemporalNowTimeZoneId(Isolate isolate, in BuiltinArguments args) => NewString(isolate, "UTC");

    public static JSValue TemporalNowPlainDateTimeISO(Isolate isolate, in BuiltinArguments args) =>
        Wrap(isolate, GenericTemporalNowISO(isolate, Arg(args, 1)).ToPlainDateTime());

    public static JSValue TemporalNowZonedDateTimeISO(Isolate isolate, in BuiltinArguments args) =>
        Wrap(isolate, GenericTemporalNowISO(isolate, Arg(args, 1)));

    public static JSValue TemporalNowPlainDateISO(Isolate isolate, in BuiltinArguments args) =>
        Wrap(isolate, GenericTemporalNowISO(isolate, Arg(args, 1)).ToPlainDate());

    public static JSValue TemporalNowPlainTimeISO(Isolate isolate, in BuiltinArguments args) =>
        Wrap(isolate, GenericTemporalNowISO(isolate, Arg(args, 1)).ToPlainTime());

    // ======== PlainDate ==================================================================================

    const string kPlainDate = "Temporal.PlainDate.prototype.";

    public static JSValue TemporalPlainDateConstructor(Isolate isolate, in BuiltinArguments args)
    {
        if (args.NewTarget.IsUndefined) return ThrowNewTargetUndefined(isolate, "Temporal.PlainDate");
        double y = ToIntegerWithTruncation(isolate, Arg(args, 1));
        double m = ToIntegerWithTruncation(isolate, Arg(args, 2));
        double d = ToIntegerWithTruncation(isolate, Arg(args, 3));
        JSValue calendarLike = Arg(args, 4);
        Calendar calendar = Calendar.Iso;
        if (!calendarLike.IsUndefined)
        {
            if (!calendarLike.IsString) ThrowTemporalTypeError(isolate, kCalendarMustBeString);
            calendar = CanonicalizeCalendar(isolate, calendarLike.As<JSString>());
        }
        if (!IsValidIsoDate(y, m, d)) ThrowTemporalRangeError(isolate, kInvalidIsoDate);
        PlainDate value = PlainDate.TryNew((long)y, (long)m, (long)d, calendar);
        return Wrap(isolate, value, args.Target, args.NewTarget);
    }

    public static JSValue TemporalPlainDateFrom(Isolate isolate, in BuiltinArguments args) =>
        Wrap(isolate, ToTemporalDate(isolate, Arg(args, 1), Arg(args, 2), true, "Temporal.PlainDate.from"));

    public static JSValue TemporalPlainDateCompare(Isolate isolate, in BuiltinArguments args)
    {
        const string method = "Temporal.PlainDate.compare";
        PlainDate one = ToTemporalDate(isolate, Arg(args, 1), method);
        PlainDate two = ToTemporalDate(isolate, Arg(args, 2), method);
        return JSValue.FromInt(PlainDate.Compare(one, two));
    }

    static PlainDate PD(Isolate isolate, in BuiltinArguments args, string name) =>
        Receiver<JSTemporalPlainDate>(isolate, args, kPlainDate + name).Value;

    public static JSValue TemporalPlainDatePrototypeCalendarId(Isolate isolate, in BuiltinArguments args) =>
        NewString(isolate, PD(isolate, args, "calendarId").Calendar.Identifier);
    public static JSValue TemporalPlainDatePrototypeEra(Isolate isolate, in BuiltinArguments args)
    {
        PlainDate d = PD(isolate, args, "era");
        return NullableString(isolate, PlainDate.Era(d.IsoDate, d.Calendar));
    }
    public static JSValue TemporalPlainDatePrototypeEraYear(Isolate isolate, in BuiltinArguments args)
    {
        PlainDate d = PD(isolate, args, "eraYear");
        return Nullable(PlainDate.EraYear(d.IsoDate, d.Calendar));
    }
    public static JSValue TemporalPlainDatePrototypeYear(Isolate isolate, in BuiltinArguments args) =>
        JSValue.FromNumber(PD(isolate, args, "year").Year);
    public static JSValue TemporalPlainDatePrototypeMonth(Isolate isolate, in BuiltinArguments args) =>
        JSValue.FromInt(PD(isolate, args, "month").Month);
    public static JSValue TemporalPlainDatePrototypeMonthCode(Isolate isolate, in BuiltinArguments args) =>
        NewString(isolate, PD(isolate, args, "monthCode").MonthCode);
    public static JSValue TemporalPlainDatePrototypeDay(Isolate isolate, in BuiltinArguments args) =>
        JSValue.FromInt(PD(isolate, args, "day").Day);
    public static JSValue TemporalPlainDatePrototypeDayOfWeek(Isolate isolate, in BuiltinArguments args) =>
        JSValue.FromInt(PlainDate.DayOfWeek(PD(isolate, args, "dayOfWeek").IsoDate));
    public static JSValue TemporalPlainDatePrototypeDayOfYear(Isolate isolate, in BuiltinArguments args) =>
        JSValue.FromInt(PlainDate.DayOfYear(PD(isolate, args, "dayOfYear").IsoDate));
    public static JSValue TemporalPlainDatePrototypeWeekOfYear(Isolate isolate, in BuiltinArguments args) =>
        Nullable(PlainDate.WeekOfYear(PD(isolate, args, "weekOfYear").IsoDate));
    public static JSValue TemporalPlainDatePrototypeYearOfWeek(Isolate isolate, in BuiltinArguments args) =>
        Nullable(PlainDate.YearOfWeek(PD(isolate, args, "YearOfWeek").IsoDate));
    public static JSValue TemporalPlainDatePrototypeDaysInWeek(Isolate isolate, in BuiltinArguments args) =>
        JSValue.FromInt(PlainDate.DaysInWeek(PD(isolate, args, "daysInWeek").IsoDate));
    public static JSValue TemporalPlainDatePrototypeDaysInMonth(Isolate isolate, in BuiltinArguments args) =>
        JSValue.FromInt(PlainDate.DaysInMonth(PD(isolate, args, "daysInMonth").IsoDate));
    public static JSValue TemporalPlainDatePrototypeDaysInYear(Isolate isolate, in BuiltinArguments args) =>
        JSValue.FromInt(PlainDate.DaysInYear(PD(isolate, args, "daysInYear").IsoDate));
    public static JSValue TemporalPlainDatePrototypeMonthsInYear(Isolate isolate, in BuiltinArguments args) =>
        JSValue.FromInt(PlainDate.MonthsInYear(PD(isolate, args, "monthsInYear").IsoDate));
    public static JSValue TemporalPlainDatePrototypeInLeapYear(Isolate isolate, in BuiltinArguments args) =>
        JSValue.FromBoolean(PlainDate.InLeapYear(PD(isolate, args, "inLeapYear").IsoDate));

    public static JSValue TemporalPlainDatePrototypeToPlainYearMonth(Isolate isolate, in BuiltinArguments args) =>
        Wrap(isolate, PD(isolate, args, "toPlainYearMonth").ToPlainYearMonth());

    public static JSValue TemporalPlainDatePrototypeToPlainMonthDay(Isolate isolate, in BuiltinArguments args) =>
        Wrap(isolate, PD(isolate, args, "toPlainMonthDay").ToPlainMonthDay());

    public static JSValue TemporalPlainDatePrototypeAdd(Isolate isolate, in BuiltinArguments args)
    {
        const string method = "Temporal.PlainDate.prototype.add";
        PlainDate date = PD(isolate, args, "add");
        Duration duration = ToTemporalDuration(isolate, Arg(args, 1), method).Value;
        Overflow overflow = ToTemporalOverflowHandleUndefined(isolate, Arg(args, 2), method);
        return Wrap(isolate, date.Add(duration, overflow));
    }

    public static JSValue TemporalPlainDatePrototypeSubtract(Isolate isolate, in BuiltinArguments args)
    {
        const string method = "Temporal.PlainDate.prototype.subtract";
        PlainDate date = PD(isolate, args, "subtract");
        Duration duration = ToTemporalDuration(isolate, Arg(args, 1), method).Value;
        Overflow overflow = ToTemporalOverflowHandleUndefined(isolate, Arg(args, 2), method);
        return Wrap(isolate, date.Subtract(duration, overflow));
    }

    public static JSValue TemporalPlainDatePrototypeWith(Isolate isolate, in BuiltinArguments args)
    {
        const string method = "Temporal.PlainDate.prototype.with";
        PlainDate date = PD(isolate, args, "with");
        CombinedRecord fields = GenericWithFields(isolate, date.Calendar, Arg(args, 1), kAllDateFlags);
        Overflow overflow = ToTemporalOverflowHandleUndefined(isolate, Arg(args, 2), method);
        PartialDate partial = fields.RegulateDate(isolate, overflow);
        return Wrap(isolate, date.With(partial, overflow));
    }

    public static JSValue TemporalPlainDatePrototypeWithCalendar(Isolate isolate, in BuiltinArguments args)
    {
        PlainDate date = PD(isolate, args, "withCalendar");
        Calendar calendar = ToTemporalCalendarIdentifier(isolate, Arg(args, 1));
        return Wrap(isolate, date.WithCalendar(calendar));
    }

    static JSValue PlainDateDifference(Isolate isolate, in BuiltinArguments args, bool isSince)
    {
        string name = isSince ? "since" : "until";
        string method = kPlainDate + name;
        PlainDate date = PD(isolate, args, name);
        PlainDate other = ToTemporalDate(isolate, Arg(args, 1), method);
        DifferenceSettings settings = DifferenceSettingsFor(isolate, date.Calendar, other.Calendar, Arg(args, 2),
            UnitGroup.Date, Unit.Day, method);
        return Wrap(isolate, isSince ? date.Since(other, settings) : date.Until(other, settings));
    }

    public static JSValue TemporalPlainDatePrototypeUntil(Isolate isolate, in BuiltinArguments args) =>
        PlainDateDifference(isolate, args, false);

    public static JSValue TemporalPlainDatePrototypeSince(Isolate isolate, in BuiltinArguments args) =>
        PlainDateDifference(isolate, args, true);

    public static JSValue TemporalPlainDatePrototypeEquals(Isolate isolate, in BuiltinArguments args)
    {
        PlainDate date = PD(isolate, args, "equals");
        PlainDate other = ToTemporalDate(isolate, Arg(args, 1), "Temporal.PlainDate.prototype.equals");
        return JSValue.FromBoolean(date.Equals(other));
    }

    public static JSValue TemporalPlainDatePrototypeToPlainDateTime(Isolate isolate, in BuiltinArguments args)
    {
        PlainDate date = PD(isolate, args, "toPlainDateTime");
        PlainTime? time = ToTimeRecordOrMidnight(isolate, Arg(args, 1), "Temporal.PlainDate.toPlainDateTime");
        return Wrap(isolate, date.ToPlainDateTime(time));
    }

    public static JSValue TemporalPlainDatePrototypeToZonedDateTime(Isolate isolate, in BuiltinArguments args)
    {
        const string method = "Temporal.PlainDate.toZonedDateTime";
        PlainDate date = PD(isolate, args, "toZonedDateTime");
        JSValue itemObj = Arg(args, 1);
        TimeZone timeZone;
        JSValue temporalTimeObj = JSValue.Undefined;
        if (itemObj.HeapObjectOrNull is JSReceiver item)
        {
            JSValue timeZoneLike = JSReceiver.GetProperty(isolate, item, isolate.Factory.InternalizeString("timeZone"));
            if (timeZoneLike.IsUndefined)
            {
                timeZone = ToTemporalTimeZoneIdentifier(isolate, item);
            }
            else
            {
                timeZone = ToTemporalTimeZoneIdentifier(isolate, timeZoneLike);
                temporalTimeObj = JSReceiver.GetProperty(isolate, item, isolate.Factory.InternalizeString("plainTime"));
            }
        }
        else
        {
            timeZone = ToTemporalTimeZoneIdentifier(isolate, itemObj);
        }
        PlainTime? temporalTime = temporalTimeObj.IsUndefined ? null : ToTemporalTime(isolate, temporalTimeObj, method);
        return Wrap(isolate, date.ToZonedDateTime(timeZone, temporalTime));
    }

    public static JSValue TemporalPlainDatePrototypeToString(Isolate isolate, in BuiltinArguments args)
    {
        const string method = "Temporal.PlainDate.prototype.toString";
        PlainDate date = PD(isolate, args, "toString");
        JSReceiver options = GetOptionsObject(isolate, Arg(args, 1), method);
        DisplayCalendar showCalendar = GetTemporalShowCalendarNameOption(isolate, options, method);
        return NewString(isolate, date.ToIxdtfString(showCalendar));
    }

    public static JSValue TemporalPlainDatePrototypeToLocaleString(Isolate isolate, in BuiltinArguments args) =>
        NewString(isolate, PD(isolate, args, "toLocaleString").ToIxdtfString(DisplayCalendar.Auto));

    public static JSValue TemporalPlainDatePrototypeToJSON(Isolate isolate, in BuiltinArguments args) =>
        NewString(isolate, PD(isolate, args, "toJSON").ToIxdtfString(DisplayCalendar.Auto));

    public static JSValue TemporalPlainDatePrototypeValueOf(Isolate isolate, in BuiltinArguments args) =>
        ThrowValueOf(isolate, "PlainDate");

    // ======== PlainTime ==================================================================================

    const string kPlainTime = "Temporal.PlainTime.prototype.";

    public static JSValue TemporalPlainTimeConstructor(Isolate isolate, in BuiltinArguments args)
    {
        if (args.NewTarget.IsUndefined) return ThrowNewTargetUndefined(isolate, "Temporal.PlainTime");
        double hour = ToIntegerWithTruncationOrZero(isolate, Arg(args, 1));
        double minute = ToIntegerWithTruncationOrZero(isolate, Arg(args, 2));
        double second = ToIntegerWithTruncationOrZero(isolate, Arg(args, 3));
        double millisecond = ToIntegerWithTruncationOrZero(isolate, Arg(args, 4));
        double microsecond = ToIntegerWithTruncationOrZero(isolate, Arg(args, 5));
        double nanosecond = ToIntegerWithTruncationOrZero(isolate, Arg(args, 6));
        if (!IsValidTime(hour, minute, second, millisecond, microsecond, nanosecond)) ThrowTemporalRangeError(isolate, kInvalidTime);
        PlainTime value = PlainTime.TryNew((long)hour, (long)minute, (long)second, (long)millisecond, (long)microsecond,
            (long)nanosecond);
        return Wrap(isolate, value, args.Target, args.NewTarget);
    }

    public static JSValue TemporalPlainTimeFrom(Isolate isolate, in BuiltinArguments args) =>
        Wrap(isolate, ToTemporalTime(isolate, Arg(args, 1), Arg(args, 2), true, "Temporal.PlainTime.from"));

    public static JSValue TemporalPlainTimeCompare(Isolate isolate, in BuiltinArguments args)
    {
        const string method = "Temporal.PlainTime.compare";
        PlainTime one = ToTemporalTime(isolate, Arg(args, 1), method);
        PlainTime two = ToTemporalTime(isolate, Arg(args, 2), method);
        return JSValue.FromInt(PlainTime.Compare(one, two));
    }

    static PlainTime PT(Isolate isolate, in BuiltinArguments args, string name) =>
        Receiver<JSTemporalPlainTime>(isolate, args, kPlainTime + name).Value;

    public static JSValue TemporalPlainTimePrototypeHour(Isolate isolate, in BuiltinArguments args) =>
        JSValue.FromInt(PT(isolate, args, "hour").Hour);
    public static JSValue TemporalPlainTimePrototypeMinute(Isolate isolate, in BuiltinArguments args) =>
        JSValue.FromInt(PT(isolate, args, "minute").Minute);
    public static JSValue TemporalPlainTimePrototypeSecond(Isolate isolate, in BuiltinArguments args) =>
        JSValue.FromInt(PT(isolate, args, "second").Second);
    public static JSValue TemporalPlainTimePrototypeMillisecond(Isolate isolate, in BuiltinArguments args) =>
        JSValue.FromInt(PT(isolate, args, "millisecond").Millisecond);
    public static JSValue TemporalPlainTimePrototypeMicrosecond(Isolate isolate, in BuiltinArguments args) =>
        JSValue.FromInt(PT(isolate, args, "microsecond").Microsecond);
    public static JSValue TemporalPlainTimePrototypeNanosecond(Isolate isolate, in BuiltinArguments args) =>
        JSValue.FromInt(PT(isolate, args, "nanosecond").Nanosecond);

    public static JSValue TemporalPlainTimePrototypeAdd(Isolate isolate, in BuiltinArguments args)
    {
        PlainTime time = PT(isolate, args, "add");
        Duration duration = ToTemporalDuration(isolate, Arg(args, 1), "Temporal.PlainTime.prototype.add").Value;
        return Wrap(isolate, time.Add(duration));
    }

    public static JSValue TemporalPlainTimePrototypeSubtract(Isolate isolate, in BuiltinArguments args)
    {
        PlainTime time = PT(isolate, args, "subtract");
        Duration duration = ToTemporalDuration(isolate, Arg(args, 1), "Temporal.PlainTime.prototype.subtract").Value;
        return Wrap(isolate, time.Subtract(duration));
    }

    public static JSValue TemporalPlainTimePrototypeWith(Isolate isolate, in BuiltinArguments args)
    {
        const string method = "Temporal.PlainTime.prototype.with";
        PlainTime time = PT(isolate, args, "with");
        JSValue like = Arg(args, 1);
        if (!IsPartialTemporalObject(isolate, like)) ThrowTemporalTypeError(isolate, kWithNoPartial);
        TimeRecord partialTime = ToTemporalTimeRecord(isolate, like.As<JSReceiver>(), method, true);
        Overflow overflow = ToTemporalOverflowHandleUndefined(isolate, Arg(args, 2), method);
        PartialTime result = partialTime.Regulate(isolate, overflow);
        return Wrap(isolate, time.With(result, overflow));
    }

    static JSValue PlainTimeDifference(Isolate isolate, in BuiltinArguments args, bool isSince)
    {
        string name = isSince ? "since" : "until";
        string method = kPlainTime + name;
        PlainTime time = PT(isolate, args, name);
        PlainTime other = ToTemporalTime(isolate, Arg(args, 1), method);
        DifferenceSettings settings = DifferenceSettingsFor(isolate, null, null, Arg(args, 2), UnitGroup.Time, Unit.Nanosecond,
            method);
        return Wrap(isolate, isSince ? time.Since(other, settings) : time.Until(other, settings));
    }

    public static JSValue TemporalPlainTimePrototypeUntil(Isolate isolate, in BuiltinArguments args) =>
        PlainTimeDifference(isolate, args, false);

    public static JSValue TemporalPlainTimePrototypeSince(Isolate isolate, in BuiltinArguments args) =>
        PlainTimeDifference(isolate, args, true);

    public static JSValue TemporalPlainTimePrototypeRound(Isolate isolate, in BuiltinArguments args)
    {
        const string method = "Temporal.PlainDateTime.prototype.round";
        PlainTime time = PT(isolate, args, "round");
        JSValue roundToObj = Arg(args, 1);
        if (roundToObj.IsUndefined) ThrowTemporalTypeError(isolate, kRoundToMissing);
        JSReceiver roundTo = StringOrOptionsObject(isolate, roundToObj, "smallestUnit", kRoundToMustBeObject);
        uint increment = GetRoundingIncrementOption(isolate, roundTo);
        RoundingMode mode = GetRoundingModeOption(isolate, roundTo, RoundingMode.HalfExpand, method);
        Unit? smallestUnit = GetTemporalUnitValuedOption(isolate, roundTo, "smallestUnit", true, method);
        ValidateTemporalUnitValue(isolate, smallestUnit, UnitGroup.Time);
        return Wrap(isolate, time.Round(new RoundingOptions(null, smallestUnit, mode, increment)));
    }

    public static JSValue TemporalPlainTimePrototypeEquals(Isolate isolate, in BuiltinArguments args)
    {
        PlainTime time = PT(isolate, args, "equals");
        PlainTime other = ToTemporalTime(isolate, Arg(args, 1), "Temporal.PlainTime.prototype.equals");
        return JSValue.FromBoolean(time.Equals(other));
    }

    /// <summary>The digits / roundingMode / smallestUnit options of the toString methods.</summary>
    static ToStringRoundingOptions GetToStringRoundingOptions(Isolate isolate, JSReceiver options, string method,
        bool validateTimeUnit = true)
    {
        Precision digits = GetTemporalFractionalSecondDigitsOption(isolate, options, method);
        RoundingMode mode = GetRoundingModeOption(isolate, options, RoundingMode.Trunc, method);
        Unit? smallestUnit = GetTemporalUnitValuedOption(isolate, options, "smallestUnit", false, method);
        if (validateTimeUnit) ValidateTemporalUnitValue(isolate, smallestUnit, UnitGroup.Time);
        return new ToStringRoundingOptions(digits, smallestUnit, mode);
    }

    public static JSValue TemporalPlainTimePrototypeToString(Isolate isolate, in BuiltinArguments args)
    {
        const string method = "Temporal.PlainTime.prototype.toString";
        PlainTime time = PT(isolate, args, "toString");
        JSReceiver options = GetOptionsObject(isolate, Arg(args, 1), method);
        ToStringRoundingOptions rounding = GetToStringRoundingOptions(isolate, options, method);
        return NewString(isolate, time.ToIxdtfString(rounding));
    }

    public static JSValue TemporalPlainTimePrototypeToLocaleString(Isolate isolate, in BuiltinArguments args) =>
        NewString(isolate, PT(isolate, args, "toLocaleString").ToIxdtfString(ToStringRoundingOptions.Default));

    public static JSValue TemporalPlainTimePrototypeToJSON(Isolate isolate, in BuiltinArguments args) =>
        NewString(isolate, PT(isolate, args, "toJSON").ToIxdtfString(ToStringRoundingOptions.Default));

    public static JSValue TemporalPlainTimePrototypeValueOf(Isolate isolate, in BuiltinArguments args) =>
        ThrowValueOf(isolate, "PlainTime");

    // ======== PlainDateTime ==============================================================================

    const string kPlainDateTime = "Temporal.PlainDateTime.prototype.";

    public static JSValue TemporalPlainDateTimeConstructor(Isolate isolate, in BuiltinArguments args)
    {
        if (args.NewTarget.IsUndefined) return ThrowNewTargetUndefined(isolate, "Temporal.PlainDateTime");
        double y = ToIntegerWithTruncation(isolate, Arg(args, 1));
        double m = ToIntegerWithTruncation(isolate, Arg(args, 2));
        double d = ToIntegerWithTruncation(isolate, Arg(args, 3));
        double hour = ToIntegerWithTruncationOrZero(isolate, Arg(args, 4));
        double minute = ToIntegerWithTruncationOrZero(isolate, Arg(args, 5));
        double second = ToIntegerWithTruncationOrZero(isolate, Arg(args, 6));
        double millisecond = ToIntegerWithTruncationOrZero(isolate, Arg(args, 7));
        double microsecond = ToIntegerWithTruncationOrZero(isolate, Arg(args, 8));
        double nanosecond = ToIntegerWithTruncationOrZero(isolate, Arg(args, 9));
        JSValue calendarLike = Arg(args, 10);
        Calendar calendar = Calendar.Iso;
        if (!calendarLike.IsUndefined)
        {
            if (!calendarLike.IsString) ThrowTemporalTypeError(isolate, kCalendarMustBeString);
            calendar = CanonicalizeCalendar(isolate, calendarLike.As<JSString>());
        }
        if (!IsValidIsoDate(y, m, d)) ThrowTemporalRangeError(isolate, kInvalidIsoDate);
        if (!IsValidTime(hour, minute, second, millisecond, microsecond, nanosecond)) ThrowTemporalRangeError(isolate, kInvalidTime);
        PlainDateTime value = PlainDateTime.TryNew((long)y, (long)m, (long)d, (long)hour, (long)minute, (long)second,
            (long)millisecond, (long)microsecond, (long)nanosecond, calendar);
        return Wrap(isolate, value, args.Target, args.NewTarget);
    }

    public static JSValue TemporalPlainDateTimeFrom(Isolate isolate, in BuiltinArguments args) =>
        Wrap(isolate, ToTemporalDateTime(isolate, Arg(args, 1), Arg(args, 2), true, "Temporal.PlainDateTime.from"));

    public static JSValue TemporalPlainDateTimeCompare(Isolate isolate, in BuiltinArguments args)
    {
        const string method = "Temporal.PlainDateTime.compare";
        PlainDateTime one = ToTemporalDateTime(isolate, Arg(args, 1), method);
        PlainDateTime two = ToTemporalDateTime(isolate, Arg(args, 2), method);
        return JSValue.FromInt(PlainDateTime.Compare(one, two));
    }

    static PlainDateTime PDT(Isolate isolate, in BuiltinArguments args, string name) =>
        Receiver<JSTemporalPlainDateTime>(isolate, args, kPlainDateTime + name).Value;

    public static JSValue TemporalPlainDateTimePrototypeCalendarId(Isolate isolate, in BuiltinArguments args) =>
        NewString(isolate, PDT(isolate, args, "calendarId").Calendar.Identifier);
    public static JSValue TemporalPlainDateTimePrototypeYear(Isolate isolate, in BuiltinArguments args) =>
        JSValue.FromNumber(PDT(isolate, args, "year").IsoDate.Year);
    public static JSValue TemporalPlainDateTimePrototypeEra(Isolate isolate, in BuiltinArguments args)
    {
        PlainDateTime d = PDT(isolate, args, "era");
        return NullableString(isolate, PlainDate.Era(d.IsoDate, d.Calendar));
    }
    public static JSValue TemporalPlainDateTimePrototypeEraYear(Isolate isolate, in BuiltinArguments args)
    {
        PlainDateTime d = PDT(isolate, args, "eraYear");
        return Nullable(PlainDate.EraYear(d.IsoDate, d.Calendar));
    }
    public static JSValue TemporalPlainDateTimePrototypeMonth(Isolate isolate, in BuiltinArguments args) =>
        JSValue.FromInt(PDT(isolate, args, "month").IsoDate.Month);
    public static JSValue TemporalPlainDateTimePrototypeMonthCode(Isolate isolate, in BuiltinArguments args) =>
        NewString(isolate, IsoCalendar.MonthCode(PDT(isolate, args, "monthCode").IsoDate.Month));
    public static JSValue TemporalPlainDateTimePrototypeDay(Isolate isolate, in BuiltinArguments args) =>
        JSValue.FromInt(PDT(isolate, args, "day").IsoDate.Day);
    public static JSValue TemporalPlainDateTimePrototypeHour(Isolate isolate, in BuiltinArguments args) =>
        JSValue.FromInt(PDT(isolate, args, "hour").Time.Hour);
    public static JSValue TemporalPlainDateTimePrototypeMinute(Isolate isolate, in BuiltinArguments args) =>
        JSValue.FromInt(PDT(isolate, args, "minute").Time.Minute);
    public static JSValue TemporalPlainDateTimePrototypeSecond(Isolate isolate, in BuiltinArguments args) =>
        JSValue.FromInt(PDT(isolate, args, "second").Time.Second);
    public static JSValue TemporalPlainDateTimePrototypeMillisecond(Isolate isolate, in BuiltinArguments args) =>
        JSValue.FromInt(PDT(isolate, args, "millisecond").Time.Millisecond);
    public static JSValue TemporalPlainDateTimePrototypeMicrosecond(Isolate isolate, in BuiltinArguments args) =>
        JSValue.FromInt(PDT(isolate, args, "microsecond").Time.Microsecond);
    public static JSValue TemporalPlainDateTimePrototypeNanosecond(Isolate isolate, in BuiltinArguments args) =>
        JSValue.FromInt(PDT(isolate, args, "nanosecond").Time.Nanosecond);
    public static JSValue TemporalPlainDateTimePrototypeDayOfWeek(Isolate isolate, in BuiltinArguments args) =>
        JSValue.FromInt(PlainDate.DayOfWeek(PDT(isolate, args, "dayOfWeek").IsoDate));
    public static JSValue TemporalPlainDateTimePrototypeDayOfYear(Isolate isolate, in BuiltinArguments args) =>
        JSValue.FromInt(PlainDate.DayOfYear(PDT(isolate, args, "dayOfYear").IsoDate));
    public static JSValue TemporalPlainDateTimePrototypeWeekOfYear(Isolate isolate, in BuiltinArguments args) =>
        Nullable(PlainDate.WeekOfYear(PDT(isolate, args, "weekOfYear").IsoDate));
    public static JSValue TemporalPlainDateTimePrototypeYearOfWeek(Isolate isolate, in BuiltinArguments args) =>
        Nullable(PlainDate.YearOfWeek(PDT(isolate, args, "YearOfWeek").IsoDate));
    public static JSValue TemporalPlainDateTimePrototypeDaysInWeek(Isolate isolate, in BuiltinArguments args) =>
        JSValue.FromInt(PlainDate.DaysInWeek(PDT(isolate, args, "daysInWeek").IsoDate));
    public static JSValue TemporalPlainDateTimePrototypeDaysInMonth(Isolate isolate, in BuiltinArguments args) =>
        JSValue.FromInt(PlainDate.DaysInMonth(PDT(isolate, args, "daysInMonth").IsoDate));
    public static JSValue TemporalPlainDateTimePrototypeDaysInYear(Isolate isolate, in BuiltinArguments args) =>
        JSValue.FromInt(PlainDate.DaysInYear(PDT(isolate, args, "daysInYear").IsoDate));
    public static JSValue TemporalPlainDateTimePrototypeMonthsInYear(Isolate isolate, in BuiltinArguments args) =>
        JSValue.FromInt(PlainDate.MonthsInYear(PDT(isolate, args, "monthsInYear").IsoDate));
    public static JSValue TemporalPlainDateTimePrototypeInLeapYear(Isolate isolate, in BuiltinArguments args) =>
        JSValue.FromBoolean(PlainDate.InLeapYear(PDT(isolate, args, "inLeapYear").IsoDate));

    public static JSValue TemporalPlainDateTimePrototypeWith(Isolate isolate, in BuiltinArguments args)
    {
        const string method = "Temporal.PlainDateTime.prototype.with";
        PlainDateTime dt = PDT(isolate, args, "with");
        CombinedRecord fields = GenericWithFields(isolate, dt.Calendar, Arg(args, 1),
            kAllDateFlags | CalendarFieldsFlag.kTimeFields);
        Overflow overflow = ToTemporalOverflowHandleUndefined(isolate, Arg(args, 2), method);
        PartialDateTime partial = fields.RegulateDateTime(isolate, overflow);
        return Wrap(isolate, dt.With(partial, overflow));
    }

    public static JSValue TemporalPlainDateTimePrototypeWithPlainTime(Isolate isolate, in BuiltinArguments args)
    {
        PlainDateTime dt = PDT(isolate, args, "withPlainTime");
        PlainTime? time = ToTimeRecordOrMidnight(isolate, Arg(args, 1), "Temporal.PlainDateTime.prototype.withPlainTime");
        return Wrap(isolate, dt.WithTime(time));
    }

    public static JSValue TemporalPlainDateTimePrototypeWithCalendar(Isolate isolate, in BuiltinArguments args)
    {
        PlainDateTime dt = PDT(isolate, args, "withCalendar");
        Calendar calendar = ToTemporalCalendarIdentifier(isolate, Arg(args, 1));
        return Wrap(isolate, dt.WithCalendar(calendar));
    }

    public static JSValue TemporalPlainDateTimePrototypeAdd(Isolate isolate, in BuiltinArguments args)
    {
        const string method = "Temporal.PlainDateTime.prototype.add";
        PlainDateTime dt = PDT(isolate, args, "add");
        Duration duration = ToTemporalDuration(isolate, Arg(args, 1), method).Value;
        Overflow overflow = ToTemporalOverflowHandleUndefined(isolate, Arg(args, 2), method);
        return Wrap(isolate, dt.Add(duration, overflow));
    }

    public static JSValue TemporalPlainDateTimePrototypeSubtract(Isolate isolate, in BuiltinArguments args)
    {
        const string method = "Temporal.PlainDateTime.prototype.subtract";
        PlainDateTime dt = PDT(isolate, args, "subtract");
        Duration duration = ToTemporalDuration(isolate, Arg(args, 1), method).Value;
        Overflow overflow = ToTemporalOverflowHandleUndefined(isolate, Arg(args, 2), method);
        return Wrap(isolate, dt.Subtract(duration, overflow));
    }

    static JSValue PlainDateTimeDifference(Isolate isolate, in BuiltinArguments args, bool isSince)
    {
        string name = isSince ? "since" : "until";
        string method = kPlainDateTime + name;
        PlainDateTime dt = PDT(isolate, args, name);
        PlainDateTime other = ToTemporalDateTime(isolate, Arg(args, 1), method);
        DifferenceSettings settings = DifferenceSettingsFor(isolate, dt.Calendar, other.Calendar, Arg(args, 2),
            UnitGroup.DateTime, Unit.Nanosecond, method);
        return Wrap(isolate, isSince ? dt.Since(other, settings) : dt.Until(other, settings));
    }

    public static JSValue TemporalPlainDateTimePrototypeUntil(Isolate isolate, in BuiltinArguments args) =>
        PlainDateTimeDifference(isolate, args, false);

    public static JSValue TemporalPlainDateTimePrototypeSince(Isolate isolate, in BuiltinArguments args) =>
        PlainDateTimeDifference(isolate, args, true);

    /// <summary>The roundTo options of PlainDateTime/ZonedDateTime.prototype.round.</summary>
    static RoundingOptions GetRoundToOptions(Isolate isolate, JSValue roundToObj, string method, Unit? extraAllowed)
    {
        if (roundToObj.IsUndefined) ThrowTemporalTypeError(isolate, kRoundToMissing);
        JSReceiver roundTo = StringOrOptionsObject(isolate, roundToObj, "smallestUnit", kRoundToMustBeObject);
        uint increment = GetRoundingIncrementOption(isolate, roundTo);
        RoundingMode mode = GetRoundingModeOption(isolate, roundTo, RoundingMode.HalfExpand, method);
        Unit? smallestUnit = GetTemporalUnitValuedOption(isolate, roundTo, "smallestUnit", true, method);
        ValidateTemporalUnitValue(isolate, smallestUnit, UnitGroup.Time, extraAllowed);
        return new RoundingOptions(null, smallestUnit, mode, increment);
    }

    public static JSValue TemporalPlainDateTimePrototypeRound(Isolate isolate, in BuiltinArguments args)
    {
        const string method = "Temporal.PlainDateTime.prototype.round";
        PlainDateTime dt = PDT(isolate, args, "round");
        RoundingOptions options = GetRoundToOptions(isolate, Arg(args, 1), method, Unit.Day);
        return Wrap(isolate, dt.Round(options));
    }

    public static JSValue TemporalPlainDateTimePrototypeEquals(Isolate isolate, in BuiltinArguments args)
    {
        PlainDateTime dt = PDT(isolate, args, "equals");
        PlainDateTime other = ToTemporalDateTime(isolate, Arg(args, 1), "Temporal.PlainDateTime.prototype.equals");
        return JSValue.FromBoolean(dt.Equals(other));
    }

    public static JSValue TemporalPlainDateTimePrototypeToString(Isolate isolate, in BuiltinArguments args)
    {
        const string method = "Temporal.DateTime.prototype.toString";
        PlainDateTime dt = PDT(isolate, args, "toString");
        JSReceiver options = GetOptionsObject(isolate, Arg(args, 1), method);
        DisplayCalendar showCalendar = GetTemporalShowCalendarNameOption(isolate, options, method);
        ToStringRoundingOptions rounding = GetToStringRoundingOptions(isolate, options, method);
        return NewString(isolate, dt.ToIxdtfString(rounding, showCalendar));
    }

    public static JSValue TemporalPlainDateTimePrototypeToLocaleString(Isolate isolate, in BuiltinArguments args) =>
        NewString(isolate, PDT(isolate, args, "toLocaleString").ToIxdtfString(ToStringRoundingOptions.Default, DisplayCalendar.Auto));

    public static JSValue TemporalPlainDateTimePrototypeToJSON(Isolate isolate, in BuiltinArguments args) =>
        NewString(isolate, PDT(isolate, args, "toJSON").ToIxdtfString(ToStringRoundingOptions.Default, DisplayCalendar.Auto));

    public static JSValue TemporalPlainDateTimePrototypeValueOf(Isolate isolate, in BuiltinArguments args) =>
        ThrowValueOf(isolate, "PlainDateTime");

    public static JSValue TemporalPlainDateTimePrototypeToZonedDateTime(Isolate isolate, in BuiltinArguments args)
    {
        const string method = "Temporal.PlainDateTime.prototype.toZonedDateTime";
        PlainDateTime dt = PDT(isolate, args, "toZonedDateTime");
        TimeZone timeZone = ToTemporalTimeZoneIdentifier(isolate, Arg(args, 1));
        Disambiguation disambiguation = GetTemporalDisambiguationOptionHandleUndefined(isolate, Arg(args, 2), method);
        return Wrap(isolate, dt.ToZonedDateTime(timeZone, disambiguation));
    }

    public static JSValue TemporalPlainDateTimePrototypeToPlainDate(Isolate isolate, in BuiltinArguments args) =>
        Wrap(isolate, PDT(isolate, args, "toPlainDate").ToPlainDate());

    public static JSValue TemporalPlainDateTimePrototypeToPlainTime(Isolate isolate, in BuiltinArguments args) =>
        Wrap(isolate, PDT(isolate, args, "toPlainTime").ToPlainTime());

    // ======== PlainYearMonth =============================================================================

    const string kPlainYearMonth = "Temporal.PlainYearMonth.prototype.";

    public static JSValue TemporalPlainYearMonthConstructor(Isolate isolate, in BuiltinArguments args)
    {
        if (args.NewTarget.IsUndefined) return ThrowNewTargetUndefined(isolate, "Temporal.PlainYearMonth");
        double referenceIsoDay = 1.0;
        double y = ToIntegerWithTruncation(isolate, Arg(args, 1));
        double m = ToIntegerWithTruncation(isolate, Arg(args, 2));
        JSValue calendarLike = Arg(args, 3);
        Calendar calendar = Calendar.Iso;
        if (!calendarLike.IsUndefined)
        {
            if (!calendarLike.IsString) ThrowTemporalTypeError(isolate, kCalendarMustBeString);
            calendar = CanonicalizeCalendar(isolate, calendarLike.As<JSString>());
        }
        JSValue referenceIsoDayObj = Arg(args, 4);
        if (!referenceIsoDayObj.IsUndefined) referenceIsoDay = ToIntegerWithTruncation(isolate, referenceIsoDayObj);
        if (!IsValidIsoDate(y, m, referenceIsoDay)) ThrowTemporalRangeError(isolate, kInvalidIsoDate);
        PlainYearMonth value = PlainYearMonth.TryNew((long)y, (long)m, (long)referenceIsoDay, calendar);
        return Wrap(isolate, value, args.Target, args.NewTarget);
    }

    public static JSValue TemporalPlainYearMonthFrom(Isolate isolate, in BuiltinArguments args) =>
        Wrap(isolate, ToTemporalYearMonth(isolate, Arg(args, 1), Arg(args, 2), true, "Temporal.PlainYearMonth.from"));

    public static JSValue TemporalPlainYearMonthCompare(Isolate isolate, in BuiltinArguments args)
    {
        const string method = "Temporal.PlainYearMonth.compare";
        PlainYearMonth one = ToTemporalYearMonth(isolate, Arg(args, 1), method);
        PlainYearMonth two = ToTemporalYearMonth(isolate, Arg(args, 2), method);
        return JSValue.FromInt(PlainYearMonth.Compare(one, two));
    }

    static PlainYearMonth PYM(Isolate isolate, in BuiltinArguments args, string name) =>
        Receiver<JSTemporalPlainYearMonth>(isolate, args, kPlainYearMonth + name).Value;

    public static JSValue TemporalPlainYearMonthPrototypeCalendarId(Isolate isolate, in BuiltinArguments args) =>
        NewString(isolate, PYM(isolate, args, "calendarId").Calendar.Identifier);
    public static JSValue TemporalPlainYearMonthPrototypeYear(Isolate isolate, in BuiltinArguments args) =>
        JSValue.FromNumber(PYM(isolate, args, "year").IsoDate.Year);
    public static JSValue TemporalPlainYearMonthPrototypeEra(Isolate isolate, in BuiltinArguments args)
    {
        PlainYearMonth d = PYM(isolate, args, "era");
        return NullableString(isolate, PlainDate.Era(d.IsoDate, d.Calendar));
    }
    public static JSValue TemporalPlainYearMonthPrototypeEraYear(Isolate isolate, in BuiltinArguments args)
    {
        PlainYearMonth d = PYM(isolate, args, "eraYear");
        return Nullable(PlainDate.EraYear(d.IsoDate, d.Calendar));
    }
    public static JSValue TemporalPlainYearMonthPrototypeMonth(Isolate isolate, in BuiltinArguments args) =>
        JSValue.FromInt(PYM(isolate, args, "month").IsoDate.Month);
    public static JSValue TemporalPlainYearMonthPrototypeMonthCode(Isolate isolate, in BuiltinArguments args) =>
        NewString(isolate, IsoCalendar.MonthCode(PYM(isolate, args, "monthCode").IsoDate.Month));
    public static JSValue TemporalPlainYearMonthPrototypeDaysInMonth(Isolate isolate, in BuiltinArguments args) =>
        JSValue.FromInt(PlainDate.DaysInMonth(PYM(isolate, args, "daysInMonth").IsoDate));
    public static JSValue TemporalPlainYearMonthPrototypeDaysInYear(Isolate isolate, in BuiltinArguments args) =>
        JSValue.FromInt(PlainDate.DaysInYear(PYM(isolate, args, "daysInYear").IsoDate));
    public static JSValue TemporalPlainYearMonthPrototypeMonthsInYear(Isolate isolate, in BuiltinArguments args) =>
        JSValue.FromInt(PlainDate.MonthsInYear(PYM(isolate, args, "monthsInYear").IsoDate));
    public static JSValue TemporalPlainYearMonthPrototypeInLeapYear(Isolate isolate, in BuiltinArguments args) =>
        JSValue.FromBoolean(PlainDate.InLeapYear(PYM(isolate, args, "inLeapYear").IsoDate));

    public static JSValue TemporalPlainYearMonthPrototypeWith(Isolate isolate, in BuiltinArguments args)
    {
        const string method = "Temporal.PlainYearMonth.prototype.with";
        PlainYearMonth ym = PYM(isolate, args, "with");
        CombinedRecord fields = GenericWithFields(isolate, ym.Calendar, Arg(args, 1),
            CalendarFieldsFlag.kYearFields | CalendarFieldsFlag.kMonthFields);
        Overflow overflow = ToTemporalOverflowHandleUndefined(isolate, Arg(args, 2), method);
        PartialDate partial = fields.RegulateDate(isolate, overflow);
        return Wrap(isolate, ym.With(partial, overflow));
    }

    public static JSValue TemporalPlainYearMonthPrototypeAdd(Isolate isolate, in BuiltinArguments args)
    {
        const string method = "Temporal.PlainYearMonth.prototype.add";
        PlainYearMonth ym = PYM(isolate, args, "add");
        Duration duration = ToTemporalDuration(isolate, Arg(args, 1), method).Value;
        Overflow overflow = ToTemporalOverflowHandleUndefined(isolate, Arg(args, 2), method);
        return Wrap(isolate, ym.Add(duration, overflow));
    }

    public static JSValue TemporalPlainYearMonthPrototypeSubtract(Isolate isolate, in BuiltinArguments args)
    {
        const string method = "Temporal.PlainYearMonth.prototype.subtract";
        PlainYearMonth ym = PYM(isolate, args, "subtract");
        Duration duration = ToTemporalDuration(isolate, Arg(args, 1), method).Value;
        Overflow overflow = ToTemporalOverflowHandleUndefined(isolate, Arg(args, 2), method);
        return Wrap(isolate, ym.Subtract(duration, overflow));
    }

    static JSValue PlainYearMonthDifference(Isolate isolate, in BuiltinArguments args, bool isSince)
    {
        string name = isSince ? "since" : "until";
        string method = kPlainYearMonth + name;
        PlainYearMonth ym = PYM(isolate, args, name);
        PlainYearMonth other = ToTemporalYearMonth(isolate, Arg(args, 1), method);
        DifferenceSettings settings = DifferenceSettingsFor(isolate, ym.Calendar, other.Calendar, Arg(args, 2),
            UnitGroup.Date, Unit.Month, method);
        return Wrap(isolate, isSince ? ym.Since(other, settings) : ym.Until(other, settings));
    }

    public static JSValue TemporalPlainYearMonthPrototypeUntil(Isolate isolate, in BuiltinArguments args) =>
        PlainYearMonthDifference(isolate, args, false);

    public static JSValue TemporalPlainYearMonthPrototypeSince(Isolate isolate, in BuiltinArguments args) =>
        PlainYearMonthDifference(isolate, args, true);

    public static JSValue TemporalPlainYearMonthPrototypeEquals(Isolate isolate, in BuiltinArguments args)
    {
        PlainYearMonth ym = PYM(isolate, args, "equals");
        PlainYearMonth other = ToTemporalYearMonth(isolate, Arg(args, 1), "Temporal.PlainYearMonth.prototype.equals");
        return JSValue.FromBoolean(ym.Equals(other));
    }

    public static JSValue TemporalPlainYearMonthPrototypeToString(Isolate isolate, in BuiltinArguments args)
    {
        const string method = "Temporal.PlainYearMonth.prototype.toString";
        PlainYearMonth ym = PYM(isolate, args, "toString");
        JSReceiver options = GetOptionsObject(isolate, Arg(args, 1), method);
        DisplayCalendar showCalendar = GetTemporalShowCalendarNameOption(isolate, options, method);
        return NewString(isolate, ym.ToIxdtfString(showCalendar));
    }

    public static JSValue TemporalPlainYearMonthPrototypeToLocaleString(Isolate isolate, in BuiltinArguments args) =>
        NewString(isolate, PYM(isolate, args, "toLocaleString").ToIxdtfString(DisplayCalendar.Auto));

    public static JSValue TemporalPlainYearMonthPrototypeToJSON(Isolate isolate, in BuiltinArguments args) =>
        NewString(isolate, PYM(isolate, args, "toJSON").ToIxdtfString(DisplayCalendar.Auto));

    public static JSValue TemporalPlainYearMonthPrototypeValueOf(Isolate isolate, in BuiltinArguments args) =>
        ThrowValueOf(isolate, "PlainYearMonth");

    public static JSValue TemporalPlainYearMonthPrototypeToPlainDate(Isolate isolate, in BuiltinArguments args)
    {
        PlainYearMonth ym = PYM(isolate, args, "toPlainDate");
        if (Arg(args, 1).HeapObjectOrNull is not JSReceiver item)
        {
            return ThrowTemporalTypeError(isolate, kYearMustBeObject);
        }
        CombinedRecord fields = PrepareCalendarFields(isolate, ym.Calendar, item, CalendarFieldsFlag.kDay, RequiredFields.kNone);
        PartialDate partialDate = fields.RegulateDate(isolate, Overflow.Constrain);
        return Wrap(isolate, ym.ToPlainDate(partialDate));
    }

    // ======== PlainMonthDay ==============================================================================

    const string kPlainMonthDay = "Temporal.PlainMonthDay.prototype.";

    public static JSValue TemporalPlainMonthDayConstructor(Isolate isolate, in BuiltinArguments args)
    {
        if (args.NewTarget.IsUndefined) return ThrowNewTargetUndefined(isolate, "Temporal.PlainYearMonth");
        double referenceIsoYear = 1972.0;
        double m = ToIntegerWithTruncation(isolate, Arg(args, 1));
        double d = ToIntegerWithTruncation(isolate, Arg(args, 2));
        JSValue calendarLike = Arg(args, 3);
        Calendar calendar = Calendar.Iso;
        if (!calendarLike.IsUndefined)
        {
            if (!calendarLike.IsString) ThrowTemporalTypeError(isolate, kCalendarMustBeString);
            calendar = CanonicalizeCalendar(isolate, calendarLike.As<JSString>());
        }
        JSValue referenceIsoYearObj = Arg(args, 4);
        if (!referenceIsoYearObj.IsUndefined) referenceIsoYear = ToIntegerWithTruncation(isolate, referenceIsoYearObj);
        if (!IsValidIsoDate(referenceIsoYear, m, d)) ThrowTemporalRangeError(isolate, kInvalidIsoDate);
        PlainMonthDay value = PlainMonthDay.TryNew((long)m, (long)d, calendar, (long)referenceIsoYear);
        return Wrap(isolate, value, args.Target, args.NewTarget);
    }

    public static JSValue TemporalPlainMonthDayFrom(Isolate isolate, in BuiltinArguments args) =>
        Wrap(isolate, ToTemporalMonthDay(isolate, Arg(args, 1), Arg(args, 2), true, "Temporal.PlainMonthDay.from"));

    static PlainMonthDay PMD(Isolate isolate, in BuiltinArguments args, string name) =>
        Receiver<JSTemporalPlainMonthDay>(isolate, args, kPlainMonthDay + name).Value;

    public static JSValue TemporalPlainMonthDayPrototypeCalendarId(Isolate isolate, in BuiltinArguments args) =>
        NewString(isolate, PMD(isolate, args, "calendarId").Calendar.Identifier);
    public static JSValue TemporalPlainMonthDayPrototypeDay(Isolate isolate, in BuiltinArguments args) =>
        JSValue.FromInt(PMD(isolate, args, "day").IsoDate.Day);
    public static JSValue TemporalPlainMonthDayPrototypeMonthCode(Isolate isolate, in BuiltinArguments args) =>
        NewString(isolate, PMD(isolate, args, "monthCode").MonthCode);

    public static JSValue TemporalPlainMonthDayPrototypeWith(Isolate isolate, in BuiltinArguments args)
    {
        const string method = "Temporal.PlainYearMonth.prototype.with";
        PlainMonthDay md = PMD(isolate, args, "with");
        CombinedRecord fields = GenericWithFields(isolate, md.Calendar, Arg(args, 1),
            CalendarFieldsFlag.kYearFields | CalendarFieldsFlag.kMonthFields | CalendarFieldsFlag.kDay);
        Overflow overflow = ToTemporalOverflowHandleUndefined(isolate, Arg(args, 2), method);
        PartialDate partial = fields.RegulateDate(isolate, overflow);
        return Wrap(isolate, md.With(partial, overflow));
    }

    public static JSValue TemporalPlainMonthDayPrototypeEquals(Isolate isolate, in BuiltinArguments args)
    {
        PlainMonthDay md = PMD(isolate, args, "equals");
        PlainMonthDay other = ToTemporalMonthDay(isolate, Arg(args, 1), "Temporal.PlainMonthDay.prototype.equals");
        return JSValue.FromBoolean(md.Equals(other));
    }

    public static JSValue TemporalPlainMonthDayPrototypeToString(Isolate isolate, in BuiltinArguments args)
    {
        const string method = "Temporal.PlainMonthDay.prototype.toString";
        PlainMonthDay md = PMD(isolate, args, "toString");
        JSReceiver options = GetOptionsObject(isolate, Arg(args, 1), method);
        DisplayCalendar showCalendar = GetTemporalShowCalendarNameOption(isolate, options, method);
        return NewString(isolate, md.ToIxdtfString(showCalendar));
    }

    public static JSValue TemporalPlainMonthDayPrototypeToJSON(Isolate isolate, in BuiltinArguments args) =>
        NewString(isolate, PMD(isolate, args, "toJSON").ToIxdtfString(DisplayCalendar.Auto));

    public static JSValue TemporalPlainMonthDayPrototypeToLocaleString(Isolate isolate, in BuiltinArguments args) =>
        NewString(isolate, PMD(isolate, args, "toLocaleString").ToIxdtfString(DisplayCalendar.Auto));

    public static JSValue TemporalPlainMonthDayPrototypeValueOf(Isolate isolate, in BuiltinArguments args) =>
        ThrowValueOf(isolate, "PlainMonthDay");

    public static JSValue TemporalPlainMonthDayPrototypeToPlainDate(Isolate isolate, in BuiltinArguments args)
    {
        PlainMonthDay md = PMD(isolate, args, "toPlainDate");
        if (Arg(args, 1).HeapObjectOrNull is not JSReceiver item)
        {
            return ThrowTemporalTypeError(isolate, kYearMustBeObject);
        }
        CombinedRecord fields = PrepareCalendarFields(isolate, md.Calendar, item, CalendarFieldsFlag.kYearFields,
            RequiredFields.kNone);
        PartialDate partialDate = fields.RegulateDate(isolate, Overflow.Constrain);
        return Wrap(isolate, md.ToPlainDate(partialDate));
    }
}
