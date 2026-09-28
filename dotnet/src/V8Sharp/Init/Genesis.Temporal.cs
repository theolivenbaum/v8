// Port of the Temporal part of src/init/bootstrapper.cc: InitializeTemporal,
// LazyInitializeDateToTemporalInstant, LazyInitializeGlobalThisTemporal,
// the NATIVE_CONTEXT_FIELDS_TEMPORAL case of
// Bootstrapper::InitializeLazyPartOfContext, and
// Genesis::InitializeGlobal_harmony_temporal. The install lists are V8's
// (NOW_LIST, PLAIN_DATE_GETTER_LIST, ...), in V8's order.
namespace V8Sharp.Init;

sealed partial class Genesis
{
    /// <summary>InitializeTemporal: creates the Temporal namespace object of the current native context once.</summary>
    public static JSObject InitializeTemporal(Isolate isolate)
    {
        NativeContext nativeContext = isolate.NativeContext;
        // Already initialized?
        if (nativeContext.TemporalObject.HeapObjectOrNull is JSObject existing) return existing;

        isolate.CountUsage("kTemporalObject");
        Factory factory = isolate.Factory;

        // -- T e m p o r a l
        JSObject temporal = factory.NewJSObject(nativeContext.ObjectFunction);
        Bootstrapper.InstallToStringTag(isolate, temporal, "Temporal");

        {   // -- N o w
            JSObject now = factory.NewJSObject(nativeContext.ObjectFunction);
            JSObject.AddProperty(isolate, temporal, factory.InternalizeString("Now"), now, PropertyAttributes.DONT_ENUM);
            Bootstrapper.InstallToStringTag(isolate, now, "Temporal.Now");
            // Note: There are NO Temporal.Now.plainTime
            Bootstrapper.SimpleInstallFunction(isolate, now, "instant", Builtin.TemporalNowInstant, 0, false);
            Bootstrapper.SimpleInstallFunction(isolate, now, "timeZoneId", Builtin.TemporalNowTimeZoneId, 0, false);
            Bootstrapper.SimpleInstallFunction(isolate, now, "plainDateTimeISO", Builtin.TemporalNowPlainDateTimeISO, 0, false);
            Bootstrapper.SimpleInstallFunction(isolate, now, "zonedDateTimeISO", Builtin.TemporalNowZonedDateTimeISO, 0, false);
            Bootstrapper.SimpleInstallFunction(isolate, now, "plainDateISO", Builtin.TemporalNowPlainDateISO, 0, false);
            Bootstrapper.SimpleInstallFunction(isolate, now, "plainTimeISO", Builtin.TemporalNowPlainTimeISO, 0, false);
        }
        {   // -- PlainDate
            JSFunction objFunc = Bootstrapper.InstallFunction(isolate, temporal, "PlainDate", InstanceType.JSTemporalPlainDateType,
                JSObject.GetHeaderSize(InstanceType.JSTemporalPlainDateType), 0, JSValue.TheHole, Builtin.TemporalPlainDateConstructor, 3,
                false);
            Bootstrapper.InstallWithIntrinsicDefaultProto(isolate, objFunc, Context.Field.JS_TEMPORAL_PLAIN_DATE_FUNCTION_INDEX);
            var prototype = (JSObject)objFunc.InstancePrototype;
            Bootstrapper.InstallToStringTag(isolate, prototype, "Temporal.PlainDate");
            Bootstrapper.SimpleInstallFunction(isolate, objFunc, "from", Builtin.TemporalPlainDateFrom, 1, false);
            Bootstrapper.SimpleInstallFunction(isolate, objFunc, "compare", Builtin.TemporalPlainDateCompare, 2, false);
            Bootstrapper.SimpleInstallGetter(isolate, prototype, factory.InternalizeString("era"), Builtin.TemporalPlainDatePrototypeEra, true);
            Bootstrapper.SimpleInstallGetter(isolate, prototype, factory.InternalizeString("eraYear"), Builtin.TemporalPlainDatePrototypeEraYear, true);
            Bootstrapper.SimpleInstallGetter(isolate, prototype, factory.InternalizeString("calendarId"), Builtin.TemporalPlainDatePrototypeCalendarId, true);
            Bootstrapper.SimpleInstallGetter(isolate, prototype, factory.InternalizeString("year"), Builtin.TemporalPlainDatePrototypeYear, true);
            Bootstrapper.SimpleInstallGetter(isolate, prototype, factory.InternalizeString("month"), Builtin.TemporalPlainDatePrototypeMonth, true);
            Bootstrapper.SimpleInstallGetter(isolate, prototype, factory.InternalizeString("monthCode"), Builtin.TemporalPlainDatePrototypeMonthCode, true);
            Bootstrapper.SimpleInstallGetter(isolate, prototype, factory.InternalizeString("day"), Builtin.TemporalPlainDatePrototypeDay, true);
            Bootstrapper.SimpleInstallGetter(isolate, prototype, factory.InternalizeString("dayOfWeek"), Builtin.TemporalPlainDatePrototypeDayOfWeek, true);
            Bootstrapper.SimpleInstallGetter(isolate, prototype, factory.InternalizeString("dayOfYear"), Builtin.TemporalPlainDatePrototypeDayOfYear, true);
            Bootstrapper.SimpleInstallGetter(isolate, prototype, factory.InternalizeString("weekOfYear"), Builtin.TemporalPlainDatePrototypeWeekOfYear, true);
            Bootstrapper.SimpleInstallGetter(isolate, prototype, factory.InternalizeString("yearOfWeek"), Builtin.TemporalPlainDatePrototypeYearOfWeek, true);
            Bootstrapper.SimpleInstallGetter(isolate, prototype, factory.InternalizeString("daysInWeek"), Builtin.TemporalPlainDatePrototypeDaysInWeek, true);
            Bootstrapper.SimpleInstallGetter(isolate, prototype, factory.InternalizeString("daysInMonth"), Builtin.TemporalPlainDatePrototypeDaysInMonth, true);
            Bootstrapper.SimpleInstallGetter(isolate, prototype, factory.InternalizeString("daysInYear"), Builtin.TemporalPlainDatePrototypeDaysInYear, true);
            Bootstrapper.SimpleInstallGetter(isolate, prototype, factory.InternalizeString("monthsInYear"), Builtin.TemporalPlainDatePrototypeMonthsInYear, true);
            Bootstrapper.SimpleInstallGetter(isolate, prototype, factory.InternalizeString("inLeapYear"), Builtin.TemporalPlainDatePrototypeInLeapYear, true);
            Bootstrapper.SimpleInstallFunction(isolate, prototype, "toPlainYearMonth", Builtin.TemporalPlainDatePrototypeToPlainYearMonth, 0, false);
            Bootstrapper.SimpleInstallFunction(isolate, prototype, "toPlainMonthDay", Builtin.TemporalPlainDatePrototypeToPlainMonthDay, 0, false);
            Bootstrapper.SimpleInstallFunction(isolate, prototype, "add", Builtin.TemporalPlainDatePrototypeAdd, 1, false);
            Bootstrapper.SimpleInstallFunction(isolate, prototype, "subtract", Builtin.TemporalPlainDatePrototypeSubtract, 1, false);
            Bootstrapper.SimpleInstallFunction(isolate, prototype, "with", Builtin.TemporalPlainDatePrototypeWith, 1, false);
            Bootstrapper.SimpleInstallFunction(isolate, prototype, "withCalendar", Builtin.TemporalPlainDatePrototypeWithCalendar, 1, false);
            Bootstrapper.SimpleInstallFunction(isolate, prototype, "until", Builtin.TemporalPlainDatePrototypeUntil, 1, false);
            Bootstrapper.SimpleInstallFunction(isolate, prototype, "since", Builtin.TemporalPlainDatePrototypeSince, 1, false);
            Bootstrapper.SimpleInstallFunction(isolate, prototype, "equals", Builtin.TemporalPlainDatePrototypeEquals, 1, false);
            Bootstrapper.SimpleInstallFunction(isolate, prototype, "toPlainDateTime", Builtin.TemporalPlainDatePrototypeToPlainDateTime, 0, false);
            Bootstrapper.SimpleInstallFunction(isolate, prototype, "toZonedDateTime", Builtin.TemporalPlainDatePrototypeToZonedDateTime, 1, false);
            Bootstrapper.SimpleInstallFunction(isolate, prototype, "toString", Builtin.TemporalPlainDatePrototypeToString, 0, false);
            Bootstrapper.SimpleInstallFunction(isolate, prototype, "toLocaleString", Builtin.TemporalPlainDatePrototypeToLocaleString, 0, false);
            Bootstrapper.SimpleInstallFunction(isolate, prototype, "toJSON", Builtin.TemporalPlainDatePrototypeToJSON, 0, false);
            Bootstrapper.SimpleInstallFunction(isolate, prototype, "valueOf", Builtin.TemporalPlainDatePrototypeValueOf, 0, false);
        }
        {   // -- PlainTime
            JSFunction objFunc = Bootstrapper.InstallFunction(isolate, temporal, "PlainTime", InstanceType.JSTemporalPlainTimeType,
                JSObject.GetHeaderSize(InstanceType.JSTemporalPlainTimeType), 0, JSValue.TheHole, Builtin.TemporalPlainTimeConstructor, 0,
                false);
            Bootstrapper.InstallWithIntrinsicDefaultProto(isolate, objFunc, Context.Field.JS_TEMPORAL_PLAIN_TIME_FUNCTION_INDEX);
            var prototype = (JSObject)objFunc.InstancePrototype;
            Bootstrapper.InstallToStringTag(isolate, prototype, "Temporal.PlainTime");
            Bootstrapper.SimpleInstallFunction(isolate, objFunc, "from", Builtin.TemporalPlainTimeFrom, 1, false);
            Bootstrapper.SimpleInstallFunction(isolate, objFunc, "compare", Builtin.TemporalPlainTimeCompare, 2, false);
            Bootstrapper.SimpleInstallGetter(isolate, prototype, factory.InternalizeString("hour"), Builtin.TemporalPlainTimePrototypeHour, true);
            Bootstrapper.SimpleInstallGetter(isolate, prototype, factory.InternalizeString("minute"), Builtin.TemporalPlainTimePrototypeMinute, true);
            Bootstrapper.SimpleInstallGetter(isolate, prototype, factory.InternalizeString("second"), Builtin.TemporalPlainTimePrototypeSecond, true);
            Bootstrapper.SimpleInstallGetter(isolate, prototype, factory.InternalizeString("millisecond"), Builtin.TemporalPlainTimePrototypeMillisecond, true);
            Bootstrapper.SimpleInstallGetter(isolate, prototype, factory.InternalizeString("microsecond"), Builtin.TemporalPlainTimePrototypeMicrosecond, true);
            Bootstrapper.SimpleInstallGetter(isolate, prototype, factory.InternalizeString("nanosecond"), Builtin.TemporalPlainTimePrototypeNanosecond, true);
            Bootstrapper.SimpleInstallFunction(isolate, prototype, "add", Builtin.TemporalPlainTimePrototypeAdd, 1, false);
            Bootstrapper.SimpleInstallFunction(isolate, prototype, "subtract", Builtin.TemporalPlainTimePrototypeSubtract, 1, false);
            Bootstrapper.SimpleInstallFunction(isolate, prototype, "with", Builtin.TemporalPlainTimePrototypeWith, 1, false);
            Bootstrapper.SimpleInstallFunction(isolate, prototype, "until", Builtin.TemporalPlainTimePrototypeUntil, 1, false);
            Bootstrapper.SimpleInstallFunction(isolate, prototype, "since", Builtin.TemporalPlainTimePrototypeSince, 1, false);
            Bootstrapper.SimpleInstallFunction(isolate, prototype, "round", Builtin.TemporalPlainTimePrototypeRound, 1, false);
            Bootstrapper.SimpleInstallFunction(isolate, prototype, "equals", Builtin.TemporalPlainTimePrototypeEquals, 1, false);
            Bootstrapper.SimpleInstallFunction(isolate, prototype, "toLocaleString", Builtin.TemporalPlainTimePrototypeToLocaleString, 0, false);
            Bootstrapper.SimpleInstallFunction(isolate, prototype, "toString", Builtin.TemporalPlainTimePrototypeToString, 0, false);
            Bootstrapper.SimpleInstallFunction(isolate, prototype, "toJSON", Builtin.TemporalPlainTimePrototypeToJSON, 0, false);
            Bootstrapper.SimpleInstallFunction(isolate, prototype, "valueOf", Builtin.TemporalPlainTimePrototypeValueOf, 0, false);
        }
        {   // -- PlainDateTime
            JSFunction objFunc = Bootstrapper.InstallFunction(isolate, temporal, "PlainDateTime", InstanceType.JSTemporalPlainDateTimeType,
                JSObject.GetHeaderSize(InstanceType.JSTemporalPlainDateTimeType), 0, JSValue.TheHole, Builtin.TemporalPlainDateTimeConstructor, 3,
                false);
            Bootstrapper.InstallWithIntrinsicDefaultProto(isolate, objFunc, Context.Field.JS_TEMPORAL_PLAIN_DATE_TIME_FUNCTION_INDEX);
            var prototype = (JSObject)objFunc.InstancePrototype;
            Bootstrapper.InstallToStringTag(isolate, prototype, "Temporal.PlainDateTime");
            Bootstrapper.SimpleInstallFunction(isolate, objFunc, "from", Builtin.TemporalPlainDateTimeFrom, 1, false);
            Bootstrapper.SimpleInstallFunction(isolate, objFunc, "compare", Builtin.TemporalPlainDateTimeCompare, 2, false);
            Bootstrapper.SimpleInstallGetter(isolate, prototype, factory.InternalizeString("calendarId"), Builtin.TemporalPlainDateTimePrototypeCalendarId, true);
            Bootstrapper.SimpleInstallGetter(isolate, prototype, factory.InternalizeString("era"), Builtin.TemporalPlainDateTimePrototypeEra, true);
            Bootstrapper.SimpleInstallGetter(isolate, prototype, factory.InternalizeString("eraYear"), Builtin.TemporalPlainDateTimePrototypeEraYear, true);
            Bootstrapper.SimpleInstallGetter(isolate, prototype, factory.InternalizeString("year"), Builtin.TemporalPlainDateTimePrototypeYear, true);
            Bootstrapper.SimpleInstallGetter(isolate, prototype, factory.InternalizeString("month"), Builtin.TemporalPlainDateTimePrototypeMonth, true);
            Bootstrapper.SimpleInstallGetter(isolate, prototype, factory.InternalizeString("monthCode"), Builtin.TemporalPlainDateTimePrototypeMonthCode, true);
            Bootstrapper.SimpleInstallGetter(isolate, prototype, factory.InternalizeString("day"), Builtin.TemporalPlainDateTimePrototypeDay, true);
            Bootstrapper.SimpleInstallGetter(isolate, prototype, factory.InternalizeString("hour"), Builtin.TemporalPlainDateTimePrototypeHour, true);
            Bootstrapper.SimpleInstallGetter(isolate, prototype, factory.InternalizeString("minute"), Builtin.TemporalPlainDateTimePrototypeMinute, true);
            Bootstrapper.SimpleInstallGetter(isolate, prototype, factory.InternalizeString("second"), Builtin.TemporalPlainDateTimePrototypeSecond, true);
            Bootstrapper.SimpleInstallGetter(isolate, prototype, factory.InternalizeString("millisecond"), Builtin.TemporalPlainDateTimePrototypeMillisecond, true);
            Bootstrapper.SimpleInstallGetter(isolate, prototype, factory.InternalizeString("microsecond"), Builtin.TemporalPlainDateTimePrototypeMicrosecond, true);
            Bootstrapper.SimpleInstallGetter(isolate, prototype, factory.InternalizeString("nanosecond"), Builtin.TemporalPlainDateTimePrototypeNanosecond, true);
            Bootstrapper.SimpleInstallGetter(isolate, prototype, factory.InternalizeString("dayOfWeek"), Builtin.TemporalPlainDateTimePrototypeDayOfWeek, true);
            Bootstrapper.SimpleInstallGetter(isolate, prototype, factory.InternalizeString("dayOfYear"), Builtin.TemporalPlainDateTimePrototypeDayOfYear, true);
            Bootstrapper.SimpleInstallGetter(isolate, prototype, factory.InternalizeString("weekOfYear"), Builtin.TemporalPlainDateTimePrototypeWeekOfYear, true);
            Bootstrapper.SimpleInstallGetter(isolate, prototype, factory.InternalizeString("yearOfWeek"), Builtin.TemporalPlainDateTimePrototypeYearOfWeek, true);
            Bootstrapper.SimpleInstallGetter(isolate, prototype, factory.InternalizeString("daysInWeek"), Builtin.TemporalPlainDateTimePrototypeDaysInWeek, true);
            Bootstrapper.SimpleInstallGetter(isolate, prototype, factory.InternalizeString("daysInMonth"), Builtin.TemporalPlainDateTimePrototypeDaysInMonth, true);
            Bootstrapper.SimpleInstallGetter(isolate, prototype, factory.InternalizeString("daysInYear"), Builtin.TemporalPlainDateTimePrototypeDaysInYear, true);
            Bootstrapper.SimpleInstallGetter(isolate, prototype, factory.InternalizeString("monthsInYear"), Builtin.TemporalPlainDateTimePrototypeMonthsInYear, true);
            Bootstrapper.SimpleInstallGetter(isolate, prototype, factory.InternalizeString("inLeapYear"), Builtin.TemporalPlainDateTimePrototypeInLeapYear, true);
            Bootstrapper.SimpleInstallFunction(isolate, prototype, "with", Builtin.TemporalPlainDateTimePrototypeWith, 1, false);
            Bootstrapper.SimpleInstallFunction(isolate, prototype, "withCalendar", Builtin.TemporalPlainDateTimePrototypeWithCalendar, 1, false);
            Bootstrapper.SimpleInstallFunction(isolate, prototype, "withPlainTime", Builtin.TemporalPlainDateTimePrototypeWithPlainTime, 0, false);
            Bootstrapper.SimpleInstallFunction(isolate, prototype, "add", Builtin.TemporalPlainDateTimePrototypeAdd, 1, false);
            Bootstrapper.SimpleInstallFunction(isolate, prototype, "subtract", Builtin.TemporalPlainDateTimePrototypeSubtract, 1, false);
            Bootstrapper.SimpleInstallFunction(isolate, prototype, "until", Builtin.TemporalPlainDateTimePrototypeUntil, 1, false);
            Bootstrapper.SimpleInstallFunction(isolate, prototype, "since", Builtin.TemporalPlainDateTimePrototypeSince, 1, false);
            Bootstrapper.SimpleInstallFunction(isolate, prototype, "round", Builtin.TemporalPlainDateTimePrototypeRound, 1, false);
            Bootstrapper.SimpleInstallFunction(isolate, prototype, "equals", Builtin.TemporalPlainDateTimePrototypeEquals, 1, false);
            Bootstrapper.SimpleInstallFunction(isolate, prototype, "toLocaleString", Builtin.TemporalPlainDateTimePrototypeToLocaleString, 0, false);
            Bootstrapper.SimpleInstallFunction(isolate, prototype, "toJSON", Builtin.TemporalPlainDateTimePrototypeToJSON, 0, false);
            Bootstrapper.SimpleInstallFunction(isolate, prototype, "toString", Builtin.TemporalPlainDateTimePrototypeToString, 0, false);
            Bootstrapper.SimpleInstallFunction(isolate, prototype, "valueOf", Builtin.TemporalPlainDateTimePrototypeValueOf, 0, false);
            Bootstrapper.SimpleInstallFunction(isolate, prototype, "toZonedDateTime", Builtin.TemporalPlainDateTimePrototypeToZonedDateTime, 1, false);
            Bootstrapper.SimpleInstallFunction(isolate, prototype, "toPlainDate", Builtin.TemporalPlainDateTimePrototypeToPlainDate, 0, false);
            Bootstrapper.SimpleInstallFunction(isolate, prototype, "toPlainTime", Builtin.TemporalPlainDateTimePrototypeToPlainTime, 0, false);
        }
        {   // -- ZonedDateTime
            JSFunction objFunc = Bootstrapper.InstallFunction(isolate, temporal, "ZonedDateTime", InstanceType.JSTemporalZonedDateTimeType,
                JSObject.GetHeaderSize(InstanceType.JSTemporalZonedDateTimeType), 0, JSValue.TheHole, Builtin.TemporalZonedDateTimeConstructor, 2,
                false);
            Bootstrapper.InstallWithIntrinsicDefaultProto(isolate, objFunc, Context.Field.JS_TEMPORAL_ZONED_DATE_TIME_FUNCTION_INDEX);
            var prototype = (JSObject)objFunc.InstancePrototype;
            Bootstrapper.InstallToStringTag(isolate, prototype, "Temporal.ZonedDateTime");
            Bootstrapper.SimpleInstallFunction(isolate, objFunc, "from", Builtin.TemporalZonedDateTimeFrom, 1, false);
            Bootstrapper.SimpleInstallFunction(isolate, objFunc, "compare", Builtin.TemporalZonedDateTimeCompare, 2, false);
            Bootstrapper.SimpleInstallGetter(isolate, prototype, factory.InternalizeString("timeZoneId"), Builtin.TemporalZonedDateTimePrototypeTimeZoneId, true);
            Bootstrapper.SimpleInstallGetter(isolate, prototype, factory.InternalizeString("calendarId"), Builtin.TemporalZonedDateTimePrototypeCalendarId, true);
            Bootstrapper.SimpleInstallGetter(isolate, prototype, factory.InternalizeString("era"), Builtin.TemporalZonedDateTimePrototypeEra, true);
            Bootstrapper.SimpleInstallGetter(isolate, prototype, factory.InternalizeString("eraYear"), Builtin.TemporalZonedDateTimePrototypeEraYear, true);
            Bootstrapper.SimpleInstallGetter(isolate, prototype, factory.InternalizeString("year"), Builtin.TemporalZonedDateTimePrototypeYear, true);
            Bootstrapper.SimpleInstallGetter(isolate, prototype, factory.InternalizeString("month"), Builtin.TemporalZonedDateTimePrototypeMonth, true);
            Bootstrapper.SimpleInstallGetter(isolate, prototype, factory.InternalizeString("monthCode"), Builtin.TemporalZonedDateTimePrototypeMonthCode, true);
            Bootstrapper.SimpleInstallGetter(isolate, prototype, factory.InternalizeString("day"), Builtin.TemporalZonedDateTimePrototypeDay, true);
            Bootstrapper.SimpleInstallGetter(isolate, prototype, factory.InternalizeString("hour"), Builtin.TemporalZonedDateTimePrototypeHour, true);
            Bootstrapper.SimpleInstallGetter(isolate, prototype, factory.InternalizeString("minute"), Builtin.TemporalZonedDateTimePrototypeMinute, true);
            Bootstrapper.SimpleInstallGetter(isolate, prototype, factory.InternalizeString("second"), Builtin.TemporalZonedDateTimePrototypeSecond, true);
            Bootstrapper.SimpleInstallGetter(isolate, prototype, factory.InternalizeString("millisecond"), Builtin.TemporalZonedDateTimePrototypeMillisecond, true);
            Bootstrapper.SimpleInstallGetter(isolate, prototype, factory.InternalizeString("microsecond"), Builtin.TemporalZonedDateTimePrototypeMicrosecond, true);
            Bootstrapper.SimpleInstallGetter(isolate, prototype, factory.InternalizeString("nanosecond"), Builtin.TemporalZonedDateTimePrototypeNanosecond, true);
            Bootstrapper.SimpleInstallGetter(isolate, prototype, factory.InternalizeString("epochMilliseconds"), Builtin.TemporalZonedDateTimePrototypeEpochMilliseconds, true);
            Bootstrapper.SimpleInstallGetter(isolate, prototype, factory.InternalizeString("epochNanoseconds"), Builtin.TemporalZonedDateTimePrototypeEpochNanoseconds, true);
            Bootstrapper.SimpleInstallGetter(isolate, prototype, factory.InternalizeString("dayOfWeek"), Builtin.TemporalZonedDateTimePrototypeDayOfWeek, true);
            Bootstrapper.SimpleInstallGetter(isolate, prototype, factory.InternalizeString("dayOfYear"), Builtin.TemporalZonedDateTimePrototypeDayOfYear, true);
            Bootstrapper.SimpleInstallGetter(isolate, prototype, factory.InternalizeString("weekOfYear"), Builtin.TemporalZonedDateTimePrototypeWeekOfYear, true);
            Bootstrapper.SimpleInstallGetter(isolate, prototype, factory.InternalizeString("yearOfWeek"), Builtin.TemporalZonedDateTimePrototypeYearOfWeek, true);
            Bootstrapper.SimpleInstallGetter(isolate, prototype, factory.InternalizeString("hoursInDay"), Builtin.TemporalZonedDateTimePrototypeHoursInDay, true);
            Bootstrapper.SimpleInstallGetter(isolate, prototype, factory.InternalizeString("daysInWeek"), Builtin.TemporalZonedDateTimePrototypeDaysInWeek, true);
            Bootstrapper.SimpleInstallGetter(isolate, prototype, factory.InternalizeString("daysInMonth"), Builtin.TemporalZonedDateTimePrototypeDaysInMonth, true);
            Bootstrapper.SimpleInstallGetter(isolate, prototype, factory.InternalizeString("daysInYear"), Builtin.TemporalZonedDateTimePrototypeDaysInYear, true);
            Bootstrapper.SimpleInstallGetter(isolate, prototype, factory.InternalizeString("monthsInYear"), Builtin.TemporalZonedDateTimePrototypeMonthsInYear, true);
            Bootstrapper.SimpleInstallGetter(isolate, prototype, factory.InternalizeString("inLeapYear"), Builtin.TemporalZonedDateTimePrototypeInLeapYear, true);
            Bootstrapper.SimpleInstallGetter(isolate, prototype, factory.InternalizeString("offsetNanoseconds"), Builtin.TemporalZonedDateTimePrototypeOffsetNanoseconds, true);
            Bootstrapper.SimpleInstallGetter(isolate, prototype, factory.InternalizeString("offset"), Builtin.TemporalZonedDateTimePrototypeOffset, true);
            Bootstrapper.SimpleInstallFunction(isolate, prototype, "with", Builtin.TemporalZonedDateTimePrototypeWith, 1, false);
            Bootstrapper.SimpleInstallFunction(isolate, prototype, "withCalendar", Builtin.TemporalZonedDateTimePrototypeWithCalendar, 1, false);
            Bootstrapper.SimpleInstallFunction(isolate, prototype, "withPlainTime", Builtin.TemporalZonedDateTimePrototypeWithPlainTime, 0, false);
            Bootstrapper.SimpleInstallFunction(isolate, prototype, "withTimeZone", Builtin.TemporalZonedDateTimePrototypeWithTimeZone, 1, false);
            Bootstrapper.SimpleInstallFunction(isolate, prototype, "add", Builtin.TemporalZonedDateTimePrototypeAdd, 1, false);
            Bootstrapper.SimpleInstallFunction(isolate, prototype, "subtract", Builtin.TemporalZonedDateTimePrototypeSubtract, 1, false);
            Bootstrapper.SimpleInstallFunction(isolate, prototype, "until", Builtin.TemporalZonedDateTimePrototypeUntil, 1, false);
            Bootstrapper.SimpleInstallFunction(isolate, prototype, "since", Builtin.TemporalZonedDateTimePrototypeSince, 1, false);
            Bootstrapper.SimpleInstallFunction(isolate, prototype, "round", Builtin.TemporalZonedDateTimePrototypeRound, 1, false);
            Bootstrapper.SimpleInstallFunction(isolate, prototype, "equals", Builtin.TemporalZonedDateTimePrototypeEquals, 1, false);
            Bootstrapper.SimpleInstallFunction(isolate, prototype, "toLocaleString", Builtin.TemporalZonedDateTimePrototypeToLocaleString, 0, false);
            Bootstrapper.SimpleInstallFunction(isolate, prototype, "toString", Builtin.TemporalZonedDateTimePrototypeToString, 0, false);
            Bootstrapper.SimpleInstallFunction(isolate, prototype, "toJSON", Builtin.TemporalZonedDateTimePrototypeToJSON, 0, false);
            Bootstrapper.SimpleInstallFunction(isolate, prototype, "valueOf", Builtin.TemporalZonedDateTimePrototypeValueOf, 0, false);
            Bootstrapper.SimpleInstallFunction(isolate, prototype, "startOfDay", Builtin.TemporalZonedDateTimePrototypeStartOfDay, 0, false);
            Bootstrapper.SimpleInstallFunction(isolate, prototype, "getTimeZoneTransition", Builtin.TemporalZonedDateTimePrototypeGetTimeZoneTransition, 1, false);
            Bootstrapper.SimpleInstallFunction(isolate, prototype, "toInstant", Builtin.TemporalZonedDateTimePrototypeToInstant, 0, false);
            Bootstrapper.SimpleInstallFunction(isolate, prototype, "toPlainDate", Builtin.TemporalZonedDateTimePrototypeToPlainDate, 0, false);
            Bootstrapper.SimpleInstallFunction(isolate, prototype, "toPlainTime", Builtin.TemporalZonedDateTimePrototypeToPlainTime, 0, false);
            Bootstrapper.SimpleInstallFunction(isolate, prototype, "toPlainDateTime", Builtin.TemporalZonedDateTimePrototypeToPlainDateTime, 0, false);
        }
        {   // -- Duration
            JSFunction objFunc = Bootstrapper.InstallFunction(isolate, temporal, "Duration", InstanceType.JSTemporalDurationType,
                JSObject.GetHeaderSize(InstanceType.JSTemporalDurationType), 0, JSValue.TheHole, Builtin.TemporalDurationConstructor, 0,
                false);
            Bootstrapper.InstallWithIntrinsicDefaultProto(isolate, objFunc, Context.Field.JS_TEMPORAL_DURATION_FUNCTION_INDEX);
            var prototype = (JSObject)objFunc.InstancePrototype;
            Bootstrapper.InstallToStringTag(isolate, prototype, "Temporal.Duration");
            Bootstrapper.SimpleInstallFunction(isolate, objFunc, "from", Builtin.TemporalDurationFrom, 1, false);
            Bootstrapper.SimpleInstallFunction(isolate, objFunc, "compare", Builtin.TemporalDurationCompare, 2, false);
            Bootstrapper.SimpleInstallGetter(isolate, prototype, factory.InternalizeString("years"), Builtin.TemporalDurationPrototypeYears, true);
            Bootstrapper.SimpleInstallGetter(isolate, prototype, factory.InternalizeString("months"), Builtin.TemporalDurationPrototypeMonths, true);
            Bootstrapper.SimpleInstallGetter(isolate, prototype, factory.InternalizeString("weeks"), Builtin.TemporalDurationPrototypeWeeks, true);
            Bootstrapper.SimpleInstallGetter(isolate, prototype, factory.InternalizeString("days"), Builtin.TemporalDurationPrototypeDays, true);
            Bootstrapper.SimpleInstallGetter(isolate, prototype, factory.InternalizeString("hours"), Builtin.TemporalDurationPrototypeHours, true);
            Bootstrapper.SimpleInstallGetter(isolate, prototype, factory.InternalizeString("minutes"), Builtin.TemporalDurationPrototypeMinutes, true);
            Bootstrapper.SimpleInstallGetter(isolate, prototype, factory.InternalizeString("seconds"), Builtin.TemporalDurationPrototypeSeconds, true);
            Bootstrapper.SimpleInstallGetter(isolate, prototype, factory.InternalizeString("milliseconds"), Builtin.TemporalDurationPrototypeMilliseconds, true);
            Bootstrapper.SimpleInstallGetter(isolate, prototype, factory.InternalizeString("microseconds"), Builtin.TemporalDurationPrototypeMicroseconds, true);
            Bootstrapper.SimpleInstallGetter(isolate, prototype, factory.InternalizeString("nanoseconds"), Builtin.TemporalDurationPrototypeNanoseconds, true);
            Bootstrapper.SimpleInstallGetter(isolate, prototype, factory.InternalizeString("sign"), Builtin.TemporalDurationPrototypeSign, true);
            Bootstrapper.SimpleInstallGetter(isolate, prototype, factory.InternalizeString("blank"), Builtin.TemporalDurationPrototypeBlank, true);
            Bootstrapper.SimpleInstallFunction(isolate, prototype, "with", Builtin.TemporalDurationPrototypeWith, 1, false);
            Bootstrapper.SimpleInstallFunction(isolate, prototype, "negated", Builtin.TemporalDurationPrototypeNegated, 0, false);
            Bootstrapper.SimpleInstallFunction(isolate, prototype, "abs", Builtin.TemporalDurationPrototypeAbs, 0, false);
            Bootstrapper.SimpleInstallFunction(isolate, prototype, "add", Builtin.TemporalDurationPrototypeAdd, 1, false);
            Bootstrapper.SimpleInstallFunction(isolate, prototype, "subtract", Builtin.TemporalDurationPrototypeSubtract, 1, false);
            Bootstrapper.SimpleInstallFunction(isolate, prototype, "round", Builtin.TemporalDurationPrototypeRound, 1, false);
            Bootstrapper.SimpleInstallFunction(isolate, prototype, "total", Builtin.TemporalDurationPrototypeTotal, 1, false);
            Bootstrapper.SimpleInstallFunction(isolate, prototype, "toLocaleString", Builtin.TemporalDurationPrototypeToLocaleString, 0, false);
            Bootstrapper.SimpleInstallFunction(isolate, prototype, "toString", Builtin.TemporalDurationPrototypeToString, 0, false);
            Bootstrapper.SimpleInstallFunction(isolate, prototype, "toJSON", Builtin.TemporalDurationPrototypeToJSON, 0, false);
            Bootstrapper.SimpleInstallFunction(isolate, prototype, "valueOf", Builtin.TemporalDurationPrototypeValueOf, 0, false);
        }
        {   // -- Instant
            JSFunction objFunc = Bootstrapper.InstallFunction(isolate, temporal, "Instant", InstanceType.JSTemporalInstantType,
                JSObject.GetHeaderSize(InstanceType.JSTemporalInstantType), 0, JSValue.TheHole, Builtin.TemporalInstantConstructor, 1,
                false);
            Bootstrapper.InstallWithIntrinsicDefaultProto(isolate, objFunc, Context.Field.JS_TEMPORAL_INSTANT_FUNCTION_INDEX);
            var prototype = (JSObject)objFunc.InstancePrototype;
            Bootstrapper.InstallToStringTag(isolate, prototype, "Temporal.Instant");
            Bootstrapper.SimpleInstallFunction(isolate, objFunc, "from", Builtin.TemporalInstantFrom, 1, false);
            Bootstrapper.SimpleInstallFunction(isolate, objFunc, "fromEpochMilliseconds", Builtin.TemporalInstantFromEpochMilliseconds, 1, false);
            Bootstrapper.SimpleInstallFunction(isolate, objFunc, "fromEpochNanoseconds", Builtin.TemporalInstantFromEpochNanoseconds, 1, false);
            Bootstrapper.SimpleInstallFunction(isolate, objFunc, "compare", Builtin.TemporalInstantCompare, 2, false);
            Bootstrapper.SimpleInstallGetter(isolate, prototype, factory.InternalizeString("epochMilliseconds"), Builtin.TemporalInstantPrototypeEpochMilliseconds, true);
            Bootstrapper.SimpleInstallGetter(isolate, prototype, factory.InternalizeString("epochNanoseconds"), Builtin.TemporalInstantPrototypeEpochNanoseconds, true);
            Bootstrapper.SimpleInstallFunction(isolate, prototype, "add", Builtin.TemporalInstantPrototypeAdd, 1, false);
            Bootstrapper.SimpleInstallFunction(isolate, prototype, "subtract", Builtin.TemporalInstantPrototypeSubtract, 1, false);
            Bootstrapper.SimpleInstallFunction(isolate, prototype, "until", Builtin.TemporalInstantPrototypeUntil, 1, false);
            Bootstrapper.SimpleInstallFunction(isolate, prototype, "since", Builtin.TemporalInstantPrototypeSince, 1, false);
            Bootstrapper.SimpleInstallFunction(isolate, prototype, "round", Builtin.TemporalInstantPrototypeRound, 1, false);
            Bootstrapper.SimpleInstallFunction(isolate, prototype, "equals", Builtin.TemporalInstantPrototypeEquals, 1, false);
            Bootstrapper.SimpleInstallFunction(isolate, prototype, "toLocaleString", Builtin.TemporalInstantPrototypeToLocaleString, 0, false);
            Bootstrapper.SimpleInstallFunction(isolate, prototype, "toString", Builtin.TemporalInstantPrototypeToString, 0, false);
            Bootstrapper.SimpleInstallFunction(isolate, prototype, "toJSON", Builtin.TemporalInstantPrototypeToJSON, 0, false);
            Bootstrapper.SimpleInstallFunction(isolate, prototype, "valueOf", Builtin.TemporalInstantPrototypeValueOf, 0, false);
            Bootstrapper.SimpleInstallFunction(isolate, prototype, "toZonedDateTimeISO", Builtin.TemporalInstantPrototypeToZonedDateTimeISO, 1, false);
        }
        {   // -- PlainYearMonth
            JSFunction objFunc = Bootstrapper.InstallFunction(isolate, temporal, "PlainYearMonth", InstanceType.JSTemporalPlainYearMonthType,
                JSObject.GetHeaderSize(InstanceType.JSTemporalPlainYearMonthType), 0, JSValue.TheHole, Builtin.TemporalPlainYearMonthConstructor, 2,
                false);
            Bootstrapper.InstallWithIntrinsicDefaultProto(isolate, objFunc, Context.Field.JS_TEMPORAL_PLAIN_YEAR_MONTH_FUNCTION_INDEX);
            var prototype = (JSObject)objFunc.InstancePrototype;
            Bootstrapper.InstallToStringTag(isolate, prototype, "Temporal.PlainYearMonth");
            Bootstrapper.SimpleInstallFunction(isolate, objFunc, "from", Builtin.TemporalPlainYearMonthFrom, 1, false);
            Bootstrapper.SimpleInstallFunction(isolate, objFunc, "compare", Builtin.TemporalPlainYearMonthCompare, 2, false);
            Bootstrapper.SimpleInstallGetter(isolate, prototype, factory.InternalizeString("calendarId"), Builtin.TemporalPlainYearMonthPrototypeCalendarId, true);
            Bootstrapper.SimpleInstallGetter(isolate, prototype, factory.InternalizeString("era"), Builtin.TemporalPlainYearMonthPrototypeEra, true);
            Bootstrapper.SimpleInstallGetter(isolate, prototype, factory.InternalizeString("eraYear"), Builtin.TemporalPlainYearMonthPrototypeEraYear, true);
            Bootstrapper.SimpleInstallGetter(isolate, prototype, factory.InternalizeString("year"), Builtin.TemporalPlainYearMonthPrototypeYear, true);
            Bootstrapper.SimpleInstallGetter(isolate, prototype, factory.InternalizeString("month"), Builtin.TemporalPlainYearMonthPrototypeMonth, true);
            Bootstrapper.SimpleInstallGetter(isolate, prototype, factory.InternalizeString("monthCode"), Builtin.TemporalPlainYearMonthPrototypeMonthCode, true);
            Bootstrapper.SimpleInstallGetter(isolate, prototype, factory.InternalizeString("daysInYear"), Builtin.TemporalPlainYearMonthPrototypeDaysInYear, true);
            Bootstrapper.SimpleInstallGetter(isolate, prototype, factory.InternalizeString("daysInMonth"), Builtin.TemporalPlainYearMonthPrototypeDaysInMonth, true);
            Bootstrapper.SimpleInstallGetter(isolate, prototype, factory.InternalizeString("monthsInYear"), Builtin.TemporalPlainYearMonthPrototypeMonthsInYear, true);
            Bootstrapper.SimpleInstallGetter(isolate, prototype, factory.InternalizeString("inLeapYear"), Builtin.TemporalPlainYearMonthPrototypeInLeapYear, true);
            Bootstrapper.SimpleInstallFunction(isolate, prototype, "with", Builtin.TemporalPlainYearMonthPrototypeWith, 1, false);
            Bootstrapper.SimpleInstallFunction(isolate, prototype, "add", Builtin.TemporalPlainYearMonthPrototypeAdd, 1, false);
            Bootstrapper.SimpleInstallFunction(isolate, prototype, "subtract", Builtin.TemporalPlainYearMonthPrototypeSubtract, 1, false);
            Bootstrapper.SimpleInstallFunction(isolate, prototype, "until", Builtin.TemporalPlainYearMonthPrototypeUntil, 1, false);
            Bootstrapper.SimpleInstallFunction(isolate, prototype, "since", Builtin.TemporalPlainYearMonthPrototypeSince, 1, false);
            Bootstrapper.SimpleInstallFunction(isolate, prototype, "equals", Builtin.TemporalPlainYearMonthPrototypeEquals, 1, false);
            Bootstrapper.SimpleInstallFunction(isolate, prototype, "toLocaleString", Builtin.TemporalPlainYearMonthPrototypeToLocaleString, 0, false);
            Bootstrapper.SimpleInstallFunction(isolate, prototype, "toString", Builtin.TemporalPlainYearMonthPrototypeToString, 0, false);
            Bootstrapper.SimpleInstallFunction(isolate, prototype, "toJSON", Builtin.TemporalPlainYearMonthPrototypeToJSON, 0, false);
            Bootstrapper.SimpleInstallFunction(isolate, prototype, "valueOf", Builtin.TemporalPlainYearMonthPrototypeValueOf, 0, false);
            Bootstrapper.SimpleInstallFunction(isolate, prototype, "toPlainDate", Builtin.TemporalPlainYearMonthPrototypeToPlainDate, 1, false);
        }
        {   // -- PlainMonthDay
            JSFunction objFunc = Bootstrapper.InstallFunction(isolate, temporal, "PlainMonthDay", InstanceType.JSTemporalPlainMonthDayType,
                JSObject.GetHeaderSize(InstanceType.JSTemporalPlainMonthDayType), 0, JSValue.TheHole, Builtin.TemporalPlainMonthDayConstructor, 2,
                false);
            Bootstrapper.InstallWithIntrinsicDefaultProto(isolate, objFunc, Context.Field.JS_TEMPORAL_PLAIN_MONTH_DAY_FUNCTION_INDEX);
            var prototype = (JSObject)objFunc.InstancePrototype;
            Bootstrapper.InstallToStringTag(isolate, prototype, "Temporal.PlainMonthDay");
            Bootstrapper.SimpleInstallFunction(isolate, objFunc, "from", Builtin.TemporalPlainMonthDayFrom, 1, false);
            Bootstrapper.SimpleInstallGetter(isolate, prototype, factory.InternalizeString("calendarId"), Builtin.TemporalPlainMonthDayPrototypeCalendarId, true);
            Bootstrapper.SimpleInstallGetter(isolate, prototype, factory.InternalizeString("monthCode"), Builtin.TemporalPlainMonthDayPrototypeMonthCode, true);
            Bootstrapper.SimpleInstallGetter(isolate, prototype, factory.InternalizeString("day"), Builtin.TemporalPlainMonthDayPrototypeDay, true);
            Bootstrapper.SimpleInstallFunction(isolate, prototype, "with", Builtin.TemporalPlainMonthDayPrototypeWith, 1, false);
            Bootstrapper.SimpleInstallFunction(isolate, prototype, "equals", Builtin.TemporalPlainMonthDayPrototypeEquals, 1, false);
            Bootstrapper.SimpleInstallFunction(isolate, prototype, "toLocaleString", Builtin.TemporalPlainMonthDayPrototypeToLocaleString, 0, false);
            Bootstrapper.SimpleInstallFunction(isolate, prototype, "toString", Builtin.TemporalPlainMonthDayPrototypeToString, 0, false);
            Bootstrapper.SimpleInstallFunction(isolate, prototype, "toJSON", Builtin.TemporalPlainMonthDayPrototypeToJSON, 0, false);
            Bootstrapper.SimpleInstallFunction(isolate, prototype, "valueOf", Builtin.TemporalPlainMonthDayPrototypeValueOf, 0, false);
            Bootstrapper.SimpleInstallFunction(isolate, prototype, "toPlainDate", Builtin.TemporalPlainMonthDayPrototypeToPlainDate, 1, false);
        }

        nativeContext.TemporalObject = temporal;
        return temporal;
    }

    /// <summary>LazyInitializeDateToTemporalInstant.</summary>
    static JSValue LazyInitializeDateToTemporalInstant(Isolate isolate, JSValue receiver, JSObject holder, Name name)
    {
        using Isolate.SaveContext saved = isolate.EnterContext(holder.GetCreationContext() ?? isolate.NativeContext);
        InitializeTemporal(isolate);
        return Bootstrapper.SimpleCreateFunction(isolate, isolate.Factory.InternalizeString("toTemporalInstant"),
            Builtin.DatePrototypeToTemporalInstant, 0, false);
    }

    /// <summary>LazyInitializeGlobalThisTemporal.</summary>
    static JSValue LazyInitializeGlobalThisTemporal(Isolate isolate, JSValue receiver, JSObject holder, Name name)
    {
        using Isolate.SaveContext saved = isolate.EnterContext(holder.GetCreationContext() ?? isolate.NativeContext);
        return InitializeTemporal(isolate);
    }

    /// <summary>
    /// Bootstrapper::InitializeLazyPartOfContext for NATIVE_CONTEXT_FIELDS_TEMPORAL: initializes the
    /// Temporal part of a native context when one of its fields is needed first (the value of
    /// globalThis.Temporal remains unchanged).
    /// </summary>
    public static void InitializeLazyTemporalPartOfContext(Isolate isolate, NativeContext nativeContext)
    {
        if (!isolate.Flags.harmony_temporal) return;
        using Isolate.SaveContext saved = isolate.EnterContext(nativeContext);
        InitializeTemporal(isolate);
    }

    /// <summary>Genesis::InitializeGlobal_harmony_temporal.</summary>
    void InitializeGlobal_harmony_temporal()
    {
        if (!_isolate.Flags.harmony_temporal) return;

        // The Temporal object is set up lazily upon first access.
        {
            JSGlobalObject global = _nativeContext.GlobalObject;
            JSString name = _factory.InternalizeString("Temporal");
            var accessor = new AccessorInfo(name, LazyInitializeGlobalThisTemporal, Accessors.ReconfigureToDataProperty)
            {
                ReplaceOnAccess = true,
            };
            JSObject.SetAccessor(_isolate, global, name, accessor, PropertyAttributes.DONT_ENUM);
        }

        // Likewise Date.toTemporalInstant.
        {
            var datePrototype = (JSObject)_nativeContext.DateFunction.InstancePrototype;
            JSString name = _factory.InternalizeString("toTemporalInstant");
            var accessor = new AccessorInfo(name, LazyInitializeDateToTemporalInstant, Accessors.ReconfigureToDataProperty)
            {
                ReplaceOnAccess = true,
            };
            JSObject.SetAccessor(_isolate, datePrototype, name, accessor, PropertyAttributes.DONT_ENUM);
        }
    }
}
