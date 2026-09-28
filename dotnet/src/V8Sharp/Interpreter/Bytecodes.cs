// Port of src/interpreter/bytecodes.h/.cc (the Bytecodes class) and the
// BytecodeTraits size/offset tables of bytecode-traits.h. The bytecode list
// itself is in Bytecodes.List.cs.
using System.Runtime.CompilerServices;

namespace V8Sharp.Interpreter;

/// <summary>Receiver conversion mode of a call (src/common/globals.h ConvertReceiverMode).</summary>
// TODO(merge): shared with builtins/ICs; move to a common namespace when one exists.
public enum ConvertReceiverMode : uint
{
    NullOrUndefined,
    NotNullOrUndefined,
    Any,
    Last = Any,
}

public static partial class Bytecodes
{
    /// <summary>The maximum number of operands a bytecode may have.</summary>
    public const int kMaxOperands = 5;

    /// <summary>The total number of bytecodes used.</summary>
    public const int kBytecodeCount = (int)kLast + 1;

    public const int kShortStarCount = (int)kLastShortStar - (int)kFirstShortStar + 1;

    // kBytecodeSizes[scale_index][bytecode], kOperandSizes[scale_index][bytecode][i],
    // kOperandOffsets[scale_index][bytecode][i], kOperandKindSizes[scale_index][type].
    static readonly byte[][] s_bytecodeSizes = new byte[3][];
    static readonly OperandSize[][][] s_operandSizes = new OperandSize[3][][];
    static readonly int[][][] s_operandOffsets = new int[3][][];
    static readonly OperandSize[][] s_operandKindSizes = new OperandSize[3][];
    static readonly OperandTypeInfo[][] s_operandTypeInfos = new OperandTypeInfo[kBytecodeCount][];
    static readonly string[] s_names = Enum.GetNames<Bytecode>();

    static Bytecodes()
    {
        ReadOnlySpan<OperandScale> scales = [OperandScale.Single, OperandScale.Double, OperandScale.Quadruple];
        for (int b = 0; b < kBytecodeCount; b++)
        {
            OperandType[] types = s_operandTypes[b];
            var infos = new OperandTypeInfo[types.Length];
            for (int i = 0; i < types.Length; i++) infos[i] = BytecodeOperands.GetOperandTypeInfo(types[i]);
            s_operandTypeInfos[b] = infos;
        }
        foreach (OperandScale scale in scales)
        {
            int scale_index = BytecodeOperands.OperandScaleAsIndex(scale);
            var sizes = new byte[kBytecodeCount];
            var operandSizes = new OperandSize[kBytecodeCount][];
            var operandOffsets = new int[kBytecodeCount][];
            for (int b = 0; b < kBytecodeCount; b++)
            {
                OperandType[] types = s_operandTypes[b];
                var opSizes = new OperandSize[types.Length];
                // CalculateOperandOffsets has one extra slot (sizeof...(operands) + 1).
                var offsets = new int[types.Length + 1];
                int offset = 1;
                for (int i = 0; i < types.Length; i++)
                {
                    OperandSize size = BytecodeOperands.ScaledOperandSize(types[i], scale);
                    opSizes[i] = size;
                    offsets[i] = offset;
                    offset += (int)size;
                }
                sizes[b] = (byte)offset;
                operandSizes[b] = opSizes;
                operandOffsets[b] = offsets;
            }
            s_bytecodeSizes[scale_index] = sizes;
            s_operandSizes[scale_index] = operandSizes;
            s_operandOffsets[scale_index] = operandOffsets;

            var kindSizes = new OperandSize[BytecodeOperands.kOperandTypeCount];
            for (int t = 0; t < kindSizes.Length; t++)
                kindSizes[t] = BytecodeOperands.ScaledOperandSize((OperandType)t, scale);
            s_operandKindSizes[scale_index] = kindSizes;
        }
    }

    /// <summary>Returns string representation of |bytecode|.</summary>
    public static string ToString(Bytecode bytecode) => s_names[(int)bytecode];

    /// <summary>Returns string representation of |bytecode| combined with
    /// |operand_scale| using the optionally provided |separator|.</summary>
    public static string ToString(Bytecode bytecode, OperandScale operand_scale, string separator = ".")
    {
        string value = ToString(bytecode);
        if (operand_scale > OperandScale.Single)
        {
            Bytecode prefix_bytecode = OperandScaleToPrefixBytecode(operand_scale);
            return value + separator + ToString(prefix_bytecode);
        }
        return value;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static byte ToByte(Bytecode bytecode)
    {
        Debug.Assert(bytecode <= kLast);
        return (byte)bytecode;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Bytecode FromByte(byte value)
    {
        var bytecode = (Bytecode)value;
        Debug.Assert(bytecode <= kLast);
        return bytecode;
    }

    /// <summary>Returns the prefix bytecode representing an operand scale.</summary>
    public static Bytecode OperandScaleToPrefixBytecode(OperandScale operand_scale) => operand_scale switch
    {
        OperandScale.Quadruple => Bytecode.ExtraWide,
        OperandScale.Double => Bytecode.Wide,
        _ => throw new UnreachableException(),
    };

    public static bool OperandScaleRequiresPrefixBytecode(OperandScale operand_scale) =>
        operand_scale != OperandScale.Single;

    public static OperandScale PrefixBytecodeToOperandScale(Bytecode bytecode) => bytecode switch
    {
        Bytecode.ExtraWide or Bytecode.DebugBreakExtraWide => OperandScale.Quadruple,
        Bytecode.Wide or Bytecode.DebugBreakWide => OperandScale.Double,
        _ => throw new UnreachableException(),
    };

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ImplicitRegisterUse GetImplicitRegisterUse(Bytecode bytecode) =>
        s_implicitRegisterUse[(int)bytecode];

    public static bool ReadsAccumulator(Bytecode bytecode) =>
        BytecodeOperands.ReadsAccumulator(GetImplicitRegisterUse(bytecode));

    public static bool WritesAccumulator(Bytecode bytecode) =>
        BytecodeOperands.WritesAccumulator(GetImplicitRegisterUse(bytecode));

    public static bool ClobbersAccumulator(Bytecode bytecode) =>
        BytecodeOperands.ClobbersAccumulator(GetImplicitRegisterUse(bytecode));

    public static bool WritesOrClobbersAccumulator(Bytecode bytecode) =>
        BytecodeOperands.WritesOrClobbersAccumulator(GetImplicitRegisterUse(bytecode));

    public static bool WritesImplicitRegister(Bytecode bytecode) =>
        BytecodeOperands.WritesImplicitRegister(GetImplicitRegisterUse(bytecode));

    /// <summary>True if |bytecode| is an accumulator load without effects, e.g. LdaConstant, LdaTrue, Ldar.</summary>
    public static bool IsAccumulatorLoadWithoutEffects(Bytecode bytecode) =>
        bytecode >= Bytecode.Ldar && bytecode <= Bytecode.LdaImmutableCurrentContextSlot;

    /// <summary>True if |bytecode| is a compare operation without external effects.</summary>
    public static bool IsCompareWithoutEffects(Bytecode bytecode) =>
        bytecode >= Bytecode.TestReferenceEqual && bytecode <= Bytecode.TestTypeOf;

    public static bool IsShortStar(Bytecode bytecode) =>
        bytecode >= kFirstShortStar && bytecode <= kLastShortStar;

    public static bool IsAnyStar(Bytecode bytecode) => bytecode == Bytecode.Star || IsShortStar(bytecode);

    /// <summary>True if |bytecode| is a register load without effects, e.g. Mov, Star.</summary>
    public static bool IsRegisterLoadWithoutEffects(Bytecode bytecode) =>
        IsShortStar(bytecode) || (bytecode >= Bytecode.Star && bytecode <= Bytecode.PopContext);

    public static bool IsConditionalJumpImmediate(Bytecode bytecode) =>
        bytecode >= Bytecode.JumpIfToBooleanTrue && bytecode <= Bytecode.JumpIfForInDone;

    public static bool IsConditionalJumpConstant(Bytecode bytecode) =>
        bytecode >= Bytecode.JumpIfNullConstant && bytecode <= Bytecode.JumpIfToBooleanFalseConstant;

    public static bool IsConditionalJump(Bytecode bytecode) =>
        bytecode >= Bytecode.JumpIfNullConstant && bytecode <= Bytecode.JumpIfForInDone;

    public static bool IsUnconditionalJump(Bytecode bytecode) =>
        bytecode >= Bytecode.JumpLoop && bytecode <= Bytecode.JumpConstant;

    public static bool IsJumpImmediate(Bytecode bytecode) =>
        bytecode == Bytecode.Jump || bytecode == Bytecode.JumpLoop || IsConditionalJumpImmediate(bytecode);

    public static bool IsJumpConstant(Bytecode bytecode) =>
        bytecode >= Bytecode.JumpConstant && bytecode <= Bytecode.JumpIfToBooleanFalseConstant;

    /// <summary>True if the bytecode is a jump that internally coerces the accumulator to a boolean.</summary>
    public static bool IsJumpIfToBoolean(Bytecode bytecode) =>
        bytecode >= Bytecode.JumpIfToBooleanTrueConstant && bytecode <= Bytecode.JumpIfToBooleanFalse;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsJump(Bytecode bytecode) =>
        bytecode >= Bytecode.JumpLoop && bytecode <= Bytecode.JumpIfForInDone;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsForwardJump(Bytecode bytecode) =>
        bytecode >= Bytecode.Jump && bytecode <= Bytecode.JumpIfForInDone;

    /// <summary>A jump without effects: any jump except ToBoolean jumps and
    /// JumpLoop (which has an implicit StackCheck).</summary>
    public static bool IsJumpWithoutEffects(Bytecode bytecode) =>
        IsJump(bytecode) && bytecode != Bytecode.JumpLoop && !IsJumpIfToBoolean(bytecode);

    public static bool IsSwitch(Bytecode bytecode) =>
        bytecode == Bytecode.SwitchOnSmiNoFeedback || bytecode == Bytecode.SwitchOnGeneratorState;

    /// <summary>True if |bytecode| has no effects: it only manipulates
    /// interpreter frame state and never throws.</summary>
    public static bool IsWithoutExternalSideEffects(Bytecode bytecode) =>
        IsAccumulatorLoadWithoutEffects(bytecode) || IsRegisterLoadWithoutEffects(bytecode) ||
        IsCompareWithoutEffects(bytecode) || IsJumpWithoutEffects(bytecode) || IsSwitch(bytecode) ||
        bytecode == Bytecode.Return;

    public static bool IsLdarOrStar(Bytecode bytecode) => bytecode == Bytecode.Ldar || IsAnyStar(bytecode);

    public static bool IsCallOrConstruct(Bytecode bytecode) => bytecode is
        Bytecode.CallAnyReceiver or Bytecode.CallProperty or Bytecode.CallProperty0 or
        Bytecode.CallProperty1 or Bytecode.CallProperty2 or Bytecode.CallUndefinedReceiver or
        Bytecode.CallUndefinedReceiver0 or Bytecode.CallUndefinedReceiver1 or
        Bytecode.CallUndefinedReceiver2 or Bytecode.Construct or Bytecode.CallWithSpread or
        Bytecode.ConstructWithSpread or Bytecode.ConstructForwardAllArgs or Bytecode.CallJSRuntime;

    public static bool IsCallRuntime(Bytecode bytecode) =>
        bytecode is Bytecode.CallRuntime or Bytecode.CallRuntimeForPair or Bytecode.InvokeIntrinsic;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsPrefixScalingBytecode(Bytecode bytecode) =>
        bytecode is Bytecode.ExtraWide or Bytecode.Wide or Bytecode.DebugBreakExtraWide or Bytecode.DebugBreakWide;

    public static bool IsCompareWithEmbeddedFeedback(Bytecode bytecode) => bytecode is
        Bytecode.TestEqualStrict or Bytecode.TestEqual or Bytecode.TestLessThan or
        Bytecode.TestGreaterThan or Bytecode.TestLessThanOrEqual or Bytecode.TestGreaterThanOrEqual;

    public static bool IsBinaryOpWithEmbeddedFeedback(Bytecode bytecode) => bytecode is
        Bytecode.Add or Bytecode.Sub or Bytecode.Mul or Bytecode.Div or Bytecode.Mod or Bytecode.Exp or
        Bytecode.BitwiseOr or Bytecode.BitwiseXor or Bytecode.BitwiseAnd or Bytecode.ShiftLeft or
        Bytecode.ShiftRight or Bytecode.ShiftRightLogical or Bytecode.AddSmi or Bytecode.SubSmi or
        Bytecode.MulSmi or Bytecode.DivSmi or Bytecode.ModSmi or Bytecode.ExpSmi or
        Bytecode.BitwiseOrSmi or Bytecode.BitwiseXorSmi or Bytecode.BitwiseAndSmi or
        Bytecode.ShiftLeftSmi or Bytecode.ShiftRightSmi or Bytecode.ShiftRightLogicalSmi;

    public static bool IsUnaryOpWithEmbeddedFeedback(Bytecode bytecode) =>
        bytecode is Bytecode.Inc or Bytecode.Dec or Bytecode.Negate or Bytecode.BitwiseNot;

    /// <summary>Returns true if the bytecode has an embedded feedback slot.</summary>
    public static bool IsEmbeddedFeedbackBytecode(Bytecode bytecode) =>
        IsCompareWithEmbeddedFeedback(bytecode) || IsBinaryOpWithEmbeddedFeedback(bytecode) ||
        IsUnaryOpWithEmbeddedFeedback(bytecode);

    /// <summary>RETURN_BYTECODE_LIST.</summary>
    public static bool Returns(Bytecode bytecode) =>
        bytecode == Bytecode.Return || bytecode == Bytecode.SuspendGenerator;

    /// <summary>UNCONDITIONAL_THROW_BYTECODE_LIST.</summary>
    public static bool UnconditionallyThrows(Bytecode bytecode) =>
        bytecode == Bytecode.Throw || bytecode == Bytecode.ReThrow;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int NumberOfOperands(Bytecode bytecode) => s_operandTypes[(int)bytecode].Length;

    public static OperandType GetOperandType(Bytecode bytecode, int i)
    {
        Debug.Assert(i >= 0 && i < NumberOfOperands(bytecode));
        return s_operandTypes[(int)bytecode][i];
    }

    /// <summary>The operand types of |bytecode| (V8 returns a kNone-terminated array).</summary>
    public static ReadOnlySpan<OperandType> GetOperandTypes(Bytecode bytecode) => s_operandTypes[(int)bytecode];

    public static bool OperandIsScalableSignedByte(Bytecode bytecode, int operand_index) =>
        s_operandTypeInfos[(int)bytecode][operand_index] == OperandTypeInfo.ScalableSignedByte;

    public static bool OperandIsScalableUnsignedByte(Bytecode bytecode, int operand_index) =>
        s_operandTypeInfos[(int)bytecode][operand_index] == OperandTypeInfo.ScalableUnsignedByte;

    public static bool OperandIsScalable(Bytecode bytecode, int operand_index) =>
        OperandIsScalableSignedByte(bytecode, operand_index) ||
        OperandIsScalableUnsignedByte(bytecode, operand_index);

    /// <summary>Returns true if the bytecode has wider operand forms.</summary>
    public static bool IsBytecodeWithScalableOperands(Bytecode bytecode)
    {
        for (int i = 0; i < NumberOfOperands(bytecode); i++)
        {
            if (OperandIsScalable(bytecode, i)) return true;
        }
        return false;
    }

    public static OperandSize GetOperandSize(Bytecode bytecode, int i, OperandScale operand_scale)
    {
        if (i >= NumberOfOperands(bytecode)) throw new ArgumentOutOfRangeException(nameof(i));
        return GetOperandSizes(bytecode, operand_scale)[i];
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ReadOnlySpan<OperandSize> GetOperandSizes(Bytecode bytecode, OperandScale operand_scale) =>
        s_operandSizes[(int)operand_scale >> 1][(int)bytecode];

    /// <summary>The offset of the i-th operand of |bytecode| relative to the start of the bytecode.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int GetOperandOffset(Bytecode bytecode, int i, OperandScale operand_scale) =>
        s_operandOffsets[(int)operand_scale >> 1][(int)bytecode][i];

    /// <summary>The size of the bytecode including its operands for |operand_scale|.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int Size(Bytecode bytecode, OperandScale operand_scale) =>
        s_bytecodeSizes[(int)operand_scale >> 1][(int)bytecode];

    /// <summary>Returns a debug break bytecode to replace |bytecode|.</summary>
    public static Bytecode GetDebugBreak(Bytecode bytecode)
    {
        Debug.Assert(!IsDebugBreak(bytecode));
        if (bytecode == Bytecode.Wide) return Bytecode.DebugBreakWide;
        if (bytecode == Bytecode.ExtraWide) return Bytecode.DebugBreakExtraWide;
        int bytecode_size = Size(bytecode, OperandScale.Single);
        foreach (Bytecode debug_break in DebugBreakPlainBytecodeList)
        {
            if (bytecode_size == Size(debug_break, OperandScale.Single)) return debug_break;
        }
        throw new UnreachableException();
    }

    /// <summary>True if there is a call in the most-frequently executed path through the handler.</summary>
    public static bool MakesCallAlongCriticalPath(Bytecode bytecode)
    {
        if (IsCallOrConstruct(bytecode) || IsCallRuntime(bytecode)) return true;
        return bytecode is Bytecode.CreateWithContext or Bytecode.CreateBlockContext or
            Bytecode.CreateCatchContext or Bytecode.CreateRegExpLiteral or Bytecode.GetIterator or
            Bytecode.ArrayDestructure;
    }

    public static ConvertReceiverMode GetReceiverMode(Bytecode bytecode) => bytecode switch
    {
        Bytecode.CallProperty or Bytecode.CallProperty0 or Bytecode.CallProperty1 or Bytecode.CallProperty2
            => ConvertReceiverMode.NotNullOrUndefined,
        Bytecode.CallUndefinedReceiver or Bytecode.CallUndefinedReceiver0 or Bytecode.CallUndefinedReceiver1
            or Bytecode.CallUndefinedReceiver2 or Bytecode.CallJSRuntime => ConvertReceiverMode.NullOrUndefined,
        Bytecode.CallAnyReceiver or Bytecode.Construct or Bytecode.CallWithSpread or
            Bytecode.ConstructWithSpread or Bytecode.InvokeIntrinsic => ConvertReceiverMode.Any,
        _ => throw new UnreachableException(),
    };

    public static bool IsDebugBreak(Bytecode bytecode) => bytecode is
        Bytecode.DebugBreak0 or Bytecode.DebugBreak1 or Bytecode.DebugBreak2 or Bytecode.DebugBreak3 or
        Bytecode.DebugBreak4 or Bytecode.DebugBreak5 or Bytecode.DebugBreak6 or Bytecode.DebugBreakWide or
        Bytecode.DebugBreakExtraWide;

    /// <summary>Returns true if |operand_type| is any type of register operand.</summary>
    public static bool IsRegisterOperandType(OperandType operand_type) => operand_type >= OperandType.Reg;

    /// <summary>Returns true if |operand_type| represents a register used as an input.</summary>
    public static bool IsRegisterInputOperandType(OperandType operand_type) =>
        operand_type is OperandType.Reg or OperandType.RegList or OperandType.RegPair or OperandType.RegInOut;

    /// <summary>Returns true if |operand_type| represents a register used as an output.</summary>
    public static bool IsRegisterOutputOperandType(OperandType operand_type) =>
        operand_type is OperandType.RegOut or OperandType.RegOutList or OperandType.RegOutPair
            or OperandType.RegOutTriple or OperandType.RegInOut;

    public static bool IsRegisterListOperandType(OperandType operand_type) =>
        operand_type is OperandType.RegList or OperandType.RegOutList;

    /// <summary>True if the handler for |bytecode| should look ahead and inline a dispatch to a Star.</summary>
    public static bool IsStarLookahead(Bytecode bytecode, OperandScale operand_scale)
    {
        if (operand_scale != OperandScale.Single) return false;
        // Short-star lookahead is required for correctness on kDebugBreak0. The
        // handler for all short-star codes re-reads the opcode from the bytecode
        // array and would not work correctly if it instead read kDebugBreak0.
        return bytecode is Bytecode.DebugBreak0 or Bytecode.LdaZero or Bytecode.LdaSmi or Bytecode.LdaNull or
            Bytecode.LdaTheHole or Bytecode.LdaTdzHole or Bytecode.LdaConstant or Bytecode.LdaUndefined or
            Bytecode.LdaGlobal or Bytecode.GetNamedProperty or Bytecode.GetKeyedProperty or
            Bytecode.LdaContextSlotNoCell or Bytecode.LdaImmutableContextSlot or
            Bytecode.LdaCurrentContextSlotNoCell or Bytecode.LdaImmutableCurrentContextSlot or
            Bytecode.Add or Bytecode.Sub or Bytecode.Mul or Bytecode.AddSmi or Bytecode.SubSmi or
            Bytecode.Inc or Bytecode.Dec or Bytecode.TypeOf or Bytecode.CallAnyReceiver or
            Bytecode.CallProperty or Bytecode.CallProperty0 or Bytecode.CallProperty1 or
            Bytecode.CallProperty2 or Bytecode.CallUndefinedReceiver or Bytecode.CallUndefinedReceiver0 or
            Bytecode.CallUndefinedReceiver1 or Bytecode.CallUndefinedReceiver2 or Bytecode.Construct or
            Bytecode.ConstructWithSpread or Bytecode.CreateObjectLiteral or Bytecode.CreateArrayLiteral or
            Bytecode.ThrowReferenceErrorIfTdzHole or Bytecode.GetTemplateObject;
    }

    /// <summary>The number of registers represented by a register operand
    /// (not for kRegList, whose size is given by the following kRegCount).</summary>
    public static uint GetNumberOfRegistersRepresentedBy(OperandType operand_type) => operand_type switch
    {
        OperandType.Reg or OperandType.RegOut or OperandType.RegInOut => 1,
        OperandType.RegPair or OperandType.RegOutPair => 2,
        OperandType.RegOutTriple => 3,
        OperandType.RegList or OperandType.RegOutList => throw new UnreachableException(),
        _ => 0,
    };

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static OperandSize SizeOfOperand(OperandType operand_type, OperandScale operand_scale) =>
        s_operandKindSizes[(int)operand_scale >> 1][(int)operand_type];

    public static bool IsRuntimeIdOperandType(OperandType operand_type) => operand_type == OperandType.RuntimeId;

    /// <summary>Returns true if |operand_type| is unsigned, false if signed.</summary>
    public static bool IsUnsignedOperandType(OperandType operand_type) =>
        BytecodeOperands.IsUnsigned(BytecodeOperands.GetOperandTypeInfo(operand_type));

    /// <summary>True if a handler is generated for |bytecode| at |operand_scale|.</summary>
    public static bool BytecodeHasHandler(Bytecode bytecode, OperandScale operand_scale) =>
        (operand_scale == OperandScale.Single && (!IsShortStar(bytecode) || bytecode == Bytecode.Star0)) ||
        IsBytecodeWithScalableOperands(bytecode);

    /// <summary>The operand scale required to hold a signed operand with |value|.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static OperandScale ScaleForSignedOperand(int value)
    {
        if (value >= sbyte.MinValue && value <= sbyte.MaxValue) return OperandScale.Single;
        if (value >= short.MinValue && value <= short.MaxValue) return OperandScale.Double;
        return OperandScale.Quadruple;
    }

    /// <summary>The operand scale required to hold an unsigned operand with |value|.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static OperandScale ScaleForUnsignedOperand(uint value)
    {
        if (value <= byte.MaxValue) return OperandScale.Single;
        if (value <= ushort.MaxValue) return OperandScale.Double;
        return OperandScale.Quadruple;
    }

    /// <summary>The operand size required to hold an unsigned operand with |value|.</summary>
    public static OperandSize SizeForUnsignedOperand(uint value)
    {
        if (value <= byte.MaxValue) return OperandSize.Byte;
        if (value <= ushort.MaxValue) return OperandSize.Short;
        return OperandSize.Quad;
    }
}
