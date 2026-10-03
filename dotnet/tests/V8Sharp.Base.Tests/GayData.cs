// Port of test/unittests/gay-fixed.{h,cc}, gay-precision.{h,cc} and
// gay-shortest.{h,cc}: 100.000 decimal representations of random doubles each,
// generated with Gay's dtoa. The data is not retyped: the V8 .cc files are
// embedded as resources (see the .csproj) and their table rows are read here.

using System.Globalization;
using System.Reflection;
using System.Text.RegularExpressions;

namespace V8Sharp.Base.Tests;

public readonly record struct PrecomputedShortest(double v, string representation, int decimal_point);

public readonly record struct PrecomputedFixed(double v, int number_digits, string representation, int decimal_point);

public readonly record struct PrecomputedPrecision(double v, int number_digits, string representation, int decimal_point);

public static partial class GayData
{
    static PrecomputedShortest[]? s_shortest;
    static PrecomputedFixed[]? s_fixed;
    static PrecomputedPrecision[]? s_precision;

    [GeneratedRegex("""^\s*\{([-+0-9.e]+),\s*(-?\d+),\s*"(\d*)",\s*(-?\d+)\},?\s*$""")]
    private static partial Regex FourFieldRow();

    [GeneratedRegex("""^\s*\{([-+0-9.e]+),\s*"(\d*)",\s*(-?\d+)\},?\s*$""")]
    private static partial Regex ThreeFieldRow();

    static IEnumerable<string> Lines(string resource)
    {
        using Stream s = Assembly.GetExecutingAssembly().GetManifestResourceStream(resource)
            ?? throw new InvalidOperationException("missing resource " + resource);
        using StreamReader r = new(s);
        while (r.ReadLine() is { } line) yield return line;
    }

    // The C++ compiler rounds the table literals correctly, and so does
    // double.Parse, so both read the same doubles.
    static double D(string s) => double.Parse(s, NumberStyles.Float, CultureInfo.InvariantCulture);

    public static ReadOnlySpan<PrecomputedShortest> PrecomputedShortestRepresentations()
    {
        if (s_shortest == null)
        {
            List<PrecomputedShortest> list = new(100000);
            foreach (string line in Lines("gay-shortest.cc"))
            {
                Match m = ThreeFieldRow().Match(line);
                if (m.Success) list.Add(new(D(m.Groups[1].Value), m.Groups[2].Value, int.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture)));
            }
            s_shortest = [.. list];
        }
        return s_shortest;
    }

    public static ReadOnlySpan<PrecomputedFixed> PrecomputedFixedRepresentations()
    {
        if (s_fixed == null)
        {
            List<PrecomputedFixed> list = new(100000);
            foreach (string line in Lines("gay-fixed.cc"))
            {
                Match m = FourFieldRow().Match(line);
                if (m.Success) list.Add(new(D(m.Groups[1].Value), int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture), m.Groups[3].Value, int.Parse(m.Groups[4].Value, CultureInfo.InvariantCulture)));
            }
            s_fixed = [.. list];
        }
        return s_fixed;
    }

    public static ReadOnlySpan<PrecomputedPrecision> PrecomputedPrecisionRepresentations()
    {
        if (s_precision == null)
        {
            List<PrecomputedPrecision> list = new(100000);
            foreach (string line in Lines("gay-precision.cc"))
            {
                Match m = FourFieldRow().Match(line);
                if (m.Success) list.Add(new(D(m.Groups[1].Value), int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture), m.Groups[3].Value, int.Parse(m.Groups[4].Value, CultureInfo.InvariantCulture)));
            }
            s_precision = [.. list];
        }
        return s_precision;
    }
}
