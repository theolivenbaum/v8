// Port of src/base/numbers/dtoa.h and dtoa.cc.

namespace V8Sharp.Base.Numbers;

public enum DtoaMode
{
    /// <summary>Return the shortest correct representation. For example the
    /// output of 0.299999999999999988897 is (the less accurate but correct)
    /// 0.3.</summary>
    DTOA_SHORTEST,
    /// <summary>Return a fixed number of digits after the decimal point. For
    /// instance fixed(0.1, 4) becomes 0.1000. If the input number is big, the
    /// output will be big.</summary>
    DTOA_FIXED,
    /// <summary>Return a fixed number of digits, no matter what the exponent is.</summary>
    DTOA_PRECISION,
}

public static partial class DoubleConversion
{
    /// <summary>
    /// The maximal length of digits a double can have in base 10 as returned
    /// by <see cref="DoubleToAscii"/>. This does neither include sign, decimal
    /// point nor exponent. DoubleToAscii null-terminates its output, so the
    /// given buffer should be at least kBase10MaximalLength + 1 characters long.
    /// </summary>
    public const int kBase10MaximalLength = 17;

    static BignumDtoaMode DtoaToBignumDtoaMode(DtoaMode dtoaMode) => dtoaMode switch
    {
        DtoaMode.DTOA_SHORTEST => BignumDtoaMode.BIGNUM_DTOA_SHORTEST,
        DtoaMode.DTOA_FIXED => BignumDtoaMode.BIGNUM_DTOA_FIXED,
        DtoaMode.DTOA_PRECISION => BignumDtoaMode.BIGNUM_DTOA_PRECISION,
        _ => throw new UnreachableException(),
    };

    /// <summary>
    /// Converts the given double 'v' to ASCII. The result should be
    /// interpreted as buffer * 10^(point-length).
    /// <list type="bullet">
    /// <item>SHORTEST: produce the least amount of digits for which the
    /// internal identity requirement is still satisfied. In this mode the
    /// 'requested_digits' parameter is ignored.</item>
    /// <item>FIXED: produces digits necessary to print a given number with
    /// 'requested_digits' digits after the decimal point. The produced digits
    /// might be too short in which case the caller has to fill the gaps with
    /// '0's. Halfway cases are rounded towards +/-Infinity (away from 0).</item>
    /// <item>PRECISION: produces 'requested_digits' where the first digit is
    /// not '0'. The function is allowed to return fewer digits. Halfway cases
    /// are again rounded away from 0.</item>
    /// </list>
    /// The buffer must be big enough to hold all digits and a terminating
    /// null character.
    /// </summary>
    public static void DoubleToAscii(double v, DtoaMode mode, int requestedDigits, Span<char> buffer,
                                     out int sign, out int length, out int point)
    {
        Debug.Assert(!new Double(v).IsSpecial);
        Debug.Assert(mode == DtoaMode.DTOA_SHORTEST || requestedDigits >= 0);

        if (new Double(v).Sign < 0)
        {
            sign = 1;
            v = -v;
        }
        else
        {
            sign = 0;
        }

        if (v == 0)
        {
            buffer[0] = '0';
            buffer[1] = '\0';
            length = 1;
            point = 1;
            return;
        }

        if (mode == DtoaMode.DTOA_PRECISION && requestedDigits == 0)
        {
            buffer[0] = '\0';
            length = 0;
            // The C++ leaves *point unassigned here; callers do not read it.
            point = 0;
            return;
        }

        bool fastWorked = mode switch
        {
            DtoaMode.DTOA_SHORTEST => FastDtoa(v, FastDtoaMode.FAST_DTOA_SHORTEST, 0, buffer, out length, out point),
            DtoaMode.DTOA_FIXED => FastFixedDtoa(v, requestedDigits, buffer, out length, out point),
            DtoaMode.DTOA_PRECISION => FastDtoa(v, FastDtoaMode.FAST_DTOA_PRECISION, requestedDigits, buffer, out length, out point),
            _ => throw new UnreachableException(),
        };
        if (fastWorked) return;

        // If the fast dtoa didn't succeed use the slower bignum version.
        BignumDtoaMode bignumMode = DtoaToBignumDtoaMode(mode);
        BignumDtoa(v, bignumMode, requestedDigits, buffer, out length, out point);
        buffer[length] = '\0';
    }
}
