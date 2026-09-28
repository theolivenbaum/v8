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
}
