// Port of src/asmjs/asm-names.h of V8 14.7: the standard library members,
// keywords and multi-character tokens the asm.js scanner knows, and the
// token numbering of AsmJsScanner (asm-scanner.h): local identifiers count
// down from kLocalsStart, the builtin tokens follow it, single characters are
// their own code, global identifiers count up from kGlobalsStart.
namespace V8Sharp.AsmJs;

/// <summary>The token ids of AsmJsScanner (asm-scanner.h's enum).</summary>
public static class AsmToken
{
    public const int kLocalsStart = -10000;
    public const int kToken_min = -9999;
    public const int kToken_max = -9998;
    public const int kToken_abs = -9997;
    public const int kToken_fround = -9996;
    public const int kToken_acos = -9995;
    public const int kToken_asin = -9994;
    public const int kToken_atan = -9993;
    public const int kToken_cos = -9992;
    public const int kToken_sin = -9991;
    public const int kToken_tan = -9990;
    public const int kToken_exp = -9989;
    public const int kToken_log = -9988;
    public const int kToken_atan2 = -9987;
    public const int kToken_pow = -9986;
    public const int kToken_imul = -9985;
    public const int kToken_clz32 = -9984;
    public const int kToken_ceil = -9983;
    public const int kToken_floor = -9982;
    public const int kToken_sqrt = -9981;
    public const int kToken_Int8Array = -9980;
    public const int kToken_Uint8Array = -9979;
    public const int kToken_Int16Array = -9978;
    public const int kToken_Uint16Array = -9977;
    public const int kToken_Int32Array = -9976;
    public const int kToken_Uint32Array = -9975;
    public const int kToken_Float32Array = -9974;
    public const int kToken_Float64Array = -9973;
    public const int kToken_E = -9972;
    public const int kToken_LN10 = -9971;
    public const int kToken_LN2 = -9970;
    public const int kToken_LOG2E = -9969;
    public const int kToken_LOG10E = -9968;
    public const int kToken_PI = -9967;
    public const int kToken_SQRT1_2 = -9966;
    public const int kToken_SQRT2 = -9965;
    public const int kToken_Infinity = -9964;
    public const int kToken_NaN = -9963;
    public const int kToken_Math = -9962;
    public const int kToken_arguments = -9961;
    public const int kToken_break = -9960;
    public const int kToken_case = -9959;
    public const int kToken_const = -9958;
    public const int kToken_continue = -9957;
    public const int kToken_default = -9956;
    public const int kToken_do = -9955;
    public const int kToken_else = -9954;
    public const int kToken_eval = -9953;
    public const int kToken_for = -9952;
    public const int kToken_function = -9951;
    public const int kToken_if = -9950;
    public const int kToken_new = -9949;
    public const int kToken_return = -9948;
    public const int kToken_switch = -9947;
    public const int kToken_var = -9946;
    public const int kToken_while = -9945;
    public const int kToken_LE = -9944;
    public const int kToken_GE = -9943;
    public const int kToken_EQ = -9942;
    public const int kToken_NE = -9941;
    public const int kToken_SHL = -9940;
    public const int kToken_SAR = -9939;
    public const int kToken_SHR = -9938;
    public const int kToken_UseAsm = -9937;
    public const int kUninitialized = 0;
    public const int kEndOfInput = -1;
    public const int kParseError = -2;
    public const int kUnsigned = -3;
    public const int kDouble = -4;
    public const int kGlobalsStart = 256;
}

/// <summary>The lists of asm-names.h.</summary>
public static class AsmNames
{
    /// <summary>STDLIB_MATH_VALUE_LIST: (name, token, value).</summary>
    public static readonly (string Name, int Token, double Value)[] StdlibMathValues =
    [
        ("E", AsmToken.kToken_E, 2.718281828459045),
        ("LN10", AsmToken.kToken_LN10, 2.302585092994046),
        ("LN2", AsmToken.kToken_LN2, 0.6931471805599453),
        ("LOG2E", AsmToken.kToken_LOG2E, 1.4426950408889634),
        ("LOG10E", AsmToken.kToken_LOG10E, 0.4342944819032518),
        ("PI", AsmToken.kToken_PI, 3.141592653589793),
        ("SQRT1_2", AsmToken.kToken_SQRT1_2, 0.7071067811865476),
        ("SQRT2", AsmToken.kToken_SQRT2, 1.4142135623730951),
    ];

    /// <summary>STDLIB_MATH_FUNCTION_LIST: (name, token).</summary>
    public static readonly (string Name, int Token)[] StdlibMathFunctions =
    [
        ("min", AsmToken.kToken_min),
        ("max", AsmToken.kToken_max),
        ("abs", AsmToken.kToken_abs),
        ("fround", AsmToken.kToken_fround),
        ("acos", AsmToken.kToken_acos),
        ("asin", AsmToken.kToken_asin),
        ("atan", AsmToken.kToken_atan),
        ("cos", AsmToken.kToken_cos),
        ("sin", AsmToken.kToken_sin),
        ("tan", AsmToken.kToken_tan),
        ("exp", AsmToken.kToken_exp),
        ("log", AsmToken.kToken_log),
        ("atan2", AsmToken.kToken_atan2),
        ("pow", AsmToken.kToken_pow),
        ("imul", AsmToken.kToken_imul),
        ("clz32", AsmToken.kToken_clz32),
        ("ceil", AsmToken.kToken_ceil),
        ("floor", AsmToken.kToken_floor),
        ("sqrt", AsmToken.kToken_sqrt),
    ];

    /// <summary>STDLIB_ARRAY_TYPE_LIST: (name, token).</summary>
    public static readonly (string Name, int Token)[] StdlibArrayTypes =
    [
        ("Int8Array", AsmToken.kToken_Int8Array),
        ("Uint8Array", AsmToken.kToken_Uint8Array),
        ("Int16Array", AsmToken.kToken_Int16Array),
        ("Uint16Array", AsmToken.kToken_Uint16Array),
        ("Int32Array", AsmToken.kToken_Int32Array),
        ("Uint32Array", AsmToken.kToken_Uint32Array),
        ("Float32Array", AsmToken.kToken_Float32Array),
        ("Float64Array", AsmToken.kToken_Float64Array),
    ];

    /// <summary>STDLIB_OTHER_LIST: (name, token).</summary>
    public static readonly (string Name, int Token)[] StdlibOther =
    [
        ("Infinity", AsmToken.kToken_Infinity),
        ("NaN", AsmToken.kToken_NaN),
        ("Math", AsmToken.kToken_Math),
    ];

    /// <summary>KEYWORD_NAME_LIST: (name, token).</summary>
    public static readonly (string Name, int Token)[] Keywords =
    [
        ("arguments", AsmToken.kToken_arguments),
        ("break", AsmToken.kToken_break),
        ("case", AsmToken.kToken_case),
        ("const", AsmToken.kToken_const),
        ("continue", AsmToken.kToken_continue),
        ("default", AsmToken.kToken_default),
        ("do", AsmToken.kToken_do),
        ("else", AsmToken.kToken_else),
        ("eval", AsmToken.kToken_eval),
        ("for", AsmToken.kToken_for),
        ("function", AsmToken.kToken_function),
        ("if", AsmToken.kToken_if),
        ("new", AsmToken.kToken_new),
        ("return", AsmToken.kToken_return),
        ("switch", AsmToken.kToken_switch),
        ("var", AsmToken.kToken_var),
        ("while", AsmToken.kToken_while),
    ];
}
