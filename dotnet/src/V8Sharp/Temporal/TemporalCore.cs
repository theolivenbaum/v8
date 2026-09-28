// The Temporal engine: the part of V8's Temporal that lives in the Rust crate
// temporal_rs (third_party/rust/temporal_capi, not in this checkout). V8's
// binding layer (src/objects/js-temporal-objects.cc, ported in
// Objects/JSTemporalObjects.cs and Builtins/Builtins.Temporal*.cs) calls into
// it for everything after argument processing. This is implemented from the
// Temporal specification's abstract operations (https://tc39.es/proposal-temporal/),
// with test262's built-ins/Temporal as the executable specification; see
// deviations.md ("Temporal").
//
// This file: the error type (temporal_rs::TemporalError), the option enums
// (temporal_rs::Unit, RoundingMode, ArithmeticOverflow, Disambiguation,
// OffsetDisambiguation, DisplayCalendar, DisplayOffset, DisplayTimeZone,
// TransitionDirection, Precision) and the rounding operations.
using System.Numerics;
using System.Runtime.CompilerServices;

namespace V8Sharp.Temporal;

/// <summary>temporal_rs::ErrorKind.</summary>
public enum TemporalErrorKind { Generic, Type, Range, Syntax, Assert }

/// <summary>
/// temporal_rs::TemporalError. The engine reports errors by throwing it; the
/// binding turns it into a JavaScript error with MessageTemplate::kTemporal
/// (ExtractRustResult in js-temporal-objects.cc).
/// </summary>
public sealed class TemporalError(TemporalErrorKind kind, string message) : Exception(message)
{
    public TemporalErrorKind Kind { get; } = kind;

    public static TemporalError Range(string message) => new(TemporalErrorKind.Range, message);
    public static TemporalError Type(string message) => new(TemporalErrorKind.Type, message);
    public static TemporalError Syntax(string message) => new(TemporalErrorKind.Syntax, message);
}

/// <summary>temporal_rs::Unit, in the order of the spec's Table 21 (largest first).</summary>
public enum Unit : byte
{
    Year,
    Month,
    Week,
    Day,
    Hour,
    Minute,
    Second,
    Millisecond,
    Microsecond,
    Nanosecond,
    Auto,
}

/// <summary>temporal_rs::RoundingMode.</summary>
public enum RoundingMode : byte { Ceil, Floor, Expand, Trunc, HalfCeil, HalfFloor, HalfExpand, HalfTrunc, HalfEven }

/// <summary>The spec's unsigned rounding modes (Table 22).</summary>
public enum UnsignedRoundingMode : byte { Infinity, Zero, HalfInfinity, HalfZero, HalfEven }

/// <summary>temporal_rs::ArithmeticOverflow.</summary>
public enum Overflow : byte { Constrain, Reject }

/// <summary>temporal_rs::Disambiguation.</summary>
public enum Disambiguation : byte { Compatible, Earlier, Later, Reject }

/// <summary>temporal_rs::OffsetDisambiguation.</summary>
public enum OffsetDisambiguation : byte { Use, Prefer, Ignore, Reject }

/// <summary>temporal_rs::DisplayCalendar.</summary>
public enum DisplayCalendar : byte { Auto, Always, Never, Critical }

/// <summary>temporal_rs::DisplayOffset.</summary>
public enum DisplayOffset : byte { Auto, Never }

/// <summary>temporal_rs::DisplayTimeZone.</summary>
public enum DisplayTimeZone : byte { Auto, Never, Critical }

/// <summary>temporal_rs::TransitionDirection.</summary>
public enum TransitionDirection : byte { Next, Previous }

/// <summary>The spec's TemporalUnitCategory.</summary>
public enum UnitGroup : byte { Date, Time, DateTime }

/// <summary>temporal_rs::Precision: fractionalSecondDigits (null precision is auto).</summary>
public readonly record struct Precision(bool IsMinute, int? Digits)
{
    public static readonly Precision Auto = new(false, null);
}

/// <summary>temporal_rs::ToStringRoundingOptions.</summary>
public readonly record struct ToStringRoundingOptions(Precision Precision, Unit? SmallestUnit, RoundingMode? RoundingMode)
{
    public static readonly ToStringRoundingOptions Default = new(Precision.Auto, null, null);
}

/// <summary>temporal_rs::DifferenceSettings.</summary>
public readonly record struct DifferenceSettings(Unit? LargestUnit, Unit? SmallestUnit, RoundingMode? RoundingMode, uint? Increment);

/// <summary>temporal_rs::RoundingOptions.</summary>
public readonly record struct RoundingOptions(Unit? LargestUnit, Unit? SmallestUnit, RoundingMode? RoundingMode, uint? Increment);

/// <summary>The spec's ToSecondsStringPrecisionRecord result.</summary>
public readonly record struct SecondsStringPrecision(Precision Precision, Unit Unit, long Increment);

/// <summary>Unit helpers (Table 21) and the precision/increment validations.</summary>
public static class Units
{
    public const long NsPerDay = 86_400_000_000_000;
    public const long NsPerHour = 3_600_000_000_000;
    public const long NsPerMinute = 60_000_000_000;
    public const long NsPerSecond = 1_000_000_000;
    public const long NsPerMillisecond = 1_000_000;
    public const long NsPerMicrosecond = 1_000;

    static readonly string[] s_singular =
        ["year", "month", "week", "day", "hour", "minute", "second", "millisecond", "microsecond", "nanosecond", "auto"];

    public static string Name(Unit unit) => s_singular[(int)unit];

    /// <summary>The length of a time unit (or day) in nanoseconds (Table 21 "Length in Nanoseconds").</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static long Length(Unit unit) => unit switch
    {
        Unit.Day => NsPerDay,
        Unit.Hour => NsPerHour,
        Unit.Minute => NsPerMinute,
        Unit.Second => NsPerSecond,
        Unit.Millisecond => NsPerMillisecond,
        Unit.Microsecond => NsPerMicrosecond,
        Unit.Nanosecond => 1,
        _ => throw new InvalidOperationException("calendar unit has no fixed length"),
    };

    public static bool IsCalendarUnit(Unit unit) => unit is Unit.Year or Unit.Month or Unit.Week;

    public static bool IsDateUnit(Unit unit) => unit <= Unit.Day;

    /// <summary>LargerOfTwoTemporalUnits.</summary>
    public static Unit Larger(Unit a, Unit b) => a <= b ? a : b;

    /// <summary>MaximumTemporalDurationRoundingIncrement: null for date units.</summary>
    public static long? MaximumRoundingIncrement(Unit unit) => unit switch
    {
        Unit.Year or Unit.Month or Unit.Week or Unit.Day => null,
        Unit.Hour => 24,
        Unit.Minute or Unit.Second => 60,
        _ => 1000,
    };

    /// <summary>ValidateTemporalRoundingIncrement.</summary>
    public static void ValidateRoundingIncrement(long increment, long dividend, bool inclusive)
    {
        long maximum = inclusive ? dividend : dividend - 1;
        if (increment > maximum) throw TemporalError.Range("roundingIncrement out of range.");
        if (dividend % increment != 0) throw TemporalError.Range("roundingIncrement does not divide evenly.");
    }

    /// <summary>ToSecondsStringPrecisionRecord.</summary>
    public static SecondsStringPrecision ToSecondsStringPrecision(Unit? smallestUnit, Precision digits)
    {
        switch (smallestUnit)
        {
            case Unit.Minute: return new(new Precision(true, null), Unit.Minute, 1);
            case Unit.Second: return new(new Precision(false, 0), Unit.Second, 1);
            case Unit.Millisecond: return new(new Precision(false, 3), Unit.Millisecond, 1);
            case Unit.Microsecond: return new(new Precision(false, 6), Unit.Microsecond, 1);
            case Unit.Nanosecond: return new(new Precision(false, 9), Unit.Nanosecond, 1);
            case null: break;
            default: throw TemporalError.Range("Invalid smallestUnit for toString.");
        }
        if (digits.IsMinute) return new(digits, Unit.Minute, 1);
        if (digits.Digits is not int d) return new(Precision.Auto, Unit.Nanosecond, 1);
        if (d == 0) return new(digits, Unit.Second, 1);
        if (d <= 3) return new(digits, Unit.Millisecond, Pow10(3 - d));
        if (d <= 6) return new(digits, Unit.Microsecond, Pow10(6 - d));
        return new(digits, Unit.Nanosecond, Pow10(9 - d));
    }

    public static long Pow10(int n)
    {
        long r = 1;
        for (int i = 0; i < n; i++) r *= 10;
        return r;
    }

    /// <summary>Resolves the unit of a GetDifferenceSettings / round call (the validation steps).</summary>
    public static (Unit largest, Unit smallest, RoundingMode mode, long increment) ResolveDifferenceSettings(
        in DifferenceSettings settings, bool isSince, UnitGroup group, Unit disallowed1, Unit disallowed2, bool hasDisallowed,
        Unit fallbackSmallest, Unit smallestLargestDefault)
    {
        Unit? largest = settings.LargestUnit;
        // ValidateTemporalUnitValue(largestUnit, unitGroup, « auto »)
        if (largest is Unit lu && lu != Unit.Auto) ValidateUnitGroup(lu, group);
        Unit largestUnit = largest ?? Unit.Auto;
        if (hasDisallowed && (largestUnit == disallowed1 || largestUnit == disallowed2))
            throw TemporalError.Range("largestUnit not allowed here.");
        if (settings.SmallestUnit is Unit su0)
        {
            if (su0 == Unit.Auto) throw TemporalError.Range("smallestUnit cannot be auto.");
            ValidateUnitGroup(su0, group);
        }
        Unit smallestUnit = settings.SmallestUnit ?? fallbackSmallest;
        if (hasDisallowed && (smallestUnit == disallowed1 || smallestUnit == disallowed2))
            throw TemporalError.Range("smallestUnit not allowed here.");
        Unit defaultLargest = Larger(smallestLargestDefault, smallestUnit);
        if (largestUnit == Unit.Auto) largestUnit = defaultLargest;
        if (Larger(largestUnit, smallestUnit) != largestUnit)
            throw TemporalError.Range("smallestUnit is larger than largestUnit.");
        long increment = settings.Increment ?? 1;
        if (MaximumRoundingIncrement(smallestUnit) is long maximum) ValidateRoundingIncrement(increment, maximum, false);
        RoundingMode mode = settings.RoundingMode ?? RoundingMode.Trunc;
        if (isSince) mode = Rounding.Negate(mode);
        return (largestUnit, smallestUnit, mode, increment);
    }

    public static void ValidateUnitGroup(Unit unit, UnitGroup group)
    {
        bool isDate = IsDateUnit(unit);
        if (isDate && group == UnitGroup.Time) throw TemporalError.Range("Found date unit, expect time unit.");
        if (!isDate && group == UnitGroup.Date) throw TemporalError.Range("Found time unit, expect date unit.");
    }
}

/// <summary>The spec's rounding operations, exact on integers (RoundNumberToIncrement and friends).</summary>
public static class Rounding
{
    /// <summary>NegateRoundingMode.</summary>
    public static RoundingMode Negate(RoundingMode mode) => mode switch
    {
        RoundingMode.Ceil => RoundingMode.Floor,
        RoundingMode.Floor => RoundingMode.Ceil,
        RoundingMode.HalfCeil => RoundingMode.HalfFloor,
        RoundingMode.HalfFloor => RoundingMode.HalfCeil,
        _ => mode,
    };

    /// <summary>GetUnsignedRoundingMode.</summary>
    public static UnsignedRoundingMode GetUnsigned(RoundingMode mode, bool isNegative) => mode switch
    {
        RoundingMode.Ceil => isNegative ? UnsignedRoundingMode.Zero : UnsignedRoundingMode.Infinity,
        RoundingMode.Floor => isNegative ? UnsignedRoundingMode.Infinity : UnsignedRoundingMode.Zero,
        RoundingMode.Expand => UnsignedRoundingMode.Infinity,
        RoundingMode.Trunc => UnsignedRoundingMode.Zero,
        RoundingMode.HalfCeil => isNegative ? UnsignedRoundingMode.HalfZero : UnsignedRoundingMode.HalfInfinity,
        RoundingMode.HalfFloor => isNegative ? UnsignedRoundingMode.HalfInfinity : UnsignedRoundingMode.HalfZero,
        RoundingMode.HalfExpand => UnsignedRoundingMode.HalfInfinity,
        RoundingMode.HalfTrunc => UnsignedRoundingMode.HalfZero,
        _ => UnsignedRoundingMode.HalfEven,
    };

    /// <summary>
    /// ApplyUnsignedRoundingMode for x = r1 + num/den (0 ≤ num &lt; den): true if the result
    /// is r2 = r1 + 1, false if it is r1. <paramref name="r1IsEven"/> is for half-even.
    /// </summary>
    public static bool RoundsUp(Int128 num, Int128 den, UnsignedRoundingMode mode, bool r1IsEven)
    {
        if (num == 0) return false;
        switch (mode)
        {
            case UnsignedRoundingMode.Zero: return false;
            case UnsignedRoundingMode.Infinity: return true;
        }
        Int128 twice = num * 2;
        if (twice < den) return false;
        if (twice > den) return true;
        return mode switch
        {
            UnsignedRoundingMode.HalfZero => false,
            UnsignedRoundingMode.HalfInfinity => true,
            _ => !r1IsEven,
        };
    }

    public static bool RoundsUp(BigInteger num, BigInteger den, UnsignedRoundingMode mode, bool r1IsEven)
    {
        if (num.IsZero) return false;
        switch (mode)
        {
            case UnsignedRoundingMode.Zero: return false;
            case UnsignedRoundingMode.Infinity: return true;
        }
        int c = (num * 2).CompareTo(den);
        if (c < 0) return false;
        if (c > 0) return true;
        return mode switch
        {
            UnsignedRoundingMode.HalfZero => false,
            UnsignedRoundingMode.HalfInfinity => true,
            _ => !r1IsEven,
        };
    }

    /// <summary>RoundNumberToIncrement(x, increment, roundingMode) on exact integers.</summary>
    public static Int128 RoundToIncrement(Int128 x, Int128 increment, RoundingMode mode)
    {
        bool isNegative = x < 0;
        Int128 ax = isNegative ? -x : x;
        Int128 q = ax / increment;
        Int128 rem = ax - q * increment;
        UnsignedRoundingMode um = GetUnsigned(mode, isNegative);
        if (RoundsUp(rem, increment, um, (q & 1) == 0)) q += 1;
        Int128 r = q * increment;
        return isNegative ? -r : r;
    }

    /// <summary>RoundNumberToIncrementAsIfPositive.</summary>
    public static Int128 RoundToIncrementAsIfPositive(Int128 x, Int128 increment, RoundingMode mode)
    {
        UnsignedRoundingMode um = GetUnsigned(mode, false);
        Int128 q = Int128Floor(x, increment);
        Int128 rem = x - q * increment;
        if (RoundsUp(rem, increment, um, (q & 1) == 0)) q += 1;
        return q * increment;
    }

    /// <summary>RoundNumberToIncrement for a double holding an integer (calendar units).</summary>
    public static double RoundToIncrement(double x, long increment, RoundingMode mode) =>
        (double)RoundToIncrement((Int128)x, increment, mode);

    /// <summary>floor(a / b) for b &gt; 0.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Int128 Int128Floor(Int128 a, Int128 b)
    {
        Int128 q = a / b;
        if ((a % b) != 0 && (a < 0)) q -= 1;
        return q;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static long FloorDiv(long a, long b)
    {
        long q = a / b;
        if ((a % b) != 0 && ((a < 0) != (b < 0))) q--;
        return q;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static long FloorMod(long a, long b)
    {
        long m = a % b;
        if (m != 0 && ((m < 0) != (b < 0))) m += b;
        return m;
    }

    /// <summary>𝔽(num / den) correctly rounded (round half to even), for den &gt; 0.</summary>
    public static double RationalToDouble(BigInteger num, BigInteger den)
    {
        if (num.IsZero) return 0;
        bool negative = num.Sign < 0;
        if (negative) num = -num;
        // Scale so that the quotient has at least 54 significant bits plus a sticky bit.
        long numBits = (long)num.GetBitLength();
        long denBits = (long)den.GetBitLength();
        int shift = (int)(55 - (numBits - denBits));
        BigInteger scaledNum = shift >= 0 ? num << shift : num;
        BigInteger scaledDen = shift >= 0 ? den : den << -shift;
        BigInteger q = BigInteger.DivRem(scaledNum, scaledDen, out BigInteger rem);
        // q has 55 or 56 bits. Build the double: value = q * 2^-shift, with sticky from rem.
        int qBits = (int)q.GetBitLength();
        int extra = qBits - 53;
        BigInteger mantissa = q >> extra;
        BigInteger dropped = q - (mantissa << extra);
        BigInteger half = BigInteger.One << (extra - 1);
        bool roundUp;
        int cmp = dropped.CompareTo(half);
        if (cmp > 0) roundUp = true;
        else if (cmp < 0) roundUp = false;
        else roundUp = !rem.IsZero || !mantissa.IsEven;
        if (roundUp) mantissa += 1;
        int exponent = extra - shift;
        double result = Math.ScaleB((double)mantissa, exponent);
        return negative ? -result : result;
    }
}
