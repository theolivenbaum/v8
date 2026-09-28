// Frame constants and small helpers shared by the interpreter's dispatch loop:
// InterpreterFrameConstants (src/execution/frame-constants.h, x64 layout),
// constant pool materialization, the context chain walk of
// InterpreterAssembler::GetContextAtDepth, and source positions for frames.
using System.Globalization;
using System.Runtime.CompilerServices;

namespace V8Sharp.Interpreter;

public static class InterpreterRuntime
{
    // Offsets from the frame pointer (register index = fp-relative slot).
    public const int kReceiverOffset = -9;
    public const int kFirstArgumentOffset = -10;
    public const int kContextOffset = -6;
    public const int kClosureOffset = -5;
    public const int kArgcOffset = -4;
    public const int kFeedbackVectorOffset = -1;
    /// <summary>The receiver plus the eight fixed slots between the parameters and the register file.</summary>
    public const int kFixedSlotsAboveParams = 9;
    /// <summary>The register stack slot of register operand 0 relative to fp (Register::FromOperand(0) = -7).</summary>
    public const int kRegisterOperandBase = -7;

    /// <summary>The actual arguments of an interpreted frame (without the receiver).</summary>
    public static JSValue[] GetFrameArguments(Isolate isolate, int fp, int argc)
    {
        if (argc == 0) return [];
        var result = new JSValue[argc];
        JSValue[] stack = isolate.RegisterStack;
        for (int i = 0; i < argc; i++) result[i] = stack[fp + kFirstArgumentOffset - i];
        return result;
    }

    /// <summary>Argument <paramref name="i"/> (0-based) of the frame at <paramref name="fp"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ref JSValue ArgumentSlot(JSValue[] stack, int fp, int i) => ref stack[fp + kFirstArgumentOffset - i];

    /// <summary>The source position of the bytecode at <paramref name="offset"/> (AbstractCode::SourcePosition).</summary>
    public static int SourcePositionAt(Isolate isolate, SharedFunctionInfo shared, BytecodeArray bytecode, int offset)
    {
        if (!bytecode.HasSourcePositionTable)
        {
            // Source positions are collected eagerly (see deviations.md), so a
            // missing table means the function has none (e.g. builtin wrappers).
            return shared.StartPosition() < 0 ? 0 : shared.StartPosition();
        }
        return bytecode.SourcePosition(offset);
    }

    /// <summary>InterpreterAssembler::GetContextAtDepth.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Context GetContextAtDepth(Context context, int depth)
    {
        while (depth-- > 0) context = Unsafe.As<Context>(context.Slots[(int)Context.Field.PREVIOUS_INDEX]._obj!);
        return context;
    }

    /// <summary>
    /// Converts the constant pool to values once (V8's constant pool already
    /// holds tagged values; V8Sharp's BytecodeArray keeps what the constant array
    /// builder's materializer produced).
    /// </summary>
    public static JSValue[] MaterializeConstantPool(Isolate isolate, BytecodeArray bytecode)
    {
        object[] pool = bytecode.ConstantPool;
        var values = new JSValue[pool.Length];
        for (int i = 0; i < pool.Length; i++) values[i] = ToValue(isolate, pool[i]);
        bytecode.ConstantPoolValues = values;
        return values;
    }

    /// <summary>One constant pool entry as a JSValue.</summary>
    public static JSValue ToValue(Isolate isolate, object? entry) => entry switch
    {
        null => JSValue.Undefined,
        JSValue v => v,
        HeapObject o => new JSValue(o),
        Smi smi => JSValue.FromInt(smi.Value),
        int i => JSValue.FromInt(i),
        double d => JSValue.FromNumber(d),
        string s => isolate.Factory.InternalizeString(s),
        PrintableConstant p when p.Brief() == "<the_hole_value>" => JSValue.TheHole,
        _ => throw new InvalidOperationException(
            "V8Sharp: constant pool entry of type " + entry.GetType().Name + " cannot be materialized: " +
            Convert.ToString(entry, CultureInfo.InvariantCulture)),
    };

    /// <summary>Factory::NewJSIteratorResult: {value, done} with the iterator result map.</summary>
    public static JSObject NewJSIteratorResult(Isolate isolate, JSValue value, bool done)
    {
        JSObject result = isolate.Factory.NewJSObjectFromMap(isolate.NativeContext.IteratorResultMap);
        result.InObjectPropertyRef(0) = value;
        result.InObjectPropertyRef(1) = JSValue.FromBoolean(done);
        return result;
    }
}
