// Tests of the Number builtins (src/builtins/builtins-number.cc, number.tq).
namespace V8Sharp.Tests.Builtins;

public class NumberBuiltinsTest : IntrinsicsTestBase
{
    string Proto(string method, double x, params JSValue[] args) => S(Call("Number.prototype." + method, Num(x), args));

    [Fact]
    public void ToFixed()
    {
        Assert.Equal("1.00", Proto("toFixed", 1, Num(2)));
        Assert.Equal("1.23", Proto("toFixed", 1.225, Num(2)));
        Assert.Equal("1", Proto("toFixed", 0.5));
        Assert.Equal("1.00", Proto("toFixed", 1.005, Num(2)));
        Assert.Equal("-1", Proto("toFixed", -0.5));
        Assert.Equal("1e+21", Proto("toFixed", 1e21, Num(2)));
        Assert.Equal("NaN", Proto("toFixed", double.NaN, Num(2)));
        Assert.Equal("RangeError: toFixed() digits argument must be between 0 and 100",
            Throws(() => Call("Number.prototype.toFixed", Num(1), Num(101))));
        Assert.Equal("TypeError: Number.prototype.toFixed requires that 'this' be a Number",
            Throws(() => Call("Number.prototype.toFixed", Str("1"))));
    }

    [Fact]
    public void ToExponentialAndPrecision()
    {
        Assert.Equal("1.23e+2", Proto("toExponential", 123, Num(2)));
        Assert.Equal("1.23e+2", Proto("toExponential", 123));
        Assert.Equal("-Infinity", Proto("toExponential", double.NegativeInfinity, Num(200)));
        Assert.Equal("RangeError: toExponential() argument must be between 0 and 100",
            Throws(() => Call("Number.prototype.toExponential", Num(1), Num(-1))));
        Assert.Equal("123.5", Proto("toPrecision", 123.456, Num(4)));
        Assert.Equal("1.2e+2", Proto("toPrecision", 123.456, Num(2)));
        Assert.Equal("123.456", Proto("toPrecision", 123.456));
        Assert.Equal("RangeError: toPrecision() argument must be between 1 and 100",
            Throws(() => Call("Number.prototype.toPrecision", Num(1), Num(0))));
    }

    [Fact]
    public void ToStringRadix()
    {
        Assert.Equal("ff", Proto("toString", 255, Num(16)));
        Assert.Equal("-11111111", Proto("toString", -255, Num(2)));
        Assert.Equal("0.1", Proto("toString", 0.5, Num(2)));
        Assert.Equal("0", Proto("toString", -0.0, Num(2)));
        Assert.Equal("1e+21", Proto("toString", 1e21));
        Assert.Equal("RangeError: toString() radix argument must be between 2 and 36",
            Throws(() => Call("Number.prototype.toString", Num(1), Num(37))));
        JSValue wrapper = New("Number", Num(42));
        Assert.Equal("101010", S(Call("Number.prototype.toString", wrapper, Num(2))));
        Assert.Equal(42, Call("Number.prototype.valueOf", wrapper).Number);
    }

    [Fact]
    public void Predicates()
    {
        Assert.True(CallStatic("Number.isInteger", Num(5)).IsTrue);
        Assert.False(CallStatic("Number.isInteger", Num(5.5)).IsTrue);
        Assert.False(CallStatic("Number.isInteger", Str("5")).IsTrue);
        Assert.True(CallStatic("Number.isSafeInteger", Num(9007199254740991)).IsTrue);
        Assert.False(CallStatic("Number.isSafeInteger", Num(9007199254740992)).IsTrue);
        Assert.True(CallStatic("Number.isNaN", JSValue.NaN).IsTrue);
        Assert.False(CallStatic("Number.isFinite", Num(double.PositiveInfinity)).IsTrue);
        Assert.True(CallStatic("Number.isFinite", Num(1)).IsTrue);
    }

    [Fact]
    public void ParseIntAndParseFloat()
    {
        Assert.Equal(255, CallStatic("parseInt", Str("ff"), Num(16)).Number);
        Assert.Equal(-12, CallStatic("parseInt", Str("  -12px")).Number);
        Assert.Equal(12, CallStatic("parseInt", Num(12.9)).Number);
        Assert.True(double.IsNegative(CallStatic("parseInt", Num(-0.5)).Number));
        Assert.True(double.IsNaN(CallStatic("parseInt", Str("1"), Num(37)).Number));
        Assert.Equal(3.14, CallStatic("parseFloat", Str("3.14abc")).Number);
        Assert.Equal(double.NegativeInfinity, CallStatic("parseFloat", Str("-Infinityx")).Number);
        Assert.False(double.IsNegative(CallStatic("parseFloat", Num(-0.0)).Number));
        Assert.Equal(123, CallStatic("parseFloat", factory.SmiToString(123)).Number);
    }

    [Fact]
    public void FormattingDifferentialAgainstOracle()
    {
        var rng = new Random(7);
        var values = new List<double> { 0, -0.0, 1, -1, 0.5, 1e21, 1e-7, 123.456, 5e-324, 1.7976931348623157e308, 0.1, 2.5, -2.5,
            1.005, 1.45, 8.345, 1e-6, 999999999999999999999.0, 4294967296, -4294967297.5, 1.0 / 3 };
        for (int i = 0; i < 150; i++)
        {
            values.Add(rng.Next(3) switch
            {
                0 => rng.NextDouble() * Math.Pow(10, rng.Next(-10, 25)) * (rng.Next(2) == 0 ? 1 : -1),
                1 => rng.Next(-100000, 100000) / 8.0,
                _ => BitConverter.Int64BitsToDouble(rng.NextInt64() & 0x7FEFFFFFFFFFFFFF),
            });
        }
        int[] digits = [0, 1, 2, 5, 10, 20, 50, 100];
        int[] radixes = [2, 3, 7, 16, 36];
        var js = new System.Text.StringBuilder("const vs = [");
        foreach (double v in values) js.Append(v == 0 && double.IsNegative(v) ? "-0" : D(v)).Append(',');
        js.Append("];\nconst out = [];\nfor (const v of vs) {\n");
        foreach (int d in digits)
        {
            js.Append($"  out.push(v.toFixed({d}), v.toExponential({d}), {(d > 0 ? $"v.toPrecision({d})" : "v.toExponential()")});\n");
        }
        foreach (int r in radixes) js.Append($"  out.push(v.toString({r}));\n");
        js.Append("}\nprint(out.join('\\n'));");
        string[] expected;
        using (var v8 = new V8Sharp.Oracle.ReferenceV8())
        {
            expected = v8.Run(js.ToString()).TrimEnd('\n').Split('\n');
        }
        var actual = new List<string>();
        foreach (double v in values)
        {
            foreach (int d in digits)
            {
                actual.Add(Proto("toFixed", v, Num(d)));
                actual.Add(Proto("toExponential", v, Num(d)));
                actual.Add(d > 0 ? Proto("toPrecision", v, Num(d)) : Proto("toExponential", v));
            }
            foreach (int r in radixes) actual.Add(Proto("toString", v, Num(r)));
        }
        Assert.Equal(expected.Length, actual.Count);
        var report = new System.Text.StringBuilder();
        int mismatches = 0;
        for (int i = 0; i < expected.Length; i++)
        {
            if (expected[i] != actual[i] && mismatches++ < 10) report.Append($"{i}: oracle {expected[i]} v8sharp {actual[i]}\n");
        }
        Assert.True(mismatches == 0, report.ToString());
    }

    [Fact]
    public void Constructor()
    {
        Assert.Equal(0, CallStatic("Number").Number);
        Assert.Equal(12, CallStatic("Number", Str(" 12 ")).Number);
        JSValue wrapper = New("Number", Str("0x10"));
        Assert.IsType<JSPrimitiveWrapper>(wrapper.Object);
        Assert.Equal(16, ((JSPrimitiveWrapper)wrapper.Object).Value.Number);
    }
}
