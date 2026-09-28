// Tests of the Math builtins (src/builtins/math.tq, builtins-math.cc).
namespace V8Sharp.Tests.Builtins;

public class MathBuiltinsTest : BuiltinsTestBase
{
    double M(string name, params double[] args)
    {
        var values = new JSValue[args.Length];
        for (int i = 0; i < args.Length; i++) values[i] = Num(args[i]);
        return CallStatic("Math." + name, values).Number;
    }

    [Fact]
    public void Rounding()
    {
        Assert.Equal(3, M("round", 2.5));
        Assert.Equal(-2, M("round", -2.5));
        Assert.True(double.IsNegative(M("round", -0.3)));
        Assert.Equal(0, M("round", 0.49999999999999994));
        Assert.Equal(-1, M("floor", -0.5));
        Assert.True(double.IsNegative(M("ceil", -0.5)));
        Assert.Equal(-4, M("trunc", -4.7));
        Assert.Equal(1073741824, M("abs", -1073741824));
        Assert.Equal(-1, M("sign", -3));
        Assert.True(double.IsNegative(M("sign", -0.0)));
    }

    [Fact]
    public void MaxMin()
    {
        Assert.Equal(double.NegativeInfinity, M("max"));
        Assert.Equal(double.PositiveInfinity, M("min"));
        Assert.Equal(3, M("max", 1, 3, 2));
        Assert.True(double.IsNaN(M("max", 1, double.NaN, 2)));
        Assert.False(double.IsNegative(M("max", -0.0, 0.0)));
        Assert.True(double.IsNegative(M("min", 0.0, -0.0)));
    }

    [Fact]
    public void IntegerOps()
    {
        Assert.Equal(32, M("clz32", 0));
        Assert.Equal(31, M("clz32", 1));
        Assert.Equal(0, M("clz32", -1));
        Assert.Equal(-5, M("imul", 0xffffffff, 5));
        Assert.Equal(1.100000023841858, M("fround", 1.1));
        Assert.Equal(1.099609375, M("f16round", 1.1));
        Assert.Equal(double.PositiveInfinity, M("f16round", 65520));
        Assert.Equal(65504, M("f16round", 65519.99));
    }

    [Fact]
    public void Transcendental()
    {
        Assert.Equal(Math.PI, M("atan2", 0, -1));
        Assert.Equal(0, M("sin", 0));
        Assert.Equal(1024, M("pow", 2, 10));
        Assert.True(double.IsNaN(M("pow", 1, double.PositiveInfinity)));
        Assert.Equal(3, M("cbrt", 27));
        Assert.Equal(2, M("log2", 4));
        Assert.Equal(3, M("sqrt", 9));
    }

    [Fact]
    public void Hypot()
    {
        Assert.Equal(0, M("hypot"));
        Assert.Equal(3, M("hypot", -3));
        Assert.Equal(5, M("hypot", 3, 4));
        Assert.Equal(double.PositiveInfinity, M("hypot", double.NaN, double.NegativeInfinity));
        Assert.True(double.IsNaN(M("hypot", double.NaN, 1)));
        Assert.Equal(7, M("hypot", 2, 3, 6));
        Assert.Equal(Math.Sqrt(4 * 9.0), M("hypot", 3, 3, 3, 3));
        Assert.Equal(double.PositiveInfinity, M("hypot", 1, 2, 3, double.NaN, double.PositiveInfinity));
    }

    [Fact]
    public void Random()
    {
        for (int i = 0; i < 1000; i++)
        {
            double r = M("random");
            Assert.InRange(r, 0.0, 0.9999999999999999);
        }
    }

    [Fact]
    public void RandomSeedIsDeterministic()
    {
        var flags = new FlagList();
        flags.random_seed = 42;
        Isolate a = Isolate.New(flags);
        double[] first = new double[5];
        using (a.Enter())
        {
            for (int i = 0; i < 5; i++) first[i] = Execution.Call(a, Get(a.NativeContext.GlobalObject, "Math.random"), JSValue.Undefined, []).Number;
        }
        Isolate b = Isolate.New(flags);
        using (b.Enter())
        {
            for (int i = 0; i < 5; i++) Assert.Equal(first[i], Execution.Call(b, Get(b.NativeContext.GlobalObject, "Math.random"), JSValue.Undefined, []).Number);
        }

        JSValue Get(JSValue obj, string path)
        {
            Isolate isolate = Isolate.Current!;
            foreach (string part in path.Split('.')) obj = ObjectOps.GetProperty(isolate, obj, isolate.Factory.InternalizeString(part));
            return obj;
        }
    }

    [Fact]
    public void SumPrecise()
    {
        JSValue Arr(params double[] values)
        {
            JSArray a = factory.NewJSArray(ElementsKind.PACKED_DOUBLE_ELEMENTS, values.Length, values.Length);
            for (int i = 0; i < values.Length; i++) ObjectOps.SetElement(i_isolate, a, (uint)i, Num(values[i]), ShouldThrow.ThrowOnError);
            return a;
        }
        double Sum(params double[] values) => CallStatic("Math.sumPrecise", Arr(values)).Number;

        Assert.True(double.IsNegative(Sum()));
        Assert.True(double.IsNegative(Sum(-0.0, -0.0)));
        Assert.Equal(0.30000000000000004, 0.1 + 0.2);
        Assert.Equal(0.30000000000000004, Sum(0.1, 0.2));
        Assert.Equal(1, Sum(1e308, 1, -1e308));
        Assert.Equal(6, Sum(1, 2, 3));
        Assert.Equal(double.PositiveInfinity, Sum(1e308, 1e308));
        Assert.True(double.IsNaN(Sum(double.PositiveInfinity, double.NegativeInfinity)));
        Assert.Equal(1e-323, Sum(5e-324, 5e-324));
        Assert.Equal("TypeError: Math.sumPrecise called on null or undefined", Throws(() => CallStatic("Math.sumPrecise")));
    }
}
