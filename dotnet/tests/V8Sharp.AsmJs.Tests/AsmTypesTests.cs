// Port of test/unittests/asmjs/asm-types-unittest.cc of V8 14.7.
using V8Sharp.AsmJs;
using Type = V8Sharp.AsmJs.AsmType;

namespace V8Sharp.AsmJs.Tests;

public class AsmTypesTests
{
    // FOR_EACH_ASM_VALUE_TYPE_LIST: (CamelName, string_name, number).
    static readonly (Func<Type> Get, string Name, int Number)[] ValueTypes =
    [
        (Type.Heap, "[]", 1),
        (Type.FloatishDoubleQ, "floatish|double?", 2),
        (Type.FloatQDoubleQ, "float?|double?", 3),
        (Type.Void, "void", 4),
        (Type.Extern, "extern", 5),
        (Type.DoubleQ, "double?", 6),
        (Type.Double, "double", 7),
        (Type.Intish, "intish", 8),
        (Type.Int, "int", 9),
        (Type.Signed, "signed", 10),
        (Type.Unsigned, "unsigned", 11),
        (Type.FixNum, "fixnum", 12),
        (Type.Floatish, "floatish", 13),
        (Type.FloatQ, "float?", 14),
        (Type.Float, "float", 15),
        (Type.Uint8Array, "Uint8Array", 16),
        (Type.Int8Array, "Int8Array", 17),
        (Type.Uint16Array, "Uint16Array", 18),
        (Type.Int16Array, "Int16Array", 19),
        (Type.Uint32Array, "Uint32Array", 20),
        (Type.Int32Array, "Int32Array", 21),
        (Type.Float32Array, "Float32Array", 22),
        (Type.Float64Array, "Float64Array", 23),
        (Type.None, "<none>", 31),
    ];

    readonly Dictionary<Type, HashSet<Type>> parents_ = new()
    {
        [Type.Uint8Array()] = [Type.Heap()],
        [Type.Int8Array()] = [Type.Heap()],
        [Type.Uint16Array()] = [Type.Heap()],
        [Type.Int16Array()] = [Type.Heap()],
        [Type.Uint32Array()] = [Type.Heap()],
        [Type.Int32Array()] = [Type.Heap()],
        [Type.Float32Array()] = [Type.Heap()],
        [Type.Float64Array()] = [Type.Heap()],
        [Type.Float()] = [Type.FloatishDoubleQ(), Type.FloatQDoubleQ(), Type.FloatQ(), Type.Floatish()],
        [Type.Floatish()] = [Type.FloatishDoubleQ()],
        [Type.FloatQ()] = [Type.FloatishDoubleQ(), Type.FloatQDoubleQ(), Type.Floatish()],
        [Type.FixNum()] = [Type.Signed(), Type.Extern(), Type.Unsigned(), Type.Int(), Type.Intish()],
        [Type.Unsigned()] = [Type.Int(), Type.Intish()],
        [Type.Signed()] = [Type.Extern(), Type.Int(), Type.Intish()],
        [Type.Int()] = [Type.Intish()],
        [Type.DoubleQ()] = [Type.FloatishDoubleQ(), Type.FloatQDoubleQ()],
        [Type.Double()] = [Type.FloatishDoubleQ(), Type.FloatQDoubleQ(), Type.DoubleQ(), Type.Extern()],
    };

    HashSet<Type> ParentsOf(Type derived) => parents_.TryGetValue(derived, out var set) ? set : [];

    static uint Bits(Type t) => t.AsValueType()!.Bitset;

    static Type Function(Func<Type> returnType, params Func<Type>[] args)
    {
        Type ret = Type.Function(returnType());
        foreach (var arg in args) ret.AsFunctionType()!.AddArgument(arg());
        return ret;
    }

    static Type Overload(params Type[] overloads)
    {
        Type ret = Type.OverloadedFunction();
        foreach (var overload in overloads) ret.AsOverloadedFunctionType()!.AddOverload(overload);
        return ret;
    }

    static List<Type> ValueTypeList()
    {
        var list = new List<Type>();
        foreach (var v in ValueTypes) list.Add(v.Get());
        return list;
    }

    [Fact]
    public void ValidateBits()
    {
        var seenTypes = new HashSet<Type>();
        var seenNumbers = new HashSet<int>();
        int totalTypes = 0;
        foreach (var (get, name, number) in ValueTypes)
        {
            ++totalTypes;
            uint parentTypes = Bits(get()) & ~(1u << number);
            if (parentTypes != 0) Assert.True(ParentsOf(get()).Count != 0, name);
            seenTypes.Add(get());
            seenNumbers.Add(number);
            // Every ASM type must have a valid number.
            Assert.NotEqual(0, number);
            // Inheritance cycles.
            Assert.Equal(0u, (1u << number) & parentTypes);
        }
        Assert.True(totalTypes > 0);
        Assert.Equal(totalTypes, seenTypes.Count);
        Assert.Equal(totalTypes, seenNumbers.Count);
    }

    [Fact]
    public void SensibleParentsMap()
    {
        foreach (var (get, name, number) in ValueTypes)
        {
            uint parents = Bits(get()) & ~(1u << number);
            // V8 counts the bits of the tagged pointer, which carries a tag bit.
            Assert.True(System.Numerics.BitOperations.PopCount(parents) == ParentsOf(get()).Count, name);
        }
    }

    [Fact]
    public void Names()
    {
        foreach (var (get, name, _) in ValueTypes) Assert.Equal(name, get().Name());

        Assert.Equal("(double, float) -> int", Function(Type.Int, Type.Double, Type.Float).Name());
        Assert.Equal("(double, float) -> int /\\ (int) -> int",
            Overload(Function(Type.Int, Type.Double, Type.Float), Function(Type.Int, Type.Int)).Name());
        Assert.Equal("fround", Type.FroundType().Name());
        Assert.Equal("(int, int...) -> signed", Type.MinMaxType(Type.Signed(), Type.Int()).Name());
        Assert.Equal("(floatish, floatish...) -> float", Type.MinMaxType(Type.Float(), Type.Floatish()).Name());
        Assert.Equal("(double?, double?...) -> double", Type.MinMaxType(Type.Double(), Type.DoubleQ()).Name());
    }

    [Fact]
    public void IsExactly()
    {
        var testTypes = ValueTypeList();
        testTypes.AddRange([
            Function(Type.Int, Type.Double),
            Function(Type.Int, Type.DoubleQ),
            Overload(Function(Type.Int, Type.Double)),
            Function(Type.Int, Type.Int, Type.Int),
            Type.MinMaxType(Type.Signed(), Type.Int()),
            Function(Type.Int, Type.Float),
            Type.FroundType(),
        ]);
        for (int ii = 0; ii < testTypes.Count; ++ii)
        {
            for (int jj = 0; jj < testTypes.Count; ++jj)
            {
                Assert.True((ii == jj) == Type.IsExactly(testTypes[ii], testTypes[jj]),
                    testTypes[ii].Name() + (ii == jj ? " is not exactly " : " is exactly ") + testTypes[jj].Name());
            }
        }
    }

    static bool FunctionsWithSameSignature(Type a, Type b) =>
        a.AsFunctionType() is not null && b.AsFunctionType() is not null && a.IsA(b);

    [Fact]
    public void IsA()
    {
        var testTypes = ValueTypeList();
        testTypes.AddRange([
            Function(Type.Int, Type.Double),
            Function(Type.Int, Type.Int, Type.Int),
            Function(Type.Int, Type.DoubleQ),
            Overload(Function(Type.Int, Type.Double)),
            Function(Type.Int, Type.Int, Type.Int),
            Type.MinMaxType(Type.Signed(), Type.Int()),
            Function(Type.Int, Type.Float),
            Type.FroundType(),
        ]);
        for (int ii = 0; ii < testTypes.Count; ++ii)
        {
            for (int jj = 0; jj < testTypes.Count; ++jj)
            {
                bool expected = ii == jj || ParentsOf(testTypes[ii]).Contains(testTypes[jj]) ||
                    FunctionsWithSameSignature(testTypes[ii], testTypes[jj]);
                Assert.True(expected == testTypes[ii].IsA(testTypes[jj]),
                    testTypes[ii].Name() + (expected ? " is not a " : " is a ") + testTypes[jj].Name());
            }
        }

        Assert.True(Function(Type.Int, Type.Int, Type.Int).IsA(Function(Type.Int, Type.Int, Type.Int)));
        Assert.False(Function(Type.Int, Type.Int, Type.Int).IsA(Function(Type.Double, Type.Int, Type.Int)));
        Assert.False(Function(Type.Int, Type.Int, Type.Int).IsA(Function(Type.Int, Type.Double, Type.Int)));
    }

    static bool Invokable(Type callee, Type signature) =>
        callee.AsCallableType()!.CanBeInvokedWith(signature.AsFunctionType()!.ReturnType,
            signature.AsFunctionType()!.Arguments);

    [Fact]
    public void CanBeInvokedWith()
    {
        var minMaxInt = Type.MinMaxType(Type.Signed(), Type.Int());
        var i2s = Function(Type.Signed, Type.Int);
        var ii2s = Function(Type.Signed, Type.Int, Type.Int);
        var iii2s = Function(Type.Signed, Type.Int, Type.Int, Type.Int);
        var iiii2s = Function(Type.Signed, Type.Int, Type.Int, Type.Int, Type.Int);

        Assert.True(Invokable(minMaxInt, ii2s));
        Assert.True(Invokable(minMaxInt, iii2s));
        Assert.True(Invokable(minMaxInt, iiii2s));
        Assert.False(Invokable(minMaxInt, i2s));

        var minMaxDouble = Type.MinMaxType(Type.Double(), Type.Double());
        var d2d = Function(Type.Double, Type.Double);
        var dd2d = Function(Type.Double, Type.Double, Type.Double);
        var ddd2d = Function(Type.Double, Type.Double, Type.Double, Type.Double);
        var dddd2d = Function(Type.Double, Type.Double, Type.Double, Type.Double, Type.Double);
        Assert.True(Invokable(minMaxDouble, dd2d));
        Assert.True(Invokable(minMaxDouble, ddd2d));
        Assert.True(Invokable(minMaxDouble, dddd2d));
        Assert.False(Invokable(minMaxDouble, d2d));

        var minMax = Overload(minMaxInt, minMaxDouble);
        Assert.False(Invokable(minMax, i2s));
        Assert.False(Invokable(minMax, d2d));
        Assert.True(Invokable(minMax, ii2s));
        Assert.True(Invokable(minMax, iii2s));
        Assert.True(Invokable(minMax, iiii2s));
        Assert.True(Invokable(minMax, dd2d));
        Assert.True(Invokable(minMax, ddd2d));
        Assert.True(Invokable(minMax, dddd2d));

        var fround = Type.FroundType();
        foreach (var arg in new[] { Type.Floatish(), Type.FloatQ(), Type.Float(), Type.DoubleQ(), Type.Double(),
                     Type.Signed(), Type.Unsigned(), Type.FixNum() })
        {
            Assert.True(fround.AsCallableType()!.CanBeInvokedWith(Type.Float(), [arg]), arg.Name());
        }

        var idf2v = Function(Type.Void, Type.Int, Type.Double, Type.Float);
        var i2d = Function(Type.Double, Type.Int);
        var i2f = Function(Type.Float, Type.Int);
        var fi2d = Function(Type.Double, Type.Float, Type.Int);
        var idif2i = Function(Type.Int, Type.Int, Type.Double, Type.Int, Type.Float);
        var overload = Overload(idf2v, i2f, /*i2d missing, */ fi2d, idif2i);
        Assert.True(Invokable(overload, idf2v));
        Assert.True(Invokable(overload, i2f));
        Assert.True(Invokable(overload, fi2d));
        Assert.True(Invokable(overload, idif2i));
        Assert.False(Invokable(overload, i2d));
        Assert.False(Invokable(i2f, i2d));
    }

    List<Type> HeapTestTypes()
    {
        var testTypes = ValueTypeList();
        testTypes.AddRange([
            Function(Type.Int, Type.Double),
            Function(Type.Int, Type.DoubleQ),
            Overload(Function(Type.Int, Type.Double)),
            Function(Type.Int, Type.Int, Type.Int),
            Type.MinMaxType(Type.Signed(), Type.Int()),
            Function(Type.Int, Type.Float),
            Type.FroundType(),
        ]);
        return testTypes;
    }

    [Fact]
    public void ElementSizeInBytes()
    {
        static int ElementSizeInBytesForType(Type type)
        {
            if (type == Type.Int8Array() || type == Type.Uint8Array()) return 1;
            if (type == Type.Int16Array() || type == Type.Uint16Array()) return 2;
            if (type == Type.Int32Array() || type == Type.Uint32Array() || type == Type.Float32Array()) return 4;
            if (type == Type.Float64Array()) return 8;
            return -1;
        }
        foreach (var t in HeapTestTypes()) Assert.Equal(ElementSizeInBytesForType(t), t.ElementSizeInBytes());
    }

    [Fact]
    public void LoadType()
    {
        static Type LoadTypeForType(Type type)
        {
            if (type == Type.Int8Array() || type == Type.Uint8Array() || type == Type.Int16Array() ||
                type == Type.Uint16Array() || type == Type.Int32Array() || type == Type.Uint32Array())
            {
                return Type.Intish();
            }
            if (type == Type.Float32Array()) return Type.FloatQ();
            if (type == Type.Float64Array()) return Type.DoubleQ();
            return Type.None();
        }
        foreach (var t in HeapTestTypes()) Assert.Same(LoadTypeForType(t), t.LoadType());
    }

    [Fact]
    public void StoreType()
    {
        static Type StoreTypeForType(Type type)
        {
            if (type == Type.Int8Array() || type == Type.Uint8Array() || type == Type.Int16Array() ||
                type == Type.Uint16Array() || type == Type.Int32Array() || type == Type.Uint32Array())
            {
                return Type.Intish();
            }
            if (type == Type.Float32Array()) return Type.FloatishDoubleQ();
            if (type == Type.Float64Array()) return Type.FloatQDoubleQ();
            return Type.None();
        }
        foreach (var t in HeapTestTypes()) Assert.Same(StoreTypeForType(t), t.StoreType());
    }
}
