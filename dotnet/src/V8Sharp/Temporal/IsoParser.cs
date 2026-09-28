// The ISO 8601 / RFC 9557 string grammar of the Temporal specification
// (section "Temporal ISO 8601 grammar") and its semantics: ParseISODateTime,
// ParseTemporalDurationString, ParseDateTimeUTCOffset, ParseTimeZoneIdentifier
// (temporal_rs uses the ixdtf crate for this).
using System.Numerics;

namespace V8Sharp.Temporal;

/// <summary>The goal symbols of ParseISODateTime.</summary>
[Flags]
public enum IsoGoal
{
    DateTime = 1,       // TemporalDateTimeString[~Zoned]
    ZonedDateTime = 2,  // TemporalDateTimeString[+Zoned]
    Instant = 4,        // TemporalInstantString
    Time = 8,           // TemporalTimeString
    YearMonth = 16,     // TemporalYearMonthString
    MonthDay = 32,      // TemporalMonthDayString
    Any = DateTime | ZonedDateTime | Instant | Time | YearMonth | MonthDay,
}

/// <summary>The result of ParseISODateTime.</summary>
public sealed class ParsedIso
{
    public long? Year;
    public int Month;
    public int Day;
    public bool HasDay = true;
    public bool HasTime;
    public IsoTime Time;
    public bool HasZ;
    public string? OffsetString;
    public string? TimeZoneAnnotation;
    public string? Calendar;
    public IsoGoal MatchedGoal;
}

/// <summary>A recursive-descent parser over the Temporal grammar.</summary>
public ref struct IsoParser(ReadOnlySpan<char> s)
{
    readonly ReadOnlySpan<char> _s = s;
    int _pos;

    public readonly bool AtEnd => _pos == _s.Length;

    readonly char Peek(int offset = 0) => _pos + offset < _s.Length ? _s[_pos + offset] : '\0';

    static bool IsDigit(char c) => c is >= '0' and <= '9';

    bool TryDigits(int count, out int value)
    {
        value = 0;
        if (_pos + count > _s.Length) return false;
        for (int i = 0; i < count; i++)
        {
            char c = _s[_pos + i];
            if (!IsDigit(c)) return false;
            value = value * 10 + (c - '0');
        }
        _pos += count;
        return true;
    }

    bool TryChar(char c)
    {
        if (Peek() != c || AtEnd) return false;
        _pos++;
        return true;
    }

    bool TryCharIgnoreCase(char upper)
    {
        char c = Peek();
        if (AtEnd || (c != upper && c != char.ToLowerInvariant(upper))) return false;
        _pos++;
        return true;
    }

    // ---- Date ------------------------------------------------------------------------------

    /// <summary>DateYear.</summary>
    bool TryYear(out long year)
    {
        int start = _pos;
        year = 0;
        char c = Peek();
        if (c is '+' or '-')
        {
            _pos++;
            if (!TryDigits(6, out int y6)) { _pos = start; return false; }
            if (c == '-' && y6 == 0) { _pos = start; return false; }  // -000000 is not allowed
            year = c == '-' ? -y6 : y6;
            return true;
        }
        if (!TryDigits(4, out int y4)) { _pos = start; return false; }
        year = y4;
        return true;
    }

    bool TryMonth(out int month)
    {
        int start = _pos;
        if (!TryDigits(2, out month)) return false;
        if (month < 1 || month > 12) { _pos = start; return false; }
        return true;
    }

    bool TryDay(out int day)
    {
        int start = _pos;
        if (!TryDigits(2, out day)) return false;
        if (day < 1 || day > 31) { _pos = start; return false; }
        return true;
    }

    /// <summary>Date: DateYear - DateMonth - DateDay | DateYear DateMonth DateDay.</summary>
    bool TryDate(out long year, out int month, out int day)
    {
        int start = _pos;
        month = day = 0;
        if (!TryYear(out year)) return false;
        if (TryChar('-'))
        {
            if (TryMonth(out month) && TryChar('-') && TryDay(out day)) return true;
        }
        else if (TryMonth(out month) && TryDay(out day))
        {
            return true;
        }
        _pos = start;
        return false;
    }

    // ---- Time ------------------------------------------------------------------------------

    bool TryHour(out int hour)
    {
        int start = _pos;
        if (!TryDigits(2, out hour)) return false;
        if (hour > 23) { _pos = start; return false; }
        return true;
    }

    bool TryMinuteSecond(out int value)
    {
        int start = _pos;
        if (!TryDigits(2, out value)) return false;
        if (value > 59) { _pos = start; return false; }
        return true;
    }

    /// <summary>TemporalDecimalFraction: returns the fraction scaled to nanoseconds.</summary>
    bool TryFraction(out long nanoseconds, out int digitCount)
    {
        nanoseconds = 0;
        digitCount = 0;
        char c = Peek();
        if (AtEnd || (c != '.' && c != ',')) return false;
        int start = _pos;
        _pos++;
        while (!AtEnd && IsDigit(Peek()))
        {
            if (digitCount == 9) { _pos = start; return false; }
            nanoseconds = nanoseconds * 10 + (Peek() - '0');
            digitCount++;
            _pos++;
        }
        if (digitCount == 0) { _pos = start; return false; }
        for (int i = digitCount; i < 9; i++) nanoseconds *= 10;
        return true;
    }

    /// <summary>Time[Extended]: returns the parsed time (second 60 becomes 59).</summary>
    bool TryTime(out IsoTime time)
    {
        time = default;
        int start = _pos;
        if (!TryHour(out int hour)) return false;
        int minute = 0, second = 0;
        long fraction = 0;
        if (TryChar(':'))
        {
            if (!TryMinuteSecond(out minute)) { _pos = start; return false; }
            int save = _pos;
            if (TryChar(':'))
            {
                if (!TryTimeSecond(out second)) { _pos = save; }
                else TryFraction(out fraction, out _);
            }
        }
        else if (TryMinuteSecond(out minute))
        {
            if (TryTimeSecond(out second)) TryFraction(out fraction, out _);
        }
        if (second == 60) second = 59;
        time = new IsoTime(hour, minute, second, (int)(fraction / 1_000_000), (int)(fraction / 1000 % 1000),
            (int)(fraction % 1000));
        return true;
    }

    bool TryTimeSecond(out int second)
    {
        int start = _pos;
        if (!TryDigits(2, out second)) return false;
        if (second > 60) { _pos = start; return false; }
        return true;
    }

    // ---- Offsets and time zones -------------------------------------------------------------

    /// <summary>UTCOffset[SubMinutePrecision]: returns the offset in nanoseconds.</summary>
    bool TryUtcOffset(bool subMinute, out long offsetNs, out bool hasSubMinute)
    {
        offsetNs = 0;
        hasSubMinute = false;
        int start = _pos;
        char sign = Peek();
        if (AtEnd || (sign != '+' && sign != '-')) return false;
        _pos++;
        if (!TryHour(out int hour)) { _pos = start; return false; }
        int minute = 0, second = 0;
        long fraction = 0;
        if (TryChar(':'))
        {
            if (!TryMinuteSecond(out minute)) { _pos = start; return false; }
            if (subMinute)
            {
                int save = _pos;
                if (TryChar(':'))
                {
                    if (!TryMinuteSecond(out second)) _pos = save;
                    else
                    {
                        hasSubMinute = true;
                        TryFraction(out fraction, out _);
                    }
                }
            }
        }
        else if (TryMinuteSecond(out minute))
        {
            if (subMinute && TryMinuteSecond(out second))
            {
                hasSubMinute = true;
                TryFraction(out fraction, out _);
            }
        }
        offsetNs = hour * Units.NsPerHour + minute * Units.NsPerMinute + second * Units.NsPerSecond + fraction;
        if (sign == '-') offsetNs = -offsetNs;
        return true;
    }

    /// <summary>UTCOffset[~SubMinutePrecision] as a time zone identifier; minutes.</summary>
    public bool TryParseUtcOffsetIdentifier(out int minutes)
    {
        minutes = 0;
        if (!TryUtcOffset(false, out long ns, out _)) return false;
        minutes = (int)(ns / Units.NsPerMinute);
        return true;
    }

    static bool IsTZLeadingChar(char c) => char.IsAsciiLetter(c) || c == '.' || c == '_';

    static bool IsTZChar(char c) => IsTZLeadingChar(c) || IsDigit(c) || c == '-' || c == '+';

    /// <summary>TimeZoneIANAName.</summary>
    public static bool IsTimeZoneIANAName(ReadOnlySpan<char> s)
    {
        if (s.Length == 0) return false;
        int componentStart = 0;
        for (int i = 0; i <= s.Length; i++)
        {
            if (i == s.Length || s[i] == '/')
            {
                ReadOnlySpan<char> component = s[componentStart..i];
                if (component.Length == 0 || !IsTZLeadingChar(component[0])) return false;
                foreach (char c in component)
                {
                    if (!IsTZChar(c)) return false;
                }
                if (component is "." or "..") return false;
                componentStart = i + 1;
            }
        }
        return true;
    }

    /// <summary>TimeZoneAnnotation: [ !opt TimeZoneIdentifier ].</summary>
    bool TryTimeZoneAnnotation(out string identifier)
    {
        identifier = "";
        int start = _pos;
        if (!TryChar('[')) return false;
        TryChar('!');
        int idStart = _pos;
        if (Peek() is '+' or '-')
        {
            if (!TryUtcOffset(false, out _, out _)) { _pos = start; return false; }
        }
        else
        {
            while (!AtEnd && (IsTZChar(Peek()) || Peek() == '/')) _pos++;
            if (!IsTimeZoneIANAName(_s[idStart.._pos])) { _pos = start; return false; }
        }
        int idEnd = _pos;
        if (!TryChar(']')) { _pos = start; return false; }
        identifier = _s[idStart..idEnd].ToString();
        return true;
    }

    /// <summary>Annotations: [ !opt key = value ]*; applies the calendar rules of ParseISODateTime.</summary>
    bool TryAnnotations(ref string? calendar)
    {
        bool calendarWasCritical = false;
        while (Peek() == '[' && !AtEnd)
        {
            int start = _pos;
            _pos++;
            bool critical = TryChar('!');
            int keyStart = _pos;
            char lead = Peek();
            if (AtEnd || !(lead is >= 'a' and <= 'z' || lead == '_')) { _pos = start; return false; }
            _pos++;
            while (!AtEnd && (Peek() is >= 'a' and <= 'z' or >= '0' and <= '9' or '-' or '_')) _pos++;
            ReadOnlySpan<char> key = _s[keyStart.._pos];
            if (!TryChar('=')) { _pos = start; return false; }
            int valueStart = _pos;
            // AnnotationValue: components of alphanumerics separated by '-'.
            while (true)
            {
                int compStart = _pos;
                while (!AtEnd && char.IsAsciiLetterOrDigit(Peek())) _pos++;
                if (_pos == compStart) { _pos = start; return false; }
                if (Peek() == '-' && !AtEnd) { _pos++; continue; }
                break;
            }
            ReadOnlySpan<char> value = _s[valueStart.._pos];
            if (!TryChar(']')) { _pos = start; return false; }
            if (key.SequenceEqual("u-ca"))
            {
                if (calendar is null)
                {
                    calendar = value.ToString();
                    calendarWasCritical = critical;
                }
                else if (critical || calendarWasCritical)
                {
                    throw TemporalError.Range("Multiple calendar annotations with a critical flag.");
                }
            }
            else if (critical)
            {
                throw TemporalError.Range("Unknown critical annotation.");
            }
        }
        return true;
    }

    /// <summary>TimeZoneAnnotation? Annotations? up to the end of the string.</summary>
    bool TryAnnotationsToEnd(ParsedIso result)
    {
        if (TryTimeZoneAnnotation(out string tz)) result.TimeZoneAnnotation = tz;
        string? calendar = null;
        if (!TryAnnotations(ref calendar)) return false;
        result.Calendar = calendar;
        return AtEnd;
    }

    // ---- The goal symbols --------------------------------------------------------------------

    /// <summary>
    /// AnnotatedDateTime[Zoned, TimeRequired] / TemporalInstantString. <paramref name="z"/>
    /// allows the UTC designator; <paramref name="offsetRequired"/> is the Instant form.
    /// </summary>
    bool TryAnnotatedDateTime(ParsedIso result, bool zoned, bool timeRequired, bool z, bool offsetRequired)
    {
        int start = _pos;
        if (!TryDate(out long year, out int month, out int day)) return false;
        result.Year = year;
        result.Month = month;
        result.Day = day;
        char sep = Peek();
        if (!AtEnd && (sep == 'T' || sep == 't' || sep == ' '))
        {
            int save = _pos;
            _pos++;
            if (TryTime(out IsoTime time))
            {
                result.HasTime = true;
                result.Time = time;
                // DateTimeUTCOffset[?Z]
                if (z && (Peek() is 'Z' or 'z') && !AtEnd)
                {
                    _pos++;
                    result.HasZ = true;
                }
                else
                {
                    int offStart = _pos;
                    if (TryUtcOffset(true, out _, out _)) result.OffsetString = _s[offStart.._pos].ToString();
                }
            }
            else
            {
                _pos = save;
            }
        }
        if (timeRequired && !result.HasTime) { _pos = start; return false; }
        if (offsetRequired && !result.HasZ && result.OffsetString is null) { _pos = start; return false; }
        if (!TryAnnotationsToEnd(result)) { _pos = start; return false; }
        if (zoned && result.TimeZoneAnnotation is null) { _pos = start; return false; }
        return true;
    }

    /// <summary>AnnotatedTime.</summary>
    bool TryAnnotatedTime(ParsedIso result)
    {
        int start = _pos;
        bool designator = TryCharIgnoreCase('T');
        int timeStart = _pos;
        if (!TryTime(out IsoTime time)) { _pos = start; return false; }
        int offStart = _pos;
        if (TryUtcOffset(true, out _, out _)) result.OffsetString = _s[offStart.._pos].ToString();
        int timeEnd = _pos;
        if (!TryAnnotationsToEnd(result)) { _pos = start; return false; }
        if (!designator)
        {
            // Early errors: the time must not also be a DateSpecMonthDay or DateSpecYearMonth.
            ReadOnlySpan<char> timeText = _s[timeStart..timeEnd];
            if (IsAmbiguousWithDate(timeText)) { _pos = start; return false; }
        }
        result.HasTime = true;
        result.Time = time;
        result.Year = null;
        return true;
    }

    static bool IsAmbiguousWithDate(ReadOnlySpan<char> text)
    {
        var p = new IsoParser(text);
        if (p.TryDateSpecYearMonth(out _, out _) && p.AtEnd) return true;
        p = new IsoParser(text);
        if (p.TryDateSpecMonthDay(out int m, out int d) && p.AtEnd && IsoCalendar.IsValidISODate(1972, m, d)) return true;
        return false;
    }

    /// <summary>DateSpecYearMonth.</summary>
    bool TryDateSpecYearMonth(out long year, out int month)
    {
        int start = _pos;
        month = 0;
        if (!TryYear(out year)) return false;
        TryChar('-');
        if (!TryMonth(out month)) { _pos = start; return false; }
        return true;
    }

    /// <summary>DateSpecMonthDay.</summary>
    bool TryDateSpecMonthDay(out int month, out int day)
    {
        int start = _pos;
        day = 0;
        if (Peek() == '-' && Peek(1) == '-') _pos += 2;
        if (!TryMonth(out month)) { _pos = start; return false; }
        TryChar('-');
        if (!TryDay(out day)) { _pos = start; return false; }
        return true;
    }

    bool TryAnnotatedYearMonth(ParsedIso result)
    {
        int start = _pos;
        if (!TryDateSpecYearMonth(out long year, out int month)) return false;
        if (!TryAnnotationsToEnd(result)) { _pos = start; return false; }
        result.Year = year;
        result.Month = month;
        result.Day = 1;
        result.HasDay = false;
        return true;
    }

    bool TryAnnotatedMonthDay(ParsedIso result)
    {
        int start = _pos;
        if (!TryDateSpecMonthDay(out int month, out int day)) return false;
        if (!TryAnnotationsToEnd(result)) { _pos = start; return false; }
        result.Year = null;
        result.Month = month;
        result.Day = day;
        return true;
    }

    bool TryGoal(IsoGoal goal, ParsedIso result)
    {
        _pos = 0;
        switch (goal)
        {
            case IsoGoal.DateTime:
                return TryAnnotatedDateTime(result, false, false, false, false);
            case IsoGoal.ZonedDateTime:
                return TryAnnotatedDateTime(result, true, false, true, false);
            case IsoGoal.Instant:
                return TryAnnotatedDateTime(result, false, true, true, true);
            case IsoGoal.Time:
                if (TryAnnotatedTime(result)) return true;
                _pos = 0;
                Reset(result);
                return TryAnnotatedDateTime(result, false, true, false, false);
            case IsoGoal.YearMonth:
                if (TryAnnotatedYearMonth(result)) return true;
                _pos = 0;
                Reset(result);
                return TryAnnotatedDateTime(result, false, false, false, false);
            case IsoGoal.MonthDay:
                if (TryAnnotatedMonthDay(result)) return true;
                _pos = 0;
                Reset(result);
                return TryAnnotatedDateTime(result, false, false, false, false);
        }
        return false;
    }

    static void Reset(ParsedIso r)
    {
        r.Year = null;
        r.Month = r.Day = 0;
        r.HasDay = true;
        r.HasTime = false;
        r.Time = default;
        r.HasZ = false;
        r.OffsetString = null;
        r.TimeZoneAnnotation = null;
        r.Calendar = null;
    }

    /// <summary>ParseISODateTime.</summary>
    public static ParsedIso Parse(ReadOnlySpan<char> s, IsoGoal goals)
    {
        var parser = new IsoParser(s);
        ReadOnlySpan<IsoGoal> order =
            [IsoGoal.ZonedDateTime, IsoGoal.DateTime, IsoGoal.Instant, IsoGoal.Time, IsoGoal.MonthDay, IsoGoal.YearMonth];
        foreach (IsoGoal goal in order)
        {
            if ((goals & goal) == 0) continue;
            var result = new ParsedIso();
            if (parser.TryGoal(goal, result) && parser.AtEnd)
            {
                result.MatchedGoal = goal;
                Validate(result, goal);
                return result;
            }
        }
        throw TemporalError.Range("Invalid ISO 8601 string: " + s.ToString());
    }

    public static ParsedIso ParseAny(ReadOnlySpan<char> s) => Parse(s, IsoGoal.Any);

    static void Validate(ParsedIso result, IsoGoal goal)
    {
        if (goal is IsoGoal.MonthDay && result.Year is null || goal is IsoGoal.YearMonth && !result.HasDay)
        {
            if (result.Calendar is { } cal && Calendar.TryCanonicalize(cal) != Calendar.Iso)
                throw TemporalError.Range("Calendar not allowed with this string form.");
        }
        if (result.Year is null && goal != IsoGoal.Time)
        {
            if (!IsoCalendar.IsValidISODate(1972, result.Month, result.Day)) throw TemporalError.Range("Invalid ISO date.");
        }
        else if (result.Year is long year)
        {
            if (!IsoCalendar.IsValidISODate(year, result.Month, result.Day)) throw TemporalError.Range("Invalid ISO date.");
        }
    }

    // ---- Offsets ------------------------------------------------------------------------------

    /// <summary>ParseDateTimeUTCOffset: the offset in nanoseconds; throws RangeError if not an offset.</summary>
    public static long ParseDateTimeUTCOffset(ReadOnlySpan<char> s)
    {
        var p = new IsoParser(s);
        if (!p.TryUtcOffset(true, out long ns, out _) || !p.AtEnd) throw TemporalError.Range("Invalid offset string.");
        return ns;
    }

    /// <summary>Whether an offset string has more than minutes (seconds or a fraction).</summary>
    public static bool OffsetHasSubMinutePrecision(ReadOnlySpan<char> s)
    {
        var p = new IsoParser(s);
        return p.TryUtcOffset(true, out _, out bool sub) && sub;
    }

    // ---- Durations ------------------------------------------------------------------------------

    /// <summary>ParseTemporalDurationString.</summary>
    public static Duration ParseDuration(ReadOnlySpan<char> s)
    {
        int pos = 0;
        int n = s.Length;
        bool negative = false;
        if (pos < n && (s[pos] == '+' || s[pos] == '-'))
        {
            negative = s[pos] == '-';
            pos++;
        }
        if (pos >= n || (s[pos] != 'P' && s[pos] != 'p')) throw Invalid(s);
        pos++;
        // Date part: Y M W D in order.
        double years = 0, months = 0, weeks = 0, days = 0, hours = 0;
        BigInteger fractionNs = BigInteger.Zero;  // the sub-unit remainder in nanoseconds
        long minutesWhole = 0, secondsWhole = 0;
        double minutes = 0, seconds = 0;
        bool any = false;
        int stage = 0;  // 1=Y 2=M 3=W 4=D
        while (pos < n && s[pos] != 'T' && s[pos] != 't')
        {
            int digitsStart = pos;
            while (pos < n && IsDigit(s[pos])) pos++;
            if (pos == digitsStart || pos >= n) throw Invalid(s);
            double value = ParseDigits(s[digitsStart..pos]);
            char d = char.ToUpperInvariant(s[pos]);
            int newStage = d switch { 'Y' => 1, 'M' => 2, 'W' => 3, 'D' => 4, _ => 0 };
            if (newStage == 0 || newStage <= stage) throw Invalid(s);
            stage = newStage;
            switch (d)
            {
                case 'Y': years = value; break;
                case 'M': months = value; break;
                case 'W': weeks = value; break;
                default: days = value; break;
            }
            pos++;
            any = true;
        }
        bool hasTime = false;
        if (pos < n)
        {
            pos++;  // T
            hasTime = true;
            int tstage = 0;  // 1=H 2=M 3=S
            bool fractionSeen = false;
            bool anyTime = false;
            while (pos < n)
            {
                if (fractionSeen) throw Invalid(s);
                int digitsStart = pos;
                while (pos < n && IsDigit(s[pos])) pos++;
                if (pos == digitsStart) throw Invalid(s);
                ReadOnlySpan<char> whole = s[digitsStart..pos];
                long fraction = 0;
                int fractionDigits = 0;
                if (pos < n && (s[pos] == '.' || s[pos] == ','))
                {
                    pos++;
                    int fs = pos;
                    while (pos < n && IsDigit(s[pos]))
                    {
                        if (pos - fs == 9) throw Invalid(s);
                        fraction = fraction * 10 + (s[pos] - '0');
                        pos++;
                    }
                    fractionDigits = pos - fs;
                    if (fractionDigits == 0) throw Invalid(s);
                    fractionSeen = true;
                }
                if (pos >= n) throw Invalid(s);
                char d = char.ToUpperInvariant(s[pos]);
                int newStage = d switch { 'H' => 1, 'M' => 2, 'S' => 3, _ => 0 };
                if (newStage == 0 || newStage <= tstage) throw Invalid(s);
                tstage = newStage;
                pos++;
                anyTime = true;
                long unitNs = d switch { 'H' => Units.NsPerHour, 'M' => Units.NsPerMinute, _ => Units.NsPerSecond };
                double value = ParseDigits(whole);
                switch (d)
                {
                    case 'H': hours = value; break;
                    case 'M': minutes = value; break;
                    default: seconds = value; break;
                }
                if (fractionDigits > 0)
                {
                    long scaled = fraction;
                    for (int i = fractionDigits; i < 9; i++) scaled *= 10;
                    // fraction × unit, in nanoseconds (exact: 10^9 divides every unit length).
                    fractionNs = (BigInteger)scaled * unitNs / 1_000_000_000;
                }
            }
            if (!anyTime) throw Invalid(s);
        }
        if (!any && !hasTime) throw Invalid(s);
        // The fraction of the smallest given unit, split into the lower units.
        long fracNs = (long)fractionNs;
        double factor = negative ? -1 : 1;
        long minutesPart = fracNs / Units.NsPerMinute;
        fracNs %= Units.NsPerMinute;
        long secondsPart = fracNs / Units.NsPerSecond;
        fracNs %= Units.NsPerSecond;
        long ms = fracNs / Units.NsPerMillisecond;
        long us = fracNs / 1000 % 1000;
        long ns = fracNs % 1000;
        _ = minutesWhole;
        _ = secondsWhole;
        return Duration.Create(years * factor, months * factor, weeks * factor, days * factor, hours * factor,
            (minutes + minutesPart) * factor, (seconds + secondsPart) * factor, ms * factor, us * factor, ns * factor);
    }

    static double ParseDigits(ReadOnlySpan<char> digits)
    {
        // ToIntegerOrInfinity(digits): StringToNumber of a decimal digit string.
        if (digits.Length <= 15)
        {
            long v = 0;
            foreach (char c in digits) v = v * 10 + (c - '0');
            return v;
        }
        return (double)BigInteger.Parse(digits, System.Globalization.NumberStyles.None,
            System.Globalization.CultureInfo.InvariantCulture);
    }

    static TemporalError Invalid(ReadOnlySpan<char> s) => TemporalError.Range("Invalid duration string: " + s.ToString());
}
