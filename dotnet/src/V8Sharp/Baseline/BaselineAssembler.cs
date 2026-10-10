// The IL counterpart of src/baseline/baseline-assembler.h (and its x64
// -inl.h): the small set of operations the baseline compiler is written in.
// V8's BaselineAssembler wraps a MacroAssembler over the interpreter frame
// (registers at fp-relative slots, the accumulator in a machine register);
// this one wraps an ILGenerator over the same frame on the isolate's register
// stack:
//
//   interpreter register rN / aN  ->  ref JSValue at fpRef + N (Register.Index)
//   the accumulator                ->  IL local `acc`
//   the current context            ->  IL local `context` (+ its frame slot)
//   the feedback vector            ->  IL local `fv`
//   the constant pool              ->  IL local `constants` (JSValue[])
//   the bytecode offset            ->  the frame's bytecode offset slot (V8
//                                      derives it from the pc through the offset table)
//
// Every value crossing a bytecode boundary lives in the frame or in these
// locals, so the IL evaluation stack is empty at every bytecode, as V8's
// baseline code has no live scratch registers across bytecodes.
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using V8Sharp.Interpreter;

namespace V8Sharp.Baseline;

internal sealed class BaselineAssembler
{
    static readonly int kJSValueSize = Unsafe.SizeOf<JSValue>();

    readonly BaselineILEmitter _il;

    public readonly LocalBuilder FpRef;       // ref JSValue: the slot at fp
    public readonly LocalBuilder PcRef;       // ref int: the bytecode offset slot's payload (low half)
    public readonly LocalBuilder Acc;         // JSValue
    public readonly LocalBuilder Context;     // Context
    public readonly LocalBuilder Fv;          // FeedbackVector
    // The feedback slots, the constant pool and the bytecodes are read through
    // refs to their first elements, computed once in the prologue: the operands
    // index them with offsets from the bytecode, which are in bounds by
    // construction, so the accesses need no bounds checks (as V8's code reads
    // the feedback vector, constant pool and bytecode array at fixed offsets).
    public readonly LocalBuilder FeedbackSlots; // ref JSValue: Fv.Slots[0]
    public readonly LocalBuilder Constants;   // ref JSValue: the constant pool's [0]
    public readonly LocalBuilder Code;        // ref byte: the bytecodes' [0] (for embedded feedback)
    public readonly LocalBuilder Function;    // JSFunction
    public readonly LocalBuilder Fp;          // int
    public readonly LocalBuilder Scratch;     // int

    public BaselineAssembler(BaselineILEmitter il)
    {
        _il = il;
        FpRef = il.DeclareLocal(typeof(JSValue).MakeByRefType());
        PcRef = il.DeclareLocal(typeof(int).MakeByRefType());
        Acc = il.DeclareLocal(typeof(JSValue));
        Context = il.DeclareLocal(typeof(Context));
        Fv = il.DeclareLocal(typeof(FeedbackVector));
        FeedbackSlots = il.DeclareLocal(typeof(JSValue).MakeByRefType());
        Constants = il.DeclareLocal(typeof(JSValue).MakeByRefType());
        Code = il.DeclareLocal(typeof(byte).MakeByRefType());
        Function = il.DeclareLocal(typeof(JSFunction));
        Fp = il.DeclareLocal(typeof(int));
        Scratch = il.DeclareLocal(typeof(int));
    }

    public BaselineILEmitter IL => _il;

    // ---- Arguments --------------------------------------------------------------------------------

    // Argument 0 is the BaselineCode the entry delegate is closed over.
    public void LoadCodeObject() => _il.Emit(OpCodes.Ldarg_0);
    public void LoadIsolate() => _il.Emit(OpCodes.Ldarg_1);
    public void LoadState() => _il.Emit(OpCodes.Ldarg_2);

    // ---- Locals ------------------------------------------------------------------------------------

    public void LoadAccumulator() => _il.Emit(OpCodes.Ldloc, Acc);
    public void LoadAccumulatorAddress() => _il.Emit(OpCodes.Ldloca, Acc);
    public void StoreAccumulator() => _il.Emit(OpCodes.Stloc, Acc);
    public void LoadContext() => _il.Emit(OpCodes.Ldloc, Context);
    public void StoreContext() => _il.Emit(OpCodes.Stloc, Context);
    public void LoadFeedbackVector() => _il.Emit(OpCodes.Ldloc, Fv);
    public void LoadFunction() => _il.Emit(OpCodes.Ldloc, Function);

    public void LoadInt(int value) => _il.Emit(OpCodes.Ldc_I4, value);
    public void LoadBool(bool value) => _il.Emit(value ? OpCodes.Ldc_I4_1 : OpCodes.Ldc_I4_0);

    // ---- Interpreter registers ----------------------------------------------------------------------

    /// <summary>Pushes the address of the frame slot of <paramref name="register"/> (fp + index).</summary>
    public void LoadRegisterAddress(Register register) => LoadFrameSlotAddress(register.Index);

    public void LoadFrameSlotAddress(int index)
    {
        _il.Emit(OpCodes.Ldloc, FpRef);
        if (index != 0)
        {
            _il.Emit(OpCodes.Ldc_I4, index * kJSValueSize);
            _il.Emit(OpCodes.Conv_I);
            _il.Emit(OpCodes.Add);
        }
    }

    /// <summary>Pushes the value of <paramref name="register"/>.</summary>
    public void LoadRegister(Register register)
    {
        LoadRegisterAddress(register);
        _il.Emit(OpCodes.Ldobj, typeof(JSValue));
    }

    /// <summary>The register's absolute index in the isolate's register stack (fp + index).</summary>
    public void LoadRegisterStackIndex(Register register)
    {
        _il.Emit(OpCodes.Ldloc, Fp);
        if (register.Index != 0)
        {
            _il.Emit(OpCodes.Ldc_I4, register.Index);
            _il.Emit(OpCodes.Add);
        }
    }

    /// <summary>StoreRegister: stores the accumulator into <paramref name="register"/>.</summary>
    public void StoreAccumulatorToRegister(Register register)
    {
        LoadRegisterAddress(register);
        _il.Emit(OpCodes.Ldloc, Acc);
        _il.Emit(OpCodes.Stobj, typeof(JSValue));
    }

    /// <summary>Stores the JSValue on top of the stack into the frame slot at <paramref name="index"/>.</summary>
    public void StoreToFrameSlot(int index, LocalBuilder valueTemp)
    {
        _il.Emit(OpCodes.Stloc, valueTemp);
        LoadFrameSlotAddress(index);
        _il.Emit(OpCodes.Ldloc, valueTemp);
        _il.Emit(OpCodes.Stobj, typeof(JSValue));
    }

    public void MoveRegister(Register from, Register to)
    {
        LoadRegisterAddress(to);
        LoadRegister(from);
        _il.Emit(OpCodes.Stobj, typeof(JSValue));
    }

    // ---- Constants and feedback ----------------------------------------------------------------------

    /// <summary>Pushes constant pool entry <paramref name="index"/> as a JSValue.</summary>
    public void LoadConstant(int index)
    {
        LoadElementAddress(Constants, index * kJSValueSize);
        _il.Emit(OpCodes.Ldobj, typeof(JSValue));
    }

    /// <summary>Pushes a ref to feedback slot <paramref name="slot"/>.</summary>
    public void LoadFeedbackSlotAddress(int slot) => LoadElementAddress(FeedbackSlots, slot * kJSValueSize);

    /// <summary>Pushes a ref to the embedded feedback byte at <paramref name="byteOffset"/> of the bytecode array.</summary>
    public void LoadEmbeddedFeedbackAddress(int byteOffset) => LoadElementAddress(Code, byteOffset);

    /// <summary>Pushes the embedded feedback byte at <paramref name="byteOffset"/> of the bytecode array (as an int).</summary>
    public void LoadEmbeddedFeedback(int byteOffset)
    {
        LoadElementAddress(Code, byteOffset);
        _il.Emit(OpCodes.Ldind_U1);
    }

    /// <summary>Pushes <paramref name="elementRef"/> + <paramref name="byteOffset"/>.</summary>
    void LoadElementAddress(LocalBuilder elementRef, int byteOffset)
    {
        _il.Emit(OpCodes.Ldloc, elementRef);
        if (byteOffset != 0)
        {
            _il.Emit(OpCodes.Ldc_I4, byteOffset);
            _il.Emit(OpCodes.Add);
        }
    }

    static readonly MethodInfo s_jsValueArrayData = ArrayDataReference(typeof(JSValue));
    static readonly MethodInfo s_byteArrayData = ArrayDataReference(typeof(byte));

    static MethodInfo ArrayDataReference(Type element) =>
        typeof(System.Runtime.InteropServices.MemoryMarshal).GetMethods()
            .Single(m => m.Name == nameof(System.Runtime.InteropServices.MemoryMarshal.GetArrayDataReference) && m.IsGenericMethodDefinition)
            .MakeGenericMethod(element);

    /// <summary>Stores a ref to the first element of the JSValue[] on the stack in <paramref name="elementRef"/>.</summary>
    public void StoreArrayDataReference(LocalBuilder elementRef, bool bytes = false)
    {
        _il.Emit(OpCodes.Call, bytes ? s_byteArrayData : s_jsValueArrayData);
        _il.Emit(OpCodes.Stloc, elementRef);
    }

    // ---- The bytecode offset --------------------------------------------------------------------------

    internal static readonly FieldInfo s_bits = typeof(JSValue).GetField("_bits", BindingFlags.NonPublic | BindingFlags.Instance)!;

    /// <summary>
    /// Records the current bytecode offset in the frame's offset slot (for
    /// handler lookup and stack traces): InterpreterRuntime.SetFramePc as IL,
    /// a store of the slot's payload. Emitted inline rather than called:
    /// RyuJIT stops inlining in big methods (its inline budget and local
    /// count limit), and a call per bytecode that can throw was a few percent
    /// of the time of the big functions (Mandreel, Box2D).
    /// </summary>
    public void StoreBytecodeOffset(int offset)
    {
        // A 4-byte store through PcRef (the prologue's ref to the slot's payload):
        // the offset is read back as (int)_bits (InterpreterRuntime.FramePc),
        // and the slot's upper half is zero (InitializeFrameSlots).
        _il.Emit(OpCodes.Ldloc, PcRef);
        _il.Emit(OpCodes.Ldc_I4, offset);
        _il.Emit(OpCodes.Stind_I4);
    }

    /// <summary>The prologue's PcRef = ref the low half of fpRef[kBytecodeOffsetOffset]._bits.</summary>
    public void InitializePcRef()
    {
        LoadFrameSlotAddress(InterpreterRuntime.kBytecodeOffsetOffset);
        _il.Emit(OpCodes.Ldflda, s_bits);
        _il.Emit(OpCodes.Stloc, PcRef);
    }

    // ---- Calls ------------------------------------------------------------------------------------------

    public void Call(MethodInfo method) => _il.Emit(OpCodes.Call, method);

    public void Pop() => _il.Emit(OpCodes.Pop);

    // ---- Control flow -----------------------------------------------------------------------------------

    public Label NewLabel() => _il.DefineLabel();
    public void Bind(Label label) => _il.MarkLabel(label);
    public void Jump(Label label) => _il.Emit(OpCodes.Br, label);
    public void JumpIfTrue(Label label) => _il.Emit(OpCodes.Brtrue, label);
    public void JumpIfFalse(Label label) => _il.Emit(OpCodes.Brfalse, label);
    public void Switch(Label[] labels) => _il.Emit(OpCodes.Switch, labels);

    public void Return()
    {
        _il.Emit(OpCodes.Ldloc, Acc);
        _il.Emit(OpCodes.Ret);
    }
}
