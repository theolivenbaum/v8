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
    public void Constructor()
    {
        Assert.Equal(0, CallStatic("Number").Number);
        Assert.Equal(12, CallStatic("Number", Str(" 12 ")).Number);
        JSValue wrapper = New("Number", Str("0x10"));
        Assert.IsType<JSPrimitiveWrapper>(wrapper.Object);
        Assert.Equal(16, ((JSPrimitiveWrapper)wrapper.Object).Value.Number);
    }
}
