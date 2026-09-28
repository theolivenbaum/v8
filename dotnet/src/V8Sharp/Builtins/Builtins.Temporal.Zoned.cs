// Port of src/builtins/builtins-temporal.cc and js-temporal-objects.cc part 3:
// the builtins of Temporal.ZonedDateTime, Duration and Instant,
// Date.prototype.toTemporalInstant (builtins-date.cc), and the registration.
using V8Sharp.Temporal;
using Calendar = V8Sharp.Temporal.Calendar;
using Duration = V8Sharp.Temporal.Duration;
using TimeZone = V8Sharp.Temporal.TimeZone;

namespace V8Sharp.Builtins;

public static partial class TemporalBuiltins
{
    // ======== ZonedDateTime ==============================================================================

    const string kZonedDateTime = "Temporal.ZonedDateTime.prototype.";

    public static JSValue TemporalZonedDateTimeConstructor(Isolate isolate, in BuiltinArguments args)
    {
        if (args.NewTarget.IsUndefined) return ThrowNewTargetUndefined(isolate, "Temporal.ZonedDateTime");
        BigInt epochNanoseconds = BigInt.FromObject(isolate, Arg(args, 1));
        Int128 ns = GetI128FromBigInt(isolate, epochNanoseconds);
        JSValue timeZoneLike = Arg(args, 2);
        if (!timeZoneLike.IsString) ThrowTemporalTypeError(isolate, "Time zone must be string");
        TimeZone timeZone = TimeZone.FromIdentifier(timeZoneLike.As<JSString>().FlatSpan());
        JSValue calendarLike = Arg(args, 3);
        Calendar calendar = Calendar.Iso;
        if (!calendarLike.IsUndefined)
        {
            if (!calendarLike.IsString) ThrowTemporalTypeError(isolate, kCalendarMustBeString);
            calendar = CanonicalizeCalendar(isolate, calendarLike.As<JSString>());
        }
        return Wrap(isolate, ZonedDateTime.Create(ns, timeZone, calendar), args.Target, args.NewTarget);
    }

    public static JSValue TemporalZonedDateTimeFrom(Isolate isolate, in BuiltinArguments args) =>
        Wrap(isolate, ToTemporalZonedDateTime(isolate, Arg(args, 1), Arg(args, 2), true, "Temporal.ZonedDateTime.from"));

    public static JSValue TemporalZonedDateTimeCompare(Isolate isolate, in BuiltinArguments args)
    {
        const string method = "Temporal.ZonedDateTime.compare";
        ZonedDateTime one = ToTemporalZonedDateTime(isolate, Arg(args, 1), method);
        ZonedDateTime two = ToTemporalZonedDateTime(isolate, Arg(args, 2), method);
        return JSValue.FromInt(one.CompareInstant(two));
    }

    static ZonedDateTime ZDT(Isolate isolate, in BuiltinArguments args, string name) =>
        Receiver<JSTemporalZonedDateTime>(isolate, args, kZonedDateTime + name).Value;

    public static JSValue TemporalZonedDateTimePrototypeCalendarId(Isolate isolate, in BuiltinArguments args) =>
        NewString(isolate, ZDT(isolate, args, "calendarId").Calendar.Identifier);
    public static JSValue TemporalZonedDateTimePrototypeTimeZoneId(Isolate isolate, in BuiltinArguments args) =>
        isolate.Factory.NewStringFromUtf16(ZDT(isolate, args, "time_zone").TimeZone.Identifier);
    public static JSValue TemporalZonedDateTimePrototypeYear(Isolate isolate, in BuiltinArguments args) =>
        JSValue.FromNumber(ZDT(isolate, args, "year").IsoDate.Year);
    public static JSValue TemporalZonedDateTimePrototypeEra(Isolate isolate, in BuiltinArguments args)
    {
        ZonedDateTime z = ZDT(isolate, args, "era");
        return NullableString(isolate, PlainDate.Era(z.IsoDate, z.Calendar));
    }
    public static JSValue TemporalZonedDateTimePrototypeEraYear(Isolate isolate, in BuiltinArguments args)
    {
        ZonedDateTime z = ZDT(isolate, args, "eraYear");
        return Nullable(PlainDate.EraYear(z.IsoDate, z.Calendar));
    }
    public static JSValue TemporalZonedDateTimePrototypeMonth(Isolate isolate, in BuiltinArguments args) =>
        JSValue.FromInt(ZDT(isolate, args, "month").IsoDate.Month);
    public static JSValue TemporalZonedDateTimePrototypeMonthCode(Isolate isolate, in BuiltinArguments args) =>
        NewString(isolate, IsoCalendar.MonthCode(ZDT(isolate, args, "monthCode").IsoDate.Month));
    public static JSValue TemporalZonedDateTimePrototypeDay(Isolate isolate, in BuiltinArguments args) =>
        JSValue.FromInt(ZDT(isolate, args, "day").IsoDate.Day);
    public static JSValue TemporalZonedDateTimePrototypeHour(Isolate isolate, in BuiltinArguments args) =>
        JSValue.FromInt(ZDT(isolate, args, "hour").Time.Hour);
    public static JSValue TemporalZonedDateTimePrototypeMinute(Isolate isolate, in BuiltinArguments args) =>
        JSValue.FromInt(ZDT(isolate, args, "minute").Time.Minute);
    public static JSValue TemporalZonedDateTimePrototypeSecond(Isolate isolate, in BuiltinArguments args) =>
        JSValue.FromInt(ZDT(isolate, args, "second").Time.Second);
    public static JSValue TemporalZonedDateTimePrototypeMillisecond(Isolate isolate, in BuiltinArguments args) =>
        JSValue.FromInt(ZDT(isolate, args, "millisecond").Time.Millisecond);
    public static JSValue TemporalZonedDateTimePrototypeMicrosecond(Isolate isolate, in BuiltinArguments args) =>
        JSValue.FromInt(ZDT(isolate, args, "microsecond").Time.Microsecond);
    public static JSValue TemporalZonedDateTimePrototypeNanosecond(Isolate isolate, in BuiltinArguments args) =>
        JSValue.FromInt(ZDT(isolate, args, "nanosecond").Time.Nanosecond);
    public static JSValue TemporalZonedDateTimePrototypeEpochMilliseconds(Isolate isolate, in BuiltinArguments args) =>
        JSValue.FromNumber(ZDT(isolate, args, "epochMilliseconds").EpochMilliseconds);
    public static JSValue TemporalZonedDateTimePrototypeEpochNanoseconds(Isolate isolate, in BuiltinArguments args) =>
        I128ToBigInt(isolate, ZDT(isolate, args, "nanoseconds").EpochNanoseconds);
    public static JSValue TemporalZonedDateTimePrototypeDayOfWeek(Isolate isolate, in BuiltinArguments args) =>
        JSValue.FromInt(PlainDate.DayOfWeek(ZDT(isolate, args, "dayOfWeek").IsoDate));
    public static JSValue TemporalZonedDateTimePrototypeDayOfYear(Isolate isolate, in BuiltinArguments args) =>
        JSValue.FromInt(PlainDate.DayOfYear(ZDT(isolate, args, "dayOfYear").IsoDate));
    public static JSValue TemporalZonedDateTimePrototypeWeekOfYear(Isolate isolate, in BuiltinArguments args) =>
        Nullable(PlainDate.WeekOfYear(ZDT(isolate, args, "weekOfYear").IsoDate));
    public static JSValue TemporalZonedDateTimePrototypeYearOfWeek(Isolate isolate, in BuiltinArguments args) =>
        Nullable(PlainDate.YearOfWeek(ZDT(isolate, args, "YearOfWeek").IsoDate));
    public static JSValue TemporalZonedDateTimePrototypeHoursInDay(Isolate isolate, in BuiltinArguments args) =>
        JSValue.FromNumber(ZDT(isolate, args, "hoursInDay").HoursInDay());
    public static JSValue TemporalZonedDateTimePrototypeDaysInWeek(Isolate isolate, in BuiltinArguments args) =>
        JSValue.FromInt(PlainDate.DaysInWeek(ZDT(isolate, args, "daysInWeek").IsoDate));
    public static JSValue TemporalZonedDateTimePrototypeDaysInMonth(Isolate isolate, in BuiltinArguments args) =>
        JSValue.FromInt(PlainDate.DaysInMonth(ZDT(isolate, args, "daysInMonth").IsoDate));
    public static JSValue TemporalZonedDateTimePrototypeDaysInYear(Isolate isolate, in BuiltinArguments args) =>
        JSValue.FromInt(PlainDate.DaysInYear(ZDT(isolate, args, "daysInYear").IsoDate));
    public static JSValue TemporalZonedDateTimePrototypeMonthsInYear(Isolate isolate, in BuiltinArguments args) =>
        JSValue.FromInt(PlainDate.MonthsInYear(ZDT(isolate, args, "monthsInYear").IsoDate));
    public static JSValue TemporalZonedDateTimePrototypeInLeapYear(Isolate isolate, in BuiltinArguments args) =>
        JSValue.FromBoolean(PlainDate.InLeapYear(ZDT(isolate, args, "inLeapYear").IsoDate));
    public static JSValue TemporalZonedDateTimePrototypeOffsetNanoseconds(Isolate isolate, in BuiltinArguments args) =>
        JSValue.FromNumber(ZDT(isolate, args, "offsetNanoseconds").OffsetNanoseconds);
    public static JSValue TemporalZonedDateTimePrototypeOffset(Isolate isolate, in BuiltinArguments args) =>
        NewString(isolate, ZDT(isolate, args, "offset").Offset);

    public static JSValue TemporalZonedDateTimePrototypeWith(Isolate isolate, in BuiltinArguments args)
    {
        const string method = "Temporal.ZonedDateTime.prototype.with";
        ZonedDateTime z = ZDT(isolate, args, "with");
        CombinedRecord fields = GenericWithFields(isolate, z.Calendar, Arg(args, 1),
            kAllDateFlags | CalendarFieldsFlag.kTimeFields | CalendarFieldsFlag.kOffset);
        JSValue options = Arg(args, 2);
        Disambiguation disambiguation = GetTemporalDisambiguationOptionHandleUndefined(isolate, options, method);
        OffsetDisambiguation offsetOption =
            GetTemporalOffsetOptionHandleUndefined(isolate, options, OffsetDisambiguation.Prefer, method);
        Overflow overflow = ToTemporalOverflowHandleUndefined(isolate, options, method);
        PartialZonedDateTime partial = fields.RegulateZoned(isolate, overflow);
        return Wrap(isolate, z.With(partial, disambiguation, offsetOption, overflow));
    }

    public static JSValue TemporalZonedDateTimePrototypeWithPlainTime(Isolate isolate, in BuiltinArguments args)
    {
        ZonedDateTime z = ZDT(isolate, args, "withPlainTime");
        JSValue plainTimeLike = Arg(args, 1);
        PlainTime? time = plainTimeLike.IsUndefined
            ? null
            : ToTemporalTime(isolate, plainTimeLike, "Temporal.ZonedDateTime.prototype.withPlainTime");
        return Wrap(isolate, z.WithPlainTime(time));
    }

    public static JSValue TemporalZonedDateTimePrototypeWithTimeZone(Isolate isolate, in BuiltinArguments args)
    {
        ZonedDateTime z = ZDT(isolate, args, "withTimeZone");
        TimeZone timeZone = ToTemporalTimeZoneIdentifier(isolate, Arg(args, 1));
        return Wrap(isolate, z.WithTimeZone(timeZone));
    }

    public static JSValue TemporalZonedDateTimePrototypeWithCalendar(Isolate isolate, in BuiltinArguments args)
    {
        ZonedDateTime z = ZDT(isolate, args, "withCalendar");
        Calendar calendar = ToTemporalCalendarIdentifier(isolate, Arg(args, 1));
        return Wrap(isolate, z.WithCalendar(calendar));
    }

    public static JSValue TemporalZonedDateTimePrototypeAdd(Isolate isolate, in BuiltinArguments args)
    {
        const string method = "Temporal.ZonedDateTime.prototype.add";
        ZonedDateTime z = ZDT(isolate, args, "add");
        Duration duration = ToTemporalDuration(isolate, Arg(args, 1), method).Value;
        Overflow overflow = ToTemporalOverflowHandleUndefined(isolate, Arg(args, 2), method);
        return Wrap(isolate, z.Add(duration, overflow));
    }

    public static JSValue TemporalZonedDateTimePrototypeSubtract(Isolate isolate, in BuiltinArguments args)
    {
        const string method = "Temporal.ZonedDateTime.prototype.subtract";
        ZonedDateTime z = ZDT(isolate, args, "subtract");
        Duration duration = ToTemporalDuration(isolate, Arg(args, 1), method).Value;
        Overflow overflow = ToTemporalOverflowHandleUndefined(isolate, Arg(args, 2), method);
        return Wrap(isolate, z.Subtract(duration, overflow));
    }

    static JSValue ZonedDateTimeDifference(Isolate isolate, in BuiltinArguments args, bool isSince)
    {
        string name = isSince ? "since" : "until";
        const string method = "Temporal.ZonedDateTime.prototype.since";
        ZonedDateTime z = ZDT(isolate, args, name);
        ZonedDateTime other = ToTemporalZonedDateTime(isolate, Arg(args, 1), method);
        DifferenceSettings settings = DifferenceSettingsFor(isolate, z.Calendar, other.Calendar, Arg(args, 2),
            UnitGroup.DateTime, Unit.Nanosecond, method);
        return Wrap(isolate, isSince ? z.Since(other, settings) : z.Until(other, settings));
    }

    public static JSValue TemporalZonedDateTimePrototypeSince(Isolate isolate, in BuiltinArguments args) =>
        ZonedDateTimeDifference(isolate, args, true);

    public static JSValue TemporalZonedDateTimePrototypeUntil(Isolate isolate, in BuiltinArguments args) =>
        ZonedDateTimeDifference(isolate, args, false);

    public static JSValue TemporalZonedDateTimePrototypeRound(Isolate isolate, in BuiltinArguments args)
    {
        const string method = "Temporal.PlainDateTime.prototype.round";
        ZonedDateTime z = ZDT(isolate, args, "round");
        RoundingOptions options = GetRoundToOptions(isolate, Arg(args, 1), method, Unit.Day);
        return Wrap(isolate, z.Round(options));
    }

    public static JSValue TemporalZonedDateTimePrototypeEquals(Isolate isolate, in BuiltinArguments args)
    {
        ZonedDateTime z = ZDT(isolate, args, "equals");
        ZonedDateTime other = ToTemporalZonedDateTime(isolate, Arg(args, 1), "Temporal.ZonedDateTime.prototype.equals");
        return JSValue.FromBoolean(z.Equals(other));
    }

    public static JSValue TemporalZonedDateTimePrototypeToString(Isolate isolate, in BuiltinArguments args)
    {
        const string method = "Temporal.ZonedDateTime.prototype.toString";
        ZonedDateTime z = ZDT(isolate, args, "toString");
        JSReceiver options = GetOptionsObject(isolate, Arg(args, 1), method);
        DisplayCalendar showCalendar = GetTemporalShowCalendarNameOption(isolate, options, method);
        Precision digits = GetTemporalFractionalSecondDigitsOption(isolate, options, method);
        DisplayOffset showOffset = GetTemporalShowOffsetOption(isolate, options, method);
        RoundingMode mode = GetRoundingModeOption(isolate, options, RoundingMode.Trunc, method);
        Unit? smallestUnit = GetTemporalUnitValuedOption(isolate, options, "smallestUnit", false, method);
        DisplayTimeZone showTimeZone = GetTemporalShowTimeZoneNameOption(isolate, options, method);
        ValidateTemporalUnitValue(isolate, smallestUnit, UnitGroup.Time);
        if (smallestUnit == Unit.Hour) ThrowTemporalRangeError(isolate, "smallestUnit cannot be Hour.");
        return isolate.Factory.NewStringFromUtf16(z.ToIxdtfString(showOffset, showTimeZone, showCalendar,
            new ToStringRoundingOptions(digits, smallestUnit, mode)));
    }

    public static JSValue TemporalZonedDateTimePrototypeToJSON(Isolate isolate, in BuiltinArguments args) =>
        isolate.Factory.NewStringFromUtf16(ZDT(isolate, args, "toJSON").ToIxdtfString(DisplayOffset.Auto, DisplayTimeZone.Auto,
            DisplayCalendar.Auto, ToStringRoundingOptions.Default));

    public static JSValue TemporalZonedDateTimePrototypeToLocaleString(Isolate isolate, in BuiltinArguments args) =>
        isolate.Factory.NewStringFromUtf16(ZDT(isolate, args, "toLocaleString").ToIxdtfString(DisplayOffset.Auto,
            DisplayTimeZone.Auto, DisplayCalendar.Auto, ToStringRoundingOptions.Default));

    public static JSValue TemporalZonedDateTimePrototypeValueOf(Isolate isolate, in BuiltinArguments args) =>
        ThrowValueOf(isolate, "ZonedDateTime");

    public static JSValue TemporalZonedDateTimePrototypeStartOfDay(Isolate isolate, in BuiltinArguments args) =>
        Wrap(isolate, ZDT(isolate, args, "startOfDay").StartOfDay());

    public static JSValue TemporalZonedDateTimePrototypeGetTimeZoneTransition(Isolate isolate, in BuiltinArguments args)
    {
        const string method = "Temporal.ZonedDateTime.prototype.getTimeZoneTransition";
        ZonedDateTime z = ZDT(isolate, args, "getTimeZoneTransition");
        JSValue directionParamObj = Arg(args, 1);
        if (directionParamObj.IsUndefined) ThrowTemporalTypeError(isolate, "Must specify a direction parameter.");
        JSReceiver directionParam = StringOrOptionsObject(isolate, directionParamObj, "direction",
            "directionParam must be object or string.");
        TransitionDirection dir = GetDirectionOption(isolate, directionParam, method);
        ZonedDateTime? transition = z.GetTimeZoneTransition(dir);
        return transition is null ? JSValue.Null : Wrap(isolate, transition);
    }

    public static JSValue TemporalZonedDateTimePrototypeToInstant(Isolate isolate, in BuiltinArguments args) =>
        Wrap(isolate, ZDT(isolate, args, "toInstant").ToInstant());

    public static JSValue TemporalZonedDateTimePrototypeToPlainDate(Isolate isolate, in BuiltinArguments args) =>
        Wrap(isolate, ZDT(isolate, args, "toPlainDate").ToPlainDate());

    public static JSValue TemporalZonedDateTimePrototypeToPlainTime(Isolate isolate, in BuiltinArguments args) =>
        Wrap(isolate, ZDT(isolate, args, "toPlainTime").ToPlainTime());

    public static JSValue TemporalZonedDateTimePrototypeToPlainDateTime(Isolate isolate, in BuiltinArguments args) =>
        Wrap(isolate, ZDT(isolate, args, "toPlainDateTime").ToPlainDateTime());

    // ======== Duration ===================================================================================

    const string kDuration = "Temporal.Duration.prototype.";

    public static JSValue TemporalDurationConstructor(Isolate isolate, in BuiltinArguments args)
    {
        if (args.NewTarget.IsUndefined) return ThrowNewTargetUndefined(isolate, "Temporal.Duration");
        Span<double> f = stackalloc double[10];
        for (int i = 0; i < 8; i++)
        {
            JSValue v = Arg(args, i + 1);
            f[i] = v.IsUndefined ? 0 : ToInt64IfIntegral(isolate, v);
        }
        for (int i = 8; i < 10; i++)
        {
            JSValue v = Arg(args, i + 1);
            f[i] = v.IsUndefined ? 0 : ToIntegerIfIntegral(isolate, v);
        }
        Duration value = Duration.Create(f[0], f[1], f[2], f[3], f[4], f[5], f[6], f[7], f[8], f[9]);
        return Wrap(isolate, value, args.Target, args.NewTarget);
    }

    public static JSValue TemporalDurationFrom(Isolate isolate, in BuiltinArguments args) =>
        ToTemporalDuration(isolate, Arg(args, 1), "Temporal.Duration.from");

    public static JSValue TemporalDurationCompare(Isolate isolate, in BuiltinArguments args)
    {
        const string method = "Temporal.Duration.compare";
        Duration one = ToTemporalDurationValue(isolate, Arg(args, 1), method);
        Duration two = ToTemporalDurationValue(isolate, Arg(args, 2), method);
        RelativeTo relativeTo = GetTemporalRelativeToOptionHandleUndefined(isolate, Arg(args, 3));
        return JSValue.FromInt(RelativeRounding.Compare(one, two, relativeTo));
    }

    static Duration D(Isolate isolate, in BuiltinArguments args, string name) =>
        Receiver<JSTemporalDuration>(isolate, args, kDuration + name).Value;

    public static JSValue TemporalDurationPrototypeYears(Isolate isolate, in BuiltinArguments args) =>
        JSValue.FromNumber(D(isolate, args, "years").Years);
    public static JSValue TemporalDurationPrototypeMonths(Isolate isolate, in BuiltinArguments args) =>
        JSValue.FromNumber(D(isolate, args, "months").Months);
    public static JSValue TemporalDurationPrototypeWeeks(Isolate isolate, in BuiltinArguments args) =>
        JSValue.FromNumber(D(isolate, args, "weeks").Weeks);
    public static JSValue TemporalDurationPrototypeDays(Isolate isolate, in BuiltinArguments args) =>
        JSValue.FromNumber(D(isolate, args, "days").Days);
    public static JSValue TemporalDurationPrototypeHours(Isolate isolate, in BuiltinArguments args) =>
        JSValue.FromNumber(D(isolate, args, "hours").Hours);
    public static JSValue TemporalDurationPrototypeMinutes(Isolate isolate, in BuiltinArguments args) =>
        JSValue.FromNumber(D(isolate, args, "minutes").Minutes);
    public static JSValue TemporalDurationPrototypeSeconds(Isolate isolate, in BuiltinArguments args) =>
        JSValue.FromNumber(D(isolate, args, "seconds").Seconds);
    public static JSValue TemporalDurationPrototypeMilliseconds(Isolate isolate, in BuiltinArguments args) =>
        JSValue.FromNumber(D(isolate, args, "milliseconds").Milliseconds);
    public static JSValue TemporalDurationPrototypeMicroseconds(Isolate isolate, in BuiltinArguments args) =>
        JSValue.FromNumber(D(isolate, args, "microseconds").Microseconds);
    public static JSValue TemporalDurationPrototypeNanoseconds(Isolate isolate, in BuiltinArguments args) =>
        JSValue.FromNumber(D(isolate, args, "nanoseconds").Nanoseconds);
    public static JSValue TemporalDurationPrototypeSign(Isolate isolate, in BuiltinArguments args) =>
        JSValue.FromInt(D(isolate, args, "sign").Sign);
    public static JSValue TemporalDurationPrototypeBlank(Isolate isolate, in BuiltinArguments args) =>
        JSValue.FromBoolean(D(isolate, args, "blank").Sign == 0);

    public static JSValue TemporalDurationPrototypeWith(Isolate isolate, in BuiltinArguments args)
    {
        Duration duration = D(isolate, args, "with");
        PartialDuration partial = ToTemporalPartialDurationRecord(isolate, Arg(args, 1));
        partial.Years ??= duration.Years;
        partial.Months ??= duration.Months;
        partial.Weeks ??= duration.Weeks;
        partial.Days ??= duration.Days;
        partial.Hours ??= duration.Hours;
        partial.Minutes ??= duration.Minutes;
        partial.Seconds ??= duration.Seconds;
        partial.Milliseconds ??= duration.Milliseconds;
        partial.Microseconds ??= duration.Microseconds;
        partial.Nanoseconds ??= duration.Nanoseconds;
        return Wrap(isolate, Duration.FromPartial(partial));
    }

    public static JSValue TemporalDurationPrototypeNegated(Isolate isolate, in BuiltinArguments args) =>
        Wrap(isolate, D(isolate, args, "negated").Negated());

    public static JSValue TemporalDurationPrototypeAbs(Isolate isolate, in BuiltinArguments args) =>
        Wrap(isolate, D(isolate, args, "abs").Abs());

    public static JSValue TemporalDurationPrototypeAdd(Isolate isolate, in BuiltinArguments args)
    {
        Duration duration = D(isolate, args, "add");
        Duration other = ToTemporalDurationValue(isolate, Arg(args, 1), "Temporal.Duration.prototype.add");
        return Wrap(isolate, duration.Add(other));
    }

    public static JSValue TemporalDurationPrototypeSubtract(Isolate isolate, in BuiltinArguments args)
    {
        Duration duration = D(isolate, args, "subtract");
        Duration other = ToTemporalDurationValue(isolate, Arg(args, 1), "Temporal.Duration.prototype.subtract");
        return Wrap(isolate, duration.Subtract(other));
    }

    public static JSValue TemporalDurationPrototypeRound(Isolate isolate, in BuiltinArguments args)
    {
        const string method = "Temporal.Duration.prototype.round";
        Duration duration = D(isolate, args, "round");
        JSValue roundToObj = Arg(args, 1);
        if (roundToObj.IsUndefined) ThrowTemporalTypeError(isolate, kRoundToMissing);
        JSReceiver roundTo = StringOrOptionsObject(isolate, roundToObj, "smallestUnit", kRoundToMustBeObject);
        Unit? largestUnit = GetTemporalUnitValuedOption(isolate, roundTo, "largestUnit", false, method);
        RelativeTo relativeTo = GetTemporalRelativeToOptionHandleUndefined(isolate, roundTo);
        uint increment = GetRoundingIncrementOption(isolate, roundTo);
        RoundingMode mode = GetRoundingModeOption(isolate, roundTo, RoundingMode.HalfExpand, method);
        Unit? smallestUnit = GetTemporalUnitValuedOption(isolate, roundTo, "smallestUnit", false, method);
        ValidateTemporalUnitValue(isolate, smallestUnit, UnitGroup.DateTime);
        var options = new RoundingOptions(largestUnit, smallestUnit, mode, increment);
        return Wrap(isolate, RelativeRounding.Round(duration, options, relativeTo));
    }

    public static JSValue TemporalDurationPrototypeTotal(Isolate isolate, in BuiltinArguments args)
    {
        const string method = "Temporal.Duration.prototype.total";
        Duration duration = D(isolate, args, "total");
        JSValue totalOfObj = Arg(args, 1);
        if (totalOfObj.IsUndefined) ThrowTemporalTypeError(isolate, "Must specify a totalOf parameter");
        JSReceiver totalOf = StringOrOptionsObject(isolate, totalOfObj, "unit", "totalOf must be an object.");
        RelativeTo relativeTo = GetTemporalRelativeToOptionHandleUndefined(isolate, totalOf);
        Unit? unit = GetTemporalUnitValuedOption(isolate, totalOf, "unit", true, method);
        ValidateTemporalUnitValue(isolate, unit, UnitGroup.DateTime);
        return JSValue.FromNumber(RelativeRounding.Total(duration, unit!.Value, relativeTo));
    }

    public static JSValue TemporalDurationPrototypeToString(Isolate isolate, in BuiltinArguments args)
    {
        const string method = "Temporal.Duration.prototype.toString";
        Duration duration = D(isolate, args, "toString");
        JSReceiver options = GetOptionsObject(isolate, Arg(args, 1), method);
        ToStringRoundingOptions rounding = GetToStringRoundingOptions(isolate, options, method);
        return NewString(isolate, duration.ToString(rounding.Precision, rounding.SmallestUnit,
            rounding.RoundingMode ?? RoundingMode.Trunc));
    }

    public static JSValue TemporalDurationPrototypeToJSON(Isolate isolate, in BuiltinArguments args) =>
        NewString(isolate, D(isolate, args, "toJSON").ToString());

    public static JSValue TemporalDurationPrototypeToLocaleString(Isolate isolate, in BuiltinArguments args) =>
        NewString(isolate, D(isolate, args, "toLocaleString").ToString());

    public static JSValue TemporalDurationPrototypeValueOf(Isolate isolate, in BuiltinArguments args) =>
        ThrowValueOf(isolate, "Duration");

    // ======== Instant ====================================================================================

    const string kInstant = "Temporal.Instant.prototype.";

    public static JSValue TemporalInstantConstructor(Isolate isolate, in BuiltinArguments args)
    {
        if (args.NewTarget.IsUndefined) return ThrowNewTargetUndefined(isolate, "Temporal.Instant");
        BigInt epochNanoseconds = BigInt.FromObject(isolate, Arg(args, 1));
        Int128 ns = GetI128FromBigInt(isolate, epochNanoseconds);
        return Wrap(isolate, Instant.TryNew(ns), args.Target, args.NewTarget);
    }

    public static JSValue TemporalInstantFrom(Isolate isolate, in BuiltinArguments args) =>
        Wrap(isolate, ToTemporalInstant(isolate, Arg(args, 1), "Temporal.Instant.from"));

    public static JSValue TemporalInstantFromEpochMilliseconds(Isolate isolate, in BuiltinArguments args)
    {
        double ms = ObjectOps.ToNumberValue(isolate, Arg(args, 1));
        if (!double.IsFinite(ms) || !(ms >= -9223372036854775808.0 && ms < 9223372036854775808.0) || Math.Round(ms) != ms)
        {
            ThrowTemporalRangeError(isolate, "Expected finite integer.");
        }
        return Wrap(isolate, Instant.FromEpochMilliseconds((long)ms));
    }

    public static JSValue TemporalInstantFromEpochNanoseconds(Isolate isolate, in BuiltinArguments args)
    {
        BigInt epochNanoseconds = BigInt.FromObject(isolate, Arg(args, 1));
        return Wrap(isolate, Instant.TryNew(GetI128FromBigInt(isolate, epochNanoseconds)));
    }

    public static JSValue TemporalInstantCompare(Isolate isolate, in BuiltinArguments args)
    {
        const string method = "Temporal.Instant.compare";
        Instant one = ToTemporalInstant(isolate, Arg(args, 1), method);
        Instant two = ToTemporalInstant(isolate, Arg(args, 2), method);
        return JSValue.FromInt(one.Compare(two));
    }

    static Instant I(Isolate isolate, in BuiltinArguments args, string name) =>
        Receiver<JSTemporalInstant>(isolate, args, kInstant + name).Value;

    public static JSValue TemporalInstantPrototypeEpochNanoseconds(Isolate isolate, in BuiltinArguments args) =>
        I128ToBigInt(isolate, I(isolate, args, "epochNanoseconds").EpochNanoseconds);

    public static JSValue TemporalInstantPrototypeEpochMilliseconds(Isolate isolate, in BuiltinArguments args) =>
        JSValue.FromNumber(I(isolate, args, "epochMilliseconds").EpochMilliseconds);

    public static JSValue TemporalInstantPrototypeAdd(Isolate isolate, in BuiltinArguments args)
    {
        Instant instant = I(isolate, args, "add");
        Duration duration = ToTemporalDurationValue(isolate, Arg(args, 1), "Temporal.Duration.prototype.add");
        return Wrap(isolate, instant.Add(duration));
    }

    public static JSValue TemporalInstantPrototypeSubtract(Isolate isolate, in BuiltinArguments args)
    {
        Instant instant = I(isolate, args, "subtract");
        Duration duration = ToTemporalDurationValue(isolate, Arg(args, 1), "Temporal.Duration.prototype.subtract");
        return Wrap(isolate, instant.Subtract(duration));
    }

    static JSValue InstantDifference(Isolate isolate, in BuiltinArguments args, bool isSince)
    {
        string name = isSince ? "since" : "until";
        string method = kInstant + name;
        Instant instant = I(isolate, args, name);
        Instant other = ToTemporalInstant(isolate, Arg(args, 1), method);
        DifferenceSettings settings = DifferenceSettingsFor(isolate, null, null, Arg(args, 2), UnitGroup.Time, Unit.Nanosecond,
            method);
        return Wrap(isolate, isSince ? instant.Since(other, settings) : instant.Until(other, settings));
    }

    public static JSValue TemporalInstantPrototypeUntil(Isolate isolate, in BuiltinArguments args) =>
        InstantDifference(isolate, args, false);

    public static JSValue TemporalInstantPrototypeSince(Isolate isolate, in BuiltinArguments args) =>
        InstantDifference(isolate, args, true);

    public static JSValue TemporalInstantPrototypeRound(Isolate isolate, in BuiltinArguments args)
    {
        const string method = "Temporal.Instant.prototype.round";
        Instant instant = I(isolate, args, "round");
        JSValue roundToObj = Arg(args, 1);
        if (roundToObj.IsUndefined) ThrowTemporalTypeError(isolate, kRoundToMissing);
        JSReceiver roundTo;
        if (roundToObj.IsString)
        {
            roundTo = StringOrOptionsObject(isolate, roundToObj, "smallestUnit", kRoundToMustBeObject);
        }
        else
        {
            roundTo = GetOptionsObject(isolate, roundToObj, method);
        }
        uint increment = GetRoundingIncrementOption(isolate, roundTo);
        RoundingMode mode = GetRoundingModeOption(isolate, roundTo, RoundingMode.HalfExpand, method);
        Unit? smallestUnit = GetTemporalUnitValuedOption(isolate, roundTo, "smallestUnit", true, method);
        ValidateTemporalUnitValue(isolate, smallestUnit, UnitGroup.Time);
        return Wrap(isolate, instant.Round(new RoundingOptions(null, smallestUnit, mode, increment)));
    }

    public static JSValue TemporalInstantPrototypeEquals(Isolate isolate, in BuiltinArguments args)
    {
        Instant instant = I(isolate, args, "equals");
        Instant other = ToTemporalInstant(isolate, Arg(args, 1), "Temporal.Instant.prototype.equals");
        return JSValue.FromBoolean(instant.EpochNanoseconds == other.EpochNanoseconds);
    }

    public static JSValue TemporalInstantPrototypeToString(Isolate isolate, in BuiltinArguments args)
    {
        const string method = "Temporal.Instant.prototype.toString";
        Instant instant = I(isolate, args, "toString");
        JSReceiver options = GetOptionsObject(isolate, Arg(args, 1), method);
        Precision digits = GetTemporalFractionalSecondDigitsOption(isolate, options, method);
        RoundingMode mode = GetRoundingModeOption(isolate, options, RoundingMode.Trunc, method);
        Unit? smallestUnit = GetTemporalUnitValuedOption(isolate, options, "smallestUnit", false, method);
        JSValue timeZoneObj = JSReceiver.GetProperty(isolate, options, isolate.Factory.InternalizeString("timeZone"));
        ValidateTemporalUnitValue(isolate, smallestUnit, UnitGroup.Time);
        if (smallestUnit == Unit.Hour)
        {
            isolate.ThrowRangeError(MessageTemplate.PropertyValueOutOfRange, isolate.Factory.InternalizeString("smallestUnit"));
        }
        TimeZone? timeZone = timeZoneObj.IsUndefined ? null : ToTemporalTimeZoneIdentifier(isolate, timeZoneObj);
        return NewString(isolate, instant.ToIxdtfString(timeZone, new ToStringRoundingOptions(digits, smallestUnit, mode)));
    }

    public static JSValue TemporalInstantPrototypeToJSON(Isolate isolate, in BuiltinArguments args) =>
        NewString(isolate, I(isolate, args, "toJSON").ToIxdtfString(null, ToStringRoundingOptions.Default));

    public static JSValue TemporalInstantPrototypeToLocaleString(Isolate isolate, in BuiltinArguments args) =>
        NewString(isolate, I(isolate, args, "toLocaleString").ToIxdtfString(null, ToStringRoundingOptions.Default));

    public static JSValue TemporalInstantPrototypeValueOf(Isolate isolate, in BuiltinArguments args) =>
        ThrowValueOf(isolate, "Instant");

    public static JSValue TemporalInstantPrototypeToZonedDateTimeISO(Isolate isolate, in BuiltinArguments args)
    {
        Instant instant = I(isolate, args, "toZonedDateTimeISO");
        TimeZone timeZone = ToTemporalTimeZoneIdentifier(isolate, Arg(args, 1));
        return Wrap(isolate, instant.ToZonedDateTimeISO(timeZone));
    }

    // ======== Date.prototype.toTemporalInstant (builtins-date.cc) ========================================

    public static JSValue DatePrototypeToTemporalInstant(Isolate isolate, in BuiltinArguments args)
    {
        if (args.Receiver.HeapObjectOrNull is not JSDate date)
        {
            return isolate.ThrowTypeError(MessageTemplate.IncompatibleMethodReceiver,
                Str(isolate, "Date.prototype.toTemporalInstant"), args.Receiver);
        }
        // 1. Let t be ? thisTimeValue(this value). 2. Let ns be ? NumberToBigInt(t) × 10^6.
        BigInt t = BigInt.FromNumber(isolate, JSValue.FromNumber(date.Value));
        BigInt ns = BigInt.Multiply(isolate, t, BigInt.FromInt64(isolate, 1_000_000));
        // 3. Return ! CreateTemporalInstant(ns).
        return Wrap(isolate, Instant.TryNew(GetI128FromBigInt(isolate, ns)));
    }
}

public static partial class BuiltinRegistry
{
    /// <summary>Wraps a Temporal builtin so that engine errors become JS errors (ExtractRustResult).</summary>
    static BuiltinFunction Guard(BuiltinFunction f) => (Isolate isolate, in BuiltinArguments args) =>
    {
        try
        {
            return f(isolate, in args);
        }
        catch (TemporalError e)
        {
            return TemporalBuiltins.ThrowTemporalError(isolate, e);
        }
    };

    static partial void RegisterTemporal()
    {
        // Every BUILTIN_LIST_TEMPORAL builtin is a public static method of TemporalBuiltins with the same name.
        foreach (System.Reflection.MethodInfo method in typeof(TemporalBuiltins).GetMethods(
                     System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static))
        {
            if (!Enum.TryParse(method.Name, out Builtin builtin)) continue;
            var function = (BuiltinFunction)Delegate.CreateDelegate(typeof(BuiltinFunction), method);
            Register(builtin, Guard(function));
        }
    }
}
