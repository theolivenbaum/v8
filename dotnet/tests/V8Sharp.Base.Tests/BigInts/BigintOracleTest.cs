// Differential tests of BigInt parsing and formatting against the real V8.

using System.Text;
using V8Sharp.Base.BigInts;
using V8Sharp.Base.Numbers;
using V8Sharp.Oracle;

namespace V8Sharp.Base.Tests.BigInts;

public class BigintOracleTest
{
    static List<string> RandomBigIntStrings(int seed, int count)
    {
        Random rng = new(seed);
        string[] pieces = [" ", "\n", " ", "+", "-", "0x", "0X", "0o", "0b", "0B", "00", "0", "1", "9", "f", "F", "z",
                           "7", "123456789", "e5", ".", "n", "_"];
        List<string> list = [];
        for (int i = 0; i < count; i++)
        {
            StringBuilder sb = new();
            int mode = rng.Next(3);
            if (mode == 0)
            {
                int n = rng.Next(1, 6);
                for (int j = 0; j < n; j++) sb.Append(pieces[rng.Next(pieces.Length)]);
            }
            else
            {
                if (rng.Next(3) == 0) sb.Append(rng.Next(2) == 0 ? "-" : "+");
                string[] prefixes = ["", "", "", "0x", "0o", "0b"];
                string prefix = prefixes[rng.Next(prefixes.Length)];
                sb.Append(prefix);
                string alphabet = prefix switch { "0x" => "0123456789abcdefABCDEF", "0o" => "01234567", "0b" => "01", _ => "0123456789" };
                int len = rng.Next(1, mode == 1 ? 30 : 600);
                for (int j = 0; j < len; j++) sb.Append(alphabet[rng.Next(alphabet.Length)]);
                if (rng.Next(6) == 0) sb.Append(pieces[rng.Next(pieces.Length)]);
                if (rng.Next(6) == 0) sb.Insert(0, "  ");
            }
            list.Add(sb.ToString());
        }
        list.AddRange(["", " ", "-", "0", "-0", "0x", "-0x1", "+0b1", "1n", "  12  ", "0o777", "0b102"]);
        return list;
    }

    [Fact]
    public void StringToBigInt()
    {
        List<string> strs = RandomBigIntStrings(1, 3000);
        Processor processor = new();
        StringBuilder js = new("const a = [");
        List<string> ours = [];
        foreach (string s in strs)
        {
            js.Append(ReferenceV8.JsQuote(s)).Append(',');
            BigIntParseStatus status = Conversions.StringToBigInt(s, out bool negative, out ulong[] digits);
            ours.Add(status switch
            {
                BigIntParseStatus.kOk => digits.Length == 0 ? "0" : processor.ToString(digits, 10, negative),
                _ => "SyntaxError",
            });
        }
        js.Append("]; print(a.map(s => { try { return String(BigInt(s)); } catch (e) { return e.name; } }).join('\\n'));");
        using ReferenceV8 v8 = new(allowNativesSyntax: false);
        string[] lines = v8.Run(js.ToString()).Split('\n');
        for (int i = 0; i < ours.Count; i++) Assert.True(lines[i] == ours[i], $"{ReferenceV8.JsQuote(strs[i])}: v8={lines[i]} ours={ours[i]}");
    }

    [Fact]
    public void BigIntToStringRadix()
    {
        Random rng = new(2);
        Processor processor = new();
        StringBuilder js = new("const a = [");
        List<string> ours = [];
        for (int i = 0; i < 1500; i++)
        {
            int len = rng.Next(1, i % 10 == 0 ? 200 : 6);
            ulong[] X = new ulong[len];
            for (int j = 0; j < len; j++) X[j] = (ulong)rng.NextInt64() ^ ((ulong)rng.Next(2) << 63);
            if (X[^1] == 0) X[^1] = 1;
            bool neg = rng.Next(2) == 0;
            int radix = rng.Next(2, 37);
            string hex = processor.ToString(X, 16, neg);
            js.Append("[").Append(neg ? "-0x" + hex[1..] : "0x" + hex).Append("n,").Append(radix).Append("],");
            ours.Add(processor.ToString(X, radix, neg));
        }
        js.Append("]; print(a.map(([x, r]) => x.toString(r)).join('\\n'));");
        using ReferenceV8 v8 = new(allowNativesSyntax: false);
        string[] lines = v8.Run(js.ToString()).Split('\n');
        for (int i = 0; i < ours.Count; i++) Assert.Equal(lines[i], ours[i]);
    }

    [Fact]
    public void BigIntLiteralToDecimal()
    {
        Assert.Equal("16", Conversions.BigIntLiteralToDecimal("0x10"));
        Assert.Equal("0", Conversions.BigIntLiteralToDecimal("0x0"));
        Assert.Equal("8", Conversions.BigIntLiteralToDecimal("0o10"));
        Assert.Equal("5", Conversions.BigIntLiteralToDecimal("0b101"));
        Assert.Equal("123456789012345678901234567890", Conversions.BigIntLiteralToDecimal("123456789012345678901234567890"));
    }
}
