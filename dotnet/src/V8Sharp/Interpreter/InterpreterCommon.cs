// The parts of src/common/globals.h, src/parsing/token.h, src/ast/variables.h
// and src/flags/flag-definitions.h that the bytecode infrastructure needs.
//
// TODO(merge): these are shared with the parser and the object model, which are
// being ported in parallel. When V8Sharp.Parsing's Token and the common
// globals land, delete the duplicates here and point the interpreter at them.
namespace V8Sharp.Interpreter;

public enum LanguageMode : byte { Sloppy, Strict }

public enum LookupHoistingMode : byte { Normal, LegacySloppy }

public enum TypeofMode : byte { Inside, NotInside }

public enum ContextMode : byte { NoContextCells, HasContextCells }

public enum CreateArgumentsType : byte { MappedArguments, UnmappedArguments, RestParameter }

public enum MaybeAssignedFlag : byte { NotAssigned, MaybeAssigned }

[Flags]
public enum DefineKeyedOwnPropertyInLiteralFlags : byte { NoFlags = 0, SetFunctionName = 1 << 0 }

[Flags]
public enum DefineKeyedOwnPropertyFlags : byte { NoFlags = 0, SetFunctionName = 1 << 0 }

public enum AddStringConstantAndInternalizeVariant : byte
{
    LhsIsStringConstant = 0,
    RhsIsStringConstant = 1,
}

/// <summary>Constants from src/common/globals.h.</summary>
public static class InterpreterConstants
{
    public const int kNoSourcePosition = -1;

    /// <summary>The bytecode offset used for the function entry source position.</summary>
    public const int kFunctionEntryBytecodeOffset = -1;

    /// <summary>The feedback slot argument for bytecodes whose feedback is embedded in the bytecode.</summary>
    public const int kFeedbackIsEmbedded = -1;

    public const int kUninitializedEmbeddedFeedback = 0;

    public const int kUnaryEmbeddedFeedbackOperandIndex = 0;
    public const int kEmbeddedFeedbackOperandIndex = 1;

    /// <summary>kJSArgcReceiverSlots: the receiver is counted in the parameter count.</summary>
    public const int kJSArgcReceiverSlots = 1;

    /// <summary>Code::kMaxArguments.</summary>
    public const int kMaxArguments = (1 << 16) - 10;

    /// <summary>FeedbackVector::kMaxOsrUrgency.</summary>
    public const int kMaxOsrUrgency = 6;
}

/// <summary>The operator tokens the bytecode array builder switches on (a subset of Token::Value).</summary>
// TODO(merge): replace with V8Sharp.Parsing's port of src/parsing/token.h.
public static class Token
{
    public enum Value : byte
    {
        // Binary operators.
        Nullish,
        Or,
        And,
        BitOr,
        BitXor,
        BitAnd,
        Shl,
        Sar,
        Shr,
        Mul,
        Div,
        Mod,
        Exp,
        Add,
        Sub,
        // Unary / count operators.
        Not,
        BitNot,
        Delete,
        TypeOf,
        Void,
        Inc,
        Dec,
        // Compare operators.
        Eq,
        EqStrict,
        NotEq,
        NotEqStrict,
        LessThan,
        GreaterThan,
        LessThanEq,
        GreaterThanEq,
        InstanceOf,
        In,
    }
}

/// <summary>
/// The Ignition flags of src/flags/flag-definitions.h, with V8's defaults.
/// </summary>
// TODO(merge): route through the isolate's FlagList when it is ported.
public static class InterpreterFlags
{
    /// <summary>--ignition-elide-noneffectful-bytecodes</summary>
    public static bool ignition_elide_noneffectful_bytecodes = true;

    /// <summary>--ignition-reo: use the ignition register equivalence optimizer.</summary>
    public static bool ignition_reo = true;

    /// <summary>--ignition-filter-expression-positions</summary>
    public static bool ignition_filter_expression_positions = true;
}

/// <summary>The type hints the bytecode generator attaches to the accumulator
/// (BytecodeGenerator::TypeHint in src/interpreter/bytecode-generator.h).</summary>
// TODO(merge): the bytecode generator port should use this enum (or move it into BytecodeGenerator).
[Flags]
public enum TypeHint : byte
{
    Boolean = 1 << 0,
    InternalizedString = 1 << 1,
    String = InternalizedString | (1 << 2),
    Any = Boolean | String,
    Unknown = 0xFF,
}

public static class TypeHints
{
    /// <summary>Check if hint2 is same or the subtype of hint1.</summary>
    public static bool IsSameOrSubTypeHint(TypeHint hint1, TypeHint hint2) => hint1 == (hint1 | hint2);

    public static bool IsStringTypeHint(TypeHint hint) => IsSameOrSubTypeHint(TypeHint.String, hint);
}

/// <summary>The kinds of source ranges used for block coverage (src/ast/ast-source-ranges.h).</summary>
// TODO(merge): replace with V8Sharp.Ast's SourceRangeKind.
public enum SourceRangeKind
{
    Body,
    Catch,
    Continuation,
    Else,
    Finally,
    Right,
    Then,
}
