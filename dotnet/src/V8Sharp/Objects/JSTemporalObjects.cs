// Port of src/objects/js-temporal-objects.{h,tq} and js-temporal-objects-inl.h:
// the JS objects of the Temporal types. Each wraps the engine's value
// (V8: a CppGCManaged<temporal_rs::T> in a tagged field; here the managed
// V8Sharp.Temporal object directly, architecture.md section 2).
using V8Sharp.Temporal;

namespace V8Sharp.Objects;

/// <summary>The common base of the JSTemporal* objects (JSObject in V8).</summary>
public abstract class JSTemporalObject(Map map) : JSObject(map)
{
    /// <summary>JSObject::AllocateForMap for the JS_TEMPORAL_*_TYPE instance types.</summary>
    internal static JSObject AllocateTemporalForMap(Map map) => map.InstanceType switch
    {
        InstanceType.JSTemporalDurationType => new JSTemporalDuration(map),
        InstanceType.JSTemporalInstantType => new JSTemporalInstant(map),
        InstanceType.JSTemporalPlainDateType => new JSTemporalPlainDate(map),
        InstanceType.JSTemporalPlainDateTimeType => new JSTemporalPlainDateTime(map),
        InstanceType.JSTemporalPlainMonthDayType => new JSTemporalPlainMonthDay(map),
        InstanceType.JSTemporalPlainTimeType => new JSTemporalPlainTime(map),
        InstanceType.JSTemporalPlainYearMonthType => new JSTemporalPlainYearMonth(map),
        _ => new JSTemporalZonedDateTime(map),
    };
}

/// <summary>JSTemporalDuration (field duration: temporal_rs::Duration).</summary>
public sealed class JSTemporalDuration(Map map) : JSTemporalObject(map)
{
    public Duration Value = null!;
}

/// <summary>JSTemporalInstant (field instant: temporal_rs::Instant).</summary>
public sealed class JSTemporalInstant(Map map) : JSTemporalObject(map)
{
    public Instant Value = null!;
}

/// <summary>JSTemporalPlainDate (field date: temporal_rs::PlainDate).</summary>
public sealed class JSTemporalPlainDate(Map map) : JSTemporalObject(map)
{
    public PlainDate Value = null!;
}

/// <summary>JSTemporalPlainDateTime (field date_time: temporal_rs::PlainDateTime).</summary>
public sealed class JSTemporalPlainDateTime(Map map) : JSTemporalObject(map)
{
    public PlainDateTime Value = null!;
}

/// <summary>JSTemporalPlainMonthDay (field month_day: temporal_rs::PlainMonthDay).</summary>
public sealed class JSTemporalPlainMonthDay(Map map) : JSTemporalObject(map)
{
    public PlainMonthDay Value = null!;
}

/// <summary>JSTemporalPlainTime (field time: temporal_rs::PlainTime).</summary>
public sealed class JSTemporalPlainTime(Map map) : JSTemporalObject(map)
{
    public PlainTime Value = null!;
}

/// <summary>JSTemporalPlainYearMonth (field year_month: temporal_rs::PlainYearMonth).</summary>
public sealed class JSTemporalPlainYearMonth(Map map) : JSTemporalObject(map)
{
    public PlainYearMonth Value = null!;
}

/// <summary>JSTemporalZonedDateTime (field zoned_date_time: temporal_rs::ZonedDateTime).</summary>
public sealed class JSTemporalZonedDateTime(Map map) : JSTemporalObject(map)
{
    public ZonedDateTime Value = null!;
}
