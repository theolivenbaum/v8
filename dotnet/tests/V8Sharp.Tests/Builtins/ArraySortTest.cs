// Tests of Array.prototype.sort / toSorted (third_party/v8/builtins/array-sort.tq,
// src/builtins/array-to-sorted.tq). The comparison order is observable through
// the comparefn. The oracle (V8 14.7) still sorts with TimSort, so the traces
// cannot be compared with it; they are checked against the algorithm of the
// ported tree instead.
using V8Sharp.Objects;

namespace V8Sharp.Tests.Builtins;

public class ArraySortTest : BuiltinsTestBase
{
    static double[] Data(int n, double seed)
    {
        var a = new double[n];
        double x = seed;
        for (int i = 0; i < n; i++)
        {
            x = (x * 1103515245 + 12345) % 2147483648;
            a[i] = x % 1000;
        }
        return a;
    }

    string Run(double[] values)
    {
        uint h = 2166136261;
        int c = 0;
        JSFunction compare = Fn((_, args) =>
        {
            int p = (int)args[0].Number;
            int q = (int)args[1].Number;
            h = unchecked((uint)((int)h ^ p) * 16777619u);
            h = unchecked((uint)((int)h ^ q) * 16777619u);
            c++;
            return N(p - q);
        }, 2);
        JSArray array = Arr(values);
        JSValue sorted = Invoke(array, "sort", compare);
        Assert.True(ReferenceEquals(sorted.HeapObjectOrNull, array));
        var first = new string[Math.Min(5, values.Length)];
        for (uint i = 0; i < first.Length; i++) first[i] = NumberToJSString(Num(Get(sorted, i)));
        return c + ":" + h + ":" + string.Join(",", first);
    }

    [Fact]
    public void BinaryInsertionSortComparisonOrder()
    {
        // Below kMaxInlineSortLength the work array is sorted by BinaryInsertionSort
        // directly. The trace is derived by hand from array-sort.tq. The oracle's
        // V8 predates the kMaxInlineSortLength shortcut and counts a run first
        // ("600/590 792/600 912/792 56/912 ..."); from the fifth call on the
        // traces agree.
        var log = new List<string>();
        JSFunction compare = Fn((_, args) =>
        {
            log.Add(args[0].Number + "/" + args[1].Number);
            return N(args[0].Number - args[1].Number);
        }, 2);
        Invoke(Arr(Data(10, 1)), "sort", compare);
        Assert.Equal("600/590 792/600 912/600 912/792 56/792 56/600 56/590 584/600 584/590 584/56 472/600 472/584 " +
                     "472/56 472/590 472/472 472/584 552/590 552/472 552/584 808/584 808/792 808/912", string.Join(" ", log));
    }

    [Fact]
    public void PowerSortRuns()
    {
        // Natural runs, galloping and several merge levels. The comparison counts
        // are those of the PowerSort port (V8 14.7, the oracle, still ran TimSort).
        Assert.Equal("22:1359960203:56,472,472,552,584", Run(Data(10, 1)));
        var g = new double[300];
        for (int i = 0; i < 300; i++) g[i] = i % 97 < 50 ? i : 1000 - i;
        Assert.EndsWith(":0,1,2,3,4", Run(g));
        var big = new double[2500];
        Data(2000, 3).CopyTo(big, 0);
        for (int i = 0; i < 500; i++) big[2000 + i] = i;
        Assert.EndsWith(":0,0,0,0,0", Run(big));
    }

    [Fact]
    public void Stable()
    {
        // Sort 600 {k, s} objects by k only; equal keys keep their order.
        double[] keys = Data(600, 5);
        var values = new JSValue[keys.Length];
        for (int i = 0; i < keys.Length; i++)
        {
            JSObject o = Obj();
            Set(o, "k", N(keys[i] % 10));
            Set(o, "s", N(i));
            values[i] = o;
        }
        JSArray array = Arr(values);
        Invoke(array, "sort", Fn((_, args) => N(Num(Get(args[0], "k")) - Num(Get(args[1], "k"))), 2));
        for (uint i = 1; i < keys.Length; i++)
        {
            double k0 = Num(Get(Get(array, i - 1), "k"));
            double k1 = Num(Get(Get(array, i), "k"));
            Assert.True(k0 <= k1);
            if (k0 == k1) Assert.True(Num(Get(Get(array, i - 1), "s")) < Num(Get(Get(array, i), "s")));
        }
    }

    [Fact]
    public void SortsSortedOutput()
    {
        double[] values = Data(1000, 11);
        JSArray array = Arr(values);
        Invoke(array, "sort", Fn((_, args) => N(args[0].Number - args[1].Number), 2));
        Array.Sort(values);
        for (uint i = 0; i < values.Length; i++) Assert.Equal(values[i], Num(Get(array, i)));
    }

    [Fact]
    public void DefaultComparison()
    {
        JSArray array = Arr(N(10), N(9), N(1), N(100), N(-1), N(-20), N(3), N(2), N(21), Undefined);
        Set(array, 11, S("a"));
        Set(array, 12, S("B"));
        Set(array, 13, JSValue.True);
        Set(array, 14, JSValue.Null);
        Invoke(array, "sort");
        Assert.Equal("-1,-20,1,10,100,2,21,3,9,B,a,null,true,undefined,undefined", Elements(array));
        // The hole ends up at the end and stays a hole.
        Assert.False(JSReceiver.HasElement(i_isolate, array, 14));
    }

    [Fact]
    public void SmiLexicographicCompare()
    {
        Assert.Equal(0, V8Sharp.Builtins.BuiltinsArray.SmiLexicographicCompare(5, 5));
        Assert.Equal(-1, V8Sharp.Builtins.BuiltinsArray.SmiLexicographicCompare(10, 9));
        Assert.Equal(-1, V8Sharp.Builtins.BuiltinsArray.SmiLexicographicCompare(1, 10));
        Assert.Equal(1, V8Sharp.Builtins.BuiltinsArray.SmiLexicographicCompare(9, 1000000000));
        Assert.Equal(-1, V8Sharp.Builtins.BuiltinsArray.SmiLexicographicCompare(-1, -20));
        Assert.Equal(-1, V8Sharp.Builtins.BuiltinsArray.SmiLexicographicCompare(-5, 3));
        Assert.Equal(-1, V8Sharp.Builtins.BuiltinsArray.SmiLexicographicCompare(0, 1));
        Assert.Equal(1, V8Sharp.Builtins.BuiltinsArray.SmiLexicographicCompare(0, -1));
    }

    [Fact]
    public void ToSortedFillsHoles()
    {
        JSArray t = Arr(3, 1, 2);
        Set(t, "length", N(5));
        JSValue copy = Invoke(t, "toSorted");
        Assert.Equal("1,2,3,undefined,undefined", Elements(copy));
        Assert.True(JSReceiver.HasElement(i_isolate, copy.As<JSReceiver>(), 4));
        Invoke(t, "sort");
        Assert.Equal("1,2,3,undefined,undefined", Elements(t));
        Assert.False(JSReceiver.HasElement(i_isolate, t, 3));
    }

    [Fact]
    public void Errors()
    {
        Assert.Equal("TypeError: The comparison function must be either a function or undefined: 3",
            Throws(() => Invoke(Arr(1), "sort", N(3))));
        Assert.Equal("1,4,5", Elements(Invoke(Arr(5, 1, 4), "sort", Undefined)));
        Assert.Equal("1,2,3", Elements(Invoke(Arr(1, 2, 3), "sort", Fn((_, _) => N(double.NaN)))));
        JSObject arrayLike = Obj();
        Set(arrayLike, "length", N(Math.Pow(2, 32)));
        Assert.Equal("RangeError: Invalid array length",
            Throws(() => Call(Get(Get(Global("Array"), "prototype"), "toSorted"), arrayLike)));
    }

    [Fact]
    public void GenericReceiver()
    {
        JSObject o = Obj();
        Set(o, 0, S("c"));
        Set(o, 2, S("a"));
        Set(o, 3, Undefined);
        Set(o, "length", N(5));
        Call(Get(Get(Global("Array"), "prototype"), "sort"), o);
        Assert.Equal("a", Str(Get(o, 0)));
        Assert.Equal("c", Str(Get(o, 1)));
        Assert.True(Get(o, 2).IsUndefined);
        Assert.True(JSReceiver.HasElement(i_isolate, o, 2));
        Assert.False(JSReceiver.HasElement(i_isolate, o, 3));
    }
}
