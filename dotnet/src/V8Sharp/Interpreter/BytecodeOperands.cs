// Port of src/interpreter/bytecode-operands.h/.cc and bytecode-traits.h.
//
// The operand-type lists are "carefully ordered for operand type range
// checks": keep the enum order exactly as in V8.
using System.Runtime.CompilerServices;

namespace V8Sharp.Interpreter;

/// <summary>Scaling factors applicable to scalable operands. The values are the
/// integer scaling factors (V8's OPERAND_SCALE_LIST).</summary>
public enum OperandScale : byte
{
    Single = 1,
    Double = 2,
    Quadruple = 4,
    Last = Quadruple,
}

/// <summary>The size classes of operand types; the values are sizes in bytes.</summary>
public enum OperandSize : byte
{
    None = 0,
    Byte = 1,
    Short = 2,
    Quad = 4,
    Last = Quad,
}

/// <summary>Primitive operand info summarizing properties of operands
/// (OPERAND_TYPE_INFO_LIST: IsScalable, IsUnsigned, UnscaledSize).</summary>
public enum OperandTypeInfo : byte
{
    None,
    ScalableSignedByte,
    ScalableUnsignedByte,
    FixedUnsignedByte,
    FixedUnsignedShort,
}

/// <summary>Operand types used by bytecodes (OPERAND_TYPE_LIST, in V8's order).</summary>
public enum OperandType : byte
{
    // INVALID_OPERAND_TYPE_LIST
    None,
    // UNSIGNED_FIXED_SCALAR_OPERAND_TYPE_LIST
    Flag8,
    Flag16,
    IntrinsicId,
    RuntimeId,
    NativeContextIndex,
    AbortReason,
    EmbeddedFeedback,
    // UNSIGNED_SCALABLE_SCALAR_OPERAND_TYPE_LIST
    ConstantPoolIndex,
    FeedbackSlot,
    ContextSlot,
    CoverageSlot,
    UImm,
    RegCount,
    // SIGNED_SCALABLE_SCALAR_OPERAND_TYPE_LIST
    Imm,
    // REGISTER_INPUT_OPERAND_TYPE_LIST
    Reg,
    RegList,
    RegPair,
    // REGISTER_OUTPUT_OPERAND_TYPE_LIST
    RegOut,
    RegOutList,
    RegOutPair,
    RegOutTriple,
    // REGISTER_OPERAND_TYPE_LIST tail
    RegInOut,

    Last = RegInOut,
}

/// <summary>How a bytecode uses the accumulator and implicit registers.</summary>
[Flags]
public enum ImplicitRegisterUse : byte
{
    None = 0,
    ReadAccumulator = 1 << 0,
    WriteAccumulator = 1 << 1,
    ClobberAccumulator = 1 << 2,
    WriteShortStar = 1 << 3,
    ReadWriteAccumulator = ReadAccumulator | WriteAccumulator,
    ReadAndClobberAccumulator = ReadAccumulator | ClobberAccumulator,
    ReadAccumulatorWriteShortStar = ReadAccumulator | WriteShortStar,
}

public static class BytecodeOperands
{
    /// <summary>The total number of bytecode operand types used.</summary>
    public const int kOperandTypeCount = (int)OperandType.Last + 1;

    /// <summary>The total number of bytecode operand scales used.</summary>
    public const int kOperandScaleCount = 3;

    // Per OperandType: the OperandTypeInfo (OPERAND_TYPE_LIST second column).
    static readonly OperandTypeInfo[] s_operandTypeInfos =
    [
        OperandTypeInfo.None,                 // None
        OperandTypeInfo.FixedUnsignedByte,    // Flag8
        OperandTypeInfo.FixedUnsignedShort,   // Flag16
        OperandTypeInfo.FixedUnsignedByte,    // IntrinsicId
        OperandTypeInfo.FixedUnsignedShort,   // RuntimeId
        OperandTypeInfo.FixedUnsignedByte,    // NativeContextIndex
        OperandTypeInfo.FixedUnsignedByte,    // AbortReason
        OperandTypeInfo.FixedUnsignedByte,    // EmbeddedFeedback
        OperandTypeInfo.ScalableUnsignedByte, // ConstantPoolIndex
        OperandTypeInfo.ScalableUnsignedByte, // FeedbackSlot
        OperandTypeInfo.ScalableUnsignedByte, // ContextSlot
        OperandTypeInfo.ScalableUnsignedByte, // CoverageSlot
        OperandTypeInfo.ScalableUnsignedByte, // UImm
        OperandTypeInfo.ScalableUnsignedByte, // RegCount
        OperandTypeInfo.ScalableSignedByte,   // Imm
        OperandTypeInfo.ScalableSignedByte,   // Reg
        OperandTypeInfo.ScalableSignedByte,   // RegList
        OperandTypeInfo.ScalableSignedByte,   // RegPair
        OperandTypeInfo.ScalableSignedByte,   // RegOut
        OperandTypeInfo.ScalableSignedByte,   // RegOutList
        OperandTypeInfo.ScalableSignedByte,   // RegOutPair
        OperandTypeInfo.ScalableSignedByte,   // RegOutTriple
        OperandTypeInfo.ScalableSignedByte,   // RegInOut
    ];

    /// <summary>OperandTraits&lt;type&gt;::kOperandTypeInfo.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static OperandTypeInfo GetOperandTypeInfo(OperandType operand_type) =>
        s_operandTypeInfos[(int)operand_type];

    /// <summary>OperandTypeInfoTraits::kIsScalable.</summary>
    public static bool IsScalable(OperandTypeInfo info) =>
        info is OperandTypeInfo.ScalableSignedByte or OperandTypeInfo.ScalableUnsignedByte;

    /// <summary>OperandTypeInfoTraits::kIsUnsigned.</summary>
    public static bool IsUnsigned(OperandTypeInfo info) =>
        info is OperandTypeInfo.ScalableUnsignedByte or OperandTypeInfo.FixedUnsignedByte
            or OperandTypeInfo.FixedUnsignedShort;

    /// <summary>OperandTypeInfoTraits::kUnscaledSize.</summary>
    public static OperandSize UnscaledSize(OperandTypeInfo info) => info switch
    {
        OperandTypeInfo.None => OperandSize.None,
        OperandTypeInfo.FixedUnsignedShort => OperandSize.Short,
        _ => OperandSize.Byte,
    };

    /// <summary>OperandScaler&lt;type, scale&gt;::kOperandSize.</summary>
    public static OperandSize ScaledOperandSize(OperandType operand_type, OperandScale operand_scale)
    {
        OperandTypeInfo info = GetOperandTypeInfo(operand_type);
        int size = (int)UnscaledSize(info) * (IsScalable(info) ? (int)operand_scale : 1);
        return (OperandSize)size;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int OperandScaleAsIndex(OperandScale operand_scale) => (int)operand_scale >> 1;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool ReadsAccumulator(ImplicitRegisterUse implicit_register_use) =>
        (implicit_register_use & ImplicitRegisterUse.ReadAccumulator) == ImplicitRegisterUse.ReadAccumulator;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool WritesAccumulator(ImplicitRegisterUse implicit_register_use) =>
        (implicit_register_use & ImplicitRegisterUse.WriteAccumulator) == ImplicitRegisterUse.WriteAccumulator;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool ClobbersAccumulator(ImplicitRegisterUse implicit_register_use) =>
        (implicit_register_use & ImplicitRegisterUse.ClobberAccumulator) == ImplicitRegisterUse.ClobberAccumulator;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool WritesOrClobbersAccumulator(ImplicitRegisterUse implicit_register_use) =>
        (implicit_register_use & (ImplicitRegisterUse.WriteAccumulator | ImplicitRegisterUse.ClobberAccumulator))
        != ImplicitRegisterUse.None;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool WritesImplicitRegister(ImplicitRegisterUse implicit_register_use) =>
        (implicit_register_use & ImplicitRegisterUse.WriteShortStar) == ImplicitRegisterUse.WriteShortStar;

    /// <summary>Returns true if |operand_type| is a scalable signed byte.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsScalableSignedByte(OperandType operand_type) =>
        operand_type >= OperandType.Imm && operand_type <= OperandType.RegInOut;

    /// <summary>Returns true if |operand_type| is a scalable unsigned byte.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsScalableUnsignedByte(OperandType operand_type) =>
        operand_type >= OperandType.ConstantPoolIndex && operand_type <= OperandType.RegCount;

    // The operator<< overloads of bytecode-operands.cc.
    public static string ToString(ImplicitRegisterUse use) => use switch
    {
        ImplicitRegisterUse.None => "None",
        ImplicitRegisterUse.ReadAccumulator => "ReadAccumulator",
        ImplicitRegisterUse.WriteAccumulator => "WriteAccumulator",
        ImplicitRegisterUse.ClobberAccumulator => "ClobberAccumulator",
        ImplicitRegisterUse.WriteShortStar => "WriteShortStar",
        ImplicitRegisterUse.ReadAndClobberAccumulator => "ReadAndClobberAccumulator",
        ImplicitRegisterUse.ReadWriteAccumulator => "ReadWriteAccumulator",
        ImplicitRegisterUse.ReadAccumulatorWriteShortStar => "ReadAccumulatorWriteShortStar",
        _ => throw new UnreachableException(),
    };

    public static string ToString(OperandScale scale) => scale switch
    {
        OperandScale.Single => "Single",
        OperandScale.Double => "Double",
        OperandScale.Quadruple => "Quadruple",
        _ => throw new UnreachableException(),
    };

    public static string ToString(OperandSize size) => size switch
    {
        OperandSize.None => "None",
        OperandSize.Byte => "Byte",
        OperandSize.Short => "Short",
        OperandSize.Quad => "Quad",
        _ => throw new UnreachableException(),
    };

    static readonly string[] s_operandTypeNames =
    [
        "None", "Flag8", "Flag16", "IntrinsicId", "RuntimeId", "NativeContextIndex", "AbortReason",
        "EmbeddedFeedback", "ConstantPoolIndex", "FeedbackSlot", "ContextSlot", "CoverageSlot", "UImm",
        "RegCount", "Imm", "Reg", "RegList", "RegPair", "RegOut", "RegOutList", "RegOutPair",
        "RegOutTriple", "RegInOut",
    ];

    // Enum.ToString is ambiguous for aliased values (Last), so use V8's names.
    public static string ToString(OperandType type) => s_operandTypeNames[(int)type];
}
