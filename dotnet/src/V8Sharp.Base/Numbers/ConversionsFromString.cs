// Port of src/numbers/conversions.cc: string to number conversions
// (InternalStringToDouble, InternalStringToIntDouble, StringToIntHelper,
// NumberParseIntHelper) over UTF-16 spans.

using System.Runtime.CompilerServices;
using V8Sharp.Base.Strings;

namespace V8Sharp.Base.Numbers;

public static partial class Conversions
{
    const ulong kQuietNaNMask = 0xfffUL << 51;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static double JunkStringValue() => BitConverter.UInt64BitsToDouble(kQuietNaNMask);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static double SignedZero(bool negative) => negative ? -0.0 : 0.0;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static bool IsDigit(int x, int radix) =>
        (x >= '0' && x <= '9' && x < '0' + radix) ||
        (radix > 10 && x >= 'a' && x < 'a' + radix - 10) ||
        (radix > 10 && x >= 'A' && x < 'A' + radix - 10);

    static bool SubStringEquals(ReadOnlySpan<char> str, ref int current, string substring)
    {
        Debug.Assert(str[current] == substring[0]);
        for (int i = 1; i < substring.Length; i++)
        {
            ++current;
            if (current == str.Length || str[current] != substring[i]) return false;
        }
        ++current;
        return true;
    }

    /// <summary>Returns true if a nonspace character has been found and false
    /// if the end was reached before finding a nonspace character.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static bool AdvanceToNonspace(ReadOnlySpan<char> str, ref int current)
    {
        while (current != str.Length)
        {
            if (!CharPredicates.IsWhiteSpaceOrLineTerminator(str[current])) return true;
            ++current;
        }
        return false;
    }

    /// <summary>Parsing integers with radix 2, 4, 8, 16, 32. Assumes current != end.</summary>
    static double InternalStringToIntDouble(int radixLog2, ReadOnlySpan<char> str, int start, bool negative, bool allowTrailingJunk)
    {
        int current = start;
        int end = str.Length;
        Debug.Assert(current != end);

        // Skip leading 0s.
        while (str[current] == '0')
        {
            ++current;
            if (current == end) return SignedZero(negative);
        }

        long number = 0;
        int exponent = 0;
        int radix = 1 << radixLog2;

        int lim0 = '0' + (radix < 10 ? radix : 10);
        int limA = 'a' + (radix - 10);
        int limUpperA = 'A' + (radix - 10);

        do
        {
            int digit;
            char c = str[current];
            if (c >= '0' && c < lim0)
            {
                digit = c - '0';
            }
            else if (c >= 'a' && c < limA)
            {
                digit = c - 'a' + 10;
            }
            else if (c >= 'A' && c < limUpperA)
            {
                digit = c - 'A' + 10;
            }
            else
            {
                // We've not found any digits, this must be junk.
                if (current == start) return JunkStringValue();
                if (allowTrailingJunk || !AdvanceToNonspace(str, ref current)) break;
                return JunkStringValue();
            }

            number = number * radix + digit;
            int overflow = (int)(number >> 53);
            if (overflow != 0)
            {
                // Overflow occurred. Need to determine which direction to round the
                // result.
                int overflowBitsCount = 1;
                while (overflow > 1)
                {
                    overflowBitsCount++;
                    overflow >>= 1;
                }

                int droppedBitsMask = (1 << overflowBitsCount) - 1;
                int droppedBits = (int)number & droppedBitsMask;
                number >>= overflowBitsCount;
                exponent = overflowBitsCount;

                bool zeroTail = true;
                while (true)
                {
                    ++current;
                    if (current == end || !IsDigit(str[current], radix)) break;
                    zeroTail = zeroTail && str[current] == '0';
                    exponent += radixLog2;
                }

                if (!allowTrailingJunk && AdvanceToNonspace(str, ref current)) return JunkStringValue();

                int middleValue = 1 << (overflowBitsCount - 1);
                if (droppedBits > middleValue)
                {
                    number++;  // Rounding up.
                }
                else if (droppedBits == middleValue)
                {
                    // Rounding to even to consistency with decimals: half-way case rounds
                    // up if significant part is odd and down otherwise.
                    if ((number & 1) != 0 || !zeroTail) number++;  // Rounding up.
                }

                // Rounding up may cause overflow.
                if ((number & (1L << 53)) != 0)
                {
                    exponent++;
                    number >>= 1;
                }
                break;
            }
            ++current;
        } while (current != end);

        Debug.Assert(number < (1L << 53));

        if (exponent == 0)
        {
            if (negative)
            {
                if (number == 0) return -0.0;
                number = -number;
            }
            return number;
        }

        Debug.Assert(number != 0);
        return Math.ScaleB(negative ? -number : number, exponent);
    }

    /// <summary>
    /// Converts a string into a double value according to ECMA-262 9.3.1
    /// (ToNumber applied to the String type, parseFloat with
    /// AllowTrailingJunk).
    /// </summary>
    public static double StringToDouble(ReadOnlySpan<char> str, ConversionFlag flags, double emptyStringValue = 0)
    {
        // To make sure that iterator dereferencing is valid the following
        // convention is used:
        // 1. Each '++current' statement is followed by check for equality to 'end'.
        // 2. If AdvanceToNonspace returned false then current == end.
        // 3. If 'current' becomes be equal to 'end' the function returns or goes to
        // 'parsing_done'.
        // 4. 'current' is not dereferenced after the 'parsing_done' label.
        // 5. Code before 'parsing_done' may rely on 'current != end'.
        int current = 0;
        int end = str.Length;
        if (!AdvanceToNonspace(str, ref current)) return emptyStringValue;

        bool allowTrailingJunk = (flags & ConversionFlag.AllowTrailingJunk) != 0;

        // The non-decimal prefix has to be the first thing after any whitespace,
        // so check for this first.
        if ((flags & (ConversionFlag.AllowNonDecimalPrefix | ConversionFlag.AllowImplicitOctal)) != 0 && str[current] == '0')
        {
            // Copy the current iterator, so that on a failure to find the prefix, we
            // rewind to the start.
            int prefixed = current + 1;
            if (prefixed == end) return 0;
            char p = str[prefixed];
            if ((flags & ConversionFlag.AllowHex) != 0 && (p == 'x' || p == 'X'))
            {
                ++prefixed;
                if (prefixed == end) return JunkStringValue();  // "0x".
                return InternalStringToIntDouble(4, str, prefixed, false, allowTrailingJunk);
            }
            if ((flags & ConversionFlag.AllowOctal) != 0 && (p == 'o' || p == 'O'))
            {
                ++prefixed;
                if (prefixed == end) return JunkStringValue();  // "0o".
                return InternalStringToIntDouble(3, str, prefixed, false, allowTrailingJunk);
            }
            if ((flags & ConversionFlag.AllowBinary) != 0 && (p == 'b' || p == 'B'))
            {
                ++prefixed;
                if (prefixed == end) return JunkStringValue();  // "0b".
                return InternalStringToIntDouble(1, str, prefixed, false, allowTrailingJunk);
            }
            if ((flags & ConversionFlag.AllowImplicitOctal) != 0)
            {
                // V8 before the fast_float switch: "0" followed only by octal
                // digits (up to trailing whitespace/junk) is a legacy octal
                // literal; an '8', '9', '.' or exponent makes it decimal.
                int i = prefixed;
                while (i < end && CharPredicates.IsOctalDigit(str[i])) i++;
                if (i == end || !(CharPredicates.IsDecimalDigit(str[i]) || str[i] == '.' || str[i] == 'e' || str[i] == 'E'))
                {
                    return InternalStringToIntDouble(3, str, prefixed, false, allowTrailingJunk);
                }
            }
        }

        // From here we are parsing a StrDecimalLiteral, as per
        // https://tc39.es/ecma262/#sec-tonumber-applied-to-the-string-type
        int parsedEnd = ParseDecimal(str, current, out double value);
        if (parsedEnd == end) return value;
        if (parsedEnd > current)
        {
            current = parsedEnd;
            if (!allowTrailingJunk && AdvanceToNonspace(str, ref current)) return JunkStringValue();
            return value;
        }

        // Failed to parse any number -- handle ±Infinity before giving up.
        Debug.Assert(current != end);
        const string kInfinityString = "Infinity";
        switch (str[current])
        {
            case '+':
                // Ignore leading plus sign.
                ++current;
                if (current == end) return JunkStringValue();
                if (str[current] != kInfinityString[0]) return JunkStringValue();
                goto case 'I';
            case 'I':
                if (!SubStringEquals(str, ref current, kInfinityString)) return JunkStringValue();
                if (!allowTrailingJunk && AdvanceToNonspace(str, ref current)) return JunkStringValue();
                return double.PositiveInfinity;
            case '-':
                ++current;
                if (current == end) return JunkStringValue();
                if (str[current] != kInfinityString[0]) return JunkStringValue();
                if (!SubStringEquals(str, ref current, kInfinityString)) return JunkStringValue();
                if (!allowTrailingJunk && AdvanceToNonspace(str, ref current)) return JunkStringValue();
                return double.NegativeInfinity;
            default:
                return JunkStringValue();
        }
    }

    public static double StringToDouble(string str, ConversionFlag flags, double emptyStringValue = 0) =>
        StringToDouble(str.AsSpan(), flags, emptyStringValue);

    /// <summary>
    /// The decimal parse V8 delegates to fast_float::from_chars (format
    /// general | no_infnan | allow_leading_plus): an optional sign, digits with
    /// an optional '.', at least one digit, and an optional exponent that is
    /// only consumed when it has digits. Returns the index after the number,
    /// or <paramref name="start"/> if nothing was parsed. The value is
    /// correctly rounded: FastFloat (Clinger, Eisel-Lemire) for up to 19
    /// significant digits, and for more when the truncated digits cannot
    /// change the result; otherwise base::Strtod over all the digits.
    /// </summary>
    static int ParseDecimal(ReadOnlySpan<char> str, int start, out double value)
    {
        value = 0;
        int end = str.Length;
        int p = start;
        bool negative = false;
        if (str[p] == '-' || str[p] == '+')
        {
            negative = str[p] == '-';
            ++p;
            if (p == end) return start;
        }
        char c = str[p];
        if ((uint)(c - '0') > 9 && c != '.') return start;

        // The first 19 significant digits, and how many there are in all.
        const int kMaxDigits = 19;
        ulong w = 0;
        int significant = 0;
        int fractionDigits = 0;
        int anyDigits = 0;
        while (p < end && (uint)(str[p] - '0') <= 9)
        {
            uint d = (uint)(str[p] - '0');
            if (significant != 0 || d != 0)
            {
                if (significant < kMaxDigits) w = w * 10 + d;
                significant++;
            }
            anyDigits++;
            p++;
        }
        if (p < end && str[p] == '.')
        {
            p++;
            while (p < end && (uint)(str[p] - '0') <= 9)
            {
                uint d = (uint)(str[p] - '0');
                if (significant != 0 || d != 0)
                {
                    if (significant < kMaxDigits) w = w * 10 + d;
                    significant++;
                }
                fractionDigits++;
                anyDigits++;
                p++;
            }
        }
        if (anyDigits == 0) return start;

        long exponent = 0;
        if (p < end && (str[p] == 'e' || str[p] == 'E'))
        {
            int location = p;
            p++;
            bool negExp = false;
            if (p < end && (str[p] == '-' || str[p] == '+'))
            {
                negExp = str[p] == '-';
                p++;
            }
            if (p >= end || (uint)(str[p] - '0') > 9)
            {
                p = location;
            }
            else
            {
                while (p < end && (uint)(str[p] - '0') <= 9)
                {
                    if (exponent < 0x10000000) exponent = exponent * 10 + (str[p] - '0');
                    p++;
                }
                if (negExp) exponent = -exponent;
            }
        }

        if (significant == 0)
        {
            value = negative ? -0.0 : 0.0;
            return p;
        }
        long q = exponent - fractionDigits;
        double result;
        if (significant <= kMaxDigits)
        {
            if (!FastFloat.TryCompute(q, w, out result)) result = SlowParseDecimal(str, start);
        }
        else
        {
            // The value lies in [w, w + 1) * 10^q': if both ends round alike,
            // that is the result.
            q += significant - kMaxDigits;
            if (!FastFloat.TryCompute(q, w, out result) ||
                !FastFloat.TryCompute(q, w + 1, out double upper) || upper != result)
            {
                result = SlowParseDecimal(str, start);
            }
        }
        value = negative ? -result : result;
        return p;
    }

    // The magnitude of the decimal literal at start (already validated by
    // ParseDecimal) through base::Strtod over all significant digits.
    internal static double SlowParseDecimal(ReadOnlySpan<char> str, int start)
    {
        SlowParseDecimal(str, start, out double value);
        return Math.Abs(value);
    }

    static int SlowParseDecimal(ReadOnlySpan<char> str, int start, out double value)
    {
        value = 0;
        int end = str.Length;
        int p = start;
        bool negative = false;
        if (str[p] == '-' || str[p] == '+')
        {
            negative = str[p] == '-';
            ++p;
            if (p == end) return start;
        }
        if (!CharPredicates.IsDecimalDigit(str[p]) && str[p] != '.') return start;

        // Significant digits (leading zeros dropped). V8's Strtod keeps at most
        // kMaxSignificantDecimalDigits, the last one standing in for a
        // non-zero tail; we do the same while scanning.
        const int kMax = DoubleConversion.kMaxSignificantDecimalDigits;
        Span<char> digits = stackalloc char[kMax];
        int count = 0;          // significant digits stored
        bool nonZeroTail = false;
        int droppedDigits = 0;  // significant digits beyond kMax - 1 (not stored)
        int fractionDigits = 0; // digits after '.' that were significant or leading zeros after the point
        int anyDigits = 0;

        while (p < end && CharPredicates.IsDecimalDigit(str[p]))
        {
            AddDigit(str[p], digits, ref count, ref droppedDigits, ref nonZeroTail);
            anyDigits++;
            p++;
        }
        if (p < end && str[p] == '.')
        {
            p++;
            while (p < end && CharPredicates.IsDecimalDigit(str[p]))
            {
                AddDigit(str[p], digits, ref count, ref droppedDigits, ref nonZeroTail);
                fractionDigits++;
                anyDigits++;
                p++;
            }
        }
        if (anyDigits == 0) return start;

        long exponent = 0;
        if (p < end && (str[p] == 'e' || str[p] == 'E'))
        {
            int location = p;
            p++;
            bool negExp = false;
            if (p < end && (str[p] == '-' || str[p] == '+'))
            {
                negExp = str[p] == '-';
                p++;
            }
            if (p >= end || !CharPredicates.IsDecimalDigit(str[p]))
            {
                p = location;
            }
            else
            {
                while (p < end && CharPredicates.IsDecimalDigit(str[p]))
                {
                    if (exponent < 0x10000000) exponent = exponent * 10 + (str[p] - '0');
                    p++;
                }
                if (negExp) exponent = -exponent;
            }
        }

        double result;
        if (count == 0)
        {
            result = 0;
        }
        else
        {
            long e = exponent - fractionDigits + droppedDigits;
            if (nonZeroTail)
            {
                // The '1' stands for the dropped non-zero tail, one digit below
                // the last stored one.
                digits[count++] = '1';
                e--;
            }
            if (e > 100000) e = 100000;
            if (e < -100000) e = -100000;
            result = DoubleConversion.Strtod(digits[..count], (int)e);
        }
        value = negative ? -result : result;
        return p;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static void AddDigit(char c, Span<char> digits, ref int count, ref int droppedDigits, ref bool nonZeroTail)
    {
        if (count == 0 && c == '0') return;  // Leading zeros are not significant.
        if (count < digits.Length - 1)
        {
            digits[count++] = c;
        }
        else
        {
            droppedDigits++;
            if (c != '0') nonZeroTail = true;
        }
    }

    /// <summary>Converts a binary string (of the form `0b[0-1]*`) into a double
    /// value according to https://tc39.es/ecma262/#sec-numericvalue</summary>
    public static double BinaryStringToDouble(ReadOnlySpan<char> str)
    {
        Debug.Assert(str[0] == '0' && (str[1] | 0x20) == 'b');
        return InternalStringToIntDouble(1, str, 2, false, false);
    }

    /// <summary>Converts an octal string (of the form `0o[0-7]*`) into a double.</summary>
    public static double OctalStringToDouble(ReadOnlySpan<char> str)
    {
        Debug.Assert(str[0] == '0' && (str[1] | 0x20) == 'o');
        return InternalStringToIntDouble(3, str, 2, false, false);
    }

    /// <summary>Converts a hex string (of the form `0x[0-9a-f]*`) into a double.</summary>
    public static double HexStringToDouble(ReadOnlySpan<char> str)
    {
        Debug.Assert(str[0] == '0' && (str[1] | 0x20) == 'x');
        return InternalStringToIntDouble(4, str, 2, false, false);
    }

    /// <summary>Converts an implicit octal string (LegacyOctalIntegerLiteral,
    /// of the form `0[0-7]*`) into a double.</summary>
    public static double ImplicitOctalStringToDouble(ReadOnlySpan<char> str) =>
        InternalStringToIntDouble(3, str, 0, false, false);

    /// <summary>String to double helper without heap allocation. Returns null
    /// if the string is longer than maxLengthForConversion.</summary>
    public static double? TryStringToDouble(ReadOnlySpan<char> str, int maxLengthForConversion = 23)
    {
        if (str.Length > maxLengthForConversion) return null;
        return StringToDouble(str, ConversionFlag.AllowNonDecimalPrefix);
    }

    /// <summary>Returns null if the string is longer than 20.</summary>
    public static double? TryStringToInt(ReadOnlySpan<char> str, int radix)
    {
        const int kMaxLengthForConversion = 20;
        if (str.Length > kMaxLengthForConversion) return null;
        return StringToInt(str, radix);
    }

    // ---- StringToIntHelper ------------------------------------------------------

    /// <summary>The internal state of V8's StringToIntHelper.</summary>
    public enum IntParseState { kRunning, kError, kJunk, kEmpty, kZero, kDone }

    public enum IntParseSign { kNegative, kPositive, kNone }

    /// <summary>
    /// ES6 18.2.5 parseInt(string, radix) and the BigInt parsing cases of
    /// https://tc39.es/proposal-bigint/: V8's StringToIntHelper. Detects the
    /// sign, radix prefix and leading zeros; the caller then parses digits
    /// from <see cref="Cursor"/>.
    /// </summary>
    public ref struct StringToIntHelper
    {
        readonly ReadOnlySpan<char> _subject;
        public int Radix;
        public int Cursor;
        public IntParseSign Sign;
        public IntParseState State;
        bool _leadingZero;
        public bool AllowBinaryAndOctalPrefixes;
        public bool AllowTrailingJunk;

        public StringToIntHelper(ReadOnlySpan<char> subject, int radix)
        {
            _subject = subject;
            Radix = radix;
            Sign = IntParseSign.kNone;
            State = IntParseState.kRunning;
            AllowTrailingJunk = true;
        }

        public readonly ReadOnlySpan<char> Subject => _subject;
        public readonly bool Negative => Sign == IntParseSign.kNegative;
        public readonly int Length => _subject.Length;

        /// <summary>DetectRadixInternal.</summary>
        public void DetectRadix()
        {
            ReadOnlySpan<char> s = _subject;
            int current = 0;
            int end = s.Length;

            if (!AdvanceToNonspace(s, ref current))
            {
                State = IntParseState.kEmpty;
                return;
            }

            if (s[current] == '+')
            {
                // Ignore leading sign; skip following spaces.
                ++current;
                if (current == end) { State = IntParseState.kJunk; return; }
                Sign = IntParseSign.kPositive;
            }
            else if (s[current] == '-')
            {
                ++current;
                if (current == end) { State = IntParseState.kJunk; return; }
                Sign = IntParseSign.kNegative;
            }

            if (Radix == 0)
            {
                // Radix detection.
                Radix = 10;
                if (s[current] == '0')
                {
                    ++current;
                    if (current == end) { State = IntParseState.kZero; return; }
                    char c = s[current];
                    if (c == 'x' || c == 'X')
                    {
                        Radix = 16;
                        ++current;
                        if (current == end) { State = IntParseState.kJunk; return; }
                    }
                    else if (AllowBinaryAndOctalPrefixes && (c == 'o' || c == 'O'))
                    {
                        Radix = 8;
                        ++current;
                        if (current == end) { State = IntParseState.kJunk; return; }
                    }
                    else if (AllowBinaryAndOctalPrefixes && (c == 'b' || c == 'B'))
                    {
                        Radix = 2;
                        ++current;
                        if (current == end) { State = IntParseState.kJunk; return; }
                    }
                    else
                    {
                        _leadingZero = true;
                    }
                }
            }
            else if (Radix == 16)
            {
                if (s[current] == '0')
                {
                    // Allow "0x" prefix.
                    ++current;
                    if (current == end) { State = IntParseState.kZero; return; }
                    if (s[current] == 'x' || s[current] == 'X')
                    {
                        ++current;
                        if (current == end) { State = IntParseState.kJunk; return; }
                    }
                    else
                    {
                        _leadingZero = true;
                    }
                }
            }
            // Skip leading zeros.
            while (s[current] == '0')
            {
                _leadingZero = true;
                ++current;
                if (current == end) { State = IntParseState.kZero; return; }
            }
            // Detect leading zeros with junk after them, if allowed.
            if (_leadingZero && AllowTrailingJunk && !IsDigit(s[current], Radix))
            {
                State = IntParseState.kZero;
                return;
            }

            if (!_leadingZero && !IsDigit(s[current], Radix))
            {
                State = IntParseState.kJunk;
                return;
            }

            Debug.Assert(Radix >= 2 && Radix <= 36);
            Cursor = current;
        }
    }

    /// <summary>ES6 18.2.5 parseInt(string, radix): V8's StringToInt /
    /// NumberParseIntHelper. radix 0 means "detect".</summary>
    public static double StringToInt(ReadOnlySpan<char> str, int radix)
    {
        StringToIntHelper helper = new(str, radix);
        helper.DetectRadix();
        double result = 0;
        if (helper.State == IntParseState.kRunning)
        {
            int current = helper.Cursor;
            int r = helper.Radix;
            if (r == 10)
            {
                result = HandleBaseTenCase(str, current);
                helper.State = IntParseState.kDone;
            }
            else if ((r & (r - 1)) == 0)
            {
                // HandlePowerOfTwoCase: GetResult() takes care of the sign bit.
                int radixLog2 = System.Numerics.BitOperations.Log2((uint)r);
                result = InternalStringToIntDouble(radixLog2, str, current, false, true);
                helper.State = IntParseState.kDone;
            }
            else
            {
                helper.State = HandleGenericCase(str, current, r, helper.AllowTrailingJunk, out result);
            }
        }
        switch (helper.State)
        {
            case IntParseState.kJunk:
            case IntParseState.kEmpty:
                return JunkStringValue();
            case IntParseState.kZero:
                return SignedZero(helper.Negative);
            case IntParseState.kDone:
                return helper.Negative ? -result : result;
            default:
                throw new UnreachableException();
        }
    }

    public static double StringToInt(string str, int radix) => StringToInt(str.AsSpan(), radix);

    static double HandleBaseTenCase(ReadOnlySpan<char> str, int current)
    {
        // Parsing with strtod.
        // Doubles are less than 1.8e308.
        const int kMaxSignificantDigits = 309;
        // The buffer may contain up to kMaxSignificantDigits + 1 digits and a zero
        // end.
        const int kBufferSize = kMaxSignificantDigits + 2;
        Span<char> buffer = stackalloc char[kBufferSize];
        int bufferPos = 0;
        int end = str.Length;
        while (str[current] >= '0' && str[current] <= '9')
        {
            if (bufferPos <= kMaxSignificantDigits)
            {
                // If the number has more than kMaxSignificantDigits it will be parsed
                // as infinity.
                buffer[bufferPos++] = str[current];
            }
            ++current;
            if (current == end) break;
        }
        return DoubleConversion.Strtod(buffer[..bufferPos], 0);
    }

    static IntParseState HandleGenericCase(ReadOnlySpan<char> str, int current, int radix, bool allowTrailingJunk, out double result)
    {
        // The following code causes accumulating rounding error for numbers greater
        // than ~2^56. It's explicitly allowed in the spec: "if R is not 2, 4, 8, 10,
        // 16, or 32, then mathInt may be an implementation-dependent approximation to
        // the mathematical integer value" (15.1.2.2).
        int end = str.Length;
        int lim0 = '0' + (radix < 10 ? radix : 10);
        int limA = 'a' + (radix - 10);
        int limUpperA = 'A' + (radix - 10);

        // NOTE: The code for computing the value may seem a bit complex at
        // first glance. It is structured to use 32-bit multiply-and-add
        // loops as long as possible to avoid losing precision.
        result = 0;
        bool done = false;
        do
        {
            // Parse the longest part of the string starting at {current}
            // possible while keeping the multiplier, and thus the part
            // itself, within 32 bits.
            uint part = 0, multiplier = 1;
            while (true)
            {
                uint d;
                char c = str[current];
                if (c >= '0' && c < lim0)
                {
                    d = (uint)(c - '0');
                }
                else if (c >= 'a' && c < limA)
                {
                    d = (uint)(c - 'a' + 10);
                }
                else if (c >= 'A' && c < limUpperA)
                {
                    d = (uint)(c - 'A' + 10);
                }
                else
                {
                    done = true;
                    break;
                }

                // Update the value of the part as long as the multiplier fits
                // in 32 bits. When we can't guarantee that the next iteration
                // will not overflow the multiplier, we stop parsing the part
                // by leaving the loop.
                const uint kMaximumMultiplier = uint.MaxValue / 36;
                uint m = multiplier * (uint)radix;
                if (m > kMaximumMultiplier) break;
                part = part * (uint)radix + d;
                multiplier = m;
                Debug.Assert(multiplier > part);

                ++current;
                if (current == end)
                {
                    done = true;
                    break;
                }
            }
            result = result * multiplier + part;
        } while (!done);

        if (!allowTrailingJunk && AdvanceToNonspace(str, ref current)) return IntParseState.kJunk;
        return IntParseState.kDone;
    }
}
