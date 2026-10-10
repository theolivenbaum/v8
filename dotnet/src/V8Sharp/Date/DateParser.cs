// Port of src/date/dateparser.h, dateparser-inl.h and dateparser.cc: the ES5
// date-time string parser with V8's legacy (Safari-compatible) fallback.
// V8 instantiates the parser for one-byte and two-byte strings; strings are
// UTF-16 here, which is the two-byte instantiation.
using System.Runtime.CompilerServices;
using CharPredicates = V8Sharp.Base.Strings.CharPredicates;

namespace V8Sharp.Date;

public static class DateParser
{
    public const int YEAR = 0;
    public const int MONTH = 1;
    public const int DAY = 2;
    public const int HOUR = 3;
    public const int MINUTE = 4;
    public const int SECOND = 5;
    public const int MILLISECOND = 6;
    public const int UTC_OFFSET = 7;
    public const int OUTPUT_SIZE = 8;

    // Range testing
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static bool Between(int x, int lo, int hi) => (uint)(x - lo) <= (uint)(hi - lo);

    // Indicates a missing value.
    const int kNone = int.MaxValue;

    // Maximal number of digits used to build the value of a numeral.
    // Remaining digits are ignored.
    const int kMaxSignificantDigits = 9;

    /// <summary>
    /// Parse the string as a date. If parsing succeeds, return true after
    /// filling out the output array as follows (all integers are Smis):
    /// [0]: year, [1]: month (0 = Jan, 1 = Feb, ...), [2]: day, [3]: hour,
    /// [4]: minute, [5]: second, [6]: millisecond, [7]: UTC offset in seconds,
    /// or NaN if no timezone specified. If parsing fails, return false
    /// (content of output array is not defined).
    /// </summary>
    public static bool Parse(Isolate? isolate, ReadOnlySpan<char> str, Span<double> @out) =>
        Parse(isolate, str, @out, out _);

    /// <summary>
    /// Parse, also reporting whether the legacy parser was used (V8 reports it
    /// through the kLegacyDateParser use counter).
    /// </summary>
    public static bool Parse(Isolate? isolate, ReadOnlySpan<char> str, Span<double> @out, out bool usedLegacyParser)
    {
        usedLegacyParser = false;
        var @in = new InputReader(str);
        var scanner = new DateStringTokenizer(ref @in);
        var tz = new TimeZoneComposer();
        var time = new TimeComposer();
        var day = new DayComposer();

        // Specification:
        // Accept ES5 ISO 8601 date-time-strings or legacy dates compatible
        // with Safari.
        // ES5 ISO 8601 dates:
        //   [('-'|'+')yy]yyyy[-MM[-DD]][THH:mm[:ss[.sss]][Z|(+|-)hh:mm]]
        //   where yyyy is in the range 0000..9999 and
        //         +/-yyyyyy is in the range -999999..+999999 -
        //           but -000000 is invalid (year zero must be positive),
        //         MM is in the range 01..12,
        //         DD is in the range 01..31,
        //         MM and DD defaults to 01 if missing,,
        //         HH is generally in the range 00..23, but can be 24 if mm, ss
        //           and sss are zero (or missing), representing midnight at the
        //           end of a day,
        //         mm and ss are in the range 00..59,
        //         sss is in the range 000..999,
        //         hh is in the range 00..23,
        //         mm, ss, and sss default to 00 if missing, and
        //         timezone defaults to Z if missing
        //           (following Safari, ISO actually demands local time).
        //  Extensions:
        //   We also allow sss to have more or less than three digits (but at
        //   least one).
        //   We allow hh:mm to be specified as hhmm.
        // Legacy dates:
        //  Any unrecognized word before the first number is ignored.
        //  Parenthesized text is ignored.
        //  An unsigned number followed by ':' is a time value, and is
        //  added to the TimeComposer. A number followed by '::' adds a second
        //  zero as well. A number followed by '.' is also a time and must be
        //  followed by milliseconds.
        //  Any other number is a date component and is added to DayComposer.
        //  A month name (or really: any word having the same first three letters
        //  as a month name) is recorded as a named month in the Day composer.
        //  A word recognizable as a time-zone is recorded as such, as is
        //  '(+|-)(hhmm|hh:)'.
        //  Legacy dates don't allow extra signs ('+' or '-') or umatched ')'
        //  after a number has been read (before the first number, any garbage
        //  is allowed).
        // Intersection of the two:
        //  A string that matches both formats (e.g. 1970-01-01) will be
        //  parsed as an ES5 date-time string - which means it will default
        //  to UTC time-zone. That's unavoidable if following the ES5
        //  specification.
        //  After a valid "T" has been read while scanning an ES5 datetime string,
        //  the input can no longer be a valid legacy date, since the "T" is a
        //  garbage string after a number has been read.

        // First try getting as far as possible with as ES5 Date Time String.
        DateToken nextUnhandledToken = ParseES5DateTime(ref scanner, ref @in, ref day, ref time, ref tz);
        if (nextUnhandledToken.IsInvalid) return false;
        bool hasReadNumber = !day.IsEmpty;
        // If there's anything left, continue with the legacy parser.
        bool legacyParser = false;
        for (DateToken token = nextUnhandledToken; !token.IsEndOfInput; token = scanner.Next(ref @in))
        {
            if (token.IsNumber)
            {
                legacyParser = true;
                hasReadNumber = true;
                int n = token.Number;
                if (scanner.SkipSymbol(ref @in, ':'))
                {
                    if (scanner.SkipSymbol(ref @in, ':'))
                    {
                        // n + "::"
                        if (!time.IsEmpty) return false;
                        time.Add(n);
                        time.Add(0);
                    }
                    else
                    {
                        // n + ":"
                        if (!time.Add(n)) return false;
                        if (scanner.Peek().IsSymbolChar('.')) scanner.Next(ref @in);
                    }
                }
                else if (scanner.SkipSymbol(ref @in, '.') && time.IsExpecting(n))
                {
                    time.Add(n);
                    if (!scanner.Peek().IsNumber) return false;
                    int ms = ReadMilliseconds(scanner.Next(ref @in));
                    if (ms < 0) return false;
                    time.AddFinal(ms);
                }
                else if (tz.IsExpecting(n))
                {
                    tz.SetAbsoluteMinute(n);
                }
                else if (time.IsExpecting(n))
                {
                    time.AddFinal(n);
                    // Require end, white space, "Z", "+" or "-" immediately after
                    // finalizing time.
                    DateToken peek = scanner.Peek();
                    if (!peek.IsEndOfInput && !peek.IsWhiteSpace && !peek.IsKeywordZ && !peek.IsAsciiSign)
                    {
                        return false;
                    }
                }
                else
                {
                    if (!day.Add(n)) return false;
                    scanner.SkipSymbol(ref @in, '-');
                }
            }
            else if (token.IsKeyword)
            {
                legacyParser = true;
                // Parse a "word" (sequence of chars. >= 'A').
                KeywordType type = token.KeywordType;
                int value = token.KeywordValue;
                if (type == KeywordType.AM_PM && !time.IsEmpty)
                {
                    time.SetHourOffset(value);
                }
                else if (type == KeywordType.MONTH_NAME)
                {
                    day.SetNamedMonth(value);
                    scanner.SkipSymbol(ref @in, '-');
                }
                else if (type == KeywordType.TIME_ZONE_NAME && hasReadNumber)
                {
                    tz.Set(value);
                }
                else
                {
                    // Garbage words are illegal if a number has been read.
                    if (hasReadNumber) return false;
                    // The first number has to be separated from garbage words by
                    // whitespace or other separators.
                    if (scanner.Peek().IsNumber) return false;
                }
            }
            else if (token.IsAsciiSign && (tz.IsUTC || !time.IsEmpty))
            {
                legacyParser = true;
                // Parse UTC offset (only after UTC or time).
                tz.SetSign(token.AsciiSign);
                // The following number may be empty.
                int n = 0;
                int length = 0;
                if (scanner.Peek().IsNumber)
                {
                    DateToken nextToken = scanner.Next(ref @in);
                    length = nextToken.Length;
                    n = nextToken.Number;
                }
                hasReadNumber = true;

                if (scanner.Peek().IsSymbolChar(':'))
                {
                    tz.SetAbsoluteHour(n);
                    // TODO(littledan): Use minutes as part of timezone?
                    tz.SetAbsoluteMinute(kNone);
                }
                else if (length == 2 || length == 1)
                {
                    // Handle time zones like GMT-8
                    tz.SetAbsoluteHour(n);
                    tz.SetAbsoluteMinute(0);
                }
                else if (length == 4 || length == 3)
                {
                    // Looks like the hhmm format
                    tz.SetAbsoluteHour(n / 100);
                    tz.SetAbsoluteMinute(n % 100);
                }
                else
                {
                    // No need to accept time zones like GMT-12345
                    return false;
                }
            }
            else if ((token.IsAsciiSign || token.IsSymbolChar(')')) && hasReadNumber)
            {
                // Extra sign or ')' is illegal if a number has been read.
                return false;
            }
            else
            {
                // Ignore other characters and whitespace.
            }
        }

        bool success = day.Write(@out) && time.Write(@out) && tz.Write(@out);

        if (legacyParser && success)
        {
            usedLegacyParser = true;
            isolate?.CountUsage("kLegacyDateParser");
        }

        return success;
    }

    /// <summary>InputReader provides basic string parsing and character classification.</summary>
    ref struct InputReader
    {
        int _index;
        readonly ReadOnlySpan<char> _buffer;
        int _ch;

        public InputReader(ReadOnlySpan<char> s)
        {
            _index = 0;
            _buffer = s;
            _ch = 0;
            Next();
        }

        public readonly int Position => _index;

        // Advance to the next character of the string.
        public void Next()
        {
            _ch = _index < _buffer.Length ? _buffer[_index] : 0;
            _index++;
        }

        // Read a string of digits as an unsigned number. Cap value at
        // kMaxSignificantDigits, but skip remaining digits if the numeral
        // is longer.
        public int ReadUnsignedNumeral()
        {
            int n = 0;
            int i = 0;
            // First, skip leading zeros
            while (_ch == '0') Next();
            // And then, do the conversion
            while (IsAsciiDigit)
            {
                if (i < kMaxSignificantDigits) n = n * 10 + _ch - '0';
                i++;
                Next();
            }
            return n;
        }

        // Read a word (sequence of chars. >= 'A'), fill the given buffer with a
        // lower-case prefix, and pad any remainder of the buffer with zeroes.
        // Return word length.
        public int ReadWord(scoped Span<int> prefix)
        {
            int len;
            for (len = 0; IsAsciiAlphaOrAbove && !IsWhiteSpaceChar; Next(), len++)
            {
                if (len < prefix.Length) prefix[len] = CharPredicates.AsciiAlphaToLower(_ch);
            }
            for (int i = len; i < prefix.Length; i++) prefix[i] = 0;
            return len;
        }

        // The skip methods return whether they actually skipped something.
        public bool Skip(int c)
        {
            if (_ch == c)
            {
                Next();
                return true;
            }
            return false;
        }

        public bool SkipWhiteSpace()
        {
            if (CharPredicates.IsWhiteSpaceOrLineTerminator(_ch))
            {
                Next();
                return true;
            }
            return false;
        }

        public bool SkipParentheses()
        {
            if (_ch != '(') return false;
            int balance = 0;
            do
            {
                if (_ch == ')') --balance;
                else if (_ch == '(') ++balance;
                Next();
            } while (balance > 0 && _ch != 0);
            return true;
        }

        // Character testing/classification. Non-ASCII digits are not supported.
        public readonly bool Is(int c) => _ch == c;
        public readonly bool IsEnd => _ch == 0;
        public readonly bool IsAsciiDigit => CharPredicates.IsDecimalDigit(_ch);
        public readonly bool IsAsciiAlphaOrAbove => _ch >= 'A';
        public readonly bool IsWhiteSpaceChar => CharPredicates.IsWhiteSpace(_ch);
        public readonly bool IsAsciiSign => _ch == '+' || _ch == '-';

        // Return 1 for '+' and -1 for '-'.
        public readonly int GetAsciiSignValue() => 44 - _ch;
    }

    enum KeywordType
    {
        INVALID,
        MONTH_NAME,
        TIME_ZONE_NAME,
        TIME_SEPARATOR,
        AM_PM,
    }

    readonly struct DateToken
    {
        const int kInvalidTokenTag = -6;
        const int kUnknownTokenTag = -5;
        const int kWhiteSpaceTag = -4;
        const int kNumberTag = -3;
        const int kSymbolTag = -2;
        const int kEndOfInputTag = -1;
        const int kKeywordTagStart = 0;

        readonly int _tag;
        readonly int _length;  // Number of characters.
        readonly int _value;

        DateToken(int tag, int length, int value)
        {
            _tag = tag;
            _length = length;
            _value = value;
        }

        public bool IsInvalid => _tag == kInvalidTokenTag;
        public bool IsUnknown => _tag == kUnknownTokenTag;
        public bool IsNumber => _tag == kNumberTag;
        public bool IsSymbol => _tag == kSymbolTag;
        public bool IsWhiteSpace => _tag == kWhiteSpaceTag;
        public bool IsEndOfInput => _tag == kEndOfInputTag;
        public bool IsKeyword => _tag >= kKeywordTagStart;

        public int Length => _length;

        public int Number
        {
            get
            {
                Debug.Assert(IsNumber);
                return _value;
            }
        }

        public KeywordType KeywordType
        {
            get
            {
                Debug.Assert(IsKeyword);
                return (KeywordType)_tag;
            }
        }

        public int KeywordValue
        {
            get
            {
                Debug.Assert(IsKeyword);
                return _value;
            }
        }

        public char Symbol
        {
            get
            {
                Debug.Assert(IsSymbol);
                return (char)_value;
            }
        }

        public bool IsSymbolChar(char symbol) => IsSymbol && Symbol == symbol;
        public bool IsKeywordType(KeywordType tag) => _tag == (int)tag;
        public bool IsFixedLengthNumber(int length) => IsNumber && _length == length;
        public bool IsAsciiSign => _tag == kSymbolTag && (_value == '-' || _value == '+');

        public int AsciiSign
        {
            get
            {
                Debug.Assert(IsAsciiSign);
                return 44 - _value;
            }
        }

        public bool IsKeywordZ => IsKeywordType(KeywordType.TIME_ZONE_NAME) && _length == 1 && _value == 0;

        // Factory functions.
        public static DateToken Keyword(KeywordType tag, int value, int length) => new((int)tag, length, value);
        public static DateToken MakeNumber(int value, int length) => new(kNumberTag, length, value);
        public static DateToken MakeSymbol(char symbol) => new(kSymbolTag, 1, symbol);
        public static DateToken EndOfInput() => new(kEndOfInputTag, 0, -1);
        public static DateToken WhiteSpace(int length) => new(kWhiteSpaceTag, length, -1);
        public static DateToken Unknown() => new(kUnknownTokenTag, 1, -1);
        public static DateToken Invalid() => new(kInvalidTokenTag, 0, -1);
    }

    /// <summary>The tokenizer; the InputReader is passed by reference (both are ref structs).</summary>
    struct DateStringTokenizer
    {
        DateToken _next;

        public DateStringTokenizer(ref InputReader @in) => _next = Scan(ref @in);

        public DateToken Next(ref InputReader @in)
        {
            DateToken result = _next;
            _next = Scan(ref @in);
            return result;
        }

        public readonly DateToken Peek() => _next;

        public bool SkipSymbol(ref InputReader @in, char symbol)
        {
            if (_next.IsSymbolChar(symbol))
            {
                _next = Scan(ref @in);
                return true;
            }
            return false;
        }

        static DateToken Scan(ref InputReader @in)
        {
            int prePos = @in.Position;
            if (@in.IsEnd) return DateToken.EndOfInput();
            if (@in.IsAsciiDigit)
            {
                int n = @in.ReadUnsignedNumeral();
                int length = @in.Position - prePos;
                return DateToken.MakeNumber(n, length);
            }
            if (@in.Skip(':')) return DateToken.MakeSymbol(':');
            if (@in.Skip('-')) return DateToken.MakeSymbol('-');
            if (@in.Skip('+')) return DateToken.MakeSymbol('+');
            if (@in.Skip('.')) return DateToken.MakeSymbol('.');
            if (@in.Skip(')')) return DateToken.MakeSymbol(')');
            if (@in.IsAsciiAlphaOrAbove && !@in.IsWhiteSpaceChar)
            {
                Span<int> buffer = stackalloc int[KeywordTable.kPrefixLength];
                int length = @in.ReadWord(buffer);
                int index = KeywordTable.Lookup(buffer, length);
                return DateToken.Keyword(KeywordTable.GetType(index), KeywordTable.GetValue(index), length);
            }
            if (@in.SkipWhiteSpace())
            {
                return DateToken.WhiteSpace(@in.Position - prePos);
            }
            if (@in.SkipParentheses())
            {
                return DateToken.Unknown();
            }
            @in.Next();
            return DateToken.Unknown();
        }
    }

    static int ReadMilliseconds(DateToken token)
    {
        // Read first three significant digits of the original numeral,
        // as inferred from the value and the number of digits.
        // I.e., use the number of digits to see if there were
        // leading zeros.
        int number = token.Number;
        int length = token.Length;
        if (length < 3)
        {
            // Less than three digits. Multiply to put most significant digit
            // in hundreds position.
            if (length == 1)
            {
                number *= 100;
            }
            else if (length == 2)
            {
                number *= 10;
            }
        }
        else if (length > 3)
        {
            if (length > kMaxSignificantDigits) length = kMaxSignificantDigits;
            // More than three digits. Divide by 10^(length - 3) to get three
            // most significant digits.
            int factor = 1;
            do
            {
                Debug.Assert(factor <= 100000000);  // factor won't overflow.
                factor *= 10;
                length--;
            } while (length > 3);
            number /= factor;
        }
        return number;
    }

    /// <summary>KeywordTable maps names of months, time zones, am/pm to numbers.</summary>
    static class KeywordTable
    {
        public const int kPrefixLength = 3;
        const int kTypeOffset = kPrefixLength;
        const int kValueOffset = kTypeOffset + 1;
        const int kEntrySize = kValueOffset + 1;

        static ReadOnlySpan<sbyte> Array =>
        [
            (sbyte)'j', (sbyte)'a', (sbyte)'n', (sbyte)KeywordType.MONTH_NAME, 1,
            (sbyte)'f', (sbyte)'e', (sbyte)'b', (sbyte)KeywordType.MONTH_NAME, 2,
            (sbyte)'m', (sbyte)'a', (sbyte)'r', (sbyte)KeywordType.MONTH_NAME, 3,
            (sbyte)'a', (sbyte)'p', (sbyte)'r', (sbyte)KeywordType.MONTH_NAME, 4,
            (sbyte)'m', (sbyte)'a', (sbyte)'y', (sbyte)KeywordType.MONTH_NAME, 5,
            (sbyte)'j', (sbyte)'u', (sbyte)'n', (sbyte)KeywordType.MONTH_NAME, 6,
            (sbyte)'j', (sbyte)'u', (sbyte)'l', (sbyte)KeywordType.MONTH_NAME, 7,
            (sbyte)'a', (sbyte)'u', (sbyte)'g', (sbyte)KeywordType.MONTH_NAME, 8,
            (sbyte)'s', (sbyte)'e', (sbyte)'p', (sbyte)KeywordType.MONTH_NAME, 9,
            (sbyte)'o', (sbyte)'c', (sbyte)'t', (sbyte)KeywordType.MONTH_NAME, 10,
            (sbyte)'n', (sbyte)'o', (sbyte)'v', (sbyte)KeywordType.MONTH_NAME, 11,
            (sbyte)'d', (sbyte)'e', (sbyte)'c', (sbyte)KeywordType.MONTH_NAME, 12,
            (sbyte)'a', (sbyte)'m', 0, (sbyte)KeywordType.AM_PM, 0,
            (sbyte)'p', (sbyte)'m', 0, (sbyte)KeywordType.AM_PM, 12,
            (sbyte)'u', (sbyte)'t', 0, (sbyte)KeywordType.TIME_ZONE_NAME, 0,
            (sbyte)'u', (sbyte)'t', (sbyte)'c', (sbyte)KeywordType.TIME_ZONE_NAME, 0,
            (sbyte)'z', 0, 0, (sbyte)KeywordType.TIME_ZONE_NAME, 0,
            (sbyte)'g', (sbyte)'m', (sbyte)'t', (sbyte)KeywordType.TIME_ZONE_NAME, 0,
            (sbyte)'c', (sbyte)'d', (sbyte)'t', (sbyte)KeywordType.TIME_ZONE_NAME, -5,
            (sbyte)'c', (sbyte)'s', (sbyte)'t', (sbyte)KeywordType.TIME_ZONE_NAME, -6,
            (sbyte)'e', (sbyte)'d', (sbyte)'t', (sbyte)KeywordType.TIME_ZONE_NAME, -4,
            (sbyte)'e', (sbyte)'s', (sbyte)'t', (sbyte)KeywordType.TIME_ZONE_NAME, -5,
            (sbyte)'m', (sbyte)'d', (sbyte)'t', (sbyte)KeywordType.TIME_ZONE_NAME, -6,
            (sbyte)'m', (sbyte)'s', (sbyte)'t', (sbyte)KeywordType.TIME_ZONE_NAME, -7,
            (sbyte)'p', (sbyte)'d', (sbyte)'t', (sbyte)KeywordType.TIME_ZONE_NAME, -7,
            (sbyte)'p', (sbyte)'s', (sbyte)'t', (sbyte)KeywordType.TIME_ZONE_NAME, -8,
            (sbyte)'t', 0, 0, (sbyte)KeywordType.TIME_SEPARATOR, 0,
            0, 0, 0, (sbyte)KeywordType.INVALID, 0,
        ];

        public static KeywordType GetType(int i) => (KeywordType)Array[i * kEntrySize + kTypeOffset];

        public static int GetValue(int i) => Array[i * kEntrySize + kValueOffset];

        // We could use perfect hashing here, but this is not a bottleneck.
        public static int Lookup(ReadOnlySpan<int> pre, int len)
        {
            ReadOnlySpan<sbyte> array = Array;
            int i;
            for (i = 0; array[i * kEntrySize + kTypeOffset] != (sbyte)KeywordType.INVALID; i++)
            {
                int j = 0;
                while (j < kPrefixLength && pre[j] == array[i * kEntrySize + j])
                {
                    j++;
                }
                // Check if we have a match and the length is legal.
                // Word longer than keyword is only allowed for month names.
                if (j == kPrefixLength &&
                    (len <= kPrefixLength || array[i * kEntrySize + kTypeOffset] == (sbyte)KeywordType.MONTH_NAME))
                {
                    return i;
                }
            }
            return i;
        }
    }

    struct TimeZoneComposer
    {
        int _sign;
        int _hour;
        int _minute;

        public TimeZoneComposer()
        {
            _sign = kNone;
            _hour = kNone;
            _minute = kNone;
        }

        public void Set(int offsetInHours)
        {
            _sign = offsetInHours < 0 ? -1 : 1;
            _hour = offsetInHours * _sign;
            _minute = 0;
        }

        public void SetSign(int sign) => _sign = sign < 0 ? -1 : 1;
        public void SetAbsoluteHour(int hour) => _hour = hour;
        public void SetAbsoluteMinute(int minute) => _minute = minute;

        public readonly bool IsExpecting(int n) => _hour != kNone && _minute == kNone && TimeComposer.IsMinute(n);

        public readonly bool IsUTC => _hour == 0 && _minute == 0;

        public readonly bool IsEmpty => _hour == kNone;

        public bool Write(Span<double> output)
        {
            if (_sign != kNone)
            {
                if (_hour == kNone) _hour = 0;
                if (_minute == kNone) _minute = 0;
                // Avoid signed integer overflow (undefined behavior) by doing unsigned
                // arithmetic.
                uint totalSecondsUnsigned = unchecked((uint)_hour * 3600U + (uint)_minute * 60U);
                if (totalSecondsUnsigned > JSValue.SmiMaxValue) return false;
                int totalSeconds = (int)totalSecondsUnsigned;
                if (_sign < 0) totalSeconds = -totalSeconds;
                output[UTC_OFFSET] = totalSeconds;
            }
            else
            {
                output[UTC_OFFSET] = double.NaN;
            }
            return true;
        }
    }

    struct TimeComposer
    {
        const int kSize = 4;
        int _c0, _c1, _c2, _c3;
        int _index;
        int _hourOffset;

        public TimeComposer()
        {
            _index = 0;
            _hourOffset = kNone;
        }

        public readonly bool IsEmpty => _index == 0;

        public readonly bool IsExpecting(int n) =>
            (_index == 1 && IsMinute(n)) || (_index == 2 && IsSecond(n)) || (_index == 3 && IsMillisecond(n));

        void Set(int i, int n)
        {
            switch (i)
            {
                case 0: _c0 = n; break;
                case 1: _c1 = n; break;
                case 2: _c2 = n; break;
                default: _c3 = n; break;
            }
        }

        public bool Add(int n)
        {
            if (_index < kSize)
            {
                Set(_index++, n);
                return true;
            }
            return false;
        }

        public bool AddFinal(int n)
        {
            if (!Add(n)) return false;
            while (_index < kSize) Set(_index++, 0);
            return true;
        }

        public void SetHourOffset(int n) => _hourOffset = n;

        public static bool IsMinute(int x) => Between(x, 0, 59);
        public static bool IsHour(int x) => Between(x, 0, 23);
        public static bool IsSecond(int x) => Between(x, 0, 59);
        static bool IsHour12(int x) => Between(x, 0, 12);
        static bool IsMillisecond(int x) => Between(x, 0, 999);

        public bool Write(Span<double> output)
        {
            // All time slots default to 0
            while (_index < kSize) Set(_index++, 0);

            int hour = _c0;
            int minute = _c1;
            int second = _c2;
            int millisecond = _c3;

            if (_hourOffset != kNone)
            {
                if (!IsHour12(hour)) return false;
                hour %= 12;
                hour += _hourOffset;
            }

            if (!IsHour(hour) || !IsMinute(minute) || !IsSecond(second) || !IsMillisecond(millisecond))
            {
                // A 24th hour is allowed if minutes, seconds, and milliseconds are 0
                if (hour != 24 || minute != 0 || second != 0 || millisecond != 0)
                {
                    return false;
                }
            }

            output[HOUR] = hour;
            output[MINUTE] = minute;
            output[SECOND] = second;
            output[MILLISECOND] = millisecond;
            return true;
        }
    }

    struct DayComposer
    {
        const int kSize = 3;
        int _c0, _c1, _c2;
        int _index;
        int _namedMonth;
        // If set, ensures that data is always parsed in year-month-date order.
        bool _isIsoDate;

        public DayComposer()
        {
            _index = 0;
            _namedMonth = kNone;
            _isIsoDate = false;
        }

        public readonly bool IsEmpty => _index == 0;

        readonly int Get(int i) => i switch { 0 => _c0, 1 => _c1, _ => _c2 };

        void Set(int i, int n)
        {
            switch (i)
            {
                case 0: _c0 = n; break;
                case 1: _c1 = n; break;
                default: _c2 = n; break;
            }
        }

        public bool Add(int n)
        {
            if (_index < kSize)
            {
                Set(_index, n);
                _index++;
                return true;
            }
            return false;
        }

        public void SetNamedMonth(int n) => _namedMonth = n;

        public void SetIsoDate() => _isIsoDate = true;

        public static bool IsMonth(int x) => Between(x, 1, 12);
        public static bool IsDay(int x) => Between(x, 1, 31);

        public bool Write(Span<double> output)
        {
            if (_index < 1) return false;
            // Day and month defaults to 1.
            while (_index < kSize) Set(_index++, 1);

            int year = 0;  // Default year is 0 (=> 2000) for KJS compatibility.
            int month = kNone;
            int day = kNone;

            if (_namedMonth == kNone)
            {
                if (_isIsoDate || (_index == 3 && !IsDay(Get(0))))
                {
                    // YMD
                    year = Get(0);
                    month = Get(1);
                    day = Get(2);
                }
                else
                {
                    // MD(Y)
                    month = Get(0);
                    day = Get(1);
                    if (_index == 3) year = Get(2);
                }
            }
            else
            {
                month = _namedMonth;
                if (_index == 1)
                {
                    // MD or DM
                    day = Get(0);
                }
                else if (!IsDay(Get(0)))
                {
                    // YMD, MYD, or YDM
                    year = Get(0);
                    day = Get(1);
                }
                else
                {
                    // DMY, MDY, or DYM
                    day = Get(0);
                    year = Get(1);
                }
            }

            if (!_isIsoDate)
            {
                if (Between(year, 0, 49))
                {
                    year += 2000;
                }
                else if (Between(year, 50, 99))
                {
                    year += 1900;
                }
            }

            if (!(year >= JSValue.SmiMinValue && year <= JSValue.SmiMaxValue) || !IsMonth(month) || !IsDay(day)) return false;

            output[YEAR] = year;
            output[MONTH] = month - 1;  // 0-based
            output[DAY] = day;
            return true;
        }
    }

    /// <summary>
    /// Tries to parse an ES5 Date Time String. Returns the next token
    /// to continue with in the legacy date string parser. If parsing is
    /// complete, returns DateToken::EndOfInput(). If terminally unsuccessful,
    /// returns DateToken::Invalid(). Otherwise parsing continues in the
    /// legacy parser.
    /// </summary>
    static DateToken ParseES5DateTime(ref DateStringTokenizer scanner, ref InputReader @in, ref DayComposer day,
        ref TimeComposer time, ref TimeZoneComposer tz)
    {
        Debug.Assert(day.IsEmpty);
        Debug.Assert(time.IsEmpty);
        Debug.Assert(tz.IsEmpty);

        // Parse mandatory date string: [('-'|'+')yy]yyyy[':'MM[':'DD]]
        if (scanner.Peek().IsAsciiSign)
        {
            // Keep the sign token, so we can pass it back to the legacy
            // parser if we don't use it.
            DateToken signToken = scanner.Next(ref @in);
            if (!scanner.Peek().IsFixedLengthNumber(6)) return signToken;
            int sign = signToken.AsciiSign;
            int year = scanner.Next(ref @in).Number;
            if (sign < 0 && year == 0) return signToken;
            day.Add(sign * year);
        }
        else if (scanner.Peek().IsFixedLengthNumber(4))
        {
            day.Add(scanner.Next(ref @in).Number);
        }
        else
        {
            return scanner.Next(ref @in);
        }
        if (scanner.SkipSymbol(ref @in, '-'))
        {
            if (!scanner.Peek().IsFixedLengthNumber(2) || !DayComposer.IsMonth(scanner.Peek().Number))
            {
                return scanner.Next(ref @in);
            }
            day.Add(scanner.Next(ref @in).Number);
            if (scanner.SkipSymbol(ref @in, '-'))
            {
                if (!scanner.Peek().IsFixedLengthNumber(2) || !DayComposer.IsDay(scanner.Peek().Number))
                {
                    return scanner.Next(ref @in);
                }
                day.Add(scanner.Next(ref @in).Number);
            }
        }
        // Check for optional time string: 'T'HH':'mm[':'ss['.'sss]]Z
        if (!scanner.Peek().IsKeywordType(KeywordType.TIME_SEPARATOR))
        {
            if (!scanner.Peek().IsEndOfInput) return scanner.Next(ref @in);
        }
        else
        {
            // ES5 Date Time String time part is present.
            scanner.Next(ref @in);
            if (!scanner.Peek().IsFixedLengthNumber(2) || !Between(scanner.Peek().Number, 0, 24))
            {
                return DateToken.Invalid();
            }
            // Allow 24:00[:00[.000]], but no other time starting with 24.
            bool hourIs24 = scanner.Peek().Number == 24;
            time.Add(scanner.Next(ref @in).Number);
            if (!scanner.SkipSymbol(ref @in, ':')) return DateToken.Invalid();
            if (!scanner.Peek().IsFixedLengthNumber(2) || !TimeComposer.IsMinute(scanner.Peek().Number) ||
                (hourIs24 && scanner.Peek().Number > 0))
            {
                return DateToken.Invalid();
            }
            time.Add(scanner.Next(ref @in).Number);
            if (scanner.SkipSymbol(ref @in, ':'))
            {
                if (!scanner.Peek().IsFixedLengthNumber(2) || !TimeComposer.IsSecond(scanner.Peek().Number) ||
                    (hourIs24 && scanner.Peek().Number > 0))
                {
                    return DateToken.Invalid();
                }
                time.Add(scanner.Next(ref @in).Number);
                if (scanner.SkipSymbol(ref @in, '.'))
                {
                    if (!scanner.Peek().IsNumber || (hourIs24 && scanner.Peek().Number > 0))
                    {
                        return DateToken.Invalid();
                    }
                    // Allow more or less than the mandated three digits.
                    time.Add(ReadMilliseconds(scanner.Next(ref @in)));
                }
            }
            // Check for optional timezone designation: 'Z' | ('+'|'-')hh':'mm
            if (scanner.Peek().IsKeywordZ)
            {
                scanner.Next(ref @in);
                tz.Set(0);
            }
            else if (scanner.Peek().IsSymbolChar('+') || scanner.Peek().IsSymbolChar('-'))
            {
                tz.SetSign(scanner.Next(ref @in).Symbol == '+' ? 1 : -1);
                if (scanner.Peek().IsFixedLengthNumber(4))
                {
                    // hhmm extension syntax.
                    int hourmin = scanner.Next(ref @in).Number;
                    int hour = hourmin / 100;
                    int min = hourmin % 100;
                    if (!TimeComposer.IsHour(hour) || !TimeComposer.IsMinute(min))
                    {
                        return DateToken.Invalid();
                    }
                    tz.SetAbsoluteHour(hour);
                    tz.SetAbsoluteMinute(min);
                }
                else
                {
                    // hh:mm standard syntax.
                    if (!scanner.Peek().IsFixedLengthNumber(2) || !TimeComposer.IsHour(scanner.Peek().Number))
                    {
                        return DateToken.Invalid();
                    }
                    tz.SetAbsoluteHour(scanner.Next(ref @in).Number);
                    if (!scanner.SkipSymbol(ref @in, ':')) return DateToken.Invalid();
                    if (!scanner.Peek().IsFixedLengthNumber(2) || !TimeComposer.IsMinute(scanner.Peek().Number))
                    {
                        return DateToken.Invalid();
                    }
                    tz.SetAbsoluteMinute(scanner.Next(ref @in).Number);
                }
            }
            if (!scanner.Peek().IsEndOfInput) return DateToken.Invalid();
        }
        // Successfully parsed ES5 Date Time String.
        // https://tc39.es/ecma262/#sec-date-time-string-format Date Time String
        // Format "When the time zone offset is absent, date-only forms are
        // interpreted as a UTC time and date-time forms are interpreted as a
        // local time."
        if (tz.IsEmpty && time.IsEmpty)
        {
            tz.Set(0);
        }
        day.SetIsoDate();
        return DateToken.EndOfInput();
    }
}
