// Copyright 2011 the V8 project authors. All rights reserved.
// Use of this source code is governed by a BSD-style license that can be
// found in the LICENSE file.

// The literal-to-double conversions the scanner needs from
// src/numbers/conversions.cc: StringToDouble (NO_CONVERSION_FLAG, applied to
// already-validated decimal literals), HexStringToDouble, OctalStringToDouble,
// BinaryStringToDouble and ImplicitOctalStringToDouble, plus
// DoubleToCString-free helpers used by the AST.
// TODO(merge): move to V8Sharp.Base Numbers (StringToDouble and friends).

using System.Globalization;

namespace V8Sharp.Parsing;

public static class NumberConversions
{
    // StringToDouble(literal, NO_CONVERSION_FLAG) for a scanned decimal
    // literal: digits, optional '.', fraction, optional exponent. .NET's
    // double.Parse is correctly rounded (IEEE round-half-even) for any length,
    // which is what V8's Strtod computes.
    public static double StringToDouble(ReadOnlySpan<char> literal)
    {
        if (literal.Length == 0) return 0;
        return double.Parse(literal, NumberStyles.AllowDecimalPoint | NumberStyles.AllowExponent | NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);
    }

    // "0x..." / "0X..."
    public static double HexStringToDouble(ReadOnlySpan<char> str) => InternalStringToIntDouble(str[2..], 4);

    // "0o..." / "0O..."
    public static double OctalStringToDouble(ReadOnlySpan<char> str) => InternalStringToIntDouble(str[2..], 3);

    // "0b..." / "0B..."
    public static double BinaryStringToDouble(ReadOnlySpan<char> str) => InternalStringToIntDouble(str[2..], 1);

    // Legacy octal "0777": skip the leading '0'.
    public static double ImplicitOctalStringToDouble(ReadOnlySpan<char> str) => InternalStringToIntDouble(str[1..], 3);

    // InternalStringToIntDouble<radix_log_2>: exact conversion of a digit
    // string in a power-of-two radix, rounding half to even at 53 bits.
    public static double InternalStringToIntDouble(ReadOnlySpan<char> digits, int radixLog2)
    {
        // Skip leading zeros.
        int i = 0;
        while (i < digits.Length && digits[i] == '0') i++;
        if (i == digits.Length) return 0;

        ulong number = 0;
        int exponent = 0;
        const int kSignificandSize = 53;
        int lim_0 = '0' + Math.Min(1 << radixLog2, 10);
        for (; i < digits.Length; i++)
        {
            int c = digits[i];
            int digit;
            if (c >= '0' && c < lim_0) digit = c - '0';
            else if (c >= 'a' && c <= 'f') digit = c - 'a' + 10;
            else if (c >= 'A' && c <= 'F') digit = c - 'A' + 10;
            else break; // Scanner-validated literals have no other characters.

            number = (number << radixLog2) + (ulong)digit;
            int overflow = (int)(number >> kSignificandSize);
            if (overflow != 0)
            {
                // Overflow occurred. Need to determine which direction to round the
                // result.
                int overflow_bits_count = 1;
                while (overflow > 1)
                {
                    overflow_bits_count++;
                    overflow >>= 1;
                }

                int dropped_bits_mask = (1 << overflow_bits_count) - 1;
                int dropped_bits = (int)number & dropped_bits_mask;
                number >>= overflow_bits_count;
                exponent = overflow_bits_count;

                bool zero_tail = true;
                for (i++; i < digits.Length; i++)
                {
                    c = digits[i];
                    if (!IsDigitInRadix(c, radixLog2)) break;
                    if (c != '0') zero_tail = false;
                    exponent += radixLog2;
                }

                int middle_value = 1 << (overflow_bits_count - 1);
                if (dropped_bits > middle_value)
                {
                    number++; // Rounding up.
                }
                else if (dropped_bits == middle_value)
                {
                    // Rounding to even to consistency with decimals: half-way case rounds
                    // up if significant part is odd and down otherwise.
                    if ((number & 1) != 0 || !zero_tail)
                    {
                        number++; // Rounding up.
                    }
                }

                // Rounding up may cause overflow.
                if ((number & (1UL << kSignificandSize)) != 0)
                {
                    exponent++;
                    number >>= 1;
                }
                break;
            }
        }

        if (exponent == 0) return (double)number;
        return Math.ScaleB((double)number, exponent);
    }

    private static bool IsDigitInRadix(int c, int radixLog2)
    {
        if (radixLog2 < 3) return c >= '0' && c < '0' + (1 << radixLog2);
        if (radixLog2 == 3) return c >= '0' && c <= '7';
        return CharPredicates.IsHexDigit(c);
    }

    // DoubleToInt32 (src/numbers/conversions-inl.h): ECMA ToInt32.
    public static int DoubleToInt32(double x)
    {
        if (double.IsFinite(x) && x <= int.MaxValue && x >= int.MinValue)
        {
            // All doubles within these limits are trivially convertable to an int.
            return (int)x;
        }
        if (!double.IsFinite(x) || x == 0) return 0;
        // Reduce modulo 2^32 using the exact binary representation.
        long bits = BitConverter.DoubleToInt64Bits(x);
        int exponent = (int)((bits >> 52) & 0x7FF) - 1075;
        ulong significand = ((ulong)bits & 0xFFFFFFFFFFFFFUL) | 0x10000000000000UL;
        uint result;
        if (exponent < 0)
        {
            if (exponent <= -53) return 0;
            result = (uint)(significand >> -exponent);
        }
        else
        {
            if (exponent > 31) return 0;
            result = (uint)(significand << exponent);
        }
        return bits < 0 ? -(int)result : (int)result;
    }

    public static uint DoubleToUint32(double x) => (uint)DoubleToInt32(x);

    // Modulo (src/numbers/conversions-inl.h / fmod): C#'s % on doubles is fmod.
    public static double Modulo(double x, double y) => x % y;

    // math::pow (src/numbers/ieee754.cc) with v8_flags.use_std_math_pow, the
    // default on every platform but AIX: the ECMAScript special cases, then
    // std::pow (Math.Pow is the C runtime pow).
    public static double Pow(double x, double y)
    {
        if (double.IsNaN(y))
        {
            // 1. If exponent is NaN, return NaN.
            return double.NaN;
        }
        if (double.IsInfinity(y) && (x == 1 || x == -1))
        {
            // 9. If exponent is +∞𝔽, then
            //   b. If abs(ℝ(base)) = 1, return NaN.
            // and
            // 10. If exponent is -∞𝔽, then
            //   b. If abs(ℝ(base)) = 1, return NaN.
            return double.NaN;
        }
        if (double.IsNaN(x))
        {
            // libm pow distinguishes between quiet and signaling NaN; JS doesn't.
            x = double.NaN;
        }

        // The following special cases just exist to match the optimizing compilers'
        // behavior, which avoid calls to `pow` in those cases.
        if (y == 2)
        {
            // x ** 2   ==>   x * x
            return x * x;
        }
        else if (y == 0.5)
        {
            // x ** 0.5   ==>  sqrt(x), except if x is -Infinity
            if (double.IsInfinity(x))
            {
                return double.PositiveInfinity;
            }
            else
            {
                // Note the +0 so that we get +0 for -0**0.5 rather than -0.
                return Math.Sqrt(x + 0);
            }
        }

        return Math.Pow(x, y);
    }

    // DoubleToStringView / DoubleToCString (src/numbers/conversions.cc): the
    // ECMAScript Number::toString. V8 takes the shortest digits from
    // dragonbox; .NET's "R" formatting also yields the shortest round-trip
    // digits.
    public static string DoubleToCString(double v)
    {
        if (double.IsNaN(v)) return "NaN";
        if (double.IsInfinity(v)) return v < 0.0 ? "-Infinity" : "Infinity";
        if (v == 0) return "0";
        if (v >= int.MinValue && v <= int.MaxValue && v == Math.Floor(v))
        {
            // This will trigger if v is -0 and -0.0 is stringified to "0".
            return ((int)v).ToString(CultureInfo.InvariantCulture);
        }

        // Shortest digits and decimal exponent from the round-trip format.
        string shortest = Math.Abs(v).ToString("R", CultureInfo.InvariantCulture);
        Span<char> digits = stackalloc char[32];
        int length = 0;
        int exponent10; // position of the decimal point relative to digits start
        int epos = shortest.IndexOfAny(['E', 'e']);
        string mantissa = epos >= 0 ? shortest[..epos] : shortest;
        int exp = epos >= 0 ? int.Parse(shortest.AsSpan(epos + 1), NumberStyles.AllowLeadingSign,
                                        CultureInfo.InvariantCulture) : 0;
        int dot = mantissa.IndexOf('.');
        int intDigits = dot >= 0 ? dot : mantissa.Length;
        bool leading = true;
        int skippedLeadingZeros = 0;
        for (int i = 0; i < mantissa.Length; i++)
        {
            char c = mantissa[i];
            if (c == '.') continue;
            if (leading && c == '0')
            {
                skippedLeadingZeros++;
                continue;
            }
            leading = false;
            digits[length++] = c;
        }
        while (length > 1 && digits[length - 1] == '0') length--;
        exponent10 = intDigits - skippedLeadingZeros + exp;
        int decimal_point = exponent10;

        System.Text.StringBuilder builder = new(32);
        if (v < 0) builder.Append('-');
        ReadOnlySpan<char> decimal_rep = digits[..length];
        if (length <= decimal_point && decimal_point <= 21)
        {
            // ECMA-262 section 9.8.1 step 6.
            builder.Append(decimal_rep);
            builder.Append('0', decimal_point - length);
        }
        else if (0 < decimal_point && decimal_point <= 21)
        {
            // ECMA-262 section 9.8.1 step 7.
            builder.Append(decimal_rep[..decimal_point]);
            builder.Append('.');
            builder.Append(decimal_rep[decimal_point..]);
        }
        else if (decimal_point <= 0 && decimal_point > -6)
        {
            // ECMA-262 section 9.8.1 step 8.
            builder.Append("0.");
            builder.Append('0', -decimal_point);
            builder.Append(decimal_rep);
        }
        else
        {
            // ECMA-262 section 9.8.1 step 9 and 10 combined.
            builder.Append(decimal_rep[0]);
            if (length != 1)
            {
                builder.Append('.');
                builder.Append(decimal_rep[1..]);
            }
            builder.Append('e');
            builder.Append(decimal_point >= 0 ? '+' : '-');
            int exponent = decimal_point - 1;
            if (exponent < 0) exponent = -exponent;
            builder.Append(exponent.ToString(CultureInfo.InvariantCulture));
        }
        return builder.ToString();
    }

    // BigIntLiteralToDecimal (src/numbers/conversions.cc): a scanned BigInt
    // literal ("0x1f", "0o17", "0b101" or decimal, without the 'n') as a
    // decimal digit string.
    public static string BigIntLiteralToDecimal(ReadOnlySpan<char> literal)
    {
        int radix = 10;
        ReadOnlySpan<char> digits = literal;
        if (literal.Length >= 2 && literal[0] == '0')
        {
            switch (literal[1])
            {
                case 'x':
                case 'X':
                    radix = 16;
                    digits = literal[2..];
                    break;
                case 'o':
                case 'O':
                    radix = 8;
                    digits = literal[2..];
                    break;
                case 'b':
                case 'B':
                    radix = 2;
                    digits = literal[2..];
                    break;
            }
        }
        System.Numerics.BigInteger value = System.Numerics.BigInteger.Zero;
        foreach (char c in digits)
        {
            if (c == '_') continue;
            value = value * radix + CharPredicates.HexValue(c);
        }
        return value.ToString(CultureInfo.InvariantCulture);
    }
}
