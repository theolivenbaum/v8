// The data of V8's BytecodeArray (src/objects/bytecode-array.h/.cc), as a plain
// class for the interpreter infrastructure.
//
// TODO(merge): the object model port owns the heap object; this class should
// become (or be wrapped by) V8Sharp.Objects.BytecodeArray : HeapObject. The
// constant pool is object[] until constants are heap objects: entries are
// Smi (boxed), double (heap numbers), string (internalized strings) or
// whatever the IConstantPoolMaterializer produced.
using System.Globalization;
using System.Text;
using V8Sharp.Codegen;

namespace V8Sharp.Interpreter;

/// <summary>
/// A constant-pool entry that knows how V8 prints it: its instance type name
/// (BytecodeExpectationsPrinter::PrintConstant) and its Brief() form
/// (BytecodeDecoder). Heap objects of the object model can implement this.
/// </summary>
public interface IPrintableConstant
{
    /// <summary>V8's InstanceType name, e.g. "SHARED_FUNCTION_INFO_TYPE".</summary>
    string InstanceTypeName { get; }

    /// <summary>The " [...]" suffix the expectations printer adds after the type
    /// (heap numbers and strings), or null.</summary>
    string? PrintedValue { get; }

    /// <summary>V8's Brief(obj) text, used by the disassembler.</summary>
    string Brief();
}

/// <summary>BytecodeArray represents a sequence of interpreter bytecodes.</summary>
public sealed class BytecodeArray
{
    const int kSystemPointerSize = 8;

    readonly byte[] _bytecodes;

    public BytecodeArray(byte[] bytecodes, int frameSize, ushort parameterCount, ushort maxArguments,
                         object[] constantPool, byte[] handlerTable)
    {
        _bytecodes = bytecodes;
        FrameSize = frameSize;
        ParameterCount = parameterCount;
        MaxArguments = maxArguments;
        ConstantPool = constantPool;
        HandlerTable = handlerTable;
    }

    /// <summary>The bytecodes (length() bytes).</summary>
    public byte[] Bytecodes => _bytecodes;

    /// <summary>The length of this bytecode array, in bytes.</summary>
    public int Length => _bytecodes.Length;

    public byte Get(int index) => _bytecodes[index];
    public void Set(int index, byte value) => _bytecodes[index] = value;

    /// <summary>The frame size in bytes (register_count * kSystemPointerSize), as in V8.</summary>
    public int FrameSize { get; set; }

    /// <summary>The register count is derived from frame_size.</summary>
    public int RegisterCount => FrameSize / kSystemPointerSize;

    /// <summary>The parameter count includes the implicit 'this' receiver.</summary>
    public ushort ParameterCount { get; set; }

    public ushort ParameterCountWithoutReceiver => (ushort)(ParameterCount - InterpreterConstants.kJSArgcReceiverSlots);

    public ushort MaxArguments { get; set; }

    /// <summary>max_frame_size: frame_size + max_arguments * kSystemPointerSize.</summary>
    public int MaxFrameSize => FrameSize + MaxArguments * kSystemPointerSize;

    public object[] ConstantPool { get; set; }

    /// <summary>The handler table (range encoding, see <see cref="Codegen.HandlerTable"/>).</summary>
    public byte[] HandlerTable { get; set; }

    /// <summary>The source position table; null until collected (V8's Smi::zero()).</summary>
    public byte[]? SourcePositionTableOrNull { get; set; }

    public bool HasSourcePositionTable => SourcePositionTableOrNull is not null;

    /// <summary>The source position table, or an empty one if it was not collected.</summary>
    public byte[] SourcePositionTable => SourcePositionTableOrNull ?? [];

    int _incomingNewTargetOrGeneratorRegister;

    public Register IncomingNewTargetOrGeneratorRegister
    {
        get => _incomingNewTargetOrGeneratorRegister == 0
            ? Register.InvalidValue()
            : Register.FromOperand(_incomingNewTargetOrGeneratorRegister);
        set
        {
            if (!value.IsValid)
            {
                _incomingNewTargetOrGeneratorRegister = 0;
            }
            else
            {
                Debug.Assert(value.Index < RegisterCount);
                Debug.Assert(value.ToOperand() != 0);
                _incomingNewTargetOrGeneratorRegister = value.ToOperand();
            }
        }
    }

    public int SourcePosition(int offset)
    {
        int position = 0;
        if (!HasSourcePositionTable) return position;
        for (var it = new SourcePositionTableIterator(SourcePositionTable,
                 SourcePositionTableIterator.IterationFilter.kJavaScriptOnly,
                 SourcePositionTableIterator.FunctionEntryFilter.kDontSkipFunctionEntry);
             !it.Done() && it.CodeOffset() <= offset; it.Advance())
        {
            position = it.SourcePosition().ScriptOffset();
        }
        return position;
    }

    public int SourceStatementPosition(int offset)
    {
        int position = 0;
        if (!HasSourcePositionTable) return position;
        for (var it = new SourcePositionTableIterator(SourcePositionTable);
             !it.Done() && it.CodeOffset() <= offset; it.Advance())
        {
            if (it.IsStatement()) position = it.SourcePosition().ScriptOffset();
        }
        return position;
    }

    /// <summary>
    /// BytecodeArray::Disassemble. V8 prints the address of every bytecode; the
    /// port prints |baseAddress| + offset instead (there are no addresses).
    /// </summary>
    public string Disassemble(ulong baseAddress = 0)
    {
        var os = new StringBuilder();
        os.Append("Parameter count ").Append(ParameterCount.ToString(CultureInfo.InvariantCulture)).Append('\n');
        os.Append("Register count ").Append(RegisterCount.ToString(CultureInfo.InvariantCulture)).Append('\n');
        os.Append("Frame size ").Append(FrameSize.ToString(CultureInfo.InvariantCulture)).Append('\n');

        var source_positions = new SourcePositionTableIterator(SourcePositionTable);
        for (var iterator = new BytecodeArrayIterator(this); !iterator.Done(); iterator.Advance())
        {
            if (!source_positions.Done() && iterator.CurrentOffset() == source_positions.CodeOffset())
            {
                os.Append(source_positions.SourcePosition().ScriptOffset().ToString(CultureInfo.InvariantCulture).PadLeft(5));
                if (source_positions.IsBreakable())
                    os.Append(source_positions.IsStatement() ? " S> " : " E> ");
                else
                    os.Append(source_positions.IsStatement() ? " s> " : " e> ");
                source_positions.Advance();
            }
            else
            {
                os.Append("         ");
            }
            os.Append("0x").Append((baseAddress + (ulong)iterator.CurrentOffset()).ToString("x", CultureInfo.InvariantCulture))
              .Append(" @ ").Append(iterator.CurrentOffset().ToString(CultureInfo.InvariantCulture).PadLeft(4)).Append(" : ");
            iterator.PrintCurrentBytecodeTo(os);
            if (Interpreter.Bytecodes.IsJump(iterator.CurrentBytecode()))
            {
                int target = iterator.GetJumpTargetOffset();
                os.Append(" (0x").Append((baseAddress + (ulong)target).ToString("x", CultureInfo.InvariantCulture))
                  .Append(" @ ").Append(target.ToString(CultureInfo.InvariantCulture)).Append(')');
            }
            if (Interpreter.Bytecodes.IsSwitch(iterator.CurrentBytecode()))
            {
                os.Append(" {");
                bool first_entry = true;
                foreach (JumpTableTargetOffset entry in iterator.GetJumpTableTargetOffsets())
                {
                    if (first_entry) first_entry = false;
                    else os.Append(',');
                    os.Append(' ').Append(entry.CaseValue.ToString(CultureInfo.InvariantCulture))
                      .Append(": @").Append(entry.TargetOffset.ToString(CultureInfo.InvariantCulture));
                }
                os.Append(" }");
            }
            os.Append('\n');
        }

        os.Append("Constant pool (size = ").Append(ConstantPool.Length.ToString(CultureInfo.InvariantCulture)).Append(")\n");
        os.Append("Handler Table (size = ").Append(HandlerTable.Length.ToString(CultureInfo.InvariantCulture)).Append(")\n");
        if (HandlerTable.Length > 0) os.Append(new Codegen.HandlerTable(HandlerTable).HandlerTableRangePrint());
        os.Append("Source Position Table (size = ")
          .Append(SourcePositionTable.Length.ToString(CultureInfo.InvariantCulture)).Append(")\n");
        return os.ToString();
    }
}
