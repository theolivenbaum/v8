// Port of src/objects/js-temporal-objects.cc, part 1: the helpers of namespace
// temporal (numeric conversions, option getters, calendar and time zone
// identifiers, PrepareCalendarFields, the ToTemporal* conversions, relativeTo,
// and the generic difference / add / with operations). The engine behind them
// (temporal_rs in V8) is V8Sharp.Temporal.
using System.Numerics;
using V8Sharp.Temporal;
using Calendar = V8Sharp.Temporal.Calendar;
using Duration = V8Sharp.Temporal.Duration;
using TimeZone = V8Sharp.Temporal.TimeZone;

namespace V8Sharp.Builtins;

public static partial class TemporalBuiltins
{
    // Common error strings
    const string kInvalidIsoDate = "Invalid ISO date.";
    const string kInvalidTime = "Invalid time";
    const string kFiniteInteger = "Expected finite integer.";
    const string kIntegerOutOfRange = "Integer out of range.";
    const string kOptionMustBeObject = "Option must be object:";
    const string kCalendarMustBeString = "Calendar must be string.";
    const string kRoundToMissing = "Must specify a roundTo parameter.";
    const string kRoundToMustBeObject = "roundTo must be an object.";
    const string kYearMustBeObject = "year argument must be an object.";
    const string kTimeZoneMissing = "Must specify time zone.";
    const string kWithNoPartial = "Argument to with() must contain some date/time fields.";

    // ---- Errors ----------------------------------------------------------------------------------

    static JSString Str(Isolate isolate, string s) => isolate.Factory.NewStringFromAsciiChecked(s);

    /// <summary>NEW_TEMPORAL_TYPE_ERROR.</summary>
    static JSValue ThrowTemporalTypeError(Isolate isolate, string message) =>
        isolate.ThrowTypeError(MessageTemplate.Temporal, Str(isolate, message));

    /// <summary>NEW_TEMPORAL_RANGE_ERROR.</summary>
    static JSValue ThrowTemporalRangeError(Isolate isolate, string message) =>
        isolate.ThrowRangeError(MessageTemplate.Temporal, Str(isolate, message));

    /// <summary>NEW_TEMPORAL_TYPE_ERROR_WITH_ARG.</summary>
    static JSValue ThrowTemporalTypeError(Isolate isolate, string message, JSValue arg) =>
        isolate.ThrowTypeError(MessageTemplate.TemporalWithArg, Str(isolate, message), arg);

    /// <summary>NEW_TEMPORAL_RANGE_ERROR_WITH_ARG.</summary>
    static JSValue ThrowTemporalRangeError(Isolate isolate, string message, JSValue arg) =>
        isolate.ThrowRangeError(MessageTemplate.TemporalWithArg, Str(isolate, message), arg);

    /// <summary>ExtractRustResult's error case: the engine's TemporalError as a JS error.</summary>
    internal static JSValue ThrowTemporalError(Isolate isolate, TemporalError error)
    {
        JSString msg = isolate.Factory.NewStringFromUtf16(error.Message);
        return error.Kind switch
        {
            TemporalErrorKind.Type => isolate.ThrowTypeError(MessageTemplate.Temporal, msg),
            TemporalErrorKind.Range => isolate.ThrowRangeError(MessageTemplate.Temporal, msg),
            TemporalErrorKind.Syntax => isolate.ThrowSyntaxError(MessageTemplate.Temporal, msg),
            _ => isolate.Throw(isolate.Factory.NewError(MessageTemplate.TemporalWithArg, Str(isolate, "Internal error:"), msg)),
        };
    }

    // ---- Numeric conversions ---------------------------------------------------------------------

    /// <summary>ToIntegerIfIntegral.</summary>
    static double ToIntegerIfIntegral(Isolate isolate, JSValue argument)
    {
        double number = ObjectOps.ToNumberValue(isolate, argument);
        if (!double.IsFinite(number) || Math.Round(number) != number) ThrowTemporalRangeError(isolate, kFiniteInteger);
        return number;
    }

    /// <summary>ToIntegerTypeIfIntegral&lt;int64_t&gt;.</summary>
    static double ToInt64IfIntegral(Isolate isolate, JSValue argument)
    {
        double d = ToIntegerIfIntegral(isolate, argument);
        if (!(d >= -9223372036854775808.0 && d < 9223372036854775808.0)) ThrowTemporalRangeError(isolate, kIntegerOutOfRange);
        return d;
    }

    /// <summary>ToIntegerWithTruncation.</summary>
    static double ToIntegerWithTruncation(Isolate isolate, JSValue argument)
    {
        double number = ObjectOps.ToNumberValue(isolate, argument);
        if (!double.IsFinite(number)) ThrowTemporalRangeError(isolate, kFiniteInteger);
        return Math.Truncate(number) + 0.0;
    }

    /// <summary>ToIntegerWithTruncationOrZero.</summary>
    static double ToIntegerWithTruncationOrZero(Isolate isolate, JSValue argument) =>
        argument.IsUndefined ? 0 : ToIntegerWithTruncation(isolate, argument);

    /// <summary>ToPositiveIntegerWithTruncation.</summary>
    static double ToPositiveIntegerWithTruncation(Isolate isolate, JSValue argument)
    {
        double integer = ToIntegerWithTruncation(isolate, argument);
        if (integer <= 0) ThrowTemporalRangeError(isolate, "Expected positive integer.");
        return integer;
    }

    /// <summary>GetI128FromBigInt: throws RangeErrors for out-of-range BigInts.</summary>
    static Int128 GetI128FromBigInt(Isolate isolate, BigInt bigint)
    {
        const string kNSOutOfRange = "Nanoseconds out of range.";
        ReadOnlySpan<ulong> digits = bigint.Digits;
        if (digits.Length > 2) ThrowTemporalRangeError(isolate, kNSOutOfRange);
        ulong low = digits.Length > 0 ? digits[0] : 0;
        ulong high = digits.Length > 1 ? digits[1] : 0;
        if ((high & (1UL << 63)) != 0) ThrowTemporalRangeError(isolate, kNSOutOfRange);
        var magnitude = new Int128(high, low);
        Int128 ns = bigint.Sign ? -magnitude : magnitude;
        if (!IsoCalendar.IsValidEpochNanoseconds(ns)) ThrowTemporalRangeError(isolate, kNSOutOfRange);
        return ns;
    }

    /// <summary>I128ToBigInt.</summary>
    static BigInt I128ToBigInt(Isolate isolate, Int128 ns)
    {
        bool sign = ns < 0;
        UInt128 magnitude = sign ? (UInt128)(-ns) : (UInt128)ns;
        Span<ulong> words = [(ulong)magnitude, (ulong)(magnitude >> 64)];
        return BigInt.FromWords64(isolate, sign, words);
    }

    /// <summary>IsValidTime on doubles.</summary>
    static bool IsValidTime(double hour, double minute, double second, double millisecond, double microsecond,
        double nanosecond) =>
        !(hour < 0 || hour > 23) && !(minute < 0 || minute > 59) && !(second < 0 || second > 59) &&
        !(millisecond < 0 || millisecond > 999) && !(microsecond < 0 || microsecond > 999) &&
        !(nanosecond < 0 || nanosecond > 999);

    /// <summary>IsValidIsoDate on doubles.</summary>
    static bool IsValidIsoDate(double year, double month, double day)
    {
        if (month < 1 || month > 12) return false;
        if (!(year >= int.MinValue && year <= int.MaxValue)) return false;
        return !(day < 1 || day > IsoCalendar.DaysInMonth((long)year, (int)month));
    }

    // ---- Option getters --------------------------------------------------------------------------

    /// <summary>GetOptionsObject (option-utils.cc).</summary>
    static JSReceiver GetOptionsObject(Isolate isolate, JSValue options, string methodName)
    {
        if (options.IsUndefined) return isolate.Factory.NewJSObjectWithNullProto();
        if (options.HeapObjectOrNull is JSReceiver receiver) return receiver;
        isolate.ThrowTypeError(MessageTemplate.InvalidArgument);
        return null!;
    }

    /// <summary>GetStringOption with a list of values (option-utils.h); null default means required.</summary>
    static T GetStringOption<T>(Isolate isolate, JSReceiver options, string property, string methodName,
        ReadOnlySpan<string> strValues, ReadOnlySpan<T> enumValues, T? defaultValue, bool hasDefault)
    {
        JSString propertyName = isolate.Factory.InternalizeString(property);
        JSValue value = ObjectOps.GetPropertyOrElement(isolate, options, propertyName);
        JSString? foundString = null;
        if (!value.IsUndefined)
        {
            foundString = ObjectOps.ToString(isolate, value);
            ReadOnlySpan<char> found = foundString.FlatSpan();
            for (int i = 0; i < strValues.Length; i++)
            {
                if (found.SequenceEqual(strValues[i])) return enumValues[i];
            }
        }
        else if (hasDefault)
        {
            return defaultValue!;
        }
        foundString ??= ReadOnlyRoots.undefined_string;
        isolate.ThrowRangeError(MessageTemplate.ValueOutOfRange, foundString, Str(isolate, methodName), propertyName);
        return default!;
    }

    /// <summary>ToMonthCode.</summary>
    static string ToMonthCode(Isolate isolate, JSValue argument)
    {
        const string kMonthCodeOutOfRange = "Month code out of range.";
        JSValue mcPrim = argument.HeapObjectOrNull is JSReceiver r
            ? JSReceiver.ToPrimitive(isolate, r, ToPrimitiveHint.String)
            : argument;
        if (!mcPrim.IsString) ThrowTemporalTypeError(isolate, kMonthCodeOutOfRange);
        string monthCode = mcPrim.As<JSString>().ToCString();
        if (monthCode.Length != 3 && monthCode.Length != 4) ThrowTemporalRangeError(isolate, kMonthCodeOutOfRange);
        if (monthCode[0] != 'M') ThrowTemporalRangeError(isolate, kMonthCodeOutOfRange);
        if (monthCode[1] < '0' || monthCode[1] > '9') ThrowTemporalRangeError(isolate, kMonthCodeOutOfRange);
        if (monthCode[2] < '0' || monthCode[2] > '9') ThrowTemporalRangeError(isolate, kMonthCodeOutOfRange);
        if (monthCode.Length == 4 && monthCode[3] != 'L') ThrowTemporalRangeError(isolate, kMonthCodeOutOfRange);
        if (monthCode[1] == '0' && monthCode[2] == '0' && monthCode.Length != 4)
            ThrowTemporalRangeError(isolate, kMonthCodeOutOfRange);
        return monthCode;
    }

    /// <summary>
    /// ToTemporalOverflowHandleUndefined: also handles the undefined check of GetOptionsObject.
    /// <paramref name="present"/> false is V8's empty MaybeDirectHandle (options not passed).
    /// </summary>
    static Overflow ToTemporalOverflowHandleUndefined(Isolate isolate, JSValue options, bool present, string methodName)
    {
        if (!present || options.IsUndefined) return Overflow.Constrain;
        if (options.HeapObjectOrNull is not JSReceiver receiver)
        {
            ThrowTemporalTypeError(isolate, kOptionMustBeObject, ReadOnlyRoots.overflow_string);
            return default;
        }
        return GetStringOption<Overflow>(isolate, receiver, "overflow", methodName, ["constrain", "reject"],
            [Overflow.Constrain, Overflow.Reject], Overflow.Constrain, true);
    }

    static Overflow ToTemporalOverflowHandleUndefined(Isolate isolate, JSValue options, string methodName) =>
        ToTemporalOverflowHandleUndefined(isolate, options, true, methodName);

    /// <summary>GetDirectionOption.</summary>
    static TransitionDirection GetDirectionOption(Isolate isolate, JSReceiver options, string methodName) =>
        GetStringOption<TransitionDirection>(isolate, options, "direction", methodName, ["next", "previous"],
            [TransitionDirection.Next, TransitionDirection.Previous], default, false);

    /// <summary>GetTemporalDisambiguationOptionHandleUndefined.</summary>
    static Disambiguation GetTemporalDisambiguationOptionHandleUndefined(Isolate isolate, JSValue options, string methodName)
    {
        if (options.IsUndefined) return Disambiguation.Compatible;
        if (options.HeapObjectOrNull is not JSReceiver receiver)
        {
            ThrowTemporalTypeError(isolate, kOptionMustBeObject, isolate.Factory.InternalizeString("disambiguation"));
            return default;
        }
        return GetStringOption<Disambiguation>(isolate, receiver, "disambiguation", methodName,
            ["compatible", "earlier", "later", "reject"],
            [Disambiguation.Compatible, Disambiguation.Earlier, Disambiguation.Later, Disambiguation.Reject],
            Disambiguation.Compatible, true);
    }

    /// <summary>GetTemporalOffsetOptionHandleUndefined.</summary>
    static OffsetDisambiguation GetTemporalOffsetOptionHandleUndefined(Isolate isolate, JSValue options,
        OffsetDisambiguation fallback, string methodName)
    {
        if (options.IsUndefined) return fallback;
        if (options.HeapObjectOrNull is not JSReceiver receiver)
        {
            ThrowTemporalTypeError(isolate, kOptionMustBeObject, ReadOnlyRoots.offset_string);
            return default;
        }
        return GetStringOption<OffsetDisambiguation>(isolate, receiver, "offset", methodName,
            ["prefer", "use", "ignore", "reject"],
            [OffsetDisambiguation.Prefer, OffsetDisambiguation.Use, OffsetDisambiguation.Ignore, OffsetDisambiguation.Reject],
            fallback, true);
    }

    /// <summary>GetTemporalFractionalSecondDigitsOption.</summary>
    static Precision GetTemporalFractionalSecondDigitsOption(Isolate isolate, JSReceiver normalizedOptions, string methodName)
    {
        JSString name = isolate.Factory.InternalizeString("fractionalSecondDigits");
        JSValue digitsVal = JSReceiver.GetProperty(isolate, normalizedOptions, name);
        if (digitsVal.IsUndefined) return Precision.Auto;
        if (!digitsVal.IsNumber)
        {
            JSString str = ObjectOps.ToString(isolate, digitsVal);
            if (!str.FlatSpan().SequenceEqual("auto")) isolate.ThrowRangeError(MessageTemplate.PropertyValueOutOfRange, name);
            return Precision.Auto;
        }
        double digitsFloat = digitsVal.Number;
        if (double.IsNaN(digitsFloat) || double.IsInfinity(digitsFloat))
            isolate.ThrowRangeError(MessageTemplate.PropertyValueOutOfRange, name);
        double digitCount = Math.Floor(digitsFloat);
        if (digitCount < 0 || digitCount > 9) isolate.ThrowRangeError(MessageTemplate.PropertyValueOutOfRange, name);
        return new Precision(false, (int)digitCount);
    }

    static readonly string[] s_unitStrings =
    [
        "year", "month", "week", "day", "hour", "minute", "second", "millisecond", "microsecond", "nanosecond", "auto",
        "years", "months", "weeks", "days", "hours", "minutes", "seconds", "milliseconds", "microseconds", "nanoseconds",
    ];

    static readonly Unit?[] s_unitValues =
    [
        Unit.Year, Unit.Month, Unit.Week, Unit.Day, Unit.Hour, Unit.Minute, Unit.Second, Unit.Millisecond, Unit.Microsecond,
        Unit.Nanosecond, Unit.Auto, Unit.Year, Unit.Month, Unit.Week, Unit.Day, Unit.Hour, Unit.Minute, Unit.Second,
        Unit.Millisecond, Unit.Microsecond, Unit.Nanosecond,
    ];

    /// <summary>GetTemporalUnitValuedOption: null is unset.</summary>
    static Unit? GetTemporalUnitValuedOption(Isolate isolate, JSReceiver normalizedOptions, string key, bool required,
        string methodName) =>
        GetStringOption<Unit?>(isolate, normalizedOptions, key, methodName, s_unitStrings, s_unitValues, null, !required);

    /// <summary>ValidateTemporalUnitValue.</summary>
    static void ValidateTemporalUnitValue(Isolate isolate, Unit? valueOrUnset, UnitGroup unitGroup, Unit? extraValues = null)
    {
        if (valueOrUnset is not Unit value) return;
        if (extraValues == value) return;
        switch (value)
        {
            case Unit.Auto:
                ThrowTemporalRangeError(isolate, "Auto unit not allowed here");
                break;
            case Unit.Year or Unit.Month or Unit.Week or Unit.Day:
                if (unitGroup is UnitGroup.Date or UnitGroup.DateTime) return;
                ThrowTemporalRangeError(isolate, "Found date unit, expect time unit");
                break;
            default:
                if (unitGroup is UnitGroup.Time or UnitGroup.DateTime) return;
                ThrowTemporalRangeError(isolate, "Found date unit, expect time unit");
                break;
        }
    }

    /// <summary>CanonicalizeCalendar.</summary>
    static Calendar CanonicalizeCalendar(Isolate isolate, JSString calendar)
    {
        Calendar? cal = Calendar.TryCanonicalize(calendar.FlatSpan());
        if (cal is null) ThrowTemporalRangeError(isolate, "Unknown calendar type", calendar);
        return cal!;
    }

    /// <summary>GetRoundingIncrementOption.</summary>
    static uint GetRoundingIncrementOption(Isolate isolate, JSReceiver normalizedOptions)
    {
        JSValue value = JSReceiver.GetProperty(isolate, normalizedOptions, isolate.Factory.InternalizeString("roundingIncrement"));
        if (value.IsUndefined) return 1;
        double integerIncrement = ToIntegerWithTruncation(isolate, value);
        if (integerIncrement < 1 || integerIncrement > 1e9) ThrowTemporalRangeError(isolate, kIntegerOutOfRange);
        return (uint)integerIncrement;
    }

    /// <summary>GetRoundingModeOption.</summary>
    static RoundingMode GetRoundingModeOption(Isolate isolate, JSReceiver options, RoundingMode fallback, string methodName) =>
        GetStringOption<RoundingMode>(isolate, options, "roundingMode", methodName,
            ["ceil", "floor", "expand", "trunc", "halfCeil", "halfFloor", "halfExpand", "halfTrunc", "halfEven"],
            [RoundingMode.Ceil, RoundingMode.Floor, RoundingMode.Expand, RoundingMode.Trunc, RoundingMode.HalfCeil,
                RoundingMode.HalfFloor, RoundingMode.HalfExpand, RoundingMode.HalfTrunc, RoundingMode.HalfEven],
            fallback, true);

    /// <summary>GetTemporalShowOffsetOption.</summary>
    static DisplayOffset GetTemporalShowOffsetOption(Isolate isolate, JSReceiver options, string methodName) =>
        GetStringOption<DisplayOffset>(isolate, options, "offset", methodName, ["auto", "never"],
            [DisplayOffset.Auto, DisplayOffset.Never], DisplayOffset.Auto, true);

    /// <summary>GetTemporalShowTimeZoneNameOption.</summary>
    static DisplayTimeZone GetTemporalShowTimeZoneNameOption(Isolate isolate, JSReceiver options, string methodName) =>
        GetStringOption<DisplayTimeZone>(isolate, options, "timeZoneName", methodName, ["auto", "never", "critical"],
            [DisplayTimeZone.Auto, DisplayTimeZone.Never, DisplayTimeZone.Critical], DisplayTimeZone.Auto, true);

    /// <summary>GetDifferenceSettingsWithoutChecks.</summary>
    static DifferenceSettings GetDifferenceSettingsWithoutChecks(Isolate isolate, JSValue optionsObj, UnitGroup unitGroup,
        Unit? fallbackSmallestUnit, string methodName)
    {
        JSReceiver options = GetOptionsObject(isolate, optionsObj, methodName);
        Unit? largestUnit = GetTemporalUnitValuedOption(isolate, options, "largestUnit", false, methodName);
        uint roundingIncrement = GetRoundingIncrementOption(isolate, options);
        RoundingMode roundingMode = GetRoundingModeOption(isolate, options, RoundingMode.Trunc, methodName);
        Unit? smallestUnit = GetTemporalUnitValuedOption(isolate, options, "smallestUnit", false, methodName);
        return new DifferenceSettings(largestUnit, smallestUnit, roundingMode, roundingIncrement);
    }

    /// <summary>GetTemporalShowCalendarNameOption.</summary>
    static DisplayCalendar GetTemporalShowCalendarNameOption(Isolate isolate, JSReceiver options, string methodName) =>
        GetStringOption<DisplayCalendar>(isolate, options, "calendarName", methodName, ["auto", "always", "never", "critical"],
            [DisplayCalendar.Auto, DisplayCalendar.Always, DisplayCalendar.Never, DisplayCalendar.Critical],
            DisplayCalendar.Auto, true);

    /// <summary>ExtractCalendarFrom: the calendar of a calendared Temporal object, or null.</summary>
    static Calendar? ExtractCalendarFrom(HeapObject calendarLike) => calendarLike switch
    {
        JSTemporalPlainDate d => d.Value.Calendar,
        JSTemporalPlainDateTime dt => dt.Value.Calendar,
        JSTemporalPlainMonthDay md => md.Value.Calendar,
        JSTemporalPlainYearMonth ym => ym.Value.Calendar,
        JSTemporalZonedDateTime z => z.Value.Calendar,
        _ => null,
    };

    /// <summary>ToTemporalCalendarIdentifier.</summary>
    static Calendar ToTemporalCalendarIdentifier(Isolate isolate, JSValue calendarLike)
    {
        if (calendarLike.HeapObjectOrNull is { } heapObject && ExtractCalendarFrom(heapObject) is { } cal) return cal;
        if (!calendarLike.IsString)
        {
            ThrowTemporalTypeError(isolate, "Calendar must be string or calendared Temporal object.");
        }
        Calendar? kind = ParseTemporalCalendarString(calendarLike.As<JSString>().FlatSpan());
        if (kind is null) ThrowTemporalRangeError(isolate, "Invalid calendar string");
        return kind!;
    }

    /// <summary>AnyCalendarKind::parse_temporal_calendar_string: ParseTemporalCalendarString + CanonicalizeCalendar.</summary>
    static Calendar? ParseTemporalCalendarString(ReadOnlySpan<char> s)
    {
        ParsedIso? parsed = null;
        try
        {
            parsed = IsoParser.ParseAny(s);
        }
        catch (TemporalError)
        {
        }
        if (parsed is not null) return parsed.Calendar is null ? Calendar.Iso : Calendar.TryCanonicalize(parsed.Calendar);
        // AnnotationValue.
        if (s.Length == 0) return null;
        int start = 0;
        for (int i = 0; i <= s.Length; i++)
        {
            if (i == s.Length || s[i] == '-')
            {
                if (i == start) return null;
                start = i + 1;
            }
            else if (!char.IsAsciiLetterOrDigit(s[i]))
            {
                return null;
            }
        }
        return Calendar.TryCanonicalize(s);
    }

    /// <summary>GetTemporalCalendarIdentifierWithISODefault.</summary>
    static Calendar GetTemporalCalendarIdentifierWithISODefault(Isolate isolate, JSReceiver options)
    {
        if (ExtractCalendarFrom(options) is { } cal) return cal;
        JSValue calendar = JSReceiver.GetProperty(isolate, options, ReadOnlyRoots.calendar_string);
        if (calendar.IsUndefined) return Calendar.Iso;
        return ToTemporalCalendarIdentifier(isolate, calendar);
    }

    // ---- Record operations -------------------------------------------------------------------------

    /// <summary>TimeRecord (unset fields are null).</summary>
    struct TimeRecord
    {
        public double? Hour, Minute, Second, Millisecond, Microsecond, Nanosecond;

        /// <summary>TimeRecord::Regulate (RegulateTime on the set fields).</summary>
        public readonly PartialTime Regulate(Isolate isolate, Overflow overflow)
        {
            var partial = new PartialTime();
            if (overflow == Overflow.Constrain)
            {
                if (Hour is double h) partial.Hour = (long)Math.Clamp(h, 0, 23);
                if (Minute is double mi) partial.Minute = (long)Math.Clamp(mi, 0, 59);
                if (Second is double s) partial.Second = (long)Math.Clamp(s, 0, 59);
                if (Millisecond is double ms) partial.Millisecond = (long)Math.Clamp(ms, 0, 999);
                if (Microsecond is double us) partial.Microsecond = (long)Math.Clamp(us, 0, 999);
                if (Nanosecond is double ns) partial.Nanosecond = (long)Math.Clamp(ns, 0, 999);
            }
            else
            {
                if (!IsValidTime(Hour ?? 0, Minute ?? 0, Second ?? 0, Millisecond ?? 0, Microsecond ?? 0, Nanosecond ?? 0))
                {
                    ThrowTemporalRangeError(isolate, "Invalid time provided");
                }
                if (Hour is double h) partial.Hour = (long)h;
                if (Minute is double mi) partial.Minute = (long)mi;
                if (Second is double s) partial.Second = (long)s;
                if (Millisecond is double ms) partial.Millisecond = (long)ms;
                if (Microsecond is double us) partial.Microsecond = (long)us;
                if (Nanosecond is double ns) partial.Nanosecond = (long)ns;
            }
            return partial;
        }
    }

    /// <summary>DateRecord.</summary>
    struct DateRecord
    {
        public double? Year, Month, Day, EraYear;
        public string? MonthCode, Era;
        public Calendar Calendar;

        static long Clamp(double d, double min, double max) => (long)Math.Clamp(d, min, max);

        static long CheckInRange(Isolate isolate, double d, double min, double max)
        {
            if (!(d >= min && d <= max)) ThrowTemporalRangeError(isolate, kIntegerOutOfRange);
            return (long)d;
        }

        /// <summary>DateRecord::Regulate.</summary>
        public readonly PartialDate Regulate(Isolate isolate, Overflow overflow)
        {
            var partial = new PartialDate(Calendar ?? Calendar.Iso) { MonthCode = MonthCode, Era = Era };
            if (overflow == Overflow.Constrain)
            {
                if (Year is double y) partial.Year = Clamp(y, int.MinValue, int.MaxValue);
                if (Month is double m) partial.Month = Clamp(m, sbyte.MinValue, sbyte.MaxValue);
                if (Day is double d) partial.Day = Clamp(d, sbyte.MinValue, sbyte.MaxValue);
                if (EraYear is double ey) partial.EraYear = Clamp(ey, int.MinValue, int.MaxValue);
            }
            else
            {
                if (Year is double y) partial.Year = CheckInRange(isolate, y, int.MinValue, int.MaxValue);
                if (Month is double m) partial.Month = CheckInRange(isolate, m, 0, byte.MaxValue);
                if (Day is double d) partial.Day = CheckInRange(isolate, d, 0, byte.MaxValue);
                if (EraYear is double ey) partial.EraYear = CheckInRange(isolate, ey, int.MinValue, int.MaxValue);
            }
            return partial;
        }
    }

    /// <summary>CombinedRecord: returned by PrepareCalendarFields.</summary>
    struct CombinedRecord
    {
        public DateRecord Date;
        public TimeRecord Time;
        public string? Offset;
        public TimeZone? TimeZone;

        public readonly PartialDate RegulateDate(Isolate isolate, Overflow overflow) => Date.Regulate(isolate, overflow);

        public readonly PartialDateTime RegulateDateTime(Isolate isolate, Overflow overflow) => new()
        {
            Date = Date.Regulate(isolate, overflow),
            Time = Time.Regulate(isolate, overflow),
        };

        public readonly PartialZonedDateTime RegulateZoned(Isolate isolate, Overflow overflow) => new()
        {
            Date = Date.Regulate(isolate, overflow),
            Time = Time.Regulate(isolate, overflow),
            Offset = Offset,
            TimeZone = TimeZone,
        };
    }

    /// <summary>GetSingleDurationField.</summary>
    static double? GetSingleDurationField(Isolate isolate, JSReceiver durationLike, string fieldName)
    {
        JSValue val = JSReceiver.GetProperty(isolate, durationLike, isolate.Factory.InternalizeString(fieldName));
        if (val.IsUndefined) return null;
        return ToIntegerIfIntegral(isolate, val);
    }

    /// <summary>GetSingleDurationFieldInteger.</summary>
    static double? GetSingleDurationFieldInteger(Isolate isolate, JSReceiver durationLike, string fieldName)
    {
        double? ret = GetSingleDurationField(isolate, durationLike, fieldName);
        if (ret is double r && !(r >= -9223372036854775808.0 && r < 9223372036854775808.0))
            ThrowTemporalRangeError(isolate, "Duration field out of range.");
        return ret;
    }

    /// <summary>ToOffsetString.</summary>
    static string ToOffsetString(Isolate isolate, JSValue argument)
    {
        JSValue offsetPrim = argument.HeapObjectOrNull is JSReceiver r
            ? JSReceiver.ToPrimitive(isolate, r, ToPrimitiveHint.String)
            : argument;
        if (!offsetPrim.IsString) ThrowTemporalTypeError(isolate, "Offset must be string.");
        string offset = offsetPrim.As<JSString>().ToCString();
        IsoParser.ParseDateTimeUTCOffset(offset);
        return offset;
    }

    /// <summary>ToTemporalTimeZoneIdentifier.</summary>
    static TimeZone ToTemporalTimeZoneIdentifier(Isolate isolate, JSValue tzLike)
    {
        if (tzLike.HeapObjectOrNull is JSTemporalZonedDateTime zdt) return zdt.Value.TimeZone;
        if (!tzLike.IsString) ThrowTemporalTypeError(isolate, "Time zone must be string or ZonedDateTime object.");
        return TimeZone.FromString(tzLike.As<JSString>().FlatSpan());
    }

    /// <summary>ToTemporalPartialDurationRecord.</summary>
    static PartialDuration ToTemporalPartialDurationRecord(Isolate isolate, JSValue durationLikeObj)
    {
        if (durationLikeObj.HeapObjectOrNull is not JSReceiver durationLike)
        {
            ThrowTemporalTypeError(isolate, "Must provide a duration.");
            return default;
        }
        var result = new PartialDuration
        {
            Days = GetSingleDurationFieldInteger(isolate, durationLike, "days"),
            Hours = GetSingleDurationFieldInteger(isolate, durationLike, "hours"),
            Microseconds = GetSingleDurationField(isolate, durationLike, "microseconds"),
            Milliseconds = GetSingleDurationFieldInteger(isolate, durationLike, "milliseconds"),
            Minutes = GetSingleDurationFieldInteger(isolate, durationLike, "minutes"),
            Months = GetSingleDurationFieldInteger(isolate, durationLike, "months"),
            Nanoseconds = GetSingleDurationField(isolate, durationLike, "nanoseconds"),
            Seconds = GetSingleDurationFieldInteger(isolate, durationLike, "seconds"),
            Weeks = GetSingleDurationFieldInteger(isolate, durationLike, "weeks"),
            Years = GetSingleDurationFieldInteger(isolate, durationLike, "years"),
        };
        if (result.Years is null && result.Months is null && result.Weeks is null && result.Days is null &&
            result.Hours is null && result.Minutes is null && result.Seconds is null && result.Milliseconds is null &&
            result.Microseconds is null && result.Nanoseconds is null)
        {
            ThrowTemporalTypeError(isolate, "Did not provide any valid Duration fields.");
        }
        return result;
    }

    /// <summary>GetSingleTimeRecordField.</summary>
    static double? GetSingleTimeRecordField(Isolate isolate, JSReceiver timeLike, string fieldName, ref bool any)
    {
        JSValue val = JSReceiver.GetProperty(isolate, timeLike, isolate.Factory.InternalizeString(fieldName));
        if (val.IsUndefined) return null;
        double field = ToIntegerWithTruncation(isolate, val);
        any = true;
        return field;
    }

    /// <summary>IsPartialTemporalObject.</summary>
    static bool IsPartialTemporalObject(Isolate isolate, JSValue value)
    {
        if (value.HeapObjectOrNull is not JSReceiver valueRecvr) return false;
        if (valueRecvr is JSTemporalPlainDate or JSTemporalPlainDateTime or JSTemporalPlainMonthDay or JSTemporalPlainTime
            or JSTemporalPlainYearMonth or JSTemporalZonedDateTime)
        {
            return false;
        }
        JSValue cal = JSReceiver.GetProperty(isolate, valueRecvr, ReadOnlyRoots.calendar_string);
        if (!cal.IsUndefined) return false;
        JSValue tz = JSReceiver.GetProperty(isolate, valueRecvr, isolate.Factory.InternalizeString("timeZone"));
        if (!tz.IsUndefined) return false;
        return true;
    }

    /// <summary>ToTemporalTimeRecord.</summary>
    static TimeRecord ToTemporalTimeRecord(Isolate isolate, JSReceiver timeLike, string methodName, bool partial = false)
    {
        bool any = false;
        double? hour = GetSingleTimeRecordField(isolate, timeLike, "hour", ref any);
        double? microsecond = GetSingleTimeRecordField(isolate, timeLike, "microsecond", ref any);
        double? millisecond = GetSingleTimeRecordField(isolate, timeLike, "millisecond", ref any);
        double? minute = GetSingleTimeRecordField(isolate, timeLike, "minute", ref any);
        double? nanosecond = GetSingleTimeRecordField(isolate, timeLike, "nanosecond", ref any);
        double? second = GetSingleTimeRecordField(isolate, timeLike, "second", ref any);
        if (!any) ThrowTemporalTypeError(isolate, "Must specify at least one time field.");
        if (partial)
        {
            return new TimeRecord
            {
                Hour = hour, Minute = minute, Second = second, Millisecond = millisecond, Microsecond = microsecond,
                Nanosecond = nanosecond,
            };
        }
        return new TimeRecord
        {
            Hour = hour ?? 0, Minute = minute ?? 0, Second = second ?? 0, Millisecond = millisecond ?? 0,
            Microsecond = microsecond ?? 0, Nanosecond = nanosecond ?? 0,
        };
    }

    /// <summary>CalendarFieldsFlag.</summary>
    [Flags]
    enum CalendarFieldsFlag : byte
    {
        kDay = 1 << 0,
        kMonthFields = 1 << 1,
        kYearFields = 1 << 2,
        kTimeFields = 1 << 3,
        kOffset = 1 << 4,
        kTimeZone = 1 << 5,
    }

    const CalendarFieldsFlag kAllDateFlags = CalendarFieldsFlag.kDay | CalendarFieldsFlag.kMonthFields | CalendarFieldsFlag.kYearFields;

    enum RequiredFields { kNone, kPartial, kTimeZone }

    /// <summary>A single run of the PrepareCalendarFields iteration: returns the value or undefined.</summary>
    static JSValue GetCalendarField(Isolate isolate, JSReceiver fields, string name, ref bool any)
    {
        JSValue value = JSReceiver.GetProperty(isolate, fields, isolate.Factory.InternalizeString(name));
        if (!value.IsUndefined) any = true;
        return value;
    }

    /// <summary>PrepareCalendarFields (the CALENDAR_FIELDS table, in property name order).</summary>
    static CombinedRecord PrepareCalendarFields(Isolate isolate, Calendar kind, JSReceiver fields, CalendarFieldsFlag whichFields,
        RequiredFields requiredFields)
    {
        bool calendarUsesEras = kind.UsesEras;
        var result = new CombinedRecord();
        result.Date.Calendar = kind;
        bool any = false;
        JSValue v;
        if ((whichFields & CalendarFieldsFlag.kDay) != 0)
        {
            v = GetCalendarField(isolate, fields, "day", ref any);
            if (!v.IsUndefined) result.Date.Day = ToPositiveIntegerWithTruncation(isolate, v);
        }
        if (calendarUsesEras && (whichFields & CalendarFieldsFlag.kYearFields) != 0)
        {
            v = GetCalendarField(isolate, fields, "era", ref any);
            if (!v.IsUndefined) result.Date.Era = ObjectOps.ToString(isolate, v).ToCString();
            v = GetCalendarField(isolate, fields, "eraYear", ref any);
            if (!v.IsUndefined) result.Date.EraYear = ToIntegerWithTruncation(isolate, v);
        }
        if ((whichFields & CalendarFieldsFlag.kTimeFields) != 0)
        {
            v = GetCalendarField(isolate, fields, "hour", ref any);
            if (!v.IsUndefined) result.Time.Hour = ToIntegerWithTruncation(isolate, v);
            v = GetCalendarField(isolate, fields, "microsecond", ref any);
            if (!v.IsUndefined) result.Time.Microsecond = ToIntegerWithTruncation(isolate, v);
            v = GetCalendarField(isolate, fields, "millisecond", ref any);
            if (!v.IsUndefined) result.Time.Millisecond = ToIntegerWithTruncation(isolate, v);
            v = GetCalendarField(isolate, fields, "minute", ref any);
            if (!v.IsUndefined) result.Time.Minute = ToIntegerWithTruncation(isolate, v);
        }
        if ((whichFields & CalendarFieldsFlag.kMonthFields) != 0)
        {
            v = GetCalendarField(isolate, fields, "month", ref any);
            if (!v.IsUndefined) result.Date.Month = ToPositiveIntegerWithTruncation(isolate, v);
            v = GetCalendarField(isolate, fields, "monthCode", ref any);
            if (!v.IsUndefined) result.Date.MonthCode = ToMonthCode(isolate, v);
        }
        if ((whichFields & CalendarFieldsFlag.kTimeFields) != 0)
        {
            v = GetCalendarField(isolate, fields, "nanosecond", ref any);
            if (!v.IsUndefined) result.Time.Nanosecond = ToIntegerWithTruncation(isolate, v);
        }
        if ((whichFields & CalendarFieldsFlag.kOffset) != 0)
        {
            v = GetCalendarField(isolate, fields, "offset", ref any);
            if (!v.IsUndefined) result.Offset = ToOffsetString(isolate, v);
        }
        if ((whichFields & CalendarFieldsFlag.kTimeFields) != 0)
        {
            v = GetCalendarField(isolate, fields, "second", ref any);
            if (!v.IsUndefined) result.Time.Second = ToIntegerWithTruncation(isolate, v);
        }
        if ((whichFields & CalendarFieldsFlag.kTimeZone) != 0)
        {
            v = GetCalendarField(isolate, fields, "timeZone", ref any);
            if (!v.IsUndefined) result.TimeZone = ToTemporalTimeZoneIdentifier(isolate, v);
            else if (requiredFields == RequiredFields.kTimeZone) ThrowTemporalTypeError(isolate, kTimeZoneMissing);
        }
        if ((whichFields & CalendarFieldsFlag.kYearFields) != 0)
        {
            v = GetCalendarField(isolate, fields, "year", ref any);
            if (!v.IsUndefined) result.Date.Year = ToIntegerWithTruncation(isolate, v);
        }
        if (requiredFields == RequiredFields.kPartial && !any)
        {
            ThrowTemporalTypeError(isolate, "Must specify at least one calendar field.");
        }
        return result;
    }

    // ---- System time -----------------------------------------------------------------------------

    /// <summary>SystemTimeZoneIdentifier: without V8_INTL_SUPPORT, UTC.</summary>
    static TimeZone SystemTimeZoneIdentifier() => TimeZone.Utc;

    /// <summary>SystemUTCEpochNanoseconds.</summary>
    static Int128 SystemUTCEpochNanoseconds(Isolate isolate)
    {
        // The embedder callback (temporal_get_epoch_nanoseconds_callback) is not ported.
        long ticks = DateTime.UtcNow.Ticks - DateTime.UnixEpoch.Ticks;
        return (Int128)ticks * 100;
    }

    /// <summary>GenericTemporalNowISO.</summary>
    static ZonedDateTime GenericTemporalNowISO(Isolate isolate, JSValue temporalTimeZoneLike)
    {
        TimeZone timeZone = temporalTimeZoneLike.IsUndefined
            ? SystemTimeZoneIdentifier()
            : ToTemporalTimeZoneIdentifier(isolate, temporalTimeZoneLike);
        Int128 ns = SystemUTCEpochNanoseconds(isolate);
        Instant instant = Instant.TryNew(ns);
        return instant.ToZonedDateTimeISO(timeZone);
    }

    // ---- Construction operations -----------------------------------------------------------------

    /// <summary>ConstructRustWrappingType: OrdinaryCreateFromConstructor + the wrapped value.</summary>
    static T Construct<T>(Isolate isolate, JSFunction target, JSValue newTarget) where T : JSTemporalObject
    {
        Map map = JSFunction.GetDerivedMap(isolate, target, newTarget.As<JSReceiver>());
        return (T)isolate.Factory.NewJSObjectFromMap(map);
    }

    /// <summary>JSTemporalFoo::GetConstructorTarget (initializing the lazy Temporal part of the context if needed).</summary>
    static JSFunction Ctor(Isolate isolate, Context.Field index)
    {
        NativeContext nativeContext = isolate.NativeContext;
        if (nativeContext.Slots[(int)index].IsUndefined) Genesis.InitializeLazyTemporalPartOfContext(isolate, nativeContext);
        return nativeContext.Slots[(int)index].As<JSFunction>();
    }

    internal static JSTemporalDuration Wrap(Isolate isolate, Duration value, JSFunction? target = null, JSValue newTarget = default)
    {
        target ??= Ctor(isolate, Context.Field.JS_TEMPORAL_DURATION_FUNCTION_INDEX);
        var obj = Construct<JSTemporalDuration>(isolate, target, newTarget.IsUndefined ? target : newTarget);
        obj.Value = value;
        return obj;
    }

    internal static JSTemporalInstant Wrap(Isolate isolate, Instant value, JSFunction? target = null, JSValue newTarget = default)
    {
        target ??= Ctor(isolate, Context.Field.JS_TEMPORAL_INSTANT_FUNCTION_INDEX);
        var obj = Construct<JSTemporalInstant>(isolate, target, newTarget.IsUndefined ? target : newTarget);
        obj.Value = value;
        return obj;
    }

    internal static JSTemporalPlainDate Wrap(Isolate isolate, PlainDate value, JSFunction? target = null, JSValue newTarget = default)
    {
        target ??= Ctor(isolate, Context.Field.JS_TEMPORAL_PLAIN_DATE_FUNCTION_INDEX);
        var obj = Construct<JSTemporalPlainDate>(isolate, target, newTarget.IsUndefined ? target : newTarget);
        obj.Value = value;
        return obj;
    }

    internal static JSTemporalPlainDateTime Wrap(Isolate isolate, PlainDateTime value, JSFunction? target = null,
        JSValue newTarget = default)
    {
        target ??= Ctor(isolate, Context.Field.JS_TEMPORAL_PLAIN_DATE_TIME_FUNCTION_INDEX);
        var obj = Construct<JSTemporalPlainDateTime>(isolate, target, newTarget.IsUndefined ? target : newTarget);
        obj.Value = value;
        return obj;
    }

    internal static JSTemporalPlainMonthDay Wrap(Isolate isolate, PlainMonthDay value, JSFunction? target = null,
        JSValue newTarget = default)
    {
        target ??= Ctor(isolate, Context.Field.JS_TEMPORAL_PLAIN_MONTH_DAY_FUNCTION_INDEX);
        var obj = Construct<JSTemporalPlainMonthDay>(isolate, target, newTarget.IsUndefined ? target : newTarget);
        obj.Value = value;
        return obj;
    }

    internal static JSTemporalPlainTime Wrap(Isolate isolate, PlainTime value, JSFunction? target = null, JSValue newTarget = default)
    {
        target ??= Ctor(isolate, Context.Field.JS_TEMPORAL_PLAIN_TIME_FUNCTION_INDEX);
        var obj = Construct<JSTemporalPlainTime>(isolate, target, newTarget.IsUndefined ? target : newTarget);
        obj.Value = value;
        return obj;
    }

    internal static JSTemporalPlainYearMonth Wrap(Isolate isolate, PlainYearMonth value, JSFunction? target = null,
        JSValue newTarget = default)
    {
        target ??= Ctor(isolate, Context.Field.JS_TEMPORAL_PLAIN_YEAR_MONTH_FUNCTION_INDEX);
        var obj = Construct<JSTemporalPlainYearMonth>(isolate, target, newTarget.IsUndefined ? target : newTarget);
        obj.Value = value;
        return obj;
    }

    internal static JSTemporalZonedDateTime Wrap(Isolate isolate, ZonedDateTime value, JSFunction? target = null,
        JSValue newTarget = default)
    {
        target ??= Ctor(isolate, Context.Field.JS_TEMPORAL_ZONED_DATE_TIME_FUNCTION_INDEX);
        var obj = Construct<JSTemporalZonedDateTime>(isolate, target, newTarget.IsUndefined ? target : newTarget);
        obj.Value = value;
        return obj;
    }

    /// <summary>ToTemporalDurationRust.</summary>
    static Duration ToTemporalDurationValue(Isolate isolate, JSValue item, string methodName)
    {
        if (item.HeapObjectOrNull is JSTemporalDuration duration) return duration.Value;
        if (!item.IsJSReceiver)
        {
            if (!item.IsString) ThrowTemporalTypeError(isolate, "Duration argument must be Duration or string.");
            return IsoParser.ParseDuration(item.As<JSString>().FlatSpan());
        }
        PartialDuration partial = ToTemporalPartialDurationRecord(isolate, item);
        return Duration.FromPartial(partial);
    }

    /// <summary>ToTemporalDuration.</summary>
    static JSTemporalDuration ToTemporalDuration(Isolate isolate, JSValue item, string methodName) =>
        Wrap(isolate, ToTemporalDurationValue(isolate, item, methodName));

    /// <summary>ToTemporalInstant.</summary>
    static Instant ToTemporalInstant(Isolate isolate, JSValue item, string methodName)
    {
        if (item.HeapObjectOrNull is JSTemporalInstant instant) return instant.Value;
        if (item.HeapObjectOrNull is JSTemporalZonedDateTime zdt) return Instant.TryNew(zdt.Value.EpochNanoseconds);
        JSValue itemPrim = item.HeapObjectOrNull is JSReceiver r ? JSReceiver.ToPrimitive(isolate, r, ToPrimitiveHint.String) : item;
        if (!itemPrim.IsString) ThrowTemporalTypeError(isolate, "Instant argument must be Instant or string.");
        return Instant.FromString(itemPrim.As<JSString>().FlatSpan());
    }

    /// <summary>ToTemporalTime.</summary>
    static PlainTime ToTemporalTime(Isolate isolate, JSValue item, JSValue options, bool hasOptions, string methodName)
    {
        if (!item.IsHeapObject) ThrowTemporalTypeError(isolate, "Time-like argument must be object or string");
        if (item.HeapObjectOrNull is JSReceiver receiver)
        {
            PartialTime partial;
            if (receiver is JSTemporalPlainTime time)
            {
                ToTemporalOverflowHandleUndefined(isolate, options, hasOptions, methodName);
                return time.Value;
            }
            else if (receiver is JSTemporalPlainDateTime dateTime)
            {
                partial = PartialTime.From(dateTime.Value.Time);
            }
            else if (receiver is JSTemporalZonedDateTime zoned)
            {
                partial = PartialTime.From(zoned.Value.Time);
            }
            else
            {
                TimeRecord record = ToTemporalTimeRecord(isolate, receiver, methodName);
                Overflow overflow0 = ToTemporalOverflowHandleUndefined(isolate, options, hasOptions, methodName);
                partial = record.Regulate(isolate, overflow0);
                return PlainTime.FromPartial(partial, overflow0);
            }
            Overflow overflow = ToTemporalOverflowHandleUndefined(isolate, options, hasOptions, methodName);
            return PlainTime.FromPartial(partial, overflow);
        }
        if (!item.IsString) ThrowTemporalTypeError(isolate, "Time-like argument must be object or string");
        PlainTime parsed = PlainTime.FromString(item.As<JSString>().FlatSpan());
        ToTemporalOverflowHandleUndefined(isolate, options, hasOptions, methodName);
        return parsed;
    }

    static PlainTime ToTemporalTime(Isolate isolate, JSValue item, string methodName) =>
        ToTemporalTime(isolate, item, default, false, methodName);

    /// <summary>ToTimeRecordOrMidnight: null is midnight.</summary>
    static PlainTime? ToTimeRecordOrMidnight(Isolate isolate, JSValue item, string methodName) =>
        item.IsUndefined ? null : ToTemporalTime(isolate, item, methodName);

    /// <summary>ToTemporalDate.</summary>
    static PlainDate ToTemporalDate(Isolate isolate, JSValue item, JSValue options, bool hasOptions, string methodName)
    {
        if (!item.IsHeapObject) ThrowTemporalTypeError(isolate, "Date argument must be object or string.");
        if (item.HeapObjectOrNull is JSReceiver receiver)
        {
            PlainDate result;
            if (receiver is JSTemporalPlainDate date)
            {
                ToTemporalOverflowHandleUndefined(isolate, options, hasOptions, methodName);
                return date.Value;
            }
            else if (receiver is JSTemporalZonedDateTime zoned)
            {
                result = PlainDate.Create(zoned.Value.IsoDate, zoned.Value.Calendar);
            }
            else if (receiver is JSTemporalPlainDateTime dateTime)
            {
                result = PlainDate.Create(dateTime.Value.IsoDate, dateTime.Value.Calendar);
            }
            else
            {
                Calendar kind = GetTemporalCalendarIdentifierWithISODefault(isolate, receiver);
                CombinedRecord fields = PrepareCalendarFields(isolate, kind, receiver, kAllDateFlags, RequiredFields.kNone);
                Overflow overflow = ToTemporalOverflowHandleUndefined(isolate, options, hasOptions, methodName);
                PartialDate partial = fields.RegulateDate(isolate, overflow);
                return PlainDate.FromPartial(partial, overflow);
            }
            ToTemporalOverflowHandleUndefined(isolate, options, hasOptions, methodName);
            return result;
        }
        if (!item.IsString) ThrowTemporalTypeError(isolate, "Date argument must be object or string.");
        PlainDate parsed = PlainDate.FromString(item.As<JSString>().FlatSpan());
        ToTemporalOverflowHandleUndefined(isolate, options, hasOptions, methodName);
        return parsed;
    }

    static PlainDate ToTemporalDate(Isolate isolate, JSValue item, string methodName) =>
        ToTemporalDate(isolate, item, default, false, methodName);

    /// <summary>ToTemporalDateTime.</summary>
    static PlainDateTime ToTemporalDateTime(Isolate isolate, JSValue item, JSValue options, bool hasOptions, string methodName)
    {
        if (!item.IsHeapObject) ThrowTemporalTypeError(isolate, "DateTime argument must be object or string.");
        if (item.HeapObjectOrNull is JSReceiver receiver)
        {
            PartialDateTime partial;
            if (receiver is JSTemporalPlainDateTime dateTime)
            {
                ToTemporalOverflowHandleUndefined(isolate, options, hasOptions, methodName);
                return dateTime.Value;
            }
            else if (receiver is JSTemporalZonedDateTime zoned)
            {
                partial = new PartialDateTime
                {
                    Date = CalendarFields.ToFields(zoned.Value.Calendar, zoned.Value.IsoDate, CalendarFieldsType.Date),
                    Time = PartialTime.From(zoned.Value.Time),
                };
            }
            else if (receiver is JSTemporalPlainDate date)
            {
                partial = new PartialDateTime
                {
                    Date = CalendarFields.ToFields(date.Value.Calendar, date.Value.IsoDate, CalendarFieldsType.Date),
                };
            }
            else
            {
                Calendar kind = GetTemporalCalendarIdentifierWithISODefault(isolate, receiver);
                CombinedRecord fields = PrepareCalendarFields(isolate, kind, receiver,
                    kAllDateFlags | CalendarFieldsFlag.kTimeFields, RequiredFields.kNone);
                Overflow overflow0 = ToTemporalOverflowHandleUndefined(isolate, options, hasOptions, methodName);
                partial = fields.RegulateDateTime(isolate, overflow0);
                return PlainDateTime.FromPartial(partial, overflow0);
            }
            Overflow overflow = ToTemporalOverflowHandleUndefined(isolate, options, hasOptions, methodName);
            return PlainDateTime.FromPartial(partial, overflow);
        }
        if (!item.IsString) ThrowTemporalTypeError(isolate, "DateTime argument must be object or string.");
        PlainDateTime parsed = PlainDateTime.FromString(item.As<JSString>().FlatSpan());
        ToTemporalOverflowHandleUndefined(isolate, options, hasOptions, methodName);
        return parsed;
    }

    static PlainDateTime ToTemporalDateTime(Isolate isolate, JSValue item, string methodName) =>
        ToTemporalDateTime(isolate, item, default, false, methodName);

    /// <summary>ToTemporalYearMonth.</summary>
    static PlainYearMonth ToTemporalYearMonth(Isolate isolate, JSValue item, JSValue options, bool hasOptions, string methodName)
    {
        if (!item.IsHeapObject) ThrowTemporalTypeError(isolate, "YearMonth argument must be object or string.");
        if (item.HeapObjectOrNull is JSReceiver receiver)
        {
            if (receiver is JSTemporalPlainYearMonth yearMonth)
            {
                ToTemporalOverflowHandleUndefined(isolate, options, hasOptions, methodName);
                return yearMonth.Value;
            }
            Calendar kind = GetTemporalCalendarIdentifierWithISODefault(isolate, receiver);
            CombinedRecord fields = PrepareCalendarFields(isolate, kind, receiver,
                CalendarFieldsFlag.kYearFields | CalendarFieldsFlag.kMonthFields, RequiredFields.kNone);
            Overflow overflow = ToTemporalOverflowHandleUndefined(isolate, options, hasOptions, methodName);
            PartialDate partial = fields.RegulateDate(isolate, overflow);
            return PlainYearMonth.FromPartial(partial, overflow);
        }
        if (!item.IsString) ThrowTemporalTypeError(isolate, "YearMonth argument must be object or string.");
        PlainYearMonth parsed = PlainYearMonth.FromString(item.As<JSString>().FlatSpan());
        ToTemporalOverflowHandleUndefined(isolate, options, hasOptions, methodName);
        return parsed;
    }

    static PlainYearMonth ToTemporalYearMonth(Isolate isolate, JSValue item, string methodName) =>
        ToTemporalYearMonth(isolate, item, default, false, methodName);

    /// <summary>ZDTOptions.</summary>
    readonly record struct ZDTOptions(Disambiguation Disambiguation, OffsetDisambiguation OffsetOption, Overflow Overflow);

    /// <summary>GetZDTOptions.</summary>
    static ZDTOptions GetZDTOptions(Isolate isolate, JSValue optionsObj, bool hasOptions, string methodName)
    {
        if (!hasOptions) return new ZDTOptions(Disambiguation.Compatible, OffsetDisambiguation.Reject, Overflow.Constrain);
        Disambiguation disambiguation = GetTemporalDisambiguationOptionHandleUndefined(isolate, optionsObj, methodName);
        OffsetDisambiguation offsetOption =
            GetTemporalOffsetOptionHandleUndefined(isolate, optionsObj, OffsetDisambiguation.Reject, methodName);
        Overflow overflow = ToTemporalOverflowHandleUndefined(isolate, optionsObj, methodName);
        return new ZDTOptions(disambiguation, offsetOption, overflow);
    }

    /// <summary>ToTemporalZonedDateTime.</summary>
    static ZonedDateTime ToTemporalZonedDateTime(Isolate isolate, JSValue item, JSValue optionsObj, bool hasOptions,
        string methodName)
    {
        if (!item.IsHeapObject) ThrowTemporalTypeError(isolate, "ZonedDateTime argument must be object or string.");
        if (item.HeapObjectOrNull is JSReceiver receiver)
        {
            if (receiver is JSTemporalZonedDateTime zoned)
            {
                GetZDTOptions(isolate, optionsObj, hasOptions, methodName);
                return zoned.Value;
            }
            Calendar kind = GetTemporalCalendarIdentifierWithISODefault(isolate, receiver);
            CombinedRecord fields = PrepareCalendarFields(isolate, kind, receiver,
                kAllDateFlags | CalendarFieldsFlag.kTimeFields | CalendarFieldsFlag.kOffset | CalendarFieldsFlag.kTimeZone,
                RequiredFields.kTimeZone);
            ZDTOptions options = GetZDTOptions(isolate, optionsObj, hasOptions, methodName);
            PartialZonedDateTime partial = fields.RegulateZoned(isolate, options.Overflow);
            return ZonedDateTime.FromPartial(partial, options.Overflow, options.Disambiguation, options.OffsetOption);
        }
        if (!item.IsString) ThrowTemporalTypeError(isolate, "ZonedDateTime argument must be object or string.");
        ParsedIso parsed = ZonedDateTime.Parse(item.As<JSString>().FlatSpan());
        ZDTOptions opts = GetZDTOptions(isolate, optionsObj, hasOptions, methodName);
        return ZonedDateTime.FromParsed(parsed, opts.Disambiguation, opts.OffsetOption);
    }

    static ZonedDateTime ToTemporalZonedDateTime(Isolate isolate, JSValue item, string methodName) =>
        ToTemporalZonedDateTime(isolate, item, default, false, methodName);

    /// <summary>ToTemporalMonthDay.</summary>
    static PlainMonthDay ToTemporalMonthDay(Isolate isolate, JSValue item, JSValue options, bool hasOptions, string methodName)
    {
        if (!item.IsHeapObject) ThrowTemporalTypeError(isolate, "MonthDay argument must be object or string.");
        if (item.HeapObjectOrNull is JSReceiver receiver)
        {
            if (receiver is JSTemporalPlainMonthDay monthDay)
            {
                ToTemporalOverflowHandleUndefined(isolate, options, hasOptions, methodName);
                return monthDay.Value;
            }
            Calendar kind = GetTemporalCalendarIdentifierWithISODefault(isolate, receiver);
            CombinedRecord fields = PrepareCalendarFields(isolate, kind, receiver,
                CalendarFieldsFlag.kYearFields | CalendarFieldsFlag.kMonthFields | CalendarFieldsFlag.kDay, RequiredFields.kNone);
            Overflow overflow = ToTemporalOverflowHandleUndefined(isolate, options, hasOptions, methodName);
            PartialDate partial = fields.RegulateDate(isolate, overflow);
            return PlainMonthDay.FromPartial(partial, overflow);
        }
        if (!item.IsString) ThrowTemporalTypeError(isolate, "MonthDay argument must be object or string.");
        PlainMonthDay parsed = PlainMonthDay.FromString(item.As<JSString>().FlatSpan());
        ToTemporalOverflowHandleUndefined(isolate, options, hasOptions, methodName);
        return parsed;
    }

    static PlainMonthDay ToTemporalMonthDay(Isolate isolate, JSValue item, string methodName) =>
        ToTemporalMonthDay(isolate, item, default, false, methodName);

    /// <summary>GetTemporalRelativeToOptionHandleUndefined.</summary>
    static RelativeTo GetTemporalRelativeToOptionHandleUndefined(Isolate isolate, JSValue options)
    {
        if (options.IsUndefined) return default;
        JSString relativeToIdent = isolate.Factory.InternalizeString("relativeTo");
        if (options.HeapObjectOrNull is not JSReceiver optionsReceiver)
        {
            ThrowTemporalTypeError(isolate, kOptionMustBeObject, relativeToIdent);
            return default;
        }
        JSValue value = JSReceiver.GetProperty(isolate, optionsReceiver, relativeToIdent);
        if (value.IsUndefined) return default;
        if (!value.IsHeapObject) ThrowTemporalTypeError(isolate, "relativeTo must be object or string.");
        if (value.HeapObjectOrNull is JSReceiver valueReceiver)
        {
            if (valueReceiver is JSTemporalZonedDateTime zoned) return new RelativeTo(null, zoned.Value);
            if (valueReceiver is JSTemporalPlainDate date) return new RelativeTo(date.Value, null);
            if (valueReceiver is JSTemporalPlainDateTime dateTime)
            {
                return new RelativeTo(PlainDate.Create(dateTime.Value.IsoDate, dateTime.Value.Calendar), null);
            }
            Calendar kind = GetTemporalCalendarIdentifierWithISODefault(isolate, valueReceiver);
            CombinedRecord fields = PrepareCalendarFields(isolate, kind, valueReceiver,
                kAllDateFlags | CalendarFieldsFlag.kTimeFields | CalendarFieldsFlag.kOffset | CalendarFieldsFlag.kTimeZone,
                RequiredFields.kNone);
            Overflow overflow = Overflow.Constrain;
            PartialZonedDateTime partial = fields.RegulateZoned(isolate, overflow);
            if (partial.TimeZone is null)
            {
                return new RelativeTo(PlainDate.FromPartial(partial.Date, overflow), null);
            }
            return new RelativeTo(null, ZonedDateTime.FromPartial(partial, overflow, Disambiguation.Compatible,
                OffsetDisambiguation.Reject));
        }
        if (!value.IsString) ThrowTemporalTypeError(isolate, "relativeTo must be object or string.");
        return RelativeToFromString(value.As<JSString>().FlatSpan());
    }

    /// <summary>OwnedRelativeTo::from_utf16_with_provider: the string case of GetTemporalRelativeToOption.</summary>
    static RelativeTo RelativeToFromString(ReadOnlySpan<char> s)
    {
        ParsedIso result = IsoParser.Parse(s, IsoGoal.ZonedDateTime | IsoGoal.DateTime);
        Calendar calendar = PlainDate.ResolveCalendar(result.Calendar);
        if (result.TimeZoneAnnotation is null)
        {
            if (result.HasZ) throw TemporalError.Range("UTC designator requires a time zone annotation.");
            return new RelativeTo(PlainDate.Create(new IsoDate(result.Year!.Value, result.Month, result.Day), calendar), null);
        }
        TimeZone timeZone = TimeZone.FromIdentifier(result.TimeZoneAnnotation);
        OffsetBehaviour offsetBehaviour = result.HasZ ? OffsetBehaviour.Exact :
            result.OffsetString is null ? OffsetBehaviour.Wall : OffsetBehaviour.Option;
        long offsetNs = 0;
        bool matchMinutes = true;
        if (offsetBehaviour == OffsetBehaviour.Option)
        {
            offsetNs = IsoParser.ParseDateTimeUTCOffset(result.OffsetString);
            if (IsoParser.OffsetHasSubMinutePrecision(result.OffsetString)) matchMinutes = false;
        }
        var isoDate = new IsoDate(result.Year!.Value, result.Month, result.Day);
        IsoTime? time = result.HasTime ? result.Time : null;
        Int128 epochNs = ZonedDateTime.InterpretISODateTimeOffset(isoDate, time, offsetBehaviour, offsetNs, timeZone,
            Disambiguation.Compatible, OffsetDisambiguation.Reject, matchMinutes);
        return new RelativeTo(null, ZonedDateTime.Create(epochNs, timeZone, calendar));
    }

    // ---- Generic operations ----------------------------------------------------------------------

    /// <summary>GenericDifferenceTemporal: reads the options after the calendar check.</summary>
    static DifferenceSettings DifferenceSettingsFor(Isolate isolate, Calendar? thisCalendar, Calendar? otherCalendar,
        JSValue options, UnitGroup group, Unit fallbackSmallestUnit, string methodName)
    {
        if (thisCalendar is not null && thisCalendar != otherCalendar)
        {
            isolate.ThrowRangeError(MessageTemplate.MismatchedCalendars);
        }
        return GetDifferenceSettingsWithoutChecks(isolate, options, group, fallbackSmallestUnit, methodName);
    }

    /// <summary>
    /// GenericWith, up to PrepareCalendarFields (the options are read by the caller, as
    /// GenericWithHelper does).
    /// </summary>
    static CombinedRecord GenericWithFields(Isolate isolate, Calendar kind, JSValue temporalLikeObj, CalendarFieldsFlag flags)
    {
        if (!IsPartialTemporalObject(isolate, temporalLikeObj)) ThrowTemporalTypeError(isolate, kWithNoPartial);
        return PrepareCalendarFields(isolate, kind, temporalLikeObj.As<JSReceiver>(), flags, RequiredFields.kPartial);
    }

    /// <summary>The roundTo / totalOf / directionParam argument: a string becomes { key: string }.</summary>
    static JSReceiver StringOrOptionsObject(Isolate isolate, JSValue arg, string key, string notObjectMessage)
    {
        if (arg.IsString)
        {
            JSObject obj = isolate.Factory.NewJSObjectWithNullProto();
            JSReceiver.CreateDataProperty(isolate, obj, isolate.Factory.InternalizeString(key), arg, ShouldThrow.ThrowOnError);
            return obj;
        }
        if (arg.HeapObjectOrNull is not JSReceiver receiver)
        {
            ThrowTemporalTypeError(isolate, notObjectMessage);
            return null!;
        }
        return receiver;
    }
}
