// Port of src/numbers/conversions.cc: number to string conversions
// (DoubleToStringView, IntToStringView, DoubleToFixed/Exponential/Precision/
// RadixStringView) and IsSpecialIndex.

using System.Runtime.CompilerServices;
using V8Sharp.Base.Strings;

namespace V8Sharp.Base.Numbers;

public static partial class Conversions
{
    // SimpleStringBuilder over a caller-supplied span.
    ref struct SimpleStringBuilder(Span<char> buffer)
    {
        readonly Span<char> _buffer = buffer;
        int _position;

        public readonly int Position => _position;

        public void AddCharacter(char c) => _buffer[_position++] = c;

        public void AddSubstring(scoped ReadOnlySpan<char> s, int n)
        {
            s[..n].CopyTo(_buffer[_position..]);
            _position += n;
        }

        public void AddString(scoped ReadOnlySpan<char> s) => AddSubstring(s, s.Length);

        public void AddPadding(char c, int count)
        {
            if (count <= 0) return;
            _buffer.Slice(_position, count).Fill(c);
            _position += count;
        }

        /// <summary>Adds the decimal representation of a positive integer
        /// with at most 3 digits.</summary>
        public void AddExponent(int value)
        {
            Debug.Assert(value >= 0 && value <= 999);
            if (value >= 100)
            {
                AddCharacter((char)('0' + value / 100));
                AddCharacter((char)('0' + value / 10 % 10));
                AddCharacter((char)('0' + value % 10));
            }
            else if (value >= 10)
            {
                AddCharacter((char)('0' + value / 10));
                AddCharacter((char)('0' + value % 10));
            }
            else
            {
                AddCharacter((char)('0' + value));
            }
        }

        public readonly ReadOnlySpan<char> Finalize() => _buffer[.._position];
    }

    /// <summary>
    /// Converts a double to a string value according to ECMA-262 9.8.1
    /// (Number::toString). The buffer should be at least
    /// <see cref="kDoubleToStringMinBufferSize"/> characters. The result is a
    /// slice of the buffer.
    /// </summary>
    /// <remarks>
    /// V8 computes the shortest digits with dragonbox (to_decimal); V8Sharp
    /// with ShortestDecimal (Schubfach, which yields the same digits).
    /// </remarks>
    public static ReadOnlySpan<char> DoubleToStringView(double v, Span<char> buffer)
    {
        if (double.IsNaN(v)) return "NaN";
        if (double.IsInfinity(v)) return v < 0.0 ? "-Infinity" : "Infinity";
        if (v == 0) return "0";
        if (IsInt32Double(v))
        {
            // This will trigger if v is -0 and -0.0 is stringified to "0".
            // (see ES section 7.1.12.1
            // https://tc39.es/ecma262/#sec-tostring-applied-to-the-number-type)
            return IntToStringView(FastD2I(v), buffer);
        }

        SimpleStringBuilder builder = new(buffer);
        ShortestDecimal.ToDecimal(v, out ulong significand, out int decimalExponent);
        if (v < 0) builder.AddCharacter('-');

        Span<char> decimalRep = stackalloc char[DoubleConversion.kBase10MaximalLength];
        int length = SignificandToChars(significand, decimalRep);
        ReadOnlySpan<char> rep = decimalRep[..length];
        int decimalPoint = length + decimalExponent;

        if (length <= decimalPoint && decimalPoint <= 21)
        {
            // ECMA-262 section 9.8.1 step 6.
            builder.AddString(rep);
            builder.AddPadding('0', decimalPoint - length);
        }
        else if (0 < decimalPoint && decimalPoint <= 21)
        {
            // ECMA-262 section 9.8.1 step 7.
            builder.AddSubstring(rep, decimalPoint);
            builder.AddCharacter('.');
            builder.AddString(rep[decimalPoint..]);
        }
        else if (decimalPoint <= 0 && decimalPoint > -6)
        {
            // ECMA-262 section 9.8.1 step 8.
            builder.AddString("0.");
            builder.AddPadding('0', -decimalPoint);
            builder.AddString(rep);
        }
        else
        {
            // ECMA-262 section 9.8.1 step 9 and 10 combined.
            builder.AddCharacter(rep[0]);
            if (length != 1)
            {
                builder.AddCharacter('.');
                builder.AddString(rep[1..]);
            }
            builder.AddCharacter('e');
            builder.AddCharacter(decimalPoint >= 0 ? '+' : '-');
            int exponent = decimalPoint - 1;
            if (exponent < 0) exponent = -exponent;
            builder.AddExponent(exponent);
        }
        return builder.Finalize();
    }

    // SignificandToChars and its helpers are heavily inspired by
    // dragonbox::to_chars.

    // The digit pairs 00 .. 99.
    static ReadOnlySpan<byte> kRadix100Table =>
    [
        (byte)'0', (byte)'0', (byte)'0', (byte)'1', (byte)'0', (byte)'2', (byte)'0', (byte)'3', (byte)'0', (byte)'4',
        (byte)'0', (byte)'5', (byte)'0', (byte)'6', (byte)'0', (byte)'7', (byte)'0', (byte)'8', (byte)'0', (byte)'9',
        (byte)'1', (byte)'0', (byte)'1', (byte)'1', (byte)'1', (byte)'2', (byte)'1', (byte)'3', (byte)'1', (byte)'4',
        (byte)'1', (byte)'5', (byte)'1', (byte)'6', (byte)'1', (byte)'7', (byte)'1', (byte)'8', (byte)'1', (byte)'9',
        (byte)'2', (byte)'0', (byte)'2', (byte)'1', (byte)'2', (byte)'2', (byte)'2', (byte)'3', (byte)'2', (byte)'4',
        (byte)'2', (byte)'5', (byte)'2', (byte)'6', (byte)'2', (byte)'7', (byte)'2', (byte)'8', (byte)'2', (byte)'9',
        (byte)'3', (byte)'0', (byte)'3', (byte)'1', (byte)'3', (byte)'2', (byte)'3', (byte)'3', (byte)'3', (byte)'4',
        (byte)'3', (byte)'5', (byte)'3', (byte)'6', (byte)'3', (byte)'7', (byte)'3', (byte)'8', (byte)'3', (byte)'9',
        (byte)'4', (byte)'0', (byte)'4', (byte)'1', (byte)'4', (byte)'2', (byte)'4', (byte)'3', (byte)'4', (byte)'4',
        (byte)'4', (byte)'5', (byte)'4', (byte)'6', (byte)'4', (byte)'7', (byte)'4', (byte)'8', (byte)'4', (byte)'9',
        (byte)'5', (byte)'0', (byte)'5', (byte)'1', (byte)'5', (byte)'2', (byte)'5', (byte)'3', (byte)'5', (byte)'4',
        (byte)'5', (byte)'5', (byte)'5', (byte)'6', (byte)'5', (byte)'7', (byte)'5', (byte)'8', (byte)'5', (byte)'9',
        (byte)'6', (byte)'0', (byte)'6', (byte)'1', (byte)'6', (byte)'2', (byte)'6', (byte)'3', (byte)'6', (byte)'4',
        (byte)'6', (byte)'5', (byte)'6', (byte)'6', (byte)'6', (byte)'7', (byte)'6', (byte)'8', (byte)'6', (byte)'9',
        (byte)'7', (byte)'0', (byte)'7', (byte)'1', (byte)'7', (byte)'2', (byte)'7', (byte)'3', (byte)'7', (byte)'4',
        (byte)'7', (byte)'5', (byte)'7', (byte)'6', (byte)'7', (byte)'7', (byte)'7', (byte)'8', (byte)'7', (byte)'9',
        (byte)'8', (byte)'0', (byte)'8', (byte)'1', (byte)'8', (byte)'2', (byte)'8', (byte)'3', (byte)'8', (byte)'4',
        (byte)'8', (byte)'5', (byte)'8', (byte)'6', (byte)'8', (byte)'7', (byte)'8', (byte)'8', (byte)'8', (byte)'9',
        (byte)'9', (byte)'0', (byte)'9', (byte)'1', (byte)'9', (byte)'2', (byte)'9', (byte)'3', (byte)'9', (byte)'4',
        (byte)'9', (byte)'5', (byte)'9', (byte)'6', (byte)'9', (byte)'7', (byte)'9', (byte)'8', (byte)'9', (byte)'9',
    ];

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static void Convert2Digits(uint n, Span<char> buffer)
    {
        Debug.Assert(n < 100);
        ReadOnlySpan<byte> table = kRadix100Table;
        buffer[0] = (char)table[(int)(2 * n)];
        buffer[1] = (char)table[(int)(2 * n + 1)];
    }

    // Returns count of digits written. (V8 reads the leading pair from
    // kRadix100HeadTable, which is the same table without the leading zero.)
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static int ConvertHeadDigits(uint n, Span<char> buffer)
    {
        Debug.Assert(n < 100);
        if (n >= 10)
        {
            Convert2Digits(n, buffer);
            return 2;
        }
        buffer[0] = (char)('0' + n);
        return 1;
    }

    static void Convert8Digits(uint n, Span<char> buffer)
    {
        const ulong kUint32Mask = uint.MaxValue;
        // 281474978 = ceil(2^48 / 1'000'000) + 1
        ulong prod = n * 281474978UL;
        prod >>= 16;
        prod += 1;
        Convert2Digits((uint)(prod >> 32), buffer);
        prod = (prod & kUint32Mask) * 100;
        Convert2Digits((uint)(prod >> 32), buffer[2..]);
        prod = (prod & kUint32Mask) * 100;
        Convert2Digits((uint)(prod >> 32), buffer[4..]);
        prod = (prod & kUint32Mask) * 100;
        Convert2Digits((uint)(prod >> 32), buffer[6..]);
    }

    // Returns count of digits written.
    static int ConvertUpTo9Digits(uint n, Span<char> buffer)
    {
        const ulong kUint32Mask = uint.MaxValue;

        if (n >= 100_000_000)
        {
            // 9 digits.
            // 1441151882 = ceil(2^57 / 100'000'000) + 1
            ulong prod = n * 1441151882UL;
            prod >>= 25;

            uint headDigit = (uint)(prod >> 32);
            Debug.Assert(headDigit < 10);
            buffer[0] = (char)('0' + headDigit);

            // Print remaining 8 digits.
            prod = (prod & kUint32Mask) * 100;
            Convert2Digits((uint)(prod >> 32), buffer[1..]);
            prod = (prod & kUint32Mask) * 100;
            Convert2Digits((uint)(prod >> 32), buffer[3..]);
            prod = (prod & kUint32Mask) * 100;
            Convert2Digits((uint)(prod >> 32), buffer[5..]);
            prod = (prod & kUint32Mask) * 100;
            Convert2Digits((uint)(prod >> 32), buffer[7..]);

            return 9;
        }
        if (n >= 1_000_000)
        {
            // 7 or 8 digits.
            // 281474978 = ceil(2^48 / 1'000'000) + 1
            ulong prod = n * 281474978UL;
            prod >>= 16;

            int headDigitCount = ConvertHeadDigits((uint)(prod >> 32), buffer);
            buffer = buffer[headDigitCount..];

            // Print remaining 6 digits.
            prod = (prod & kUint32Mask) * 100;
            Convert2Digits((uint)(prod >> 32), buffer);
            prod = (prod & kUint32Mask) * 100;
            Convert2Digits((uint)(prod >> 32), buffer[2..]);
            prod = (prod & kUint32Mask) * 100;
            Convert2Digits((uint)(prod >> 32), buffer[4..]);

            return 6 + headDigitCount;
        }
        if (n >= 10_000)
        {
            // 5 or 6 digits.
            // 429497 = ceil(2^32 / 10'000)
            ulong prod = n * 429497UL;

            int headDigitCount = ConvertHeadDigits((uint)(prod >> 32), buffer);
            buffer = buffer[headDigitCount..];

            // Print remaining 4 digits.
            prod = (prod & kUint32Mask) * 100;
            Convert2Digits((uint)(prod >> 32), buffer);
            prod = (prod & kUint32Mask) * 100;
            Convert2Digits((uint)(prod >> 32), buffer[2..]);

            return 4 + headDigitCount;
        }
        if (n >= 100)
        {
            // 3 or 4 digits.
            // 42949673 = ceil(2^32 / 10'000)
            ulong prod = n * 42949673UL;

            int headDigitCount = ConvertHeadDigits((uint)(prod >> 32), buffer);
            buffer = buffer[headDigitCount..];

            // Print remaining 2 digits.
            prod = (prod & kUint32Mask) * 100;
            Convert2Digits((uint)(prod >> 32), buffer);

            return 2 + headDigitCount;
        }
        // 1 or 2 digits.
        return ConvertHeadDigits(n, buffer);
    }

    // Returns count of digits written.
    internal static int SignificandToChars(ulong n, Span<char> buffer)
    {
        Debug.Assert(n < 99999999999999999);  // Only supports up to 17 digits

        if (n >= 100_000_000)
        {
            // If we have at least 9 digits, split into 2 blocks. The second one always
            // has exactly 8 digits.
            uint firstBlock = (uint)(n / 100_000_000);
            uint secondBlock = (uint)(n % 100_000_000);

            int firstBlockDigits = ConvertUpTo9Digits(firstBlock, buffer);
            Convert8Digits(secondBlock, buffer[firstBlockDigits..]);
            return firstBlockDigits + 8;
        }
        return ConvertUpTo9Digits((uint)n, buffer);
    }

    /// <summary>Number::toString(10) as a string.</summary>
    public static string DoubleToCString(double v)
    {
        if (IsInt32Double(v)) return IntToCString((int)v);
        Span<char> buffer = stackalloc char[kDoubleToStringMinBufferSize];
        return new string(DoubleToStringView(v, buffer));
    }

    static readonly string[] s_smallInts = BuildSmallInts();

    static string[] BuildSmallInts()
    {
        string[] a = new string[256];
        for (int i = 0; i < a.Length; i++) a[i] = i.ToString(System.Globalization.CultureInfo.InvariantCulture);
        return a;
    }

    /// <summary>Converts an int to a string. The result is located at the
    /// end of the buffer (as in V8, not necessarily at its start).</summary>
    public static ReadOnlySpan<char> IntToStringView(int n, Span<char> buffer)
    {
        bool negative = true;
        if (n >= 0)
        {
            n = -n;
            negative = false;
        }
        // Build the string backwards from the least significant digit.
        int i = buffer.Length;
        do
        {
            // We ensured n <= 0, so the subtraction does the right addition.
            buffer[--i] = (char)('0' - (n % 10));
            n /= 10;
        } while (n != 0);
        if (negative) buffer[--i] = '-';
        return buffer[i..];
    }

    public static string IntToCString(int n)
    {
        if ((uint)n < (uint)s_smallInts.Length) return s_smallInts[n];
        Span<char> buffer = stackalloc char[12];
        return new string(IntToStringView(n, buffer));
    }

    /// <summary>Number.prototype.toFixed(f), 0 &lt;= f &lt;= kMaxFractionDigits.</summary>
    public static ReadOnlySpan<char> DoubleToFixedStringView(double value, int f, Span<char> buffer)
    {
        const double kFirstNonFixed = 1e21;
        Debug.Assert(f >= 0);
        Debug.Assert(f <= kMaxFractionDigits);

        bool negative = false;
        double absValue = value;
        if (value < 0)
        {
            absValue = -value;
            negative = true;
        }

        // If abs_value has more than kDoubleToFixedMaxDigitsBeforePoint digits before
        // the point use the non-fixed conversion routine.
        if (absValue >= kFirstNonFixed) return DoubleToStringView(value, buffer);

        // Find a sufficiently precise decimal representation of n.
        // Add space for the '\0' byte.
        const int kDecimalRepCapacity = kDoubleToFixedMaxDigitsBeforePoint + kMaxFractionDigits + 1;
        Span<char> decimalRep = stackalloc char[kDecimalRepCapacity];
        DoubleConversion.DoubleToAscii(value, DtoaMode.DTOA_FIXED, f, decimalRep, out _, out int decimalRepLength, out int decimalPoint);

        // Create a representation that is padded with zeros if needed.
        int zeroPrefixLength = 0;
        int zeroPostfixLength = 0;

        if (decimalPoint <= 0)
        {
            zeroPrefixLength = -decimalPoint + 1;
            decimalPoint = 1;
        }

        if (zeroPrefixLength + decimalRepLength < decimalPoint + f)
        {
            zeroPostfixLength = decimalPoint + f - decimalRepLength - zeroPrefixLength;
        }

        int repLength = zeroPrefixLength + decimalRepLength + zeroPostfixLength;
        Span<char> repBuffer = stackalloc char[repLength + 1];
        SimpleStringBuilder repBuilder = new(repBuffer);
        repBuilder.AddPadding('0', zeroPrefixLength);
        repBuilder.AddString(decimalRep[..decimalRepLength]);
        repBuilder.AddPadding('0', zeroPostfixLength);
        ReadOnlySpan<char> rep = repBuilder.Finalize();

        // Create the result string by appending a minus and putting in a
        // decimal point if needed.
        SimpleStringBuilder builder = new(buffer);
        if (negative) builder.AddCharacter('-');
        builder.AddSubstring(rep, decimalPoint);
        if (f > 0)
        {
            builder.AddCharacter('.');
            builder.AddSubstring(rep[decimalPoint..], f);
        }
        return builder.Finalize();
    }

    public static string DoubleToFixedCString(double value, int f)
    {
        Span<char> buffer = stackalloc char[kDoubleToFixedMaxChars + 1];
        return new string(DoubleToFixedStringView(value, f, buffer));
    }

    static ReadOnlySpan<char> CreateExponentialRepresentation(scoped ReadOnlySpan<char> decimalRep, int exponent, bool negative,
                                                             int significantDigits, Span<char> buffer)
    {
        bool negativeExponent = false;
        if (exponent < 0)
        {
            negativeExponent = true;
            exponent = -exponent;
        }

        SimpleStringBuilder builder = new(buffer);
        if (negative) builder.AddCharacter('-');
        builder.AddCharacter(decimalRep[0]);
        if (significantDigits != 1)
        {
            builder.AddCharacter('.');
            Debug.Assert(significantDigits >= decimalRep.Length);
            builder.AddString(decimalRep[1..]);
            builder.AddPadding('0', significantDigits - decimalRep.Length);
        }

        builder.AddCharacter('e');
        builder.AddCharacter(negativeExponent ? '-' : '+');
        builder.AddExponent(exponent);
        return builder.Finalize();
    }

    /// <summary>Number.prototype.toExponential(f); f == -1 means undefined
    /// (the shortest representation).</summary>
    public static ReadOnlySpan<char> DoubleToExponentialStringView(double value, int f, Span<char> buffer)
    {
        // f might be -1 to signal that f was undefined in JavaScript.
        Debug.Assert(f >= -1 && f <= kMaxFractionDigits);

        bool negative = false;
        if (value < 0)
        {
            value = -value;
            negative = true;
        }

        // f corresponds to the digits after the point. There is always one digit
        // before the point. The number of requested_digits equals hence f + 1.
        // And we have to add one character for the null-terminator.
        const int kV8DtoaBufferCapacity = kMaxFractionDigits + 1 + 1;
        Span<char> decimalRep = stackalloc char[kV8DtoaBufferCapacity];
        int decimalRepLength;
        int decimalPoint;

        if (f == -1)
        {
            DoubleConversion.DoubleToAscii(value, DtoaMode.DTOA_SHORTEST, 0, decimalRep, out _, out decimalRepLength, out decimalPoint);
            f = decimalRepLength - 1;
        }
        else
        {
            DoubleConversion.DoubleToAscii(value, DtoaMode.DTOA_PRECISION, f + 1, decimalRep, out _, out decimalRepLength, out decimalPoint);
        }
        Debug.Assert(decimalRepLength > 0);
        Debug.Assert(decimalRepLength <= f + 1);

        int exponent = decimalPoint - 1;
        return CreateExponentialRepresentation(decimalRep[..decimalRepLength], exponent, negative, f + 1, buffer);
    }

    public static string DoubleToExponentialCString(double value, int f)
    {
        Span<char> buffer = stackalloc char[kDoubleToExponentialMaxChars + 1];
        return new string(DoubleToExponentialStringView(value, f, buffer));
    }

    /// <summary>Number.prototype.toPrecision(p), 1 &lt;= p &lt;= kMaxFractionDigits.</summary>
    public static ReadOnlySpan<char> DoubleToPrecisionStringView(double value, int p, Span<char> buffer)
    {
        const int kMinimalDigits = 1;
        Debug.Assert(p >= kMinimalDigits && p <= kMaxFractionDigits);

        bool negative = false;
        if (value < 0)
        {
            value = -value;
            negative = true;
        }

        // Find a sufficiently precise decimal representation of n.
        // Add one for the terminating null character.
        const int kV8DtoaBufferCapacity = kMaxFractionDigits + 1;
        Span<char> decimalRep = stackalloc char[kV8DtoaBufferCapacity];
        DoubleConversion.DoubleToAscii(value, DtoaMode.DTOA_PRECISION, p, decimalRep, out _, out int decimalRepLength, out int decimalPoint);
        Debug.Assert(decimalRepLength <= p);

        int exponent = decimalPoint - 1;

        if (exponent < -6 || exponent >= p)
        {
            return CreateExponentialRepresentation(decimalRep[..decimalRepLength], exponent, negative, p, buffer);
        }

        // Use fixed notation.
        SimpleStringBuilder builder = new(buffer);
        if (negative) builder.AddCharacter('-');
        if (decimalPoint <= 0)
        {
            builder.AddString("0.");
            builder.AddPadding('0', -decimalPoint);
            builder.AddString(decimalRep[..decimalRepLength]);
            builder.AddPadding('0', p - decimalRepLength);
        }
        else
        {
            int m = Math.Min(decimalRepLength, decimalPoint);
            builder.AddSubstring(decimalRep, m);
            builder.AddPadding('0', decimalPoint - decimalRepLength);
            if (decimalPoint < p)
            {
                builder.AddCharacter('.');
                int extra = negative ? 2 : 1;
                if (decimalRepLength > decimalPoint)
                {
                    int len = decimalRepLength - decimalPoint;
                    int n = Math.Min(len, p - (builder.Position - extra));
                    builder.AddSubstring(decimalRep[decimalPoint..], n);
                }
                builder.AddPadding('0', extra + (p - builder.Position));
            }
        }
        return builder.Finalize();
    }

    public static string DoubleToPrecisionCString(double value, int p)
    {
        Span<char> buffer = stackalloc char[kDoubleToPrecisionMaxChars + 1];
        return new string(DoubleToPrecisionStringView(value, p, buffer));
    }

    const string kRadixChars = "0123456789abcdefghijklmnopqrstuvwxyz";

    /// <summary>Number.prototype.toString(radix) for finite non-zero values
    /// (callers handle zero). The buffer must hold kDoubleToRadixMaxChars.</summary>
    public static ReadOnlySpan<char> DoubleToRadixStringView(double value, int radix, Span<char> buffer)
    {
        // We don't expect to see zero here (callers should handle it).
        Debug.Assert(value != 0.0);

        // Certain invalid inputs will cause this function to corrupt memory (write
        // out-of-bounds of the given buffer), so defend against that with CHECKs.
        if (!(radix >= 2 && radix <= 36)) throw new ArgumentOutOfRangeException(nameof(radix));
        if (!double.IsFinite(value)) throw new ArgumentOutOfRangeException(nameof(value));

        int integerCursor = buffer.Length / 2;
        int fractionCursor = integerCursor;

        bool negative = value < 0;
        if (negative) value = -value;

        // Split the value into an integer part and a fractional part.
        double integer = Math.Floor(value);
        double fraction = value - integer;
        // We only compute fractional digits up to the input double's precision.
        double delta = 0.5 * (new Double(value).NextDouble() - value);
        // If the delta rounded down to zero, use the minimum (denormal) delta
        // value. (.NET never flushes denormals, so V8's
        // base::FPU::GetFlushDenormals() check is always false here.)
        if (delta <= 0) delta = new Double(0.0).NextDouble();
        if (fraction >= delta)
        {
            // Insert decimal point.
            buffer[fractionCursor++] = '.';
            do
            {
                // Shift up by one digit.
                fraction *= radix;
                delta *= radix;
                // Write digit.
                int digit = (int)fraction;
                buffer[fractionCursor++] = kRadixChars[digit];
                // Calculate remainder.
                fraction -= digit;
                // Round to even.
                if (fraction > 0.5 || (fraction == 0.5 && (digit & 1) != 0))
                {
                    if (fraction + delta > 1)
                    {
                        // We need to back trace already written digits in case of carry-over.
                        while (true)
                        {
                            fractionCursor--;
                            if (fractionCursor == buffer.Length / 2)
                            {
                                Debug.Assert(buffer[fractionCursor] == '.');
                                // Carry over to the integer part.
                                integer += 1;
                                break;
                            }
                            char c = buffer[fractionCursor];
                            // Reconstruct digit.
                            digit = c > '9' ? (c - 'a' + 10) : (c - '0');
                            if (digit + 1 < radix)
                            {
                                buffer[fractionCursor++] = kRadixChars[digit + 1];
                                break;
                            }
                        }
                        break;
                    }
                }
            } while (fraction >= delta);
        }

        // Compute integer digits. Fill unrepresented digits with zero.
        while (new Double(integer / radix).Exponent > 0)
        {
            integer /= radix;
            buffer[--integerCursor] = '0';
        }
        do
        {
            double remainder = integer % radix;  // Modulo (fmod)
            buffer[--integerCursor] = kRadixChars[(int)remainder];
            integer = (integer - remainder) / radix;
        } while (integer > 0);

        // Add sign and terminate string.
        if (negative) buffer[--integerCursor] = '-';
        Debug.Assert(fractionCursor > integerCursor);
        return buffer[integerCursor..fractionCursor];
    }

    /// <summary>Number.prototype.toString(radix) as a string, including the
    /// cases the builtin handles before calling V8's DoubleToRadixStringView
    /// (NaN, Infinity, zero, radix 10).</summary>
    public static string DoubleToRadixCString(double value, int radix)
    {
        if (radix == 10) return DoubleToCString(value);
        if (double.IsNaN(value)) return "NaN";
        if (double.IsInfinity(value)) return value < 0 ? "-Infinity" : "Infinity";
        if (value == 0) return "0";
        char[] buffer = System.Buffers.ArrayPool<char>.Shared.Rent(kDoubleToRadixMaxChars);
        try
        {
            return new string(DoubleToRadixStringView(value, radix, buffer.AsSpan(0, kDoubleToRadixMaxChars)));
        }
        finally
        {
            System.Buffers.ArrayPool<char>.Shared.Return(buffer);
        }
    }

    /// <summary>Returns DoubleToString(StringToDouble(string)) == string.</summary>
    public static bool IsSpecialIndex(ReadOnlySpan<char> buffer)
    {
        // Max length of canonical double: -X.XXXXXXXXXXXXXXXXX-eXXX
        const int kBufferSize = 24;
        int length = buffer.Length;
        if (length == 0 || length > kBufferSize) return false;
        // If the first char is not a digit or a '-' or we can't match 'NaN' or
        // '(-)Infinity', bailout immediately.
        int offset = 0;
        if (!CharPredicates.IsDecimalDigit(buffer[0]))
        {
            if (buffer[0] == '-')
            {
                if (length == 1) return false;  // Just '-' is bad.
                if (!CharPredicates.IsDecimalDigit(buffer[1]))
                {
                    if (buffer[1] == 'I' && length == 9)
                    {
                        // Allow matching of '-Infinity' below.
                    }
                    else
                    {
                        return false;
                    }
                }
                offset++;
            }
            else if (buffer[0] == 'I' && length == 8)
            {
                // Allow matching of 'Infinity' below.
            }
            else if (buffer[0] == 'N' && length == 3)
            {
                // Match NaN.
                return buffer[1] == 'a' && buffer[2] == 'N';
            }
            else
            {
                return false;
            }
        }
        // Expected fast path: key is an integer.
        const int kRepresentableIntegerLength = 15;  // (-)XXXXXXXXXXXXXXX
        if (length - offset <= kRepresentableIntegerLength)
        {
            int initialOffset = offset;
            bool matches = true;
            for (; offset < length; offset++) matches &= CharPredicates.IsDecimalDigit(buffer[offset]);
            if (matches)
            {
                // Match 0 and -0.
                if (buffer[initialOffset] == '0') return initialOffset == length - 1;
                return true;
            }
        }
        // Slow path: test DoubleToString(StringToDouble(string)) == string.
        double d = StringToDouble(buffer, ConversionFlag.NoConversionFlag);
        if (double.IsNaN(d)) return false;
        // Compute reverse string.
        Span<char> reverseBuffer = stackalloc char[kDoubleToStringMinBufferSize];
        ReadOnlySpan<char> reverseString = DoubleToStringView(d, reverseBuffer);
        return reverseString.SequenceEqual(buffer);
    }
}
