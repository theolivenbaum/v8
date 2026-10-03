// Tests of Temporal: the engine (V8Sharp.Temporal, standing in for temporal_rs)
// and the binding (Builtins.Temporal*.cs, Init/Genesis.Temporal.cs). test262's
// built-ins/Temporal is the conformance suite; these pin down the pieces
// that are easy to get subtly wrong (exact rounding, calendar arithmetic
// edges, the grammar) and the V8-specific binding behaviour.
using System.Numerics;
using V8Sharp.Codegen;
using V8Sharp.Temporal;
using Duration = V8Sharp.Temporal.Duration;
using TimeZone = V8Sharp.Temporal.TimeZone;

namespace V8Sharp.Tests.Temporal;

public class TemporalEngineTest
{
    [Fact]
    public void EpochDaysRoundTrip()
    {
        Assert.Equal(0, IsoCalendar.EpochDays(1970, 1, 1));
        Assert.Equal(-1, IsoCalendar.EpochDays(1969, 12, 31));
        Assert.Equal(new IsoDate(2000, 2, 29), IsoCalendar.FromEpochDays(IsoCalendar.EpochDays(2000, 2, 29)));
        for (long d = -100_000_001; d <= 100_000_001; d += 99_991)
        {
            IsoDate date = IsoCalendar.FromEpochDays(d);
            Assert.Equal(d, IsoCalendar.EpochDays(date));
        }
        // The limits of the representable range.
        Assert.Equal(new IsoDate(-271821, 4, 20), IsoCalendar.FromEpochDays(-100_000_000));
        Assert.Equal(new IsoDate(275760, 9, 13), IsoCalendar.FromEpochDays(100_000_000));
    }

    [Fact]
    public void WeekOfYear()
    {
        Assert.Equal((1, 2021L), IsoCalendar.WeekOfYear(new IsoDate(2021, 1, 4)));
        Assert.Equal((53, 2020L), IsoCalendar.WeekOfYear(new IsoDate(2021, 1, 3)));
        Assert.Equal((1, 2020L), IsoCalendar.WeekOfYear(new IsoDate(2019, 12, 30)));
        Assert.Equal(1, IsoCalendar.DayOfWeek(new IsoDate(2024, 1, 1)));
        Assert.Equal(7, IsoCalendar.DayOfWeek(new IsoDate(-1, 1, 3)));
    }

    [Theory]
    [InlineData(25, 10, RoundingMode.HalfEven, 20)]
    [InlineData(35, 10, RoundingMode.HalfEven, 40)]
    [InlineData(-25, 10, RoundingMode.HalfEven, -20)]
    [InlineData(-25, 10, RoundingMode.HalfCeil, -20)]
    [InlineData(-25, 10, RoundingMode.HalfFloor, -30)]
    [InlineData(-21, 10, RoundingMode.Ceil, -20)]
    [InlineData(-21, 10, RoundingMode.Floor, -30)]
    [InlineData(-21, 10, RoundingMode.Expand, -30)]
    [InlineData(-29, 10, RoundingMode.Trunc, -20)]
    [InlineData(25, 10, RoundingMode.HalfTrunc, 20)]
    public void RoundNumberToIncrement(long x, long increment, RoundingMode mode, long expected) =>
        Assert.Equal((Int128)expected, Rounding.RoundToIncrement((Int128)x, (Int128)increment, mode));

    [Fact]
    public void RationalToDoubleIsCorrectlyRounded()
    {
        Assert.Equal(1.0 / 3, Rounding.RationalToDouble(1, 3));
        Assert.Equal(-2.0 / 3, Rounding.RationalToDouble(-2, 3));
        Assert.Equal(1.0134408602150538, Rounding.RationalToDouble(754, 744));
        // 2^53 + 1 is a tie between 2^53 and 2^53 + 2: half to even.
        Assert.Equal(9007199254740992.0, Rounding.RationalToDouble((BigInteger.One << 53) + 1, 1));
        Assert.Equal(9007199254740996.0, Rounding.RationalToDouble((BigInteger.One << 53) + 3, 1));
        Assert.Equal(1e-9, Rounding.RationalToDouble(1, 1_000_000_000));
    }

    [Fact]
    public void DurationValidityBounds()
    {
        Assert.True(Duration.IsValid(0, 0, 0, 0, 0, 0, 9007199254740991, 999, 999, 999));
        Assert.False(Duration.IsValid(0, 0, 0, 0, 0, 0, 9007199254740991, 999, 999, 1000));
        Assert.False(Duration.IsValid(4294967296, 0, 0, 0, 0, 0, 0, 0, 0, 0));
        Assert.False(Duration.IsValid(1, -1, 0, 0, 0, 0, 0, 0, 0, 0));
        Assert.False(Duration.IsValid(0, 0, 0, 0, 0, 0, 0, 0, 0, double.PositiveInfinity));
        Assert.True(Duration.IsValid(0, 0, 0, 0, 0, 0, 0, 0, 0, 9.007199254740991e24));
    }

    [Theory]
    [InlineData("PT1.5H", "PT1H30M")]
    [InlineData("-P1Y2M3W4DT5H6M7.008009010S", "-P1Y2M3W4DT5H6M7.00800901S")]
    [InlineData("PT0.000000001S", "PT0.000000001S")]
    [InlineData("pt1m", "PT1M")]
    [InlineData("P0D", "PT0S")]
    [InlineData("PT1,5M", "PT1M30S")]
    public void ParseDuration(string input, string expected) => Assert.Equal(expected, IsoParser.ParseDuration(input).ToString());

    [Theory]
    [InlineData("P")]
    [InlineData("PT")]
    [InlineData("P1D1Y")]
    [InlineData("PT1.5H2M")]
    [InlineData("PT0.0000000001S")]
    [InlineData("P1.5D")]
    public void ParseDurationRejects(string input) => Assert.Throws<TemporalError>(() => IsoParser.ParseDuration(input));

    [Fact]
    public void TimeStringsAmbiguousWithDates()
    {
        foreach (string s in new[] { "2021-12", "1214", "0229", "12-14", "202112" })
        {
            Assert.Throws<TemporalError>(() => PlainTime.FromString(s));
            Assert.NotNull(PlainTime.FromString("T" + s));
        }
        foreach (string s in new[] { "2021-13", "1232", "0230", "0000" }) Assert.NotNull(PlainTime.FromString(s));
    }

    [Fact]
    public void Annotations()
    {
        Assert.Equal("iso8601", IsoParser.Parse("2020-01-01[u-ca=iso8601][foo=bar]", IsoGoal.DateTime).Calendar);
        Assert.Throws<TemporalError>(() => IsoParser.Parse("2020-01-01[!foo=bar]", IsoGoal.DateTime));
        Assert.Throws<TemporalError>(() => IsoParser.Parse("2020-01-01[U-CA=iso8601]", IsoGoal.DateTime));
        Assert.Throws<TemporalError>(() => IsoParser.Parse("2020-01-01[u-ca=iso8601][!u-ca=gregory]", IsoGoal.DateTime));
        Assert.Equal("Europe/Vienna", IsoParser.Parse("2020-01-01T00:00Z[Europe/Vienna]", IsoGoal.Instant).TimeZoneAnnotation);
        Assert.Throws<TemporalError>(() => IsoParser.Parse("-000000-01-01", IsoGoal.DateTime));
    }

    [Fact]
    public void DateUntilClampsTheEndOfMonth()
    {
        // 2021-12-31 to 2024-02-29 is 2 years, 1 month and 29 days (Jan 31 + 29 days).
        Assert.Equal(new DateDuration(2, 1, 0, 29), IsoCalendar.DateUntil(new IsoDate(2021, 12, 31), new IsoDate(2024, 2, 29), Unit.Year));
        Assert.Equal(new DateDuration(0, -1, 0, -2), IsoCalendar.DateUntil(new IsoDate(2020, 3, 31), new IsoDate(2020, 2, 27), Unit.Month));
        Assert.Equal(new DateDuration(0, 0, 4, 1), IsoCalendar.DateUntil(new IsoDate(2020, 2, 1), new IsoDate(2020, 3, 1), Unit.Week));
    }

    [Fact]
    public void RoundingWindowMovesPastConstrainedEnd()
    {
        // proposal-temporal#3168: P1MT10H from 2020-01-31 is 1 month + 10 hours of a 31-day month.
        var relativeTo = new RelativeTo(PlainDate.TryNew(2020, 1, 31, Calendar.Iso), null);
        Duration d = Duration.Create(0, 1, 0, 0, 10, 0, 0, 0, 0, 0);
        Assert.Equal(1.0134408602150538, RelativeRounding.Total(d, Unit.Month, relativeTo));
        Duration rounded = RelativeRounding.Round(d, new RoundingOptions(null, Unit.Month, RoundingMode.Expand, 1), relativeTo);
        Assert.Equal("P2M", rounded.ToString());
    }

    [Fact]
    public void OffsetTimeZones()
    {
        TimeZone tz = TimeZone.FromString("+05:30");
        Assert.Equal("+05:30", tz.Identifier);
        Assert.Equal(19_800_000_000_000L, tz.GetOffsetNanosecondsFor(0));
        Assert.Equal("UTC", TimeZone.FromString("2020-01-01T00:00Z").Identifier);
        Assert.Throws<TemporalError>(() => TimeZone.FromString("+01:00:01"));
        Assert.Throws<TemporalError>(() => TimeZone.FromString("Mars/Olympus_Mons"));
        Assert.True(TimeZone.Equals(TimeZone.FromString("utc"), TimeZone.Utc));
    }
}

public class TemporalBindingTest : TestWithContext
{
    string Run(string source) => ObjectOps.ToString(i_isolate, Compiler.CompileAndRun(i_isolate, source)).ToString();

    [Fact]
    public void TemporalIsInstalledLazily()
    {
        Assert.Equal("false,true,true,false", Run("""
            var d = Object.getOwnPropertyDescriptor(globalThis, "Temporal");
            [d.enumerable, d.writable, d.configurable, "get" in d].join();
            """));
        Assert.Equal("[object Temporal],[object Temporal.Now],Temporal.PlainDate", Run("""
            [String(Temporal), String(Temporal.Now), Temporal.PlainDate.prototype[Symbol.toStringTag]].join();
            """));
        Assert.Equal("1970-01-01T00:00:00Z", Run("new Date(0).toTemporalInstant().toString()"));
    }

    [Fact]
    public void ErrorMessagesFollowTheBinding()
    {
        Assert.Equal("RangeError: Temporal error: Invalid ISO date.", Run("try { new Temporal.PlainDate(2021, 2, 29) } catch (e) { String(e) }"));
        Assert.Equal("TypeError: Do not use Temporal.Duration.prototype.valueOf; use Temporal.Duration.prototype.compare for comparison.",
            Run("try { +new Temporal.Duration() } catch (e) { String(e) }"));
        Assert.Equal("RangeError: smallestUnit value is out of range.",
            Run("try { Temporal.Instant.fromEpochNanoseconds(0n).toString({ smallestUnit: 'hour' }) } catch (e) { String(e) }"));
    }

    [Fact]
    public void Arithmetic()
    {
        Assert.Equal("2024-03-30T01:30:00+01:00[+01:00]", Run("""
            Temporal.ZonedDateTime.from("2024-02-29T01:30+01:00[+01:00]").add({ months: 1, days: 1 }).toString()
            """));
        Assert.Equal("P1Y11M29DT23H", Run("""
            Temporal.PlainDateTime.from("2020-01-01T01:00").until("2021-12-31T00:00", { largestUnit: "years" }).toString()
            """));
        Assert.Equal("-PT1H30M", Run("Temporal.PlainTime.from('12:00').since('13:30').toString()"));
        Assert.Equal("123456789.123456791", Run("""
            Temporal.Duration.from({ seconds: 123456789, nanoseconds: 123456789 }).total("seconds").toFixed(9)
            """));
    }
}
