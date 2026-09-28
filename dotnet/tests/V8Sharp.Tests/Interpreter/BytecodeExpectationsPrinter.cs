// Port of test/unittests/interpreter/bytecode-expectations-printer.h/.cc and
// the file layout of generate-bytecode-expectations.cc.
//
// V8's printer compiles the snippet with a live isolate and prints the
// resulting BytecodeArray. The formatting is ported here against
// V8Sharp.Interpreter.BytecodeArray; compiling is delegated to an
// IBytecodeExpectationsCompiler.
// TODO(merge): the BytecodeGenerator port provides the compiler (parse,
// scope analysis, generate, then pick the top-level function, the global
// function named test_function_name, the module, or the callee).
using V8Sharp.Runtime;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using V8Sharp.Codegen;
using V8Sharp.Interpreter;

namespace V8Sharp.Tests.Interpreter;

/// <summary>Compiles a golden snippet the way BytecodeExpectationsPrinter::PrintExpectation does.</summary>
public interface IBytecodeExpectationsCompiler
{
    /// <summary>|sourceCode| is the snippet, already wrapped in a function when
    /// options.wrap; returns the bytecode of the top-level script, the module,
    /// the global function |functionName|, or the callee (options.print_callee).</summary>
    BytecodeArray Compile(string sourceCode, BytecodeExpectationsHeaderOptions options, string functionName);
}

public sealed class BytecodeExpectationsPrinter(IBytecodeExpectationsCompiler? compiler = null)
{
    const string kIndent = "  ";
    const string kDefaultTopFunctionName = "__genbckexp_wrapper__";
    const int kSystemPointerSize = 8;

    BytecodeExpectationsHeaderOptions _options = new();

    public void SetOptions(BytecodeExpectationsHeaderOptions options) => _options = options;

    static string NameForNativeContextIntrinsicIndex(uint idx)
    {
        if (idx == NativeContextFields.REFLECT_APPLY_INDEX) return "reflect_apply";
        if (idx == NativeContextFields.REFLECT_CONSTRUCT_INDEX) return "reflect_construct";
        return "UnknownIntrinsicIndex";
    }

    public static string WrapCodeInFunction(string functionName, string functionBody) =>
        "function " + functionName + "() {" + functionBody + "}\n" + functionName + "();";

    static void PrintEscapedString(StringBuilder stream, string str)
    {
        foreach (char c in str)
        {
            switch (c)
            {
                case '"': stream.Append("\\\""); break;
                case '\\': stream.Append("\\\\"); break;
                default: stream.Append(c); break;
            }
        }
    }

    static void PrintBytecodeOperand(StringBuilder stream, BytecodeArrayIterator bytecode_iterator, Bytecode bytecode,
                                     int op_index)
    {
        OperandType op_type = Bytecodes.GetOperandType(bytecode, op_index);
        OperandSize op_size = Bytecodes.GetOperandSize(bytecode, op_index, bytecode_iterator.CurrentOperandScale());

        string size_tag = op_size switch
        {
            OperandSize.Byte => "8",
            OperandSize.Short => "16",
            OperandSize.Quad => "32",
            _ => throw new UnreachableException(),
        };

        if (Bytecodes.IsRegisterOperandType(op_type))
        {
            Register register_value = bytecode_iterator.GetRegisterOperand(op_index);
            stream.Append('R');
            if (op_size != OperandSize.Byte) stream.Append(size_tag);
            if (register_value.IsCurrentContext)
            {
                stream.Append("(context)");
            }
            else if (register_value.IsFunctionClosure)
            {
                stream.Append("(closure)");
            }
            else if (register_value.IsParameter)
            {
                int parameter_index = register_value.ToParameterIndex();
                if (parameter_index == 0) stream.Append("(this)");
                else stream.Append("(arg").Append(Int(parameter_index - 1)).Append(')');
            }
            else
            {
                stream.Append('(').Append(Int(register_value.Index)).Append(')');
            }
            return;
        }

        switch (op_type)
        {
            case OperandType.Flag8:
                stream.Append("Flag").Append(size_tag).Append('(');
                stream.Append("0x").Append(bytecode_iterator.GetFlag8Operand(op_index).ToString("x", CultureInfo.InvariantCulture));
                break;
            case OperandType.Flag16:
                stream.Append("Flag").Append(size_tag).Append('(');
                stream.Append("0x").Append(bytecode_iterator.GetFlag16Operand(op_index).ToString("x", CultureInfo.InvariantCulture));
                break;
            case OperandType.EmbeddedFeedback:
                // Ignore embedded feedback bytes in bytecode expectation test.
                Debug.Assert(Bytecodes.IsEmbeddedFeedbackBytecode(bytecode));
                stream.Append("EmbeddedFeedback(");
                break;
            case OperandType.ConstantPoolIndex:
            {
                stream.Append('U').Append(size_tag).Append('(');
                stream.Append(UInt(bytecode_iterator.GetConstantPoolIndexOperand(op_index)));
                object constant = bytecode_iterator.GetConstantForOperand(op_index);
                stream.Append(':');
                // For strings, just print the value, since the instance type isn't that
                // interesting (and is in the constant pool if we need it).
                if (constant is string str)
                {
                    ConstantPrinting.PrintV8String(stream, str);
                }
                else
                {
                    // Otherwise print the full constant with instance type, same as in
                    // the constant pool.
                    ConstantPrinting.PrintConstant(stream, constant);
                }
                break;
            }
            case OperandType.FeedbackSlot:
                stream.Append("FBV");
                if (op_size != OperandSize.Byte) stream.Append(size_tag);
                stream.Append('(').Append(UInt(bytecode_iterator.GetFeedbackSlotOperand(op_index)));
                break;
            case OperandType.ContextSlot:
                stream.Append('C').Append(size_tag).Append('(').Append(UInt(bytecode_iterator.GetContextSlotOperand(op_index)));
                break;
            case OperandType.CoverageSlot:
                stream.Append('c').Append(size_tag).Append('(').Append(UInt(bytecode_iterator.GetCoverageSlotOperand(op_index)));
                break;
            case OperandType.UImm:
                stream.Append('U').Append(size_tag).Append('(')
                      .Append(UInt(bytecode_iterator.GetUnsignedImmediateOperand(op_index)));
                break;
            case OperandType.Imm:
                stream.Append('I').Append(size_tag).Append('(').Append(Int(bytecode_iterator.GetImmediateOperand(op_index)));
                break;
            case OperandType.RegCount:
                stream.Append("RegCount").Append(size_tag).Append('(')
                      .Append(UInt(bytecode_iterator.GetRegisterCountOperand(op_index)));
                break;
            case OperandType.RuntimeId:
            {
                stream.Append('U').Append(size_tag).Append('(');
                FunctionId id = bytecode_iterator.GetRuntimeIdOperand(op_index);
                stream.Append("Runtime::k").Append(RuntimeFunctions.Name(id));
                break;
            }
            case OperandType.IntrinsicId:
            {
                stream.Append('U').Append(size_tag).Append('(');
                FunctionId id = bytecode_iterator.GetIntrinsicIdOperand(op_index);
                stream.Append("Runtime::k").Append(RuntimeFunctions.Name(id));
                break;
            }
            case OperandType.NativeContextIndex:
            {
                stream.Append('U').Append(size_tag).Append('(');
                uint idx = bytecode_iterator.GetNativeContextIndexOperand(op_index);
                stream.Append('%').Append(NameForNativeContextIntrinsicIndex(idx));
                break;
            }
            case OperandType.AbortReason:
            {
                stream.Append('U').Append(size_tag).Append('(');
                AbortReason reason = bytecode_iterator.GetAbortReasonOperand(op_index);
                if (AbortReasons.IsValidAbortReason((int)reason))
                    stream.Append("AbortReason::").Append(AbortReasons.GetAbortReason(reason));
                else
                    stream.Append("Invalid abort reason: ").Append(Int((int)reason));
                break;
            }
            default:
                throw new UnreachableException();
        }

        stream.Append(')');
    }

    static string Int(int v) => v.ToString(CultureInfo.InvariantCulture);
    static string UInt(uint v) => v.ToString(CultureInfo.InvariantCulture);

    static void PrintBytecode(StringBuilder stream, BytecodeArrayIterator bytecode_iterator)
    {
        Bytecode bytecode = bytecode_iterator.CurrentBytecode();
        OperandScale operand_scale = bytecode_iterator.CurrentOperandScale();
        if (Bytecodes.OperandScaleRequiresPrefixBytecode(operand_scale))
        {
            Bytecode prefix = Bytecodes.OperandScaleToPrefixBytecode(operand_scale);
            stream.Append("B(").Append(Bytecodes.ToString(prefix)).Append("), ");
        }
        stream.Append("B(").Append(Bytecodes.ToString(bytecode)).Append(')');
        int operands_count = Bytecodes.NumberOfOperands(bytecode);
        for (int op_index = 0; op_index < operands_count; ++op_index)
        {
            stream.Append(", ");
            PrintBytecodeOperand(stream, bytecode_iterator, bytecode, op_index);
        }
    }

    static void PrintSourcePosition(StringBuilder stream, ref SourcePositionTableIterator source_iterator,
                                    int bytecode_offset)
    {
        const int kPositionWidth = 4;
        if (!source_iterator.Done() && source_iterator.CodeOffset() == bytecode_offset)
        {
            stream.Append("/* ")
                  .Append(Int(source_iterator.SourcePosition().ScriptOffset()).PadLeft(kPositionWidth));
            if (source_iterator.IsStatement())
                stream.Append(source_iterator.IsBreakable() ? " S> */ " : " s> */ ");
            else
                stream.Append(source_iterator.IsBreakable() ? " E> */ " : " e> */ ");
            source_iterator.Advance();
        }
        else
        {
            stream.Append("   ").Append(' ', kPositionWidth).Append("       ");
        }
    }

    static void PrintFrameSize(StringBuilder stream, BytecodeArray bytecode_array)
    {
        int frame_size = bytecode_array.FrameSize;
        Debug.Assert(frame_size % kSystemPointerSize == 0);
        stream.Append("frame size: ").Append(Int(frame_size / kSystemPointerSize))
              .Append("\nparameter count: ").Append(Int(bytecode_array.ParameterCount)).Append('\n');
    }

    static void PrintBytecodeSequence(StringBuilder stream, BytecodeArray bytecode_array)
    {
        stream.Append("bytecode array length: ").Append(Int(bytecode_array.Length)).Append("\nbytecodes: [\n");

        var source_iterator = new SourcePositionTableIterator(bytecode_array.SourcePositionTable);
        for (var bytecode_iterator = new BytecodeArrayIterator(bytecode_array); !bytecode_iterator.Done();
             bytecode_iterator.Advance())
        {
            stream.Append(kIndent);
            PrintSourcePosition(stream, ref source_iterator, bytecode_iterator.CurrentOffset());
            PrintBytecode(stream, bytecode_iterator);
            stream.Append(",\n");
        }
        stream.Append("]\n");
    }

    static void PrintConstantPool(StringBuilder stream, object[] constant_pool)
    {
        stream.Append("constant pool: [\n");
        foreach (object constant in constant_pool)
        {
            stream.Append(kIndent);
            ConstantPrinting.PrintConstant(stream, constant);
            stream.Append(",\n");
        }
        stream.Append("]\n");
    }

    public void PrintCodeSnippet(StringBuilder stream, string body)
    {
        stream.Append("snippet: \"\n");
        // std::getline semantics: a final newline does not start another line.
        int start = 0;
        while (start < body.Length)
        {
            int nl = body.IndexOf('\n', start);
            string body_line = nl < 0 ? body[start..] : body[start..nl];
            stream.Append(kIndent);
            PrintEscapedString(stream, body_line);
            stream.Append('\n');
            if (nl < 0) break;
            start = nl + 1;
        }
        stream.Append("\"\n");
    }

    static void PrintHandlers(StringBuilder stream, BytecodeArray bytecode_array)
    {
        stream.Append("handlers: [\n");
        var table = new HandlerTable(bytecode_array.HandlerTable);
        for (uint i = 0, num_entries = table.NumberOfRangeEntries(); i < num_entries; ++i)
        {
            stream.Append("  [").Append(Int(table.GetRangeStart(i))).Append(", ").Append(Int(table.GetRangeEnd(i)))
                  .Append(", ").Append(Int(table.GetRangeHandler(i))).Append("],\n");
        }
        stream.Append("]\n");
    }

    public void PrintBytecodeArray(StringBuilder stream, BytecodeArray bytecode_array)
    {
        PrintFrameSize(stream, bytecode_array);
        PrintBytecodeSequence(stream, bytecode_array);
        PrintConstantPool(stream, bytecode_array.ConstantPool);
        PrintHandlers(stream, bytecode_array);
    }

    /// <summary>Compiles |snippet| and prints its expectation (needs a compiler).</summary>
    public void PrintExpectation(StringBuilder stream, string snippet)
    {
        if (compiler is null)
            throw new InvalidOperationException("PrintExpectation needs an IBytecodeExpectationsCompiler");
        string test_function_name = _options.test_function_name.Length == 0
            ? kDefaultTopFunctionName
            : _options.test_function_name;
        string source_code = _options.wrap ? WrapCodeInFunction(test_function_name, snippet) : snippet;
        BytecodeArray bytecode_array = compiler.Compile(source_code, _options, test_function_name);
        PrintCodeSnippet(stream, snippet);
        PrintBytecodeArray(stream, bytecode_array);
        stream.Append('\n');
    }

    /// <summary>ProgramOptions::PrintHeader of generate-bytecode-expectations.cc.</summary>
    public static void PrintHeader(StringBuilder stream, BytecodeExpectationsHeaderOptions header_options)
    {
        stream.Append("---").Append("\nwrap: ").Append(header_options.wrap ? "yes" : "no");
        if (header_options.test_function_name.Length != 0)
            stream.Append("\ntest function name: ").Append(header_options.test_function_name);
        if (header_options.module) stream.Append("\nmodule: yes");
        if (header_options.top_level) stream.Append("\ntop level: yes");
        if (header_options.print_callee) stream.Append("\nprint callee: yes");
        if (header_options.extra_flags.Length != 0) stream.Append("\nextra flags: ").Append(header_options.extra_flags);
        stream.Append("\n\n");
    }

    /// <summary>The preamble GenerateExpectationsFile writes before the header.</summary>
    public const string kFilePreamble = "#\n# Autogenerated by generate-bytecode-expectations.\n#\n\n";
}
