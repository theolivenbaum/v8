// Port of the parts of src/common/globals.h and src/objects/objects.h that the
// object model uses: shared enums and constants.
//
// Naming: V8's kFoo enumerators drop the k (LanguageMode.Strict); V8's C-style
// ALL_CAPS enumerators keep their names (TransitionFlag.INSERT_TRANSITION) so
// they stay greppable against the C++. LanguageMode, FunctionKind, ScopeType and
// VariableMode come from V8Sharp.Parsing (see ParsingGlobals.Temp.cs).
namespace V8Sharp.Common;

public enum StoreOrigin
{
    MaybeKeyed,
    Named,
}

/// <summary>
/// V8's ShouldThrow. Operations taking it return false instead of throwing a
/// TypeError when it is <see cref="DontThrow"/>.
/// </summary>
public enum ShouldThrow
{
    DontThrow = 0,
    ThrowOnError = 1,
}

public enum ComparisonResult
{
    LessThan = -1,
    Equal = 0,
    GreaterThan = 1,
    Undefined = 2,
}

public enum ToPrimitiveHint
{
    Default,
    Number,
    String,
}

public enum OrdinaryToPrimitiveHint
{
    Number,
    String,
}

public enum PropertyNormalizationMode
{
    CLEAR_INOBJECT_PROPERTIES,
    KEEP_INOBJECT_PROPERTIES,
}

public enum TransitionFlag
{
    INSERT_TRANSITION,
    OMIT_TRANSITION,
}

public enum TransitionKindFlag
{
    SIMPLE_PROPERTY_TRANSITION,
    PROPERTY_TRANSITION,
    PROTOTYPE_TRANSITION,
    SPECIAL_TRANSITION,
}

public enum DescriptorFlag
{
    ALL_DESCRIPTORS,
    OWN_DESCRIPTORS,
}

public enum EnforceDefineSemantics
{
    Set,
    Define,
}

public enum AccessorComponent
{
    ACCESSOR_GETTER,
    ACCESSOR_SETTER,
}

public enum OnNonExistent
{
    ThrowReferenceError,
    ReturnUndefined,
}

public enum ElementTypes
{
    All,
    StringAndSymbol,
}

public enum InterceptorResult
{
    False = 0,
    True = 1,
    NotIntercepted = 2,
}

public enum WhereToStart
{
    StartAtReceiver,
    StartAtPrototype,
}

/// <summary>V8's Operation (src/common/operation.h), for comparisons and arithmetic.</summary>
public enum Operation
{
    // Binary operations.
    Add,
    Subtract,
    Multiply,
    Divide,
    Modulus,
    Exponentiate,
    BitwiseAnd,
    BitwiseOr,
    BitwiseXor,
    ShiftLeft,
    ShiftRight,
    ShiftRightLogical,
    // Unary operations.
    BitwiseNot,
    Negate,
    Increment,
    Decrement,
    // Comparisons.
    Equal,
    StrictEqual,
    LessThan,
    LessThanOrEqual,
    GreaterThan,
    GreaterThanOrEqual,
}

/// <summary>Engine-side constants of src/common/globals.h (V8Sharp.Parsing owns the parser-side <c>Globals</c>).</summary>
public static class EngineGlobals
{
    public const double kMaxSafeInteger = 9007199254740991.0;  // 2^53 - 1
    public const double kMinSafeInteger = -9007199254740991.0;
    public const ulong kMaxSafeIntegerUint64 = 9007199254740991UL;
    public const uint kMaxUInt32 = uint.MaxValue;
    public const int kMaxInt = int.MaxValue;
    public const int kMinInt = int.MinValue;

    /// <summary>
    /// V8's hole NaN bit pattern (kHoleNanInt64), used for holes in
    /// FixedDoubleArray. Distinct from any NaN JavaScript can produce, because
    /// every NaN stored into a double array is canonicalized first.
    /// </summary>
    public const long kHoleNanInt64 = unchecked((long)0xFFF7FFFFFFF7FFFFUL);

    /// <summary>ComparisonResultToBool (objects.cc).</summary>
    public static bool ComparisonResultToBool(Operation op, ComparisonResult result)
    {
        switch (op)
        {
            case Operation.LessThan:
                return result == ComparisonResult.LessThan;
            case Operation.LessThanOrEqual:
                return result is ComparisonResult.LessThan or ComparisonResult.Equal;
            case Operation.GreaterThan:
                return result == ComparisonResult.GreaterThan;
            case Operation.GreaterThanOrEqual:
                return result is ComparisonResult.GreaterThan or ComparisonResult.Equal;
            default:
                throw new ArgumentOutOfRangeException(nameof(op));
        }
    }
}
