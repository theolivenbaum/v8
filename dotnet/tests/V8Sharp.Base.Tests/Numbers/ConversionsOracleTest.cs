// Differential tests of the number conversions against the real V8 (the
// oracle): thousands of random doubles and strings, compared byte for byte.

using System.Globalization;
using System.Text;
using V8Sharp.Base.Numbers;
using V8Sharp.Oracle;

namespace V8Sharp.Base.Tests.Numbers;

public class ConversionsOracleTest
{
    const string Prelude = """
        const __dv = new DataView(new ArrayBuffer(8));
        function D(h, l) { __dv.setUint32(0, h); __dv.setUint32(4, l); return __dv.getFloat64(0); }
        function B(x) {
          if (x !== x) return "NaN";
          __dv.setFloat64(0, x);
          return __dv.getUint32(0).toString(16) + ":" + __dv.getUint32(4).toString(16);
        }
        """;

    static string Bits(double x)
    {
        if (double.IsNaN(x)) return "NaN";
        ulong b = BitConverter.DoubleToUInt64Bits(x);
        return ((uint)(b >> 32)).ToString("x", CultureInfo.InvariantCulture) + ":" +
               ((uint)b).ToString("x", CultureInfo.InvariantCulture);
    }

    static string Lit(double x)
    {
        ulong b = BitConverter.DoubleToUInt64Bits(x);
        return "D(" + (uint)(b >> 32) + "," + (uint)b + ")";
    }

    /// <summary>A mix of doubles: raw random bit patterns, small integers and
    /// simple fractions, values around powers of ten, denormals.</summary>
    static List<double> RandomDoubles(int seed, int count, bool finiteOnly = true)
    {
        Random rng = new(seed);
        List<double> list = new(count);
        while (list.Count < count)
        {
            double d;
            switch (rng.Next(6))
            {
                case 0:
                    d = BitConverter.UInt64BitsToDouble((ulong)rng.NextInt64() ^ ((ulong)rng.Next(2) << 63));
                    break;
                case 1:
                    d = rng.Next(-1000000, 1000000) / Math.Pow(10, rng.Next(0, 12));
                    break;
                case 2:
                    d = Math.Pow(10, rng.Next(-30, 30)) * (1 + (rng.NextDouble() - 0.5) * 1e-14);
                    break;
                case 3:
                    d = BitConverter.UInt64BitsToDouble((ulong)rng.NextInt64(1, 1L << 52));
                    break;
                case 4:
                    d = (rng.NextDouble() - 0.5) * Math.Pow(2, rng.Next(-60, 80));
                    break;
                default:
                    d = rng.Next(-100000, 100000) + rng.Next(0, 8) * 0.125;
                    break;
            }
            if (finiteOnly && !double.IsFinite(d)) continue;
            list.Add(d);
        }
        list.AddRange([0.0, -0.0, 5e-324, -5e-324, double.MaxValue, 1e21, 1e-7, 123e-20, 0.5, 1.5, 2.5, -2.5,
                       0.000001, 1e20, 999999999999999999999.0, 4.35, 1.45, 8.345, 0.1 + 0.2]);
        return list;
    }

    static void Compare(string script, IReadOnlyList<string> expectedOfOurs, List<string> inputs)
    {
        using ReferenceV8 v8 = new(allowNativesSyntax: false);
        string output = v8.Run(Prelude + script);
        string[] lines = output.Split('\n');
        StringBuilder failures = new();
        int failed = 0;
        for (int i = 0; i < expectedOfOurs.Count; i++)
        {
            string theirs = i < lines.Length ? lines[i] : "<missing>";
            if (theirs != expectedOfOurs[i])
            {
                if (++failed <= 20) failures.Append(inputs[i]).Append(": v8=").Append(theirs).Append(" ours=").Append(expectedOfOurs[i]).Append('\n');
            }
        }
        Assert.True(failed == 0, $"{failed} of {expectedOfOurs.Count} differ:\n{failures}");
    }

    [Fact]
    public void NumberToString()
    {
        List<double> ds = RandomDoubles(1, 20000);
        ds.AddRange([double.NaN, double.PositiveInfinity, double.NegativeInfinity]);
        StringBuilder js = new("const a = [");
        List<string> ours = [], inputs = [];
        foreach (double d in ds)
        {
            js.Append(double.IsNaN(d) ? "NaN" : double.IsInfinity(d) ? (d > 0 ? "Infinity" : "-Infinity") : Lit(d)).Append(',');
            ours.Add(Conversions.DoubleToCString(d));
            inputs.Add(Bits(d));
        }
        js.Append("]; print(a.map(String).join('\\n'));");
        Compare(js.ToString(), ours, inputs);
    }

    [Fact]
    public void NumberToFixed()
    {
        Random rng = new(2);
        List<double> ds = RandomDoubles(3, 8000);
        StringBuilder js = new("const a = [");
        List<string> ours = [], inputs = [];
        foreach (double d in ds)
        {
            int f = rng.Next(0, 101);
            js.Append('[').Append(Lit(d)).Append(',').Append(f).Append("],");
            ours.Add(Conversions.DoubleToFixedCString(d, f));
            inputs.Add(Bits(d) + " f=" + f);
        }
        js.Append("]; print(a.map(([x, f]) => x.toFixed(f)).join('\\n'));");
        Compare(js.ToString(), ours, inputs);
    }

    [Fact]
    public void NumberToPrecision()
    {
        Random rng = new(4);
        List<double> ds = RandomDoubles(5, 8000);
        StringBuilder js = new("const a = [");
        List<string> ours = [], inputs = [];
        foreach (double d in ds)
        {
            int p = rng.Next(1, 101);
            js.Append('[').Append(Lit(d)).Append(',').Append(p).Append("],");
            ours.Add(Conversions.DoubleToPrecisionCString(d, p));
            inputs.Add(Bits(d) + " p=" + p);
        }
        js.Append("]; print(a.map(([x, p]) => x.toPrecision(p)).join('\\n'));");
        Compare(js.ToString(), ours, inputs);
    }

    [Fact]
    public void NumberToExponential()
    {
        Random rng = new(6);
        List<double> ds = RandomDoubles(7, 8000);
        StringBuilder js = new("const a = [");
        List<string> ours = [], inputs = [];
        foreach (double d in ds)
        {
            int f = rng.Next(-1, 101);
            js.Append('[').Append(Lit(d)).Append(',').Append(f).Append("],");
            ours.Add(Conversions.DoubleToExponentialCString(d, f));
            inputs.Add(Bits(d) + " f=" + f);
        }
        js.Append("]; print(a.map(([x, f]) => f < 0 ? x.toExponential() : x.toExponential(f)).join('\\n'));");
        Compare(js.ToString(), ours, inputs);
    }

    [Fact]
    public void NumberToStringRadix()
    {
        Random rng = new(8);
        List<double> ds = RandomDoubles(9, 8000);
        StringBuilder js = new("const a = [");
        List<string> ours = [], inputs = [];
        foreach (double d in ds)
        {
            int r = rng.Next(2, 37);
            js.Append('[').Append(Lit(d)).Append(',').Append(r).Append("],");
            ours.Add(Conversions.DoubleToRadixCString(d, r));
            inputs.Add(Bits(d) + " r=" + r);
        }
        js.Append("]; print(a.map(([x, r]) => x.toString(r)).join('\\n'));");
        Compare(js.ToString(), ours, inputs);
    }

    static List<string> RandomNumericStrings(int seed, int count)
    {
        Random rng = new(seed);
        List<double> ds = RandomDoubles(seed + 100, count);
        string[] pieces = [" ", "\t", "\n", "\u00a0", "\ufeff", "\u2028", "+", "-", ".", "e", "E", "e+", "e-", "0x", "0X", "0o", "0b",
                           "Infinity", "-Infinity", "+Infinity", "Infinit", "NaN", "x", "_", "1_0", "00", "0", "9", "5", "z", "Z", "a",
                           "f", "G", "\u0660", "\u2009", "\u3000"];
        List<string> list = new(count * 2);
        foreach (double d in ds)
        {
            string s = rng.Next(4) switch
            {
                0 => Conversions.DoubleToCString(d),
                1 => Conversions.DoubleToExponentialCString(d, rng.Next(-1, 25)),
                2 => Conversions.DoubleToPrecisionCString(d, rng.Next(1, 40)),
                _ => d.ToString("R", CultureInfo.InvariantCulture),
            };
            // Mutate: prefix/suffix junk or whitespace, drop characters.
            int mode = rng.Next(8);
            if (mode == 0) s = pieces[rng.Next(pieces.Length)] + s;
            else if (mode == 1) s += pieces[rng.Next(pieces.Length)];
            else if (mode == 2 && s.Length > 1) s = s.Remove(rng.Next(s.Length), 1);
            else if (mode == 3) s = "  " + s + " \n";
            list.Add(s);
        }
        for (int i = 0; i < count; i++)
        {
            StringBuilder sb = new();
            int n = rng.Next(1, 6);
            for (int j = 0; j < n; j++) sb.Append(pieces[rng.Next(pieces.Length)]);
            list.Add(sb.ToString());
        }
        // Long digit strings (more than kMaxSignificantDecimalDigits).
        for (int i = 0; i < 50; i++)
        {
            StringBuilder sb = new();
            int n = rng.Next(700, 1200);
            for (int j = 0; j < n; j++) sb.Append((char)('0' + rng.Next(10)));
            if (rng.Next(2) == 0) sb.Insert(rng.Next(sb.Length), '.');
            sb.Append('e').Append(rng.Next(-1300, 400));
            list.Add(sb.ToString());
        }
        return list;
    }

    [Fact]
    public void StringToNumber()
    {
        List<string> strs = RandomNumericStrings(10, 6000);
        StringBuilder js = new("const a = [");
        List<string> ours = [], inputs = [];
        foreach (string s in strs)
        {
            js.Append(ReferenceV8.JsQuote(s)).Append(',');
            ours.Add(Bits(Conversions.StringToDouble(s, ConversionFlag.AllowNonDecimalPrefix)));
            inputs.Add(ReferenceV8.JsQuote(s));
        }
        js.Append("]; print(a.map(s => B(Number(s))).join('\\n'));");
        Compare(js.ToString(), ours, inputs);
    }

    [Fact]
    public void ParseFloat()
    {
        List<string> strs = RandomNumericStrings(11, 6000);
        StringBuilder js = new("const a = [");
        List<string> ours = [], inputs = [];
        foreach (string s in strs)
        {
            js.Append(ReferenceV8.JsQuote(s)).Append(',');
            ours.Add(Bits(Conversions.StringToDouble(s, ConversionFlag.AllowTrailingJunk, double.NaN)));
            inputs.Add(ReferenceV8.JsQuote(s));
        }
        js.Append("]; print(a.map(s => B(parseFloat(s))).join('\\n'));");
        Compare(js.ToString(), ours, inputs);
    }

    [Fact]
    public void ParseInt()
    {
        Random rng = new(12);
        List<string> strs = RandomNumericStrings(13, 4000);
        for (int i = 0; i < 2000; i++)
        {
            StringBuilder sb = new();
            int n = rng.Next(1, 40);
            for (int j = 0; j < n; j++) sb.Append("0123456789abcdefghijklmnopqrstuvwxyzABCXYZ"[rng.Next(42)]);
            strs.Add(sb.ToString());
        }
        StringBuilder js = new("const a = [");
        List<string> ours = [], inputs = [];
        foreach (string s in strs)
        {
            int r = rng.Next(3) == 0 ? 0 : rng.Next(2, 37);
            js.Append('[').Append(ReferenceV8.JsQuote(s)).Append(',').Append(r).Append("],");
            ours.Add(Bits(Conversions.StringToInt(s, r)));
            inputs.Add(ReferenceV8.JsQuote(s) + " r=" + r);
        }
        js.Append("]; print(a.map(([s, r]) => B(parseInt(s, r))).join('\\n'));");
        Compare(js.ToString(), ours, inputs);
    }
}
