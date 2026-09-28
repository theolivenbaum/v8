// Tests of the BigInt builtins and of src/objects/bigint.cc, plus a
// differential test against the oracle on random big values.
using System.Globalization;
using System.Text;
using V8Sharp.Oracle;
using Op = V8Sharp.Builtins.BuiltinsBigInt.Operation;

namespace V8Sharp.Tests.Builtins;

public class BigIntBuiltinsTest : BuiltinsTestBase
{
    BigInt B(string s) => s.StartsWith("-0x", StringComparison.Ordinal) ? BigInt.BigIntLiteral(i_isolate, s) : BigInt.FromObject(i_isolate, Str(s));

    string Dec(BigInt b) => BigInt.ToString(i_isolate, b).ToCString();

    [Fact]
    public void ConstructorAndToString()
    {
        Assert.Equal("123", S(CallStatic("BigInt", Num(123))));
        Assert.Equal("-9007199254740993", Dec(B("-9007199254740993")));
        Assert.Equal("18446744073709551616", Dec((BigInt)CallStatic("BigInt", Num(18446744073709551616.0)).Object));
        Assert.Equal("255", Dec(B("0xff")));
        Assert.Equal("0", Dec(B("  ")));
        Assert.Equal("RangeError: The number 1.5 cannot be converted to a BigInt because it is not an integer",
            Throws(() => CallStatic("BigInt", Num(1.5))));
        Assert.Equal("SyntaxError: Cannot convert 1x to a BigInt", Throws(() => CallStatic("BigInt", Str("1x"))));
        Assert.Equal("SyntaxError: Cannot convert -0x1 to a BigInt", Throws(() => CallStatic("BigInt", Str("-0x1"))));
        Assert.Equal("TypeError: BigInt is not a constructor", Throws(() => New("BigInt", Num(1))));
        Assert.Equal("ff", S(Call("BigInt.prototype.toString", B("255"), Num(16))));
        Assert.Equal("-1010", S(Call("BigInt.prototype.toString", B("-10"), Num(2))));
        Assert.Equal("12", S(Call("BigInt.prototype.toLocaleString", B("12"))));
        Assert.Equal("TypeError: BigInt.prototype.valueOf requires that 'this' be a BigInt",
            Throws(() => Call("BigInt.prototype.valueOf", Num(1))));
    }

    [Fact]
    public void AsIntNAndAsUintN()
    {
        Assert.Equal("-1", Dec((BigInt)CallStatic("BigInt.asIntN", Num(8), B("255")).Object));
        Assert.Equal("255", Dec((BigInt)CallStatic("BigInt.asUintN", Num(8), B("-1")).Object));
        Assert.Equal("18446744073709551615", Dec((BigInt)CallStatic("BigInt.asUintN", Num(64), B("-1")).Object));
        Assert.Equal("0", Dec((BigInt)CallStatic("BigInt.asIntN", Num(0), B("5")).Object));
        Assert.Equal("RangeError: Invalid value: not (convertible to) a safe integer",
            Throws(() => CallStatic("BigInt.asIntN", Num(-1), B("5"))));
    }

    [Fact]
    public void Operators()
    {
        string Bin(string a, string b, Op op) => Dec(BuiltinsBigInt.BinaryOp(i_isolate, B(a), B(b), op));
        Assert.Equal("0", Bin("5", "-5", Op.kAdd));
        Assert.Equal("-3", Bin("-7", "2", Op.kDivide));
        Assert.Equal("-1", Bin("-7", "2", Op.kModulus));
        Assert.Equal("1267650600228229401496703205376", Bin("2", "100", Op.kExponentiate));
        Assert.Equal("-8", Bin("-2", "3", Op.kExponentiate));
        Assert.Equal("-4", Bin("-7", "1", Op.kShiftRight));
        Assert.Equal("-1", Bin("-1", "1000", Op.kShiftRight));
        Assert.Equal("-14", Bin("-7", "1", Op.kShiftLeft));
        Assert.Equal("-8", Bin("-7", "-8", Op.kBitwiseAnd));
        Assert.Equal("RangeError: Division by zero", Throws(() => BuiltinsBigInt.BinaryOp(i_isolate, B("1"), B("0"), Op.kDivide)));
        Assert.Equal("RangeError: Maximum BigInt size exceeded",
            Throws(() => BuiltinsBigInt.BinaryOp(i_isolate, B("1"), B("1073741824"), Op.kShiftLeft)));
        Assert.Equal("TypeError: Cannot mix BigInt and other types, use explicit conversions",
            Throws(() => BuiltinsBigInt.BinaryOp(i_isolate, B("1"), Num(1), Op.kAdd)));
        Assert.Equal("TypeError: BigInts have no unsigned right shift, use >> instead",
            Throws(() => BuiltinsBigInt.BinaryOp(i_isolate, B("1"), B("1"), Op.kShiftRightLogical)));
        Assert.Equal("RangeError: Exponent must be positive",
            Throws(() => BuiltinsBigInt.BinaryOp(i_isolate, B("1"), B("-1"), Op.kExponentiate)));
        Assert.Equal("-1", Dec(BuiltinsBigInt.UnaryOp(i_isolate, B("0"), Op.kDecrement)));
        Assert.Equal("-6", Dec(BuiltinsBigInt.UnaryOp(i_isolate, B("5"), Op.kBitwiseNot)));
        Assert.Equal("4", Dec(BuiltinsBigInt.UnaryOp(i_isolate, B("-5"), Op.kBitwiseNot)));
        Assert.Equal("18446744073709551616", Dec(BuiltinsBigInt.UnaryOp(i_isolate, B("18446744073709551615"), Op.kIncrement)));
    }

    // Port of test/unittests/numbers/bigint-unittest.cc.
    void Compare(BigInt x, double value, ComparisonResult expected) =>
        Assert.Equal(expected, BigInt.CompareToDouble(x, value));

    BigInt NewFromInt(int value) => BigInt.FromNumber(i_isolate, JSValue.FromInt(value));

    BigInt BigIntLiteral(string s) => BigInt.BigIntLiteral(i_isolate, s);

    [Fact]
    public void CompareToDouble()
    {
        BigInt zero = NewFromInt(0);
        BigInt one = NewFromInt(1);
        BigInt minus_one = NewFromInt(-1);

        // Non-finite doubles.
        Compare(zero, double.NaN, ComparisonResult.Undefined);
        Compare(one, double.PositiveInfinity, ComparisonResult.LessThan);
        Compare(one, double.NegativeInfinity, ComparisonResult.GreaterThan);

        // Unequal sign.
        Compare(one, -1, ComparisonResult.GreaterThan);
        Compare(minus_one, 1, ComparisonResult.LessThan);

        // Cases involving zero.
        Compare(zero, 0, ComparisonResult.Equal);
        Compare(zero, -0.0, ComparisonResult.Equal);
        Compare(one, 0, ComparisonResult.GreaterThan);
        Compare(minus_one, 0, ComparisonResult.LessThan);
        Compare(zero, 1, ComparisonResult.LessThan);
        Compare(zero, -1, ComparisonResult.GreaterThan);

        // Small doubles.
        Compare(zero, 0.25, ComparisonResult.LessThan);
        Compare(one, 0.5, ComparisonResult.GreaterThan);
        Compare(one, -0.5, ComparisonResult.GreaterThan);
        Compare(zero, -0.25, ComparisonResult.GreaterThan);
        Compare(minus_one, -0.5, ComparisonResult.LessThan);

        // Different bit lengths.
        BigInt four = NewFromInt(4);
        BigInt minus_five = NewFromInt(-5);
        Compare(four, 3.9, ComparisonResult.GreaterThan);
        Compare(four, 1.5, ComparisonResult.GreaterThan);
        Compare(four, 8, ComparisonResult.LessThan);
        Compare(four, 16, ComparisonResult.LessThan);
        Compare(minus_five, -4.9, ComparisonResult.LessThan);
        Compare(minus_five, -4, ComparisonResult.LessThan);
        Compare(minus_five, -25, ComparisonResult.GreaterThan);

        // Same bit length, difference in first digit.
        double big_double = 4428155326412785451008.0;
        BigInt big = BigIntLiteral("0xF10D00000000000000");
        Compare(big, big_double, ComparisonResult.GreaterThan);
        big = BigIntLiteral("0xE00D00000000000000");
        Compare(big, big_double, ComparisonResult.LessThan);

        double other_double = -13758438578910658560.0;
        BigInt other = BigIntLiteral("-0xBEEFC1FE00000000");
        Compare(other, other_double, ComparisonResult.GreaterThan);
        other = BigIntLiteral("-0xBEEFCBFE00000000");
        Compare(other, other_double, ComparisonResult.LessThan);

        // Same bit length, difference in non-first digit.
        big = BigIntLiteral("0xF00D00000000000001");
        Compare(big, big_double, ComparisonResult.GreaterThan);
        big = BigIntLiteral("0xF00A00000000000000");
        Compare(big, big_double, ComparisonResult.LessThan);

        other = BigIntLiteral("-0xBEEFCAFE00000001");
        Compare(other, other_double, ComparisonResult.LessThan);

        // Same bit length, difference in fractional part.
        Compare(one, 1.5, ComparisonResult.LessThan);
        Compare(minus_one, -1.25, ComparisonResult.GreaterThan);
        big = NewFromInt(0xF00D00);
        Compare(big, 15731968.125, ComparisonResult.LessThan);
        Compare(big, 15731967.875, ComparisonResult.GreaterThan);
        big = BigIntLiteral("0x123456789AB");
        Compare(big, 1250999896491.125, ComparisonResult.LessThan);

        // Equality!
        Compare(one, 1, ComparisonResult.Equal);
        Compare(minus_one, -1, ComparisonResult.Equal);
        big = BigIntLiteral("0xF00D00000000000000");
        Compare(big, big_double, ComparisonResult.Equal);

        BigInt two_52 = BigIntLiteral("0x10000000000000");
        Compare(two_52, 4503599627370496.0, ComparisonResult.Equal);
    }

    [Fact]
    public void ToNumberRounding()
    {
        Assert.Equal(9007199254740992.0, BigInt.ToNumber(i_isolate, B("9007199254740993")).Number);  // tie to even
        Assert.Equal(9007199254740996.0, BigInt.ToNumber(i_isolate, B("9007199254740995")).Number);
        Assert.Equal(double.PositiveInfinity, BigInt.ToNumber(i_isolate, B("0x1" + new string('0', 256))).Number);
        Assert.Equal(-1.7976931348623157e308, BigInt.ToNumber(i_isolate, B("-0xfffffffffffff8" + new string('0', 242))).Number);
        Assert.Equal(12, CallStatic("Number", B("12")).Number);
    }

    // Random BigInts through every operator, compared with the oracle.
    [Fact]
    public void DifferentialAgainstOracle()
    {
        var rng = new Random(12345);
        var values = new List<string>();
        values.AddRange(["0", "1", "-1", "2", "-2", "18446744073709551615", "-18446744073709551616", "9007199254740993"]);
        int[] digitCounts = [1, 2, 3, 5, 8, 20, 40, 80, 150, 300, 800];
        foreach (int digits in digitCounts)
        {
            for (int k = 0; k < 4; k++)
            {
                var sb = new StringBuilder();
                if (rng.Next(2) == 0) sb.Append('-');
                sb.Append("0x");
                for (int d = 0; d < digits; d++) sb.Append(rng.NextInt64().ToString("x16", CultureInfo.InvariantCulture));
                values.Add(sb.ToString());
            }
        }

        var js = new StringBuilder("const v = [");
        foreach (string v in values) js.Append(v.StartsWith('-') ? "-" + v[1..] + "n," : v + "n,");
        js.Append("""
            ];
            const out = [];
            for (let i = 0; i < v.length; i++) {
              const x = v[i];
              out.push(x.toString(), x.toString(16), x.toString(7), x.toString(36), String(Number(x)),
                       BigInt.asIntN(65, x).toString(), BigInt.asUintN(100, x).toString(), (~x).toString(), (-x).toString());
              for (let j = 0; j < v.length; j += 3) {
                const y = v[j];
                out.push((x + y).toString(), (x - y).toString(), (x * y).toString(),
                         y === 0n ? "div0" : (x / y).toString(), y === 0n ? "div0" : (x % y).toString(),
                         (x & y).toString(), (x | y).toString(), (x ^ y).toString(), String(x < y), String(x == Number(y)));
              }
              out.push((x << 67n).toString(), (x >> 67n).toString(), (x >> -3n).toString(), (x ** 3n).toString());
            }
            print(out.join("\n"));
            """);
        string oracle;
        using (var v8 = new ReferenceV8())
        {
            oracle = v8.Run(js.ToString());
        }
        string[] expected = oracle.TrimEnd('\n').Split('\n');

        var bigints = new List<BigInt>();
        foreach (string v in values) bigints.Add(B(v));
        var actual = new List<string>();
        string Bin(BigInt a, BigInt b, Op op) => Dec(BuiltinsBigInt.BinaryOp(i_isolate, a, b, op));
        for (int i = 0; i < bigints.Count; i++)
        {
            BigInt x = bigints[i];
            actual.Add(Dec(x));
            actual.Add(BigInt.ToString(i_isolate, x, 16).ToCString());
            actual.Add(BigInt.ToString(i_isolate, x, 7).ToCString());
            actual.Add(BigInt.ToString(i_isolate, x, 36).ToCString());
            actual.Add(S(BigInt.ToNumber(i_isolate, x)));
            actual.Add(Dec(BigInt.AsIntN(i_isolate, 65, x)));
            actual.Add(Dec(BigInt.AsUintN(i_isolate, 100, x)));
            actual.Add(Dec(BuiltinsBigInt.UnaryOp(i_isolate, x, Op.kBitwiseNot)));
            actual.Add(Dec(BuiltinsBigInt.UnaryOp(i_isolate, x, Op.kNegate)));
            for (int j = 0; j < bigints.Count; j += 3)
            {
                BigInt y = bigints[j];
                actual.Add(Bin(x, y, Op.kAdd));
                actual.Add(Bin(x, y, Op.kSubtract));
                actual.Add(Bin(x, y, Op.kMultiply));
                actual.Add(y.IsZero ? "div0" : Bin(x, y, Op.kDivide));
                actual.Add(y.IsZero ? "div0" : Bin(x, y, Op.kModulus));
                actual.Add(Bin(x, y, Op.kBitwiseAnd));
                actual.Add(Bin(x, y, Op.kBitwiseOr));
                actual.Add(Bin(x, y, Op.kBitwiseXor));
                actual.Add(BigInt.CompareToBigInt(x, y) == ComparisonResult.LessThan ? "true" : "false");
                actual.Add(BigInt.EqualToNumber(x, BigInt.ToNumber(i_isolate, y)) ? "true" : "false");
            }
            actual.Add(Bin(x, B("67"), Op.kShiftLeft));
            actual.Add(Bin(x, B("67"), Op.kShiftRight));
            actual.Add(Bin(x, B("-3"), Op.kShiftRight));
            actual.Add(Bin(x, B("3"), Op.kExponentiate));
        }

        Assert.Equal(expected.Length, actual.Count);
        int mismatches = 0;
        var report = new StringBuilder();
        for (int i = 0; i < expected.Length; i++)
        {
            if (expected[i] != actual[i] && mismatches++ < 10) report.Append(i).Append(": ").Append(expected[i]).Append(" != ").Append(actual[i]).Append('\n');
        }
        Assert.True(mismatches == 0, report.ToString());
    }
}
