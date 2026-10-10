// Turns the expectation text of a golden file (frame size, parameter count,
// bytecodes with source positions, constant pool, handlers) back into a
// BytecodeArray, so that printing it with BytecodeExpectationsPrinter can be
// checked against the text it came from. Not a V8 file: V8 only prints.
using V8Sharp.Runtime;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using V8Sharp.Codegen;
using V8Sharp.Interpreter;

namespace V8Sharp.Tests.Interpreter;

public static class GoldenBytecodeAssembler
{
    public sealed class GoldenFormatException(string message) : Exception(message);

    public static BytecodeArray Assemble(string expectation)
    {
        string[] lines = expectation.Split('\n');
        int i = 0;

        string Next()
        {
            while (i < lines.Length && lines[i].Trim().Length == 0) i++;
            if (i >= lines.Length) throw new GoldenFormatException("unexpected end of expectation");
            return lines[i++];
        }

        int frame_size = ParseIntAfter(Next(), "frame size: ");
        int parameter_count = ParseIntAfter(Next(), "parameter count: ");
        int length = ParseIntAfter(Next(), "bytecode array length: ");
        Expect(Next(), "bytecodes: [");

        var bytes = new List<byte>();
        var positions = new SourcePositionTableBuilder();
        string line;
        while ((line = Next()) != "]")
        {
            AssembleBytecodeLine(line, bytes, positions);
        }
        if (bytes.Count != length)
            throw new GoldenFormatException($"assembled {bytes.Count} bytes, expected bytecode array length {length}");

        Expect(Next(), "constant pool: [");
        var constants = new List<object>();
        while ((line = Next()) != "]")
        {
            constants.Add(ParseConstant(StripListItem(line)));
        }

        Expect(Next(), "handlers: [");
        var handlers = new HandlerTableBuilder();
        while ((line = Next()) != "]")
        {
            string item = StripListItem(line);
            if (!item.StartsWith('[') || !item.EndsWith(']')) throw new GoldenFormatException("bad handler: " + line);
            string[] parts = item[1..^1].Split(", ");
            int id = handlers.NewHandlerEntry();
            handlers.SetTryRegionStart(id, int.Parse(parts[0], CultureInfo.InvariantCulture));
            handlers.SetTryRegionEnd(id, int.Parse(parts[1], CultureInfo.InvariantCulture));
            handlers.SetHandlerTarget(id, int.Parse(parts[2], CultureInfo.InvariantCulture));
        }

        var array = new BytecodeArray([.. bytes], frame_size * 8, checked((ushort)parameter_count), 0,
                                      [.. constants], handlers.ToHandlerTable())
        {
            SourcePositionTableOrNull = positions.ToSourcePositionTable(),
        };
        return array;
    }

    static int ParseIntAfter(string line, string prefix)
    {
        if (!line.StartsWith(prefix, StringComparison.Ordinal)) throw new GoldenFormatException("expected " + prefix + ": " + line);
        return int.Parse(line[prefix.Length..], CultureInfo.InvariantCulture);
    }

    static void Expect(string line, string expected)
    {
        if (line != expected) throw new GoldenFormatException("expected '" + expected + "', got '" + line + "'");
    }

    static string StripListItem(string line)
    {
        string item = line.Trim();
        if (!item.EndsWith(',')) throw new GoldenFormatException("list item without ',': " + line);
        return item[..^1];
    }

    static void AssembleBytecodeLine(string line, List<byte> bytes, SourcePositionTableBuilder positions)
    {
        if (!line.StartsWith("  ", StringComparison.Ordinal)) throw new GoldenFormatException("bad bytecode line: " + line);
        string rest = line[2..];
        // "/* 1234 S> */ " or 14 spaces.
        if (rest.StartsWith("/*", StringComparison.Ordinal))
        {
            int end = rest.IndexOf("*/", StringComparison.Ordinal);
            string marker = rest[2..end].Trim(); // "42 S>"
            string[] parts = marker.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            int pos = int.Parse(parts[0], CultureInfo.InvariantCulture);
            char kind = parts[1][0];
            bool is_statement = kind is 'S' or 's';
            bool is_breakable = kind is 'S' or 'E';
            positions.AddPosition(bytes.Count, new SourcePosition(pos), is_statement, is_breakable);
            rest = rest[(end + 2)..];
        }
        rest = rest.Trim();
        if (!rest.EndsWith(',')) throw new GoldenFormatException("bytecode line without ',': " + line);
        List<string> tokens = SplitOperands(rest[..^1]);

        int t = 0;
        OperandScale scale = OperandScale.Single;
        Bytecode bytecode = ParseBytecodeToken(tokens[t++]);
        if (Bytecodes.IsPrefixScalingBytecode(bytecode))
        {
            bytes.Add(Bytecodes.ToByte(bytecode));
            scale = Bytecodes.PrefixBytecodeToOperandScale(bytecode);
            bytecode = ParseBytecodeToken(tokens[t++]);
        }
        bytes.Add(Bytecodes.ToByte(bytecode));

        int operand_count = Bytecodes.NumberOfOperands(bytecode);
        if (tokens.Count - t != operand_count)
            throw new GoldenFormatException($"{Bytecodes.ToString(bytecode)} expects {operand_count} operands: {line}");
        ReadOnlySpan<OperandSize> sizes = Bytecodes.GetOperandSizes(bytecode, scale);
        for (int i = 0; i < operand_count; i++)
        {
            OperandType type = Bytecodes.GetOperandType(bytecode, i);
            uint value = ParseOperand(bytecode, type, sizes[i], tokens[t + i]);
            switch (sizes[i])
            {
                case OperandSize.Byte:
                    bytes.Add((byte)value);
                    break;
                case OperandSize.Short:
                    bytes.Add((byte)value);
                    bytes.Add((byte)(value >> 8));
                    break;
                case OperandSize.Quad:
                    bytes.Add((byte)value);
                    bytes.Add((byte)(value >> 8));
                    bytes.Add((byte)(value >> 16));
                    bytes.Add((byte)(value >> 24));
                    break;
                default:
                    throw new UnreachableException();
            }
        }
    }

    static Bytecode ParseBytecodeToken(string token)
    {
        if (!token.StartsWith("B(", StringComparison.Ordinal) || !token.EndsWith(')'))
            throw new GoldenFormatException("expected B(...): " + token);
        string name = token[2..^1];
        if (!Enum.TryParse(name, ignoreCase: false, out Bytecode bytecode) || Bytecodes.ToString(bytecode) != name)
            throw new GoldenFormatException("unknown bytecode: " + name);
        return bytecode;
    }

    /// <summary>Splits "R(0), U8(1:\"a, b\"), ..." at top-level ", ".</summary>
    static List<string> SplitOperands(string text)
    {
        var tokens = new List<string>();
        int depth = 0;
        bool in_string = false;
        int start = 0;
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (in_string)
            {
                if (c == '\\') i++;
                else if (c == '"') in_string = false;
                continue;
            }
            switch (c)
            {
                case '"': in_string = true; break;
                case '(':
                case '[': depth++; break;
                case ')':
                case ']': depth--; break;
                case ',' when depth == 0:
                    tokens.Add(text[start..i].Trim());
                    start = i + 1;
                    break;
            }
        }
        tokens.Add(text[start..].Trim());
        return tokens;
    }

    static (string Tag, string Payload) SplitToken(string token)
    {
        int open = token.IndexOf('(');
        if (open < 0 || !token.EndsWith(')')) throw new GoldenFormatException("bad operand: " + token);
        return (token[..open], token[(open + 1)..^1]);
    }

    static string SizeTag(OperandSize size) => size switch
    {
        OperandSize.Byte => "8",
        OperandSize.Short => "16",
        OperandSize.Quad => "32",
        _ => throw new UnreachableException(),
    };

    static void ExpectTag(string actual, string expected, string token)
    {
        if (actual != expected) throw new GoldenFormatException($"expected operand tag {expected}: {token}");
    }

    static uint ParseOperand(Bytecode bytecode, OperandType type, OperandSize size, string token)
    {
        (string tag, string payload) = SplitToken(token);
        string size_tag = SizeTag(size);
        if (Bytecodes.IsRegisterOperandType(type))
        {
            ExpectTag(tag, size == OperandSize.Byte ? "R" : "R" + size_tag, token);
            Register reg = payload switch
            {
                "context" => Register.CurrentContext(),
                "closure" => Register.FunctionClosure(),
                "this" => Register.FromParameterIndex(0),
                _ when payload.StartsWith("arg", StringComparison.Ordinal) =>
                    Register.FromParameterIndex(int.Parse(payload[3..], CultureInfo.InvariantCulture) + 1),
                _ => new Register(int.Parse(payload, CultureInfo.InvariantCulture)),
            };
            return (uint)reg.ToOperand();
        }
        switch (type)
        {
            case OperandType.Flag8:
            case OperandType.Flag16:
                ExpectTag(tag, "Flag" + size_tag, token);
                if (!payload.StartsWith("0x", StringComparison.Ordinal)) throw new GoldenFormatException("bad flag: " + token);
                return uint.Parse(payload[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            case OperandType.EmbeddedFeedback:
                ExpectTag(tag, "EmbeddedFeedback", token);
                if (payload.Length != 0) throw new GoldenFormatException("bad embedded feedback: " + token);
                return InterpreterConstants.kUninitializedEmbeddedFeedback;
            case OperandType.ConstantPoolIndex:
            {
                ExpectTag(tag, "U" + size_tag, token);
                int colon = payload.IndexOf(':');
                return uint.Parse(payload[..colon], CultureInfo.InvariantCulture);
            }
            case OperandType.FeedbackSlot:
                ExpectTag(tag, size == OperandSize.Byte ? "FBV" : "FBV" + size_tag, token);
                return uint.Parse(payload, CultureInfo.InvariantCulture);
            case OperandType.ContextSlot:
                ExpectTag(tag, "C" + size_tag, token);
                return uint.Parse(payload, CultureInfo.InvariantCulture);
            case OperandType.CoverageSlot:
                ExpectTag(tag, "c" + size_tag, token);
                return uint.Parse(payload, CultureInfo.InvariantCulture);
            case OperandType.UImm:
                ExpectTag(tag, "U" + size_tag, token);
                return uint.Parse(payload, CultureInfo.InvariantCulture);
            case OperandType.Imm:
                ExpectTag(tag, "I" + size_tag, token);
                return (uint)int.Parse(payload, CultureInfo.InvariantCulture);
            case OperandType.RegCount:
                ExpectTag(tag, "RegCount" + size_tag, token);
                return uint.Parse(payload, CultureInfo.InvariantCulture);
            case OperandType.RuntimeId:
            case OperandType.IntrinsicId:
            {
                ExpectTag(tag, "U" + size_tag, token);
                const string prefix = "Runtime::k";
                if (!payload.StartsWith(prefix, StringComparison.Ordinal) ||
                    !RuntimeFunctions.TryFromName(payload[prefix.Length..], out FunctionId id))
                {
                    throw new GoldenFormatException("unknown runtime function: " + token);
                }
                return type == OperandType.RuntimeId ? (uint)id : (uint)IntrinsicsHelper.FromRuntimeId(id);
            }
            case OperandType.NativeContextIndex:
            {
                ExpectTag(tag, "U" + size_tag, token);
                int index = payload.StartsWith('%') ? NativeContextFields.IndexForName(payload[1..]) : -1;
                if (index < 0) throw new GoldenFormatException("unknown native context index: " + token);
                return (uint)index;
            }
            case OperandType.AbortReason:
            {
                ExpectTag(tag, "U" + size_tag, token);
                const string prefix = "AbortReason::";
                for (var r = AbortReason.NoReason; r < AbortReason.LastErrorMessage; r++)
                {
                    if (payload == prefix + AbortReasons.GetAbortReason(r)) return (uint)r;
                }
                throw new GoldenFormatException("unknown abort reason: " + token);
            }
            default:
                throw new GoldenFormatException($"unexpected operand type {BytecodeOperands.ToString(type)} in {Bytecodes.ToString(bytecode)}");
        }
    }

    /// <summary>Parses a printed constant back into the value the printer prints the same way.</summary>
    public static object ParseConstant(string text)
    {
        int bracket = text.IndexOf(" [", StringComparison.Ordinal);
        string type = bracket < 0 ? text : text[..bracket];
        string? value = bracket < 0 ? null : text[(bracket + 2)..^1];
        if (type == "Smi" && value is not null)
        {
            return Smi.FromInt(int.Parse(value, CultureInfo.InvariantCulture));
        }
        // Heap numbers and strings must come back as double and string, so the
        // printer's number and string formatting is what is checked.
        if (type == "HEAP_NUMBER_TYPE" && value is not null)
        {
            if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double d) &&
                ConstantPrinting.PrintDouble(d) == value)
            {
                return d;
            }
            throw new GoldenFormatException("heap number does not round-trip: " + text);
        }
        if (type.EndsWith("_STRING_TYPE", StringComparison.Ordinal) && value is not null)
        {
            if (value.StartsWith('"') && value.EndsWith('"'))
            {
                string s = UnescapeJsonString(value[1..^1]);
                if (ConstantPrinting.StringInstanceTypeName(s) == type) return s;
            }
            throw new GoldenFormatException("string does not round-trip: " + text);
        }
        return new PrintableConstant(type, value, text);
    }

    // Inverse of AsEscapedUC16ForJSON.
    static string UnescapeJsonString(string s)
    {
        var sb = new StringBuilder(s.Length);
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            if (c != '\\')
            {
                sb.Append(c);
                continue;
            }
            char e = s[++i];
            switch (e)
            {
                case 'n': sb.Append('\n'); break;
                case 'r': sb.Append('\r'); break;
                case 't': sb.Append('\t'); break;
                case '"': sb.Append('"'); break;
                case 'u':
                    sb.Append((char)int.Parse(s.AsSpan(i + 1, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture));
                    i += 4;
                    break;
                default:
                    sb.Append('\\').Append(e);
                    break;
            }
        }
        return sb.ToString();
    }
}
