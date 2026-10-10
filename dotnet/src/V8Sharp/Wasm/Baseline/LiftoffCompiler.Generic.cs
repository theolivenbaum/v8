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
using System.Runtime.Intrinsics;
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
        _genericCount++;
        _instanceSpecific = true;
        int k = AddConstant(_instructions[_instIndex]);
        int n = operands.Length;
        _asm.Settle(n);
        int first = _asm.Height - n;
        for (int i = 0; i < n; i++)
        {
            LoadOpStack();
            _asm.LoadSettled(first + i);
            if (operands[i] == WasmKind.S128) WasmValues.EmitToValue(_il, WasmKind.S128);
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
            if (results[i] == WasmKind.S128) WasmValues.EmitFromValue(_il, WasmKind.S128);
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
        _instanceSpecific = true;
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
        ulong offset = 0;
        int lane = -1;
        long immLow = 0, immHigh = 0;
        switch (index)
        {
            case <= 0x0b or 0x5c or 0x5d:
                (memory, offset) = ReadMemArg();
                break;
            case >= 0x54 and <= 0x5b:
                (memory, offset) = ReadMemArg();
                lane = ReadU8();
                break;
            case 0x0c:
            case 0x0d:
                immLow = BitConverter.ToInt64(_bytes, _pos);
                immHigh = BitConverter.ToInt64(_bytes, _pos + 8);
                _pos += 16;
                break;
            case >= 0x15 and <= 0x22:
                lane = ReadU8();
                break;
        }
        if (name.Contains("f16", StringComparison.Ordinal))
        {
            Unsupported("fp16");
        }
        if (!_reachable) return;
        if (TryNativeSimd(index, opcode, memory, offset, lane, immLow, immHigh)) return;
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

    static readonly MethodInfo s_simdConst = typeof(WasmSimd).GetMethod(nameof(WasmSimd.Const))!;

    static MethodInfo? SimdMethod(WasmOpcode opcode)
    {
        string? name = Enum.GetName(opcode);
        return name is null ? null : typeof(WasmSimd).GetMethod(name["kExpr".Length..], BindingFlags.Public | BindingFlags.Static);
    }

    /// <summary>
    /// The SIMD instructions with an IL implementation (WasmSimd.cs): those
    /// whose method exists, loads and stores. Returns false for the others.
    /// </summary>
    bool TryNativeSimd(uint index, WasmOpcode opcode, int memory, ulong offset, int lane, long immLow, long immHigh)
    {
        const WasmKind S = WasmKind.S128;
        switch (index)
        {
            case 0x0c: // v128.const
                _il.Emit(OpCodes.Ldc_I8, immLow);
                _il.Emit(OpCodes.Ldc_I8, immHigh);
                _il.Emit(OpCodes.Call, s_simdConst);
                _asm.PushStack(S);
                return true;
            case 0x0d: // i8x16.shuffle
                _asm.PopToStack(2);
                _il.Emit(OpCodes.Ldc_I8, immLow);
                _il.Emit(OpCodes.Ldc_I8, immHigh);
                _il.Emit(OpCodes.Call, s_simdConst);
                _il.Emit(OpCodes.Call, SimdMethod(opcode)!);
                _asm.PushStack(S);
                return true;
            case 0x00: // v128.load
                SimdLoad(memory, offset, 16, null, null);
                return true;
            case 0x0b: // v128.store
                SimdStore(memory, offset, 16, null, -1);
                return true;
            case >= 0x01 and <= 0x06: // v128.load8x8_s ... load32x2_u
                SimdLoad(memory, offset, 8, OpCodes.Ldind_I8, SimdMethod(opcode));
                return true;
            case 0x07:
                SimdLoad(memory, offset, 1, OpCodes.Ldind_U1, SimdMethod(opcode));
                return true;
            case 0x08:
                SimdLoad(memory, offset, 2, OpCodes.Ldind_U2, SimdMethod(opcode));
                return true;
            case 0x09:
            case 0x5c:
                SimdLoad(memory, offset, 4, OpCodes.Ldind_I4, SimdMethod(opcode));
                return true;
            case 0x0a:
            case 0x5d:
                SimdLoad(memory, offset, 8, OpCodes.Ldind_I8, SimdMethod(opcode));
                return true;
            case >= 0x54 and <= 0x57: // v128.loadN_lane
                return SimdLoadLane(memory, offset, index - 0x54, lane);
            case >= 0x58 and <= 0x5b: // v128.storeN_lane
                return SimdStoreLane(memory, offset, index - 0x58, lane);
        }
        MethodInfo? method = SimdMethod(opcode);
        if (method is null) return false;
        ParameterInfo[] parameters = method.GetParameters();
        int operands = lane >= 0 ? parameters.Length - 1 : parameters.Length;
        _asm.PopToStack(operands);
        if (lane >= 0) _il.Emit(OpCodes.Ldc_I4, lane);
        _il.Emit(OpCodes.Call, method);
        Type ret = method.ReturnType;
        _asm.PushStack(ret == typeof(int) ? WasmKind.I32 : ret == typeof(long) ? WasmKind.I64 :
            ret == typeof(float) ? WasmKind.F32 : ret == typeof(double) ? WasmKind.F64 : S);
        return true;
    }

    static readonly MethodInfo s_extract8 = typeof(WasmSimd).GetMethod(nameof(WasmSimd.I8x16ExtractLaneU))!;
    static readonly MethodInfo s_extract16 = typeof(WasmSimd).GetMethod(nameof(WasmSimd.I16x8ExtractLaneU))!;
    static readonly MethodInfo s_extract32 = typeof(WasmSimd).GetMethod(nameof(WasmSimd.I32x4ExtractLane))!;
    static readonly MethodInfo s_extract64 = typeof(WasmSimd).GetMethod(nameof(WasmSimd.I64x2ExtractLane))!;
    static readonly MethodInfo s_replace8 = typeof(WasmSimd).GetMethod(nameof(WasmSimd.I8x16ReplaceLane))!;
    static readonly MethodInfo s_replace16 = typeof(WasmSimd).GetMethod(nameof(WasmSimd.I16x8ReplaceLane))!;
    static readonly MethodInfo s_replace32 = typeof(WasmSimd).GetMethod(nameof(WasmSimd.I32x4ReplaceLane))!;
    static readonly MethodInfo s_replace64 = typeof(WasmSimd).GetMethod(nameof(WasmSimd.I64x2ReplaceLane))!;

    /// <summary>A vector load: <paramref name="size"/> bytes, then <paramref name="convert"/> (none: the whole vector).</summary>
    void SimdLoad(int memory, ulong offset, int size, OpCode? load, MethodInfo? convert)
    {
        LocalBuilder address = _asm.Temp(WasmKind.I64);
        _asm.PopToStack(1);
        BoundsCheck(memory, offset, size, address);
        EmitMemoryPointer(memory, address);
        _il.Emit(OpCodes.Unaligned, (byte)1);
        if (load is { } op)
        {
            _il.Emit(op);
            _il.Emit(OpCodes.Call, convert!);
        }
        else
        {
            _il.Emit(OpCodes.Ldobj, typeof(Vector128<byte>));
        }
        _asm.PushStack(WasmKind.S128);
    }

    void SimdStore(int memory, ulong offset, int size, OpCode? store, int lane)
    {
        LocalBuilder address = _asm.Temp(WasmKind.I64);
        LocalBuilder value = _asm.Temp(WasmKind.S128);
        _asm.PopToStack(2);
        _il.Emit(OpCodes.Stloc, value);
        BoundsCheck(memory, offset, size, address);
        EmitMemoryPointer(memory, address);
        _il.Emit(OpCodes.Ldloc, value);
        _il.Emit(OpCodes.Unaligned, (byte)1);
        _il.Emit(OpCodes.Stobj, typeof(Vector128<byte>));
    }

    bool SimdLoadLane(int memory, ulong offset, uint width, int lane)
    {
        (int size, OpCode load, MethodInfo replace) = width switch
        {
            0 => (1, OpCodes.Ldind_U1, s_replace8),
            1 => (2, OpCodes.Ldind_U2, s_replace16),
            2 => (4, OpCodes.Ldind_I4, s_replace32),
            _ => (8, OpCodes.Ldind_I8, s_replace64),
        };
        LocalBuilder address = _asm.Temp(WasmKind.I64);
        LocalBuilder vector = _asm.Temp(WasmKind.S128);
        _asm.PopToStack(2);
        _il.Emit(OpCodes.Stloc, vector);
        BoundsCheck(memory, offset, size, address);
        _il.Emit(OpCodes.Ldloc, vector);
        EmitMemoryPointer(memory, address);
        _il.Emit(OpCodes.Unaligned, (byte)1);
        _il.Emit(load);
        _il.Emit(OpCodes.Ldc_I4, lane);
        _il.Emit(OpCodes.Call, replace);
        _asm.PushStack(WasmKind.S128);
        return true;
    }

    bool SimdStoreLane(int memory, ulong offset, uint width, int lane)
    {
        (int size, OpCode store, MethodInfo extract) = width switch
        {
            0 => (1, OpCodes.Stind_I1, s_extract8),
            1 => (2, OpCodes.Stind_I2, s_extract16),
            2 => (4, OpCodes.Stind_I4, s_extract32),
            _ => (8, OpCodes.Stind_I8, s_extract64),
        };
        LocalBuilder address = _asm.Temp(WasmKind.I64);
        LocalBuilder vector = _asm.Temp(WasmKind.S128);
        _asm.PopToStack(2);
        _il.Emit(OpCodes.Stloc, vector);
        BoundsCheck(memory, offset, size, address);
        EmitMemoryPointer(memory, address);
        _il.Emit(OpCodes.Ldloc, vector);
        _il.Emit(OpCodes.Ldc_I4, lane);
        _il.Emit(OpCodes.Call, extract);
        _il.Emit(OpCodes.Unaligned, (byte)1);
        _il.Emit(store);
        return true;
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
