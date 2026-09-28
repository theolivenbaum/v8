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
//   the bytecode offset            ->  InterpreterFrameRecord.Pc (V8 derives it
//                                      from the pc through the offset table)
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

    readonly ILGenerator _il;

    public readonly LocalBuilder FpRef;       // ref JSValue: the slot at fp
    public readonly LocalBuilder Frame;       // ref InterpreterFrameRecord
    public readonly LocalBuilder Acc;         // JSValue
    public readonly LocalBuilder Context;     // Context
    public readonly LocalBuilder Fv;          // FeedbackVector
    public readonly LocalBuilder Constants;   // JSValue[]
    public readonly LocalBuilder Code;        // byte[] (the bytecodes, for embedded feedback)
    public readonly LocalBuilder Function;    // JSFunction
    public readonly LocalBuilder Fp;          // int
    public readonly LocalBuilder Scratch;     // int

    public BaselineAssembler(ILGenerator il)
    {
        _il = il;
        FpRef = il.DeclareLocal(typeof(JSValue).MakeByRefType());
        Frame = il.DeclareLocal(typeof(InterpreterFrameRecord).MakeByRefType());
        Acc = il.DeclareLocal(typeof(JSValue));
        Context = il.DeclareLocal(typeof(Context));
        Fv = il.DeclareLocal(typeof(FeedbackVector));
        Constants = il.DeclareLocal(typeof(JSValue[]));
        Code = il.DeclareLocal(typeof(byte[]));
        Function = il.DeclareLocal(typeof(JSFunction));
        Fp = il.DeclareLocal(typeof(int));
        Scratch = il.DeclareLocal(typeof(int));
    }

    public ILGenerator IL => _il;

    // ---- Arguments --------------------------------------------------------------------------------

    public void LoadIsolate() => _il.Emit(OpCodes.Ldarg_0);
    public void LoadState() => _il.Emit(OpCodes.Ldarg_1);

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
        _il.Emit(OpCodes.Ldloc, Constants);
        _il.Emit(OpCodes.Ldc_I4, index);
        _il.Emit(OpCodes.Ldelem, typeof(JSValue));
    }

    /// <summary>Pushes a ref to the embedded feedback byte at <paramref name="byteOffset"/> of the bytecode array.</summary>
    public void LoadEmbeddedFeedbackAddress(int byteOffset)
    {
        _il.Emit(OpCodes.Ldloc, Code);
        _il.Emit(OpCodes.Ldc_I4, byteOffset);
        _il.Emit(OpCodes.Ldelema, typeof(byte));
    }

    // ---- The bytecode offset --------------------------------------------------------------------------

    static readonly FieldInfo s_framePc = typeof(InterpreterFrameRecord).GetField(nameof(InterpreterFrameRecord.Pc))!;

    /// <summary>Records the current bytecode offset in the frame (for handler lookup and stack traces).</summary>
    public void StoreBytecodeOffset(int offset)
    {
        _il.Emit(OpCodes.Ldloc, Frame);
        _il.Emit(OpCodes.Ldc_I4, offset);
        _il.Emit(OpCodes.Stfld, s_framePc);
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
