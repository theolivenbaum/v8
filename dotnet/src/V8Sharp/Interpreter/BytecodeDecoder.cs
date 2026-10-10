// Port of src/interpreter/bytecode-decoder.h/.cc. Operands are little-endian
// (V8_TARGET_LITTLE_ENDIAN), read from spans.
using V8Sharp.Runtime;
using System.Buffers.Binary;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using V8Sharp.Codegen;

namespace V8Sharp.Interpreter;

public static class BytecodeDecoder
{
    /// <summary>String::kMaxShortPrintLength.</summary>
    const int kMaxShortPrintLength = 1024;

    /// <summary>Decodes a register operand in a byte array.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Register DecodeRegisterOperand(ReadOnlySpan<byte> operandStart, OperandType operandType,
                                                 OperandScale operandScale)
    {
        Debug.Assert(Bytecodes.IsRegisterOperandType(operandType));
        int operand = DecodeSignedOperand(operandStart, operandType, operandScale);
        return Register.FromOperand(operand);
    }

    /// <summary>Decodes a register list operand in a byte array.</summary>
    public static RegisterList DecodeRegisterListOperand(ReadOnlySpan<byte> operandStart, uint count,
                                                         OperandType operandType, OperandScale operandScale)
    {
        Register first_reg = DecodeRegisterOperand(operandStart, operandType, operandScale);
        return new RegisterList(first_reg.Index, (int)count);
    }

    /// <summary>Decodes a signed operand in a byte array.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int DecodeSignedOperand(ReadOnlySpan<byte> operandStart, OperandType operandType,
                                          OperandScale operandScale)
    {
        Debug.Assert(!Bytecodes.IsUnsignedOperandType(operandType));
        return Bytecodes.SizeOfOperand(operandType, operandScale) switch
        {
            OperandSize.Byte => (sbyte)operandStart[0],
            OperandSize.Short => BinaryPrimitives.ReadInt16LittleEndian(operandStart),
            OperandSize.Quad => BinaryPrimitives.ReadInt32LittleEndian(operandStart),
            _ => throw new UnreachableException(),
        };
    }

    /// <summary>Decodes an unsigned operand in a byte array.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static uint DecodeUnsignedOperand(ReadOnlySpan<byte> operandStart, OperandType operandType,
                                             OperandScale operandScale)
    {
        Debug.Assert(Bytecodes.IsUnsignedOperandType(operandType));
        return Bytecodes.SizeOfOperand(operandType, operandScale) switch
        {
            OperandSize.Byte => operandStart[0],
            OperandSize.Short => BinaryPrimitives.ReadUInt16LittleEndian(operandStart),
            OperandSize.Quad => BinaryPrimitives.ReadUInt32LittleEndian(operandStart),
            _ => throw new UnreachableException(),
        };
    }

    /// <summary>Reads an embedded feedback byte (V8 reads it racily from concurrent compilers).</summary>
    public static byte RacyDecodeEmbeddedFeedback(ReadOnlySpan<byte> operandStart) => operandStart[0];

    static string NameForRuntimeId(FunctionId idx) => RuntimeFunctions.Name(idx);

    static string NameForNativeContextIndex(uint idx) =>
        NativeContextFields.NameForIndex((int)idx) ?? throw new UnreachableException();

    /// <summary>Decode a single bytecode and operands to a string.</summary>
    public static string Decode(ReadOnlySpan<byte> bytecodeStart, object[]? constantPool, bool withHex = true)
    {
        var os = new StringBuilder();
        Decode(os, bytecodeStart, constantPool, withHex);
        return os.ToString();
    }

    /// <summary>Decode a single bytecode and operands to |os|.</summary>
    public static StringBuilder Decode(StringBuilder os, ReadOnlySpan<byte> bytecodeStart, object[]? constantPool,
                                       bool withHex = true)
    {
        Bytecode bytecode = Bytecodes.FromByte(bytecodeStart[0]);
        int prefix_offset = 0;
        OperandScale operand_scale = OperandScale.Single;
        if (Bytecodes.IsPrefixScalingBytecode(bytecode))
        {
            prefix_offset = 1;
            operand_scale = Bytecodes.PrefixBytecodeToOperandScale(bytecode);
            bytecode = Bytecodes.FromByte(bytecodeStart[1]);
        }

        // Prepare to print bytecode and operands as hex digits.
        if (withHex)
        {
            int bytecode_size = Bytecodes.Size(bytecode, operand_scale);
            for (int i = 0; i < prefix_offset + bytecode_size; i++)
            {
                os.Append(bytecodeStart[i].ToString("x2", CultureInfo.InvariantCulture)).Append(' ');
            }
            const int kBytecodeColumnSize = Bytecodes.kMaxOperands + 1;
            for (int i = prefix_offset + bytecode_size; i < kBytecodeColumnSize; i++)
            {
                os.Append("   ");
            }
        }

        os.Append(Bytecodes.ToString(bytecode, operand_scale));

        // Operands for the debug break are from the original instruction.
        if (Bytecodes.IsDebugBreak(bytecode)) return os;

        int number_of_operands = Bytecodes.NumberOfOperands(bytecode);
        if (number_of_operands > 0) os.Append(' ');
        for (int i = 0; i < number_of_operands; i++)
        {
            OperandType op_type = Bytecodes.GetOperandType(bytecode, i);
            int operand_offset = Bytecodes.GetOperandOffset(bytecode, i, operand_scale);
            ReadOnlySpan<byte> operand_start = bytecodeStart[(prefix_offset + operand_offset)..];
            switch (op_type)
            {
                case OperandType.ConstantPoolIndex:
                {
                    uint idx = DecodeUnsignedOperand(operand_start, op_type, operand_scale);
                    object? obj = constantPool is not null && idx < constantPool.Length ? constantPool[idx] : null;
                    os.Append('[').Append(idx.ToString(CultureInfo.InvariantCulture)).Append(':');
                    if (obj is string str)
                    {
                        os.Append('"');
                        if (str.Length > kMaxShortPrintLength)
                        {
                            AppendUC16(os, str.AsSpan(0, kMaxShortPrintLength - 3));
                            os.Append("...");
                        }
                        else
                        {
                            AppendUC16(os, str);
                        }
                        os.Append('"');
                    }
                    else
                    {
                        os.Append(ConstantPrinting.Brief(obj));
                    }
                    os.Append(']');
                    break;
                }
                case OperandType.FeedbackSlot:
                    // TODO(leszeks): If we had the feedback metadata here, we could print
                    // the feedback slot type -- or with the feedback vector we could even
                    // print the feedback itself inline.
                    os.Append("FBV[")
                      .Append(DecodeUnsignedOperand(operand_start, op_type, operand_scale).ToString(CultureInfo.InvariantCulture))
                      .Append(']');
                    break;
                case OperandType.EmbeddedFeedback:
                {
                    byte feedback = RacyDecodeEmbeddedFeedback(operand_start);
                    os.Append("EmbeddedFeedback[");
                    if (Bytecodes.IsBinaryOpWithEmbeddedFeedback(bytecode) ||
                        Bytecodes.IsUnaryOpWithEmbeddedFeedback(bytecode))
                    {
                        os.Append(BinaryOperationFeedback.TypeIndexToString((BinaryOperationFeedback.TypeIndex)feedback));
                    }
                    else if (Bytecodes.IsCompareWithEmbeddedFeedback(bytecode))
                    {
                        os.Append(CompareOperationFeedback.TypeIndexToString((CompareOperationFeedback.TypeIndex)feedback));
                    }
                    else
                    {
                        os.Append(feedback.ToString(CultureInfo.InvariantCulture));
                    }
                    os.Append(']');
                    break;
                }
                case OperandType.ContextSlot:
                case OperandType.CoverageSlot:
                case OperandType.UImm:
                    os.Append('[')
                      .Append(DecodeUnsignedOperand(operand_start, op_type, operand_scale).ToString(CultureInfo.InvariantCulture))
                      .Append(']');
                    break;
                case OperandType.IntrinsicId:
                {
                    var id = (IntrinsicsHelper.IntrinsicId)DecodeUnsignedOperand(operand_start, op_type, operand_scale);
                    os.Append('[').Append(NameForRuntimeId(IntrinsicsHelper.ToRuntimeId(id))).Append(']');
                    break;
                }
                case OperandType.NativeContextIndex:
                {
                    uint id = DecodeUnsignedOperand(operand_start, op_type, operand_scale);
                    os.Append('[').Append(NameForNativeContextIndex(id)).Append(']');
                    break;
                }
                case OperandType.RuntimeId:
                    os.Append('[')
                      .Append(NameForRuntimeId((FunctionId)DecodeUnsignedOperand(operand_start, op_type, operand_scale)))
                      .Append(']');
                    break;
                case OperandType.AbortReason:
                    os.Append('[')
                      .Append(AbortReasons.GetAbortReason((AbortReason)DecodeUnsignedOperand(operand_start, op_type, operand_scale)))
                      .Append(']');
                    break;
                case OperandType.Imm:
                    os.Append('[')
                      .Append(DecodeSignedOperand(operand_start, op_type, operand_scale).ToString(CultureInfo.InvariantCulture))
                      .Append(']');
                    break;
                case OperandType.Flag8:
                case OperandType.Flag16:
                    os.Append('#')
                      .Append(DecodeUnsignedOperand(operand_start, op_type, operand_scale).ToString("x", CultureInfo.InvariantCulture));
                    break;
                case OperandType.Reg:
                case OperandType.RegOut:
                case OperandType.RegInOut:
                {
                    Register reg = DecodeRegisterOperand(operand_start, op_type, operand_scale);
                    os.Append(reg.ToString());
                    break;
                }
                case OperandType.RegOutTriple:
                {
                    RegisterList reg_list = DecodeRegisterListOperand(operand_start, 3, op_type, operand_scale);
                    os.Append(reg_list.FirstRegister().ToString()).Append('-').Append(reg_list.LastRegister().ToString());
                    break;
                }
                case OperandType.RegOutPair:
                case OperandType.RegPair:
                {
                    RegisterList reg_list = DecodeRegisterListOperand(operand_start, 2, op_type, operand_scale);
                    os.Append(reg_list.FirstRegister().ToString()).Append('-').Append(reg_list.LastRegister().ToString());
                    break;
                }
                case OperandType.RegOutList:
                case OperandType.RegList:
                {
                    Debug.Assert(i < number_of_operands - 1);
                    Debug.Assert(Bytecodes.GetOperandType(bytecode, i + 1) == OperandType.RegCount);
                    int reg_count_offset = Bytecodes.GetOperandOffset(bytecode, i + 1, operand_scale);
                    ReadOnlySpan<byte> reg_count_operand = bytecodeStart[(prefix_offset + reg_count_offset)..];
                    uint count = DecodeUnsignedOperand(reg_count_operand, OperandType.RegCount, operand_scale);
                    RegisterList reg_list = DecodeRegisterListOperand(operand_start, count, op_type, operand_scale);
                    os.Append(reg_list.FirstRegister().ToString()).Append('-').Append(reg_list.LastRegister().ToString());
                    i++; // Skip kRegCount.
                    break;
                }
                default:
                    // kNone, and kRegCount which is dealt with in kRegList.
                    throw new UnreachableException();
            }
            if (i != number_of_operands - 1) os.Append(", ");
        }
        return os;
    }

    // String::PrintUC16: printable ASCII as is, other code units escaped
    // (AsUC16: "\xNN" up to 0xFF, "\uNNNN" above).
    static void AppendUC16(StringBuilder os, ReadOnlySpan<char> s)
    {
        foreach (char c in s)
        {
            if (c >= 0x20 && c <= 0x7E) os.Append(c);
            else if (c <= 0xFF) os.Append("\\x").Append(((int)c).ToString("x2", CultureInfo.InvariantCulture));
            else os.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
        }
    }
}
