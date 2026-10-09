// Instructions compiled code delegates to the interpreter: the GC, SIMD,
// atomic, table and bulk-memory instructions without an IL fast path.
//
// Liftoff calls a builtin or the runtime for complex instructions (struct
// and array allocation, table and bulk-memory operations). V8Sharp calls the
// interpreter's implementation of the instruction itself: the operands go to
// the interpreter's operand stack, the instruction runs with an interpreter
// frame of the instance, and the results come back. The value kinds of
// operands and results come from V8's opcode signatures (wasm-opcodes.h) or,
// for GC instructions, from the types the immediates name.
using System.Reflection;
using System.Reflection.Emit;
using Wacs.Core.Instructions.GC;
using Wacs.Core.Runtime;
using Wacs.Core.Types;
using Wacs.Core.Types.Defs;

using Label = System.Reflection.Emit.Label;

namespace V8Sharp.Wasm.Baseline;

internal sealed partial class LiftoffCompiler
{
    static readonly FieldInfo s_dataContext = typeof(WasmInstanceData).GetField(nameof(WasmInstanceData.Context))!;
    static readonly FieldInfo s_contextOpStack = typeof(ExecContext).GetField(nameof(ExecContext.OpStack))!;

    static MethodInfo OpStackMethod(string name) => typeof(OpStack).GetMethod(name, BindingFlags.Public | BindingFlags.Instance)!;

    static readonly MethodInfo[] s_push =
    [
        OpStackMethod(nameof(OpStack.PushI32)), OpStackMethod(nameof(OpStack.PushI64)),
        OpStackMethod(nameof(OpStack.PushF32)), OpStackMethod(nameof(OpStack.PushF64)),
        OpStackMethod(nameof(OpStack.PushValue)), OpStackMethod(nameof(OpStack.PushValue)),
    ];

    static readonly MethodInfo[] s_pop =
    [
        OpStackMethod(nameof(OpStack.PopI32)), OpStackMethod(nameof(OpStack.PopI64)),
        OpStackMethod(nameof(OpStack.PopF32)), OpStackMethod(nameof(OpStack.PopF64)),
        OpStackMethod(nameof(OpStack.PopAny)), OpStackMethod(nameof(OpStack.PopAny)),
    ];

    void LoadOpStack()
    {
        _il.Emit(OpCodes.Ldloc, _dataLocal);
        _il.Emit(OpCodes.Ldfld, s_dataContext);
        _il.Emit(OpCodes.Ldfld, s_contextOpStack);
    }

    /// <summary>Runs the current instruction in the interpreter.</summary>
    void GenericInstruction(WasmKind[] operands, WasmKind[] results)
    {
        if (!_reachable) return;
        int k = AddConstant(_instructions[_instIndex]);
        int n = operands.Length;
        _asm.Settle(n);
        int first = _asm.Height - n;
        for (int i = 0; i < n; i++)
        {
            LoadOpStack();
            _asm.LoadSettled(first + i);
            _il.Emit(OpCodes.Call, s_push[(int)operands[i]]);
        }
        _asm.Drop(n);
        EmitStorePc();
        _il.Emit(OpCodes.Ldc_I4, k);
        EmitRuntimeCall(nameof(RuntimeWasm.ExecuteInstruction));
        for (int i = results.Length - 1; i >= 0; i--)
        {
            LoadOpStack();
            _il.Emit(OpCodes.Call, s_pop[(int)results[i]]);
            _il.Emit(OpCodes.Stloc, _asm.Slot(first + i, results[i]));
        }
        foreach (WasmKind kind in results) _asm.PushSlot(kind);
    }

    static WasmKind KindOfLetter(char c) => c switch
    {
        'i' => WasmKind.I32,
        'l' => WasmKind.I64,
        'f' => WasmKind.F32,
        'd' => WasmKind.F64,
        's' => WasmKind.S128,
        _ => throw new LiftoffBailout("signature " + c),
    };

    /// <summary>The operands and results of a V8 signature string ("i_il").</summary>
    static (WasmKind[] Operands, WasmKind[] Results) ParseSignature(string sig)
    {
        int bar = sig.IndexOf('_');
        string result = sig[..bar], operands = sig[(bar + 1)..];
        WasmKind[] r = result == "v" ? [] : [KindOfLetter(result[0])];
        var o = new WasmKind[operands == "v" ? 0 : operands.Length];
        for (int i = 0; i < o.Length; i++) o[i] = KindOfLetter(operands[i]);
        return (o, r);
    }

    // ---- GC ----------------------------------------------------------------------

    CompositeType TypeAt(uint index) => _module.Types[(TypeIdx)index].Expansion;

    WasmKind[] StructFields(uint type)
    {
        var st = (StructType)TypeAt(type);
        var kinds = new WasmKind[st.FieldTypes.Length];
        for (int i = 0; i < kinds.Length; i++) kinds[i] = WasmKinds.Of(st.FieldTypes[i].StorageType);
        return kinds;
    }

    WasmKind ArrayElement(uint type) => WasmKinds.Of(((ArrayType)TypeAt(type)).ElementType.StorageType);

    void GCOp(uint index)
    {
        const WasmKind I = WasmKind.I32, R = WasmKind.Ref;
        switch (index)
        {
            case 0x00: GenericInstruction(StructFields(ReadU32()), [R]); return;
            case 0x01: ReadU32(); GenericInstruction([], [R]); return;
            case 0x02:
            case 0x03:
            case 0x04:
            {
                uint type = ReadU32();
                uint field = ReadU32();
                GenericInstruction([R], [StructFields(type)[field]]);
                return;
            }
            case 0x05:
            {
                uint type = ReadU32();
                uint field = ReadU32();
                GenericInstruction([R, StructFields(type)[field]], []);
                return;
            }
            case 0x06: GenericInstruction([ArrayElement(ReadU32()), I], [R]); return;
            case 0x07: ReadU32(); GenericInstruction([I], [R]); return;
            case 0x08:
            {
                WasmKind element = ArrayElement(ReadU32());
                uint n = ReadU32();
                var operands = new WasmKind[n];
                Array.Fill(operands, element);
                GenericInstruction(operands, [R]);
                return;
            }
            case 0x09:
            case 0x0a:
                ReadU32();
                ReadU32();
                GenericInstruction([I, I], [R]);
                return;
            case 0x0b:
            case 0x0c:
            case 0x0d:
                GenericInstruction([R, I], [ArrayElement(ReadU32())]);
                return;
            case 0x0e: GenericInstruction([R, I, ArrayElement(ReadU32())], []); return;
            case 0x0f: GenericInstruction([R], [I]); return;
            case 0x10: GenericInstruction([R, I, ArrayElement(ReadU32()), I], []); return;
            case 0x11:
                ReadU32();
                ReadU32();
                GenericInstruction([R, I, R, I, I], []);
                return;
            case 0x12:
            case 0x13:
                ReadU32();
                ReadU32();
                GenericInstruction([R, I, I, I], []);
                return;
            case 0x14:
            case 0x15:
                ReadHeapType();
                GenericInstruction([R], [I]);
                return;
            case 0x16:
            case 0x17:
                ReadHeapType();
                GenericInstruction([R], [R]);
                return;
            case 0x18:
            case 0x19:
            {
                ReadU8();
                int depth = (int)ReadU32();
                ReadHeapType();
                ReadHeapType();
                BrOnCast(depth, onFail: index == 0x19);
                return;
            }
            case 0x1a:
            case 0x1b:
                GenericInstruction([R], [R]);
                return;
            case 0x1c: GenericInstruction([I], [R]); return;
            case 0x1d:
            case 0x1e:
                GenericInstruction([R], [I]);
                return;
        }
        Unsupported("GC opcode 0xfb" + index.ToString("x2", System.Globalization.CultureInfo.InvariantCulture));
    }

    /// <summary>br_on_cast / br_on_cast_fail: the cast test is the interpreter's.</summary>
    void BrOnCast(int depth, bool onFail)
    {
        if (!_reachable) return;
        Control target = ControlAt(depth);
        ValType targetType = _instructions[_instIndex] switch
        {
            InstBrOnCast b => b.TargetType,
            InstBrOnCastFail f => f.TargetType,
            _ => throw new LiftoffBailout("br_on_cast instruction"),
        };
        int k = AddConstant(targetType);
        _asm.SpillAll(_asm.Height - 1);
        _asm.LoadSettled(_asm.Height - 1);
        _il.Emit(OpCodes.Ldarg_0);
        _il.Emit(OpCodes.Ldc_I4, k);
        _il.Emit(OpCodes.Call, RuntimeWasm.Method(nameof(RuntimeWasm.RefMatches)));
        Label fallThrough = _il.DefineLabel();
        _il.Emit(onFail ? OpCodes.Brtrue : OpCodes.Brfalse, fallThrough);
        EmitBranchFromSettled(target);
        _il.MarkLabel(fallThrough);
    }

    // ---- SIMD ----------------------------------------------------------------------

    void SimdOp(uint index)
    {
        WasmOpcode opcode = WasmOpcodes.Prefixed(WasmOpcodes.kSimdPrefix, index);
        string name = WasmOpcodes.OpcodeName(opcode);
        int memory = -1;
        switch (index)
        {
            case <= 0x0b or 0x5c or 0x5d:
                memory = ReadMemArg().Memory;
                break;
            case >= 0x54 and <= 0x5b:
                memory = ReadMemArg().Memory;
                ReadU8();
                break;
            case 0x0c:
            case 0x0d:
                _pos += 16;
                break;
            case >= 0x15 and <= 0x22:
                ReadU8();
                break;
        }
        if (name.StartsWith("f16x8", StringComparison.Ordinal) || name.Contains("_f16x8", StringComparison.Ordinal) ||
            name.Contains("f16", StringComparison.Ordinal))
        {
            Unsupported("fp16");
        }
        if (!_reachable) return;
        WasmKind[] operands, results;
        if (index == 0x0c)
        {
            (operands, results) = ([], [WasmKind.S128]);
        }
        else if (WasmOpcodes.Signature(opcode) is { } sig)
        {
            (operands, results) = ParseSignature(sig);
        }
        else if (name.Contains("extract_lane", StringComparison.Ordinal))
        {
            (operands, results) = ([WasmKind.S128], [LaneKind(name)]);
        }
        else if (name.Contains("replace_lane", StringComparison.Ordinal))
        {
            (operands, results) = ([WasmKind.S128, LaneKind(name)], [WasmKind.S128]);
        }
        else
        {
            Unsupported("SIMD opcode " + name);
            return;
        }
        if (memory >= 0 && IsMemory64(memory)) operands[0] = WasmKind.I64;
        GenericInstruction(operands, results);
    }

    static WasmKind LaneKind(string name) => name[..5] switch
    {
        "i8x16" or "i16x8" or "i32x4" => WasmKind.I32,
        "i64x2" => WasmKind.I64,
        "f32x4" => WasmKind.F32,
        "f64x2" => WasmKind.F64,
        _ => throw new LiftoffBailout("lane " + name),
    };

    // ---- Atomics ---------------------------------------------------------------------

    void AtomicOp(uint index)
    {
        WasmOpcode opcode = WasmOpcodes.Prefixed(WasmOpcodes.kAtomicPrefix, index);
        if (index == 0x03)
        {
            ReadU8();
            GenericInstruction([], []);
            return;
        }
        if (index > 0x4e) Unsupported("atomic opcode " + WasmOpcodes.OpcodeName(opcode));
        int memory = ReadMemArg().Memory;
        if (!_reachable) return;
        string? sig = WasmOpcodes.Signature(opcode, IsMemory64(memory));
        if (sig is null) Unsupported("atomic opcode " + WasmOpcodes.OpcodeName(opcode));
        var (operands, results) = ParseSignature(sig!);
        GenericInstruction(operands, results);
    }
}
