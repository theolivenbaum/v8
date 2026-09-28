// Port of test/unittests/date/date-unittest.cc, plus tests of the Date
// builtins and a differential test of Date.parse and the Date string formats
// against the oracle.
using System.Text;
using System.Text.RegularExpressions;
using V8Sharp.Date;
using V8Sharp.Oracle;
using V8Sharp.Tests.Builtins;

namespace V8Sharp.Tests.Date;

public class DateTest : BuiltinsTestBase
{
    void CheckDST(long time)
    {
        DateCache dateCache = i_isolate.DateCache;
        long actual = dateCache.ToLocal(time);
        long expected = time + dateCache.GetLocalOffsetFromOS(time, true);
        Assert.Equal(expected, actual);
    }

    sealed class DateCacheMock(int localOffset, DateCacheMock.Rule[] rules) : DateCache
    {
        public readonly record struct Rule(int Year, int StartMonth, int StartDay, int EndMonth, int EndDay, int OffsetSec);

        public override int GetDaylightSavingsOffsetFromOS(long timeSec)
        {
            int days = DaysFromTime(timeSec * 1000);
            int timeInDaySec = TimeInDay(timeSec * 1000, days) / 1000;
            YearMonthDayFromDays(days, out int year, out int month, out int day);
            Rule? rule = FindRuleFor(year, month, day, timeInDaySec);
            return rule is null ? 0 : rule.Value.OffsetSec * 1000;
        }

        public override int GetLocalOffsetFromOS(long timeMs, bool isUtc) =>
            localOffset + GetDaylightSavingsOffsetFromOS(timeMs / 1000);

        Rule? FindRuleFor(int year, int month, int day, int timeInDaySec)
        {
            Rule? result = null;
            foreach (Rule rule in rules)
            {
                if (Match(rule, year, month, day, timeInDaySec)) result = rule;
            }
            return result;
        }

        bool Match(Rule rule, int year, int month, int day, int timeInDaySec)
        {
            if (rule.Year != 0 && rule.Year != year) return false;
            if (rule.StartMonth > month) return false;
            if (rule.EndMonth < month) return false;
            int startDay = ComputeRuleDay(year, rule.StartMonth, rule.StartDay);
            if (rule.StartMonth == month && startDay > day) return false;
            if (rule.StartMonth == month && startDay == day && 2 * 3600 > timeInDaySec) return false;
            int endDay = ComputeRuleDay(year, rule.EndMonth, rule.EndDay);
            if (rule.EndMonth == month && endDay < day) return false;
            if (rule.EndMonth == month && endDay == day && 2 * 3600 <= timeInDaySec) return false;
            return true;
        }

        int ComputeRuleDay(int year, int month, int day)
        {
            if (day != 0) return day;
            int days = DaysFromYearMonth(year, month);
            // Find the first Sunday of the month.
            while (Weekday(days + day) != 6) day++;
            return day + 1;
        }
    }

    static long TimeFromYearMonthDay(DateCache dateCache, int year, int month, int day)
    {
        long result = dateCache.DaysFromYearMonth(year, month);
        return (result + day - 1) * DateCache.kMsPerDay;
    }

    [Fact]
    public void DaylightSavingsTime()
    {
        DateCacheMock.Rule[] rules =
        [
            new(0, 2, 0, 10, 0, 3600),  // DST from March to November in any year.
            new(2010, 2, 0, 7, 20, 3600),  // DST from March to August 20 in 2010.
            new(2010, 7, 20, 8, 10, 0),  // No DST from August 20 to September 10 in 2010.
            new(2010, 8, 10, 10, 0, 3600),  // DST from September 10 to November in 2010.
        ];

        int localOffsetMs = -36000000;  // -10 hours.
        var dateCache = new DateCacheMock(localOffsetMs, rules);
        i_isolate.DateCache = dateCache;

        long startOf2010 = TimeFromYearMonthDay(dateCache, 2010, 0, 1);
        long startOf2011 = TimeFromYearMonthDay(dateCache, 2011, 0, 1);
        long august20 = TimeFromYearMonthDay(dateCache, 2010, 7, 20);
        long september10 = TimeFromYearMonthDay(dateCache, 2010, 8, 10);
        CheckDST((august20 + september10) / 2);
        CheckDST(september10);
        CheckDST(september10 + 2 * 3600);
        CheckDST(september10 + 2 * 3600 - 1000);
        CheckDST(august20 + 2 * 3600);
        CheckDST(august20 + 2 * 3600 - 1000);
        CheckDST(august20);
        // Check each day of 2010.
        for (long time = startOf2011 + 2 * 3600; time >= startOf2010; time -= DateCache.kMsPerDay)
        {
            CheckDST(time);
            CheckDST(time - 1000);
            CheckDST(time + 1000);
        }
        // Check one day from 2010 to 2100.
        for (int year = 2100; year >= 2010; year--)
        {
            CheckDST(TimeFromYearMonthDay(dateCache, year, 5, 5));
        }
        CheckDST((august20 + september10) / 2);
        CheckDST(september10);
        CheckDST(september10 + 2 * 3600);
        CheckDST(september10 + 2 * 3600 - 1000);
        CheckDST(august20 + 2 * 3600);
        CheckDST(august20 + 2 * 3600 - 1000);
        CheckDST(august20);
    }

    [Fact]
    public void DateParseLegacyUseCounter()
    {
        int legacyParseCount = 0;
        void Parse(string s)
        {
            Span<double> output = stackalloc double[DateParser.OUTPUT_SIZE];
            DateParser.Parse(i_isolate, s, output, out bool legacy);
            if (legacy) legacyParseCount++;
        }
        Assert.Equal(0, legacyParseCount);
        Parse("2015-02-31");
        Assert.Equal(0, legacyParseCount);
        Parse("2015-02-31T11:22:33.444Z01:23");
        Assert.Equal(0, legacyParseCount);
        Parse("2015-02-31T11:22:33.444");
        Assert.Equal(0, legacyParseCount);
        Parse("2000 01 01");
        Assert.Equal(1, legacyParseCount);
        Parse("2015-02-31T11:22:33.444     ");
        Assert.Equal(1, legacyParseCount);
    }

    [Fact]
    public void Builtins()
    {
        JSValue d = New("Date", Num(2000), Num(0), Num(1), Num(8));
        Assert.Equal(2000, Call("Date.prototype.getFullYear", d).Number);
        Assert.Equal(8, Call("Date.prototype.getHours", d).Number);
        Assert.Equal(6, Call("Date.prototype.getDay", d).Number);
        Assert.Equal(100, Call("Date.prototype.getYear", d).Number);
        double utc = CallStatic("Date.UTC", Num(2000), Num(0), Num(1), Num(8)).Number;
        Assert.Equal(946713600000, utc);
        JSValue u = New("Date", Num(utc));
        Assert.Equal("2000-01-01T08:00:00.000Z", S(Call("Date.prototype.toISOString", u)));
        Assert.Equal("Sat, 01 Jan 2000 08:00:00 GMT", S(Call("Date.prototype.toUTCString", u)));
        Assert.Equal("2000-01-01T08:00:00.000Z", S(Call("Date.prototype.toJSON", u)));
        Assert.Equal(946713600000, Call("Date.prototype.setUTCHours", u, Num(8)).Number);
        Assert.Equal(946713600001, Call("Date.prototype.setUTCMilliseconds", u, Num(1)).Number);
        Assert.Equal(1, Call("Date.prototype.getUTCMilliseconds", u).Number);
        Assert.Equal("+275760-09-13T00:00:00.000Z", S(Call("Date.prototype.toISOString", New("Date", Num(8.64e15)))));
        Assert.Equal("-000001-01-01T00:00:00.000Z", S(Call("Date.prototype.toISOString", New("Date", Num(-62198755200000)))));
        Assert.Equal("Invalid Date", S(Call("Date.prototype.toString", New("Date", Num(8.64e15 + 1)))));
        Assert.Equal("RangeError: Invalid time value", Throws(() => Call("Date.prototype.toISOString", New("Date", JSValue.NaN))));
        Assert.Equal("TypeError: this is not a Date object.", Throws(() => Call("Date.prototype.getTime", Num(1))));
        Assert.Equal("TypeError: Method Date.prototype.setTime called on incompatible receiver 1",
            Throws(() => Call("Date.prototype.setTime", Num(1), Num(1))));
        Assert.True(Call("Date.prototype.toJSON", New("Date", JSValue.NaN)).IsNull);
        Assert.False(double.IsNegative(Call("Date.prototype.setTime", New("Date"), Num(-0.0)).Number));
        Assert.Equal(-1, Call("Date.prototype.setTime", New("Date"), Num(-1.5)).Number);
        Assert.IsType<SeqString>(CallStatic("Date").Object);
        Assert.True(CallStatic("Date.now").Number > 1.7e12);
    }

    [Fact]
    public void ToPrimitive()
    {
        JSValue d = New("Date", Num(0));
        JSValue toPrimitive = Get(G("Date.prototype"), "constructor");
        JSValue fn = ObjectOps.GetProperty(i_isolate, G("Date.prototype"), ReadOnlyRoots.to_primitive_symbol);
        Assert.Equal(0, Execution.Call(i_isolate, fn, d, [Str("number")]).Number);
        Assert.Equal("TypeError: Invalid hint: foo", Throws(() => Execution.Call(i_isolate, fn, d, [Str("foo")])));
        Assert.Equal("TypeError: Method Date.prototype [ @@toPrimitive ] called on incompatible receiver 1",
            Throws(() => Execution.Call(i_isolate, fn, Num(1), [Str("number")])));
        Assert.NotNull(toPrimitive.HeapObjectOrNull);
    }

    /// <summary>The single-quoted strings of mjsunit's date tests, plus extra formats.</summary>
    static List<string> DateStrings()
    {
        var result = new List<string>();
        string? root = FindTestDir();
        if (root is not null)
        {
            foreach (string file in (string[])["date-parse.js", "date.js"])
            {
                string text = File.ReadAllText(Path.Combine(root, file));
                foreach (Match m in Regex.Matches(text, @"'([^'\\\n]{1,80})'"))
                {
                    result.Add(m.Groups[1].Value);
                }
            }
        }
        result.AddRange([
            "", " ", "2000", "2000-01", "2000-01-01", "2000-01-01T00:00", "2000-01-01T00:00Z", "2000-01-01T24:00",
            "2000-01-01T24:00:01", "+002000-01-01T00:00:00Z", "-000000-01-01T00:00:00Z", "-000001-01-01T00:00:00Z",
            "2000-01-01T12:30:45.1234567+05:30", "2000-01-01T12:30:45.1+0530", "2000-13-01", "2000-02-30",
            "Jan 1 2000", "1 Jan 2000", "2000 Jan 1", "1/2/3", "12/31/1999 23:59:59", "12/31/99 11:59 PM",
            "Thu, 01 Jan 1970 00:00:00 GMT+0100", "Thu Jan 01 1970 00:00:00 GMT-0800 (PST)", "July 4 1976 12:00 EDT",
            "Tue Feb 29 2000", "2000-01-01 00:00:00", "2000/01/01", "Sat Jan 01 2000 08:00:00 GMT+0000 (Coordinated Universal Time)",
            "Mon Jan 1 2001 1:2:3.456", "(comment) 2000 (another) 1 1", "2000-01-01T00:00:00.000+24:00", "10:00 am Jan 1 2000",
            "12:00 am Jan 1 2000", "13:00 pm Jan 1 2000", "Jan 1 2000 GMT-8", "Jan 1 2000 UTC+0130", "Jan 1 2000 Z",
            "275760-09-13", "+275760-09-13T00:00:00.000Z", "+275760-09-13T00:00:00.001Z", "-271821-04-20T00:00:00Z",
            "-271821-04-19T23:59:59.999Z", "2000-01-01TZ", "2000-01-01T", "T10:00", "1970", "0", "99", "49 1 1",
            "12:00:00", "Jan", "2000\u00a0Jan\u20031", "2000-01-01T00:00:00,5Z", "1e3", "2000-01-01T00:00:00.Z",
        ]);
        return result;
    }

    static string? FindTestDir()
    {
        string? dir = AppContext.BaseDirectory;
        while (dir is not null)
        {
            string candidate = Path.Combine(dir, "test", "mjsunit");
            if (File.Exists(Path.Combine(candidate, "date-parse.js"))) return candidate;
            dir = Path.GetDirectoryName(dir);
        }
        return null;
    }

    [Fact]
    public void ParseDifferentialAgainstOracle()
    {
        List<string> inputs = DateStrings();
        var js = new StringBuilder("const inputs = [");
        foreach (string s in inputs) js.Append(ReferenceV8.JsQuote(s)).Append(',');
        js.Append("];\nprint(inputs.map(s => String(Date.parse(s))).join('\\n'));");
        string[] expected;
        using (var v8 = new ReferenceV8())
        {
            expected = v8.Run(js.ToString()).TrimEnd('\n').Split('\n');
        }
        Assert.Equal(inputs.Count, expected.Length);
        var report = new StringBuilder();
        int mismatches = 0;
        for (int i = 0; i < inputs.Count; i++)
        {
            string actual = S(CallStatic("Date.parse", Str(inputs[i])));
            if (actual != expected[i] && mismatches++ < 20) report.Append($"'{inputs[i]}': oracle {expected[i]}, v8sharp {actual}\n");
        }
        Assert.True(mismatches == 0, $"{mismatches} of {inputs.Count} differ:\n{report}");
    }

    [Fact]
    public void FormatsDifferentialAgainstOracle()
    {
        // Times across the range, including negative years, far future and
        // DST boundaries of common zones.
        double[] times =
        [
            0, -1, 1, 946713600000, 951782400000, 1710054000000, 1710057600000, 1730595600000, -62198755200000,
            -62198755200001, 8.64e15, -8.64e15, 253402300800000, -2208988800000, 1e12, 123456789012, -123456789012,
            2147483647000, 2147483648000, 4102444800000, -30610224000000, 1720000000000.5,
        ];
        // Without ICU, V8 applies the current standard offset to every date
        // (DateCache::GetLocalOffsetFromOS); the oracle has ICU and applies
        // the historical offsets (LMT and the like). Outside UTC the two only
        // agree where the zone's rules have not changed, so only compare
        // 1971..2037 there (deviations.md, Date).
        if (TimeZoneInfo.Local.BaseUtcOffset != TimeSpan.Zero || TimeZoneInfo.Local.SupportsDaylightSavingTime)
        {
            times = Array.FindAll(times, t => t >= 31536000000 && t < 2145916800000);
        }
        var js = new StringBuilder("const times = [");
        foreach (double t in times) js.Append(D(t)).Append(',');
        js.Append("""
            ];
            const out = [];
            for (const t of times) {
              const d = new Date(t);
              out.push(d.toISOString(), d.toUTCString(), d.toDateString(), d.toString().replace(/ \(.*\)$/, ''),
                       d.toTimeString().replace(/ \(.*\)$/, ''), d.getTimezoneOffset(), d.getFullYear(), d.getMonth(),
                       d.getDate(), d.getDay(), d.getHours(), d.getMinutes(), d.getSeconds(), d.getMilliseconds(),
                       d.getUTCFullYear(), d.getUTCDay(), d.getYear(), JSON.stringify(d),
                       new Date(d.getFullYear(), d.getMonth(), d.getDate(), 12).getTime(),
                       Date.UTC(d.getUTCFullYear(), d.getUTCMonth() + 1, 0), new Date(t).setMonth(13),
                       new Date(t).setUTCDate(40), new Date(t).setFullYear(2012));
            }
            print(out.join('\n'));
            """);
        string[] expected;
        using (var v8 = new ReferenceV8())
        {
            expected = v8.Run(js.ToString()).TrimEnd('\n').Split('\n');
        }

        var actual = new List<string>();
        string CallS(string method, JSValue d) => S(Call("Date.prototype." + method, d));
        string StripZone(string s) => Regex.Replace(s, @" \(.*\)$", "");
        foreach (double t in times)
        {
            JSValue d = New("Date", Num(t));
            actual.Add(CallS("toISOString", d));
            actual.Add(CallS("toUTCString", d));
            actual.Add(CallS("toDateString", d));
            actual.Add(StripZone(CallS("toString", d)));
            actual.Add(StripZone(CallS("toTimeString", d)));
            foreach (string m in (string[])["getTimezoneOffset", "getFullYear", "getMonth", "getDate", "getDay", "getHours",
                         "getMinutes", "getSeconds", "getMilliseconds", "getUTCFullYear", "getUTCDay", "getYear"])
            {
                actual.Add(CallS(m, d));
            }
            actual.Add("\"" + CallS("toJSON", d) + "\"");
            actual.Add(S(Call("Date.prototype.getTime", New("Date", Call("Date.prototype.getFullYear", d),
                Call("Date.prototype.getMonth", d), Call("Date.prototype.getDate", d), Num(12)))));
            actual.Add(S(CallStatic("Date.UTC", Call("Date.prototype.getUTCFullYear", d),
                Num(Call("Date.prototype.getUTCMonth", d).Number + 1), Num(0))));
            actual.Add(S(Call("Date.prototype.setMonth", New("Date", Num(t)), Num(13))));
            actual.Add(S(Call("Date.prototype.setUTCDate", New("Date", Num(t)), Num(40))));
            actual.Add(S(Call("Date.prototype.setFullYear", New("Date", Num(t)), Num(2012))));
        }
        Assert.Equal(expected.Length, actual.Count);
        var report = new StringBuilder();
        int mismatches = 0;
        for (int i = 0; i < expected.Length; i++)
        {
            if (expected[i] != actual[i] && mismatches++ < 20) report.Append($"{i}: oracle {expected[i]}, v8sharp {actual[i]}\n");
        }
        Assert.True(mismatches == 0, report.ToString());
    }

    [Fact]
    public void TimezoneName()
    {
        // Without ICU, V8 prints the C library's short name (tm_zone).
        string s = S(Call("Date.prototype.toString", New("Date", Num(43200000))));
        Assert.Matches(@"^Thu Jan 01 1970 \d\d:\d\d:\d\d GMT[+-]\d{4} \(.+\)$", s);
        if (TimeZoneInfo.Local.BaseUtcOffset == TimeSpan.Zero && !TimeZoneInfo.Local.SupportsDaylightSavingTime &&
            TimeZoneInfo.Local.StandardName == "Coordinated Universal Time")
        {
            Assert.Equal("Thu Jan 01 1970 12:00:00 GMT+0000 (UTC)", s);
        }
    }
}
