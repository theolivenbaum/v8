// TODO(merge): delete. Temporary stand-in for V8Sharp.Base.Numbers.Conversions
// (the port of src/numbers/conversions.{h,cc}), which another agent is porting
// into V8Sharp.Base with exactly these signatures. The implementations here are
// simple (exact BigInteger arithmetic and .NET's shortest round-trip
// formatting) but follow the ECMAScript algorithms, so results match V8.
using System.Globalization;
using System.Numerics;
using System.Text;

namespace V8Sharp.Base.Numbers;

/// <summary>V8's ConversionFlag (src/numbers/conversions.h).</summary>
public enum ConversionFlag
{
    NoConversionFlag,
    AllowNonDecimalPrefix,
    AllowTrailingJunk,
}

public static class Conversions
{
    // ---- Number to string -------------------------------------------------

    /// <summary>Number::toString(10), ECMA-262 Number::toString.</summary>
    public static string DoubleToCString(double v)
    {
        if (double.IsNaN(v)) return "NaN";
        if (v == 0) return "0";
        if (double.IsInfinity(v)) return v > 0 ? "Infinity" : "-Infinity";
        if (v >= int.MinValue && v <= int.MaxValue && v == Math.Floor(v)) return IntToCString((int)v);

        var sb = new StringBuilder(32);
        if (v < 0) { sb.Append('-'); v = -v; }
        ShortestDigits(v, out string digits, out int n);
        int k = digits.Length;
        if (k <= n && n <= 21)
        {
            sb.Append(digits);
            sb.Append('0', n - k);
        }
        else if (0 < n && n <= 21)
        {
            sb.Append(digits, 0, n);
            sb.Append('.');
            sb.Append(digits, n, k - n);
        }
        else if (-6 < n && n <= 0)
        {
            sb.Append("0.");
            sb.Append('0', -n);
            sb.Append(digits);
        }
        else
        {
            sb.Append(digits[0]);
            if (k > 1) { sb.Append('.'); sb.Append(digits, 1, k - 1); }
            sb.Append('e');
            int e = n - 1;
            sb.Append(e >= 0 ? '+' : '-');
            sb.Append(Math.Abs(e).ToString(CultureInfo.InvariantCulture));
        }
        return sb.ToString();
    }

    public static string IntToCString(int n) => n.ToString(CultureInfo.InvariantCulture);

    /// <summary>Number.prototype.toFixed; |value| &lt; 1e21 and 0 &lt;= f &lt;= 100.</summary>
    public static string DoubleToFixedCString(double value, int f)
    {
        Debug.Assert(Math.Abs(value) < 1e21);
        bool negative = value < 0;
        if (negative) value = -value;
        // n = round-half-up(value * 10^f), computed exactly.
        Decompose(value, out BigInteger mant, out int exp);
        BigInteger num = mant * BigInteger.Pow(10, f);
        BigInteger n = exp >= 0 ? num << exp : RoundHalfUp(num, BigInteger.One << -exp);
        string s = n.ToString(CultureInfo.InvariantCulture);
        if (f > 0)
        {
            if (s.Length <= f) s = new string('0', f + 1 - s.Length) + s;
            s = s[..^f] + "." + s[^f..];
        }
        return negative ? "-" + s : s;
    }

    /// <summary>Number.prototype.toExponential; f == -1 means "as many digits as necessary".</summary>
    public static string DoubleToExponentialCString(double value, int f)
    {
        bool negative = value < 0;
        if (negative) value = -value;
        string digits;
        int e;
        if (value == 0)
        {
            digits = new string('0', Math.Max(f, 0) + 1);
            e = 0;
        }
        else if (f == -1)
        {
            ShortestDigits(value, out digits, out int n);
            e = n - 1;
        }
        else
        {
            FixedPrecisionDigits(value, f + 1, out digits, out e);
        }
        var sb = new StringBuilder();
        if (negative) sb.Append('-');
        sb.Append(digits[0]);
        if (digits.Length > 1) { sb.Append('.'); sb.Append(digits, 1, digits.Length - 1); }
        sb.Append('e');
        sb.Append(e >= 0 ? '+' : '-');
        sb.Append(Math.Abs(e).ToString(CultureInfo.InvariantCulture));
        return sb.ToString();
    }

    /// <summary>Number.prototype.toPrecision, 1 &lt;= p &lt;= 100.</summary>
    public static string DoubleToPrecisionCString(double value, int p)
    {
        bool negative = value < 0;
        if (negative) value = -value;
        string digits;
        int e;
        if (value == 0)
        {
            digits = new string('0', p);
            e = 0;
        }
        else
        {
            FixedPrecisionDigits(value, p, out digits, out e);
        }
        var sb = new StringBuilder();
        if (negative) sb.Append('-');
        if (e < -6 || e >= p)
        {
            sb.Append(digits[0]);
            if (p > 1) { sb.Append('.'); sb.Append(digits, 1, p - 1); }
            sb.Append('e');
            sb.Append(e >= 0 ? '+' : '-');
            sb.Append(Math.Abs(e).ToString(CultureInfo.InvariantCulture));
        }
        else if (e == p - 1)
        {
            sb.Append(digits);
        }
        else if (e >= 0)
        {
            sb.Append(digits, 0, e + 1);
            sb.Append('.');
            sb.Append(digits, e + 1, p - (e + 1));
        }
        else
        {
            sb.Append("0.");
            sb.Append('0', -(e + 1));
            sb.Append(digits);
        }
        return sb.ToString();
    }

    /// <summary>Number.prototype.toString(radix) for finite non-zero values (V8's algorithm).</summary>
    public static string DoubleToRadixCString(double value, int radix)
    {
        const string chars = "0123456789abcdefghijklmnopqrstuvwxyz";
        const int kBufferSize = 2200;
        Span<char> buffer = new char[kBufferSize];
        int integerCursor = kBufferSize / 2;
        int fractionCursor = integerCursor;
        bool negative = value < 0;
        if (negative) value = -value;
        double integer = Math.Floor(value);
        double fraction = value - integer;
        double delta = 0.5 * (Math.BitIncrement(value) - value);
        if (delta <= 0) delta = double.Epsilon;
        if (fraction >= delta)
        {
            buffer[fractionCursor++] = '.';
            do
            {
                fraction *= radix;
                delta *= radix;
                int digit = (int)fraction;
                buffer[fractionCursor++] = chars[digit];
                fraction -= digit;
                if (fraction > 0.5 || (fraction == 0.5 && (digit & 1) != 0))
                {
                    if (fraction + delta > 1)
                    {
                        while (true)
                        {
                            fractionCursor--;
                            if (fractionCursor == kBufferSize / 2)
                            {
                                integer += 1;
                                break;
                            }
                            char c = buffer[fractionCursor];
                            digit = c > '9' ? (c - 'a' + 10) : (c - '0');
                            if (digit + 1 < radix)
                            {
                                buffer[fractionCursor++] = chars[digit + 1];
                                break;
                            }
                        }
                        break;
                    }
                }
            } while (fraction >= delta);
        }
        while (BinaryExponent(integer / radix) > 0)
        {
            integer /= radix;
            buffer[--integerCursor] = '0';
        }
        do
        {
            double remainder = integer % radix; // fmod, V8's Modulo
            buffer[--integerCursor] = chars[(int)remainder];
            integer = (integer - remainder) / radix;
        } while (integer > 0);
        if (negative) buffer[--integerCursor] = '-';
        return new string(buffer[integerCursor..fractionCursor]);
    }

    // ---- String to number -------------------------------------------------

    /// <summary>
    /// ECMA-262 StringToNumber (with <see cref="ConversionFlag.AllowNonDecimalPrefix"/>)
    /// and parseFloat (with <see cref="ConversionFlag.AllowTrailingJunk"/>).
    /// </summary>
    public static double StringToDouble(ReadOnlySpan<char> str, ConversionFlag flag, double emptyStringValue = 0)
    {
        int i = 0, end = str.Length;
        while (i < end && IsWhiteSpaceOrLineTerminator(str[i])) i++;
        if (flag != ConversionFlag.AllowTrailingJunk)
            while (end > i && IsWhiteSpaceOrLineTerminator(str[end - 1])) end--;
        if (i == end) return emptyStringValue;
        ReadOnlySpan<char> s = str[i..end];
        bool allowJunk = flag == ConversionFlag.AllowTrailingJunk;

        if (flag == ConversionFlag.AllowNonDecimalPrefix && s.Length > 2 && s[0] == '0')
        {
            int radix = s[1] switch { 'x' or 'X' => 16, 'o' or 'O' => 8, 'b' or 'B' => 2, _ => 0 };
            if (radix != 0) return RadixDigitsToDouble(s[2..], radix);
        }

        int p = 0;
        bool negative = false;
        if (s[p] == '+' || s[p] == '-')
        {
            negative = s[p] == '-';
            p++;
        }
        const string kInfinity = "Infinity";
        if (s[p..].StartsWith(kInfinity, StringComparison.Ordinal))
        {
            if (!allowJunk && p + kInfinity.Length != s.Length) return double.NaN;
            return negative ? double.NegativeInfinity : double.PositiveInfinity;
        }
        int start = p;
        int intDigits = 0, fracDigits = 0;
        while (p < s.Length && IsDecimalDigit(s[p])) { p++; intDigits++; }
        if (p < s.Length && s[p] == '.')
        {
            p++;
            while (p < s.Length && IsDecimalDigit(s[p])) { p++; fracDigits++; }
        }
        if (intDigits == 0 && fracDigits == 0) return double.NaN;
        int mantissaEnd = p;
        if (p < s.Length && (s[p] == 'e' || s[p] == 'E'))
        {
            int q = p + 1;
            if (q < s.Length && (s[q] == '+' || s[q] == '-')) q++;
            int expDigits = 0;
            while (q < s.Length && IsDecimalDigit(s[q])) { q++; expDigits++; }
            if (expDigits > 0) p = q;
            else if (!allowJunk) return double.NaN;
        }
        if (p != s.Length && !allowJunk) return double.NaN;
        double result = double.Parse(s[start..p], NumberStyles.AllowDecimalPoint | NumberStyles.AllowExponent, CultureInfo.InvariantCulture);
        return negative ? -result : result;
    }

    /// <summary>parseInt semantics over a flat string (V8's StringToInt).</summary>
    public static double StringToInt(ReadOnlySpan<char> str, int radix)
    {
        int i = 0;
        while (i < str.Length && IsWhiteSpaceOrLineTerminator(str[i])) i++;
        bool negative = false;
        if (i < str.Length && (str[i] == '+' || str[i] == '-'))
        {
            negative = str[i] == '-';
            i++;
        }
        bool stripPrefix = true;
        if (radix != 0)
        {
            if (radix < 2 || radix > 36) return double.NaN;
            if (radix != 16) stripPrefix = false;
        }
        else
        {
            radix = 10;
        }
        if (stripPrefix && i + 1 < str.Length && str[i] == '0' && (str[i + 1] == 'x' || str[i + 1] == 'X'))
        {
            i += 2;
            radix = 16;
        }
        int start = i;
        while (i < str.Length && DigitValue(str[i]) < radix) i++;
        if (i == start) return double.NaN;
        double r;
        if (radix == 10)
        {
            // Decimal digits: round correctly, as V8 does via strtod.
            r = double.Parse(str[start..i], NumberStyles.None, CultureInfo.InvariantCulture);
        }
        else
        {
            r = RadixDigitsToDouble(str[start..i], radix);
        }
        return negative ? -r : r;
    }

    // ---- Number to integer ------------------------------------------------

    /// <summary>ECMA-262 ToInt32 on a double.</summary>
    public static int DoubleToInt32(double x)
    {
        if (x >= int.MinValue && x <= int.MaxValue)
        {
            // Truncation matches for in-range values (NaN fails the comparisons).
            return (int)x;
        }
        if (double.IsNaN(x) || double.IsInfinity(x)) return 0;
        double t = Math.Truncate(x);
        double m = t % 4294967296.0;
        if (m < 0) m += 4294967296.0;
        return unchecked((int)(uint)m);
    }

    /// <summary>ECMA-262 ToUint32 on a double.</summary>
    public static uint DoubleToUint32(double x) => unchecked((uint)DoubleToInt32(x));

    // ---- Helpers ----------------------------------------------------------

    static bool IsDecimalDigit(char c) => (uint)(c - '0') <= 9;

    static int DigitValue(char c)
    {
        if ((uint)(c - '0') <= 9) return c - '0';
        if ((uint)((c | 0x20) - 'a') <= 25) return (c | 0x20) - 'a' + 10;
        return 99;
    }

    /// <summary>ECMA-262 WhiteSpace and LineTerminator code points.</summary>
    public static bool IsWhiteSpaceOrLineTerminator(char c) => c switch
    {
        '\t' or '\n' or '\v' or '\f' or '\r' or ' ' or '\u00A0' or '\u1680' or '\u2028' or '\u2029' or '\u202F' or '\u205F' or '\u3000' or '\uFEFF' => true,
        >= '\u2000' and <= '\u200A' => true,
        _ => false,
    };

    static double RadixDigitsToDouble(ReadOnlySpan<char> s, int radix)
    {
        if (s.IsEmpty) return double.NaN;
        BigInteger n = BigInteger.Zero;
        foreach (char c in s)
        {
            int d = DigitValue(c);
            if (d >= radix) return double.NaN;
            n = n * radix + d;
        }
        return BigIntegerToDouble(n);
    }

    /// <summary>Correctly rounded (half to even) conversion.</summary>
    static double BigIntegerToDouble(BigInteger n)
    {
        if (n.IsZero) return 0;
        long bits = (long)n.GetBitLength();
        if (bits <= 53) return (double)(ulong)n;
        if (bits > 1024) return double.PositiveInfinity;
        int shift = (int)(bits - 54);
        BigInteger top = n >> shift; // 54 bits: 53 + round bit
        bool sticky = !(n & ((BigInteger.One << shift) - 1)).IsZero;
        ulong t = (ulong)top;
        ulong mant = t >> 1;
        bool round = (t & 1) != 0;
        if (round && (sticky || (mant & 1) != 0)) mant++;
        return Math.ScaleB(mant, shift + 1);
    }

    static int BinaryExponent(double d)
    {
        long bits = BitConverter.DoubleToInt64Bits(d);
        int biased = (int)((bits >> 52) & 0x7FF);
        if (biased == 0) return -1074;
        return biased - 0x3FF - 52;
    }

    static void Decompose(double value, out BigInteger mantissa, out int exponent)
    {
        long bits = BitConverter.DoubleToInt64Bits(value);
        int biased = (int)((bits >> 52) & 0x7FF);
        long frac = bits & 0xFFFFFFFFFFFFFL;
        if (biased == 0)
        {
            mantissa = frac;
            exponent = -1074;
        }
        else
        {
            mantissa = frac | (1L << 52);
            exponent = biased - 1075;
        }
    }

    static BigInteger RoundHalfUp(BigInteger num, BigInteger den)
    {
        BigInteger q = BigInteger.DivRem(num, den, out BigInteger r);
        if (r * 2 >= den) q += 1;
        return q;
    }

    /// <summary>Shortest round-trip digits: value = 0.digits * 10^n.</summary>
    static void ShortestDigits(double v, out string digits, out int n)
    {
        // .NET Core 3.0+ "R" yields the shortest round-trippable representation.
        string r = v.ToString("R", CultureInfo.InvariantCulture);
        int ePos = r.IndexOfAny(['E', 'e']);
        string mant = ePos >= 0 ? r[..ePos] : r;
        int exp10 = ePos >= 0 ? int.Parse(r.AsSpan(ePos + 1), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture) : 0;
        int dot = mant.IndexOf('.');
        string intPart = dot >= 0 ? mant[..dot] : mant;
        string fracPart = dot >= 0 ? mant[(dot + 1)..] : "";
        string all = intPart + fracPart;
        int pointPos = intPart.Length + exp10;
        int lead = 0;
        while (lead < all.Length - 1 && all[lead] == '0') { lead++; pointPos--; }
        all = all[lead..].TrimEnd('0');
        if (all.Length == 0) all = "0";
        digits = all;
        n = pointPos;
    }

    /// <summary>
    /// Exactly p significant digits of value (value &gt; 0), rounding half up on
    /// the exact binary value; e is the decimal exponent of the first digit.
    /// </summary>
    static void FixedPrecisionDigits(double value, int p, out string digits, out int e)
    {
        Decompose(value, out BigInteger mant, out int exp);
        // Estimate e = floor(log10(value)).
        e = (int)Math.Floor(Math.Log10(value));
        for (int attempt = 0; attempt < 3; attempt++)
        {
            // n = round(value / 10^(e - p + 1))
            int scale = e - p + 1;
            BigInteger num = mant, den = BigInteger.One;
            if (exp >= 0) num <<= exp; else den <<= -exp;
            if (scale >= 0) den *= BigInteger.Pow(10, scale); else num *= BigInteger.Pow(10, -scale);
            BigInteger n = RoundHalfUp(num, den);
            string s = n.ToString(CultureInfo.InvariantCulture);
            if (s.Length == p) { digits = s; return; }
            if (s.Length > p)
            {
                // Either the estimate was low or rounding carried into a new digit.
                if (s.Length == p + 1 && s.TrimEnd('0').Length <= 1 && n == BigInteger.Pow(10, p))
                {
                    digits = s[..p];
                    e += 1;
                    return;
                }
                e += 1;
            }
            else
            {
                e -= 1;
            }
        }
        throw new InvalidOperationException("FixedPrecisionDigits did not converge");
    }
}
