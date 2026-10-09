// Port of src/asmjs/asm-types.{h,cc} of V8 14.7: the asm.js type lattice.
//
// V8 encodes a value type as a tagged bitset in place of a pointer and the
// callable types as zone objects; V8Sharp has one AsmValueType instance per
// bitset (so IsExactly compares the bitsets, as V8's does) and a class per
// callable type.
namespace V8Sharp.AsmJs;

/// <summary>V8's AsmType: a value type (a bitset) or a callable type.</summary>
public abstract class AsmType
{
    // List of V(CamelName, string_name, number, parent_types)
    // (FOR_EACH_ASM_VALUE_TYPE_LIST): kAsm##CamelName = (1u << number) | parent_types.
    public const uint kAsmHeap = 1u << 1;
    public const uint kAsmFloatishDoubleQ = 1u << 2;
    public const uint kAsmFloatQDoubleQ = 1u << 3;
    public const uint kAsmVoid = 1u << 4;
    public const uint kAsmExtern = 1u << 5;
    public const uint kAsmDoubleQ = (1u << 6) | kAsmFloatishDoubleQ | kAsmFloatQDoubleQ;
    public const uint kAsmDouble = (1u << 7) | kAsmDoubleQ | kAsmExtern;
    public const uint kAsmIntish = 1u << 8;
    public const uint kAsmInt = (1u << 9) | kAsmIntish;
    public const uint kAsmSigned = (1u << 10) | kAsmInt | kAsmExtern;
    public const uint kAsmUnsigned = (1u << 11) | kAsmInt;
    public const uint kAsmFixNum = (1u << 12) | kAsmSigned | kAsmUnsigned;
    public const uint kAsmFloatish = (1u << 13) | kAsmFloatishDoubleQ;
    public const uint kAsmFloatQ = (1u << 14) | kAsmFloatQDoubleQ | kAsmFloatish;
    public const uint kAsmFloat = (1u << 15) | kAsmFloatQ;
    public const uint kAsmUint8Array = (1u << 16) | kAsmHeap;
    public const uint kAsmInt8Array = (1u << 17) | kAsmHeap;
    public const uint kAsmUint16Array = (1u << 18) | kAsmHeap;
    public const uint kAsmInt16Array = (1u << 19) | kAsmHeap;
    public const uint kAsmUint32Array = (1u << 20) | kAsmHeap;
    public const uint kAsmInt32Array = (1u << 21) | kAsmHeap;
    public const uint kAsmFloat32Array = (1u << 22) | kAsmHeap;
    public const uint kAsmFloat64Array = (1u << 23) | kAsmHeap;
    public const uint kAsmNone = 1u << 31;

    static readonly AsmValueType s_heap = new(kAsmHeap, "[]");
    static readonly AsmValueType s_floatishDoubleQ = new(kAsmFloatishDoubleQ, "floatish|double?");
    static readonly AsmValueType s_floatQDoubleQ = new(kAsmFloatQDoubleQ, "float?|double?");
    static readonly AsmValueType s_void = new(kAsmVoid, "void");
    static readonly AsmValueType s_extern = new(kAsmExtern, "extern");
    static readonly AsmValueType s_doubleQ = new(kAsmDoubleQ, "double?");
    static readonly AsmValueType s_double = new(kAsmDouble, "double");
    static readonly AsmValueType s_intish = new(kAsmIntish, "intish");
    static readonly AsmValueType s_int = new(kAsmInt, "int");
    static readonly AsmValueType s_signed = new(kAsmSigned, "signed");
    static readonly AsmValueType s_unsigned = new(kAsmUnsigned, "unsigned");
    static readonly AsmValueType s_fixNum = new(kAsmFixNum, "fixnum");
    static readonly AsmValueType s_floatish = new(kAsmFloatish, "floatish");
    static readonly AsmValueType s_floatQ = new(kAsmFloatQ, "float?");
    static readonly AsmValueType s_float = new(kAsmFloat, "float");
    static readonly AsmValueType s_uint8Array = new(kAsmUint8Array, "Uint8Array");
    static readonly AsmValueType s_int8Array = new(kAsmInt8Array, "Int8Array");
    static readonly AsmValueType s_uint16Array = new(kAsmUint16Array, "Uint16Array");
    static readonly AsmValueType s_int16Array = new(kAsmInt16Array, "Int16Array");
    static readonly AsmValueType s_uint32Array = new(kAsmUint32Array, "Uint32Array");
    static readonly AsmValueType s_int32Array = new(kAsmInt32Array, "Int32Array");
    static readonly AsmValueType s_float32Array = new(kAsmFloat32Array, "Float32Array");
    static readonly AsmValueType s_float64Array = new(kAsmFloat64Array, "Float64Array");
    static readonly AsmValueType s_none = new(kAsmNone, "<none>");

    public static AsmType Heap() => s_heap;
    public static AsmType FloatishDoubleQ() => s_floatishDoubleQ;
    public static AsmType FloatQDoubleQ() => s_floatQDoubleQ;
    public static AsmType Void() => s_void;
    public static AsmType Extern() => s_extern;
    public static AsmType DoubleQ() => s_doubleQ;
    public static AsmType Double() => s_double;
    public static AsmType Intish() => s_intish;
    public static AsmType Int() => s_int;
    public static AsmType Signed() => s_signed;
    public static AsmType Unsigned() => s_unsigned;
    public static AsmType FixNum() => s_fixNum;
    public static AsmType Floatish() => s_floatish;
    public static AsmType FloatQ() => s_floatQ;
    public static AsmType Float() => s_float;
    public static AsmType Uint8Array() => s_uint8Array;
    public static AsmType Int8Array() => s_int8Array;
    public static AsmType Uint16Array() => s_uint16Array;
    public static AsmType Int16Array() => s_int16Array;
    public static AsmType Uint32Array() => s_uint32Array;
    public static AsmType Int32Array() => s_int32Array;
    public static AsmType Float32Array() => s_float32Array;
    public static AsmType Float64Array() => s_float64Array;
    public static AsmType None() => s_none;

    public AsmValueType? AsValueType() => this as AsmValueType;
    public AsmCallableType? AsCallableType() => this as AsmCallableType;
    public AsmFunctionType? AsFunctionType() => this as AsmFunctionType;
    public AsmOverloadedFunctionType? AsOverloadedFunctionType() => this as AsmOverloadedFunctionType;

    /// <summary>
    /// A function returning <paramref name="ret"/>. Callers still need to
    /// invoke AddArgument with the returned type to fully create this type.
    /// </summary>
    public static AsmType Function(AsmType ret) => new AsmFunctionType(ret);

    /// <summary>
    /// Overloaded function types. Not creatable by asm source, but useful to
    /// represent the overloaded stdlib functions.
    /// </summary>
    public static AsmType OverloadedFunction() => new AsmOverloadedFunctionType();

    /// <summary>The type for fround(src).</summary>
    public static AsmType FroundType() => new AsmFroundType();

    /// <summary>The (variadic) type for min and max.</summary>
    public static AsmType MinMaxType(AsmType dest, AsmType src) => new AsmMinMaxType(dest, src);

    public abstract string Name();

    /// <summary>
    /// IsExactly returns true if x is the exact same type as y. For
    /// non-value types (e.g., callables), this returns x == y.
    /// </summary>
    public static bool IsExactly(AsmType? x, AsmType? y)
    {
        if (x is null) return y is null;
        if (x is AsmValueType avt)
        {
            return y is AsmValueType tavt && avt.Bitset == tavt.Bitset;
        }
        return ReferenceEquals(x, y);
    }

    /// <summary>
    /// IsA is used to query whether this is an instance of that (i.e., if this
    /// is a type derived from that.) For non-value types (e.g., callables),
    /// this returns this == that.
    /// </summary>
    public abstract bool IsA(AsmType that);

    public const int kNotHeapType = -1;

    /// <summary>Returns the element size if this is a heap type. Otherwise returns kNotHeapType.</summary>
    public int ElementSizeInBytes()
    {
        if (this is not AsmValueType value) return kNotHeapType;
        return value.Bitset switch
        {
            kAsmInt8Array or kAsmUint8Array => 1,
            kAsmInt16Array or kAsmUint16Array => 2,
            kAsmInt32Array or kAsmUint32Array or kAsmFloat32Array => 4,
            kAsmFloat64Array => 8,
            _ => kNotHeapType,
        };
    }

    /// <summary>Returns the load type if this is a heap type. AsmType::None is returned if this is not a heap type.</summary>
    public AsmType LoadType()
    {
        if (this is not AsmValueType value) return None();
        return value.Bitset switch
        {
            kAsmInt8Array or kAsmUint8Array or kAsmInt16Array or kAsmUint16Array or kAsmInt32Array or kAsmUint32Array => Intish(),
            kAsmFloat32Array => FloatQ(),
            kAsmFloat64Array => DoubleQ(),
            _ => None(),
        };
    }

    /// <summary>Returns the store type if this is a heap type. AsmType::None is returned if this is not a heap type.</summary>
    public AsmType StoreType()
    {
        if (this is not AsmValueType value) return None();
        return value.Bitset switch
        {
            kAsmInt8Array or kAsmUint8Array or kAsmInt16Array or kAsmUint16Array or kAsmInt32Array or kAsmUint32Array => Intish(),
            kAsmFloat32Array => FloatishDoubleQ(),
            kAsmFloat64Array => FloatQDoubleQ(),
            _ => None(),
        };
    }
}

/// <summary>V8's AsmValueType.</summary>
public sealed class AsmValueType : AsmType
{
    readonly string _name;

    internal AsmValueType(uint bitset, string name)
    {
        Bitset = bitset;
        _name = name;
    }

    public uint Bitset { get; }

    public override string Name() => _name;

    public override bool IsA(AsmType that) =>
        that is AsmValueType tavt && (Bitset & tavt.Bitset) == tavt.Bitset;
}

/// <summary>V8's AsmCallableType.</summary>
public abstract class AsmCallableType : AsmType
{
    public abstract bool CanBeInvokedWith(AsmType returnType, List<AsmType> args);

    public override bool IsA(AsmType other) => ReferenceEquals(other.AsCallableType(), this);
}

/// <summary>V8's AsmFunctionType.</summary>
public sealed class AsmFunctionType(AsmType returnType) : AsmCallableType
{
    readonly List<AsmType> _args = [];

    public void AddArgument(AsmType type) => _args.Add(type);
    public List<AsmType> Arguments => _args;
    public AsmType ReturnType { get; } = returnType;

    public override string Name()
    {
        var ret = new System.Text.StringBuilder("(");
        for (int ii = 0; ii < _args.Count; ++ii)
        {
            ret.Append(_args[ii].Name());
            if (ii != _args.Count - 1) ret.Append(", ");
        }
        ret.Append(") -> ");
        ret.Append(ReturnType.Name());
        return ret.ToString();
    }

    public override bool IsA(AsmType other)
    {
        if (other.AsFunctionType() is not { } that) return false;
        if (!IsExactly(ReturnType, that.ReturnType)) return false;
        if (_args.Count != that._args.Count) return false;
        for (int ii = 0; ii < _args.Count; ++ii)
        {
            if (!IsExactly(_args[ii], that._args[ii])) return false;
        }
        return true;
    }

    public override bool CanBeInvokedWith(AsmType returnType, List<AsmType> args)
    {
        if (!IsExactly(ReturnType, returnType)) return false;
        if (_args.Count != args.Count) return false;
        for (int ii = 0; ii < _args.Count; ++ii)
        {
            if (!args[ii].IsA(_args[ii])) return false;
        }
        return true;
    }
}

/// <summary>V8's AsmOverloadedFunctionType.</summary>
public sealed class AsmOverloadedFunctionType : AsmCallableType
{
    readonly List<AsmType> _overloads = [];

    public void AddOverload(AsmType overload) => _overloads.Add(overload);

    public override string Name()
    {
        var ret = new System.Text.StringBuilder();
        for (int ii = 0; ii < _overloads.Count; ++ii)
        {
            if (ii != 0) ret.Append(" /\\ ");
            ret.Append(_overloads[ii].Name());
        }
        return ret.ToString();
    }

    public override bool CanBeInvokedWith(AsmType returnType, List<AsmType> args)
    {
        foreach (AsmType overload in _overloads)
        {
            if (overload.AsCallableType()!.CanBeInvokedWith(returnType, args)) return true;
        }
        return false;
    }
}

/// <summary>asm-types.cc AsmFroundType.</summary>
sealed class AsmFroundType : AsmCallableType
{
    public override string Name() => "fround";

    public override bool CanBeInvokedWith(AsmType returnType, List<AsmType> args)
    {
        if (args.Count != 1) return false;
        AsmType arg = args[0];
        return arg.IsA(Floatish()) || arg.IsA(DoubleQ()) || arg.IsA(Signed()) || arg.IsA(Unsigned());
    }
}

/// <summary>asm-types.cc AsmMinMaxType.</summary>
sealed class AsmMinMaxType(AsmType dest, AsmType src) : AsmCallableType
{
    readonly AsmType _returnType = dest;
    readonly AsmType _arg = src;

    public override bool CanBeInvokedWith(AsmType returnType, List<AsmType> args)
    {
        if (!IsExactly(_returnType, returnType)) return false;
        if (args.Count < 2) return false;
        foreach (AsmType arg in args)
        {
            if (!arg.IsA(_arg)) return false;
        }
        return true;
    }

    public override string Name() => "(" + _arg.Name() + ", " + _arg.Name() + "...) -> " + _returnType.Name();
}
