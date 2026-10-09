// LiftoffCompiler::LoadMem, StoreMem, BoundsCheckMem, CurrentMemoryPages,
// MemoryGrow, GlobalGet and GlobalSet (src/wasm/baseline/liftoff-compiler.cc).
//
// V8 relies on guard regions (or an explicit check) and a trap handler for
// out-of-bounds accesses. V8Sharp's linear memory is a managed byte[] (see
// deviations.md): each access compares its end with the memory's size, kept
// with the array in IL locals and re-read after anything that can grow the
// memory (calls, memory.grow), and reads or writes through a managed pointer
// into the array, unaligned (V8's loads and stores are unaligned too).
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.InteropServices;
using Wacs.Core.Runtime;
using Wacs.Core.Runtime.Types;
using Wacs.Core.Types.Defs;

using Label = System.Reflection.Emit.Label;

namespace V8Sharp.Wasm.Baseline;

internal sealed partial class LiftoffCompiler
{
    static readonly MethodInfo s_arrayDataReference = typeof(MemoryMarshal).GetMethods()
        .First(m => m.Name == nameof(MemoryMarshal.GetArrayDataReference) && m.IsGenericMethodDefinition)
        .MakeGenericMethod(typeof(byte));

    bool IsMemory64(int memory) => _data.Memories[memory].Type.Limits.AddressType == AddrType.I64;

    WasmKind AddressKind(int memory) => IsMemory64(memory) ? WasmKind.I64 : WasmKind.I32;

    WasmKind TableAddressKind(int table) =>
        _data.Tables[table].Type.Limits.AddressType == AddrType.I64 ? WasmKind.I64 : WasmKind.I32;

    /// <summary>Reads a memarg: the memory index and the offset.</summary>
    (int Memory, ulong Offset) ReadMemArg()
    {
        uint align = ReadU32();
        int memory = 0;
        if ((align & 0x40) != 0) memory = (int)ReadU32();
        ulong offset = ReadU64();
        return (memory, offset);
    }

    /// <summary>Loads memory <paramref name="memory"/>'s array.</summary>
    void LoadMemoryArray(int memory)
    {
        if (_memArray[memory] is { } cached)
        {
            _il.Emit(OpCodes.Ldloc, cached);
            return;
        }
        _il.Emit(OpCodes.Ldloc, _dataLocal);
        _il.Emit(OpCodes.Ldfld, s_dataMemories);
        _il.Emit(OpCodes.Ldc_I4, memory);
        _il.Emit(OpCodes.Ldelem_Ref);
        _il.Emit(OpCodes.Ldfld, s_memoryData);
    }

    /// <summary>Loads memory <paramref name="memory"/>'s size in bytes (an int64).</summary>
    void LoadMemorySize(int memory)
    {
        if (_memSize[memory] is { } cached)
        {
            _il.Emit(OpCodes.Ldloc, cached);
            return;
        }
        _il.Emit(OpCodes.Ldloc, _dataLocal);
        _il.Emit(OpCodes.Ldfld, s_dataMemories);
        _il.Emit(OpCodes.Ldc_I4, memory);
        _il.Emit(OpCodes.Ldelem_Ref);
        _il.Emit(OpCodes.Call, s_memoryByteLength);
        _il.Emit(OpCodes.Conv_U8);
    }

    /// <summary>
    /// BoundsCheckMem: the index on the IL stack becomes the effective
    /// address in <paramref name="address"/>, checked to have
    /// <paramref name="size"/> bytes in memory.
    /// </summary>
    void BoundsCheck(int memory, ulong offset, int size, LocalBuilder address)
    {
        if (IsMemory64(memory))
        {
            // address + offset may overflow 64 bits: the helper checks.
            _il.Emit(OpCodes.Ldc_I8, (long)offset);
            _il.Emit(OpCodes.Ldc_I4, size);
            LoadMemorySize(memory);
            EmitRuntimeCall(nameof(RuntimeWasm.EffectiveAddress64));
            _il.Emit(OpCodes.Stloc, address);
            return;
        }
        // A 32-bit index plus a 32-bit offset fits in 64 bits.
        _il.Emit(OpCodes.Conv_U8);
        if (offset >= 1UL << 32)
        {
            // Out of bounds whatever the index (V8 traps statically).
            _il.Emit(OpCodes.Pop);
            EmitTrap(MessageTemplate.WasmTrapMemOutOfBounds);
            _il.Emit(OpCodes.Ldc_I8, 0L);
            _il.Emit(OpCodes.Stloc, address);
            return;
        }
        if (offset != 0)
        {
            _il.Emit(OpCodes.Ldc_I8, (long)offset);
            _il.Emit(OpCodes.Add);
        }
        _il.Emit(OpCodes.Stloc, address);
        Label inBounds = _il.DefineLabel();
        _il.Emit(OpCodes.Ldloc, address);
        _il.Emit(OpCodes.Ldc_I8, (long)size);
        _il.Emit(OpCodes.Add);
        LoadMemorySize(memory);
        _il.Emit(OpCodes.Ble_Un, inBounds);
        EmitTrap(MessageTemplate.WasmTrapMemOutOfBounds);
        _il.MarkLabel(inBounds);
    }

    /// <summary>Pushes a managed pointer to memory[address].</summary>
    void EmitMemoryPointer(int memory, LocalBuilder address)
    {
        LoadMemoryArray(memory);
        _il.Emit(OpCodes.Call, s_arrayDataReference);
        _il.Emit(OpCodes.Ldloc, address);
        _il.Emit(OpCodes.Conv_I);
        _il.Emit(OpCodes.Add);
    }

    void LoadStore(byte opcode)
    {
        var (memory, offset) = ReadMemArg();
        if (!_reachable) return;
        const WasmKind I = WasmKind.I32, L = WasmKind.I64, F = WasmKind.F32, D = WasmKind.F64;
        (int size, OpCode op, OpCode? extend, WasmKind kind, bool store) = opcode switch
        {
            0x28 => (4, OpCodes.Ldind_I4, (OpCode?)null, I, false),
            0x29 => (8, OpCodes.Ldind_I8, null, L, false),
            0x2a => (4, OpCodes.Ldind_R4, null, F, false),
            0x2b => (8, OpCodes.Ldind_R8, null, D, false),
            0x2c => (1, OpCodes.Ldind_I1, null, I, false),
            0x2d => (1, OpCodes.Ldind_U1, null, I, false),
            0x2e => (2, OpCodes.Ldind_I2, null, I, false),
            0x2f => (2, OpCodes.Ldind_U2, null, I, false),
            0x30 => (1, OpCodes.Ldind_I1, OpCodes.Conv_I8, L, false),
            0x31 => (1, OpCodes.Ldind_U1, OpCodes.Conv_U8, L, false),
            0x32 => (2, OpCodes.Ldind_I2, OpCodes.Conv_I8, L, false),
            0x33 => (2, OpCodes.Ldind_U2, OpCodes.Conv_U8, L, false),
            0x34 => (4, OpCodes.Ldind_I4, OpCodes.Conv_I8, L, false),
            0x35 => (4, OpCodes.Ldind_U4, OpCodes.Conv_U8, L, false),
            0x36 => (4, OpCodes.Stind_I4, null, I, true),
            0x37 => (8, OpCodes.Stind_I8, null, L, true),
            0x38 => (4, OpCodes.Stind_R4, null, F, true),
            0x39 => (8, OpCodes.Stind_R8, null, D, true),
            0x3a => (1, OpCodes.Stind_I1, null, I, true),
            0x3b => (2, OpCodes.Stind_I2, null, I, true),
            0x3c => (1, OpCodes.Stind_I1, OpCodes.Conv_I4, L, true),
            0x3d => (2, OpCodes.Stind_I2, OpCodes.Conv_I4, L, true),
            0x3e => (4, OpCodes.Stind_I4, OpCodes.Conv_I4, L, true),
            _ => throw new LiftoffBailout("memory opcode"),
        };
        LocalBuilder address = _asm.Temp(WasmKind.I64);
        if (!store)
        {
            _asm.PopToStack(1);
            BoundsCheck(memory, offset, size, address);
            EmitMemoryPointer(memory, address);
            _il.Emit(OpCodes.Unaligned, (byte)1);
            _il.Emit(op);
            if (extend is { } e) _il.Emit(e);
            _asm.PushStack(kind);
            return;
        }
        _asm.PopToStack(2);
        LocalBuilder value = _asm.Temp(kind);
        _il.Emit(OpCodes.Stloc, value);
        BoundsCheck(memory, offset, size, address);
        EmitMemoryPointer(memory, address);
        _il.Emit(OpCodes.Ldloc, value);
        if (extend is { } x) _il.Emit(x);
        _il.Emit(OpCodes.Unaligned, (byte)1);
        _il.Emit(op);
    }

    void MemorySize(int memory)
    {
        if (!_reachable) return;
        LoadMemorySize(memory);
        _il.Emit(OpCodes.Ldc_I8, 65536L);
        _il.Emit(OpCodes.Div_Un);
        if (IsMemory64(memory))
        {
            _asm.PushStack(WasmKind.I64);
        }
        else
        {
            _il.Emit(OpCodes.Conv_I4);
            _asm.PushStack(WasmKind.I32);
        }
    }

    void MemoryGrow(int memory)
    {
        if (!_reachable) return;
        bool is64 = IsMemory64(memory);
        _asm.PopToStack(1);
        if (!is64) _il.Emit(OpCodes.Conv_U8);
        _il.Emit(OpCodes.Ldc_I4, memory);
        EmitRuntimeCall(nameof(RuntimeWasm.MemoryGrow));
        if (!is64) _il.Emit(OpCodes.Conv_I4);
        ReloadMemories();
        _asm.PushStack(is64 ? WasmKind.I64 : WasmKind.I32);
    }

    void MemoryCopy(int dst, int src)
    {
        if (!_reachable) return;
        WasmKind dk = AddressKind(dst), sk = AddressKind(src);
        _asm.PopToStack(3);
        // (d, s, n): n is i64 only if both memories are 64-bit.
        bool n64 = dk == WasmKind.I64 && sk == WasmKind.I64;
        LocalBuilder n = _asm.Temp(WasmKind.I64);
        LocalBuilder s = _asm.Temp(WasmKind.I64);
        _il.Emit(n64 ? OpCodes.Conv_I8 : OpCodes.Conv_U8);
        _il.Emit(OpCodes.Stloc, n);
        _il.Emit(sk == WasmKind.I64 ? OpCodes.Conv_I8 : OpCodes.Conv_U8);
        _il.Emit(OpCodes.Stloc, s);
        if (dk != WasmKind.I64) _il.Emit(OpCodes.Conv_U8);
        _il.Emit(OpCodes.Ldloc, s);
        _il.Emit(OpCodes.Ldloc, n);
        _il.Emit(OpCodes.Ldc_I4, dst);
        _il.Emit(OpCodes.Ldc_I4, src);
        EmitRuntimeCall(nameof(RuntimeWasm.MemoryCopy));
    }

    void MemoryFill(int memory)
    {
        if (!_reachable) return;
        WasmKind ak = AddressKind(memory);
        _asm.PopToStack(3);
        LocalBuilder n = _asm.Temp(WasmKind.I64);
        LocalBuilder v = _asm.Temp(WasmKind.I32);
        _il.Emit(ak == WasmKind.I64 ? OpCodes.Conv_I8 : OpCodes.Conv_U8);
        _il.Emit(OpCodes.Stloc, n);
        _il.Emit(OpCodes.Stloc, v);
        if (ak != WasmKind.I64) _il.Emit(OpCodes.Conv_U8);
        _il.Emit(OpCodes.Ldloc, v);
        _il.Emit(OpCodes.Ldloc, n);
        _il.Emit(OpCodes.Ldc_I4, memory);
        EmitRuntimeCall(nameof(RuntimeWasm.MemoryFill));
    }

    // ---- Globals -------------------------------------------------------------

    static readonly MethodInfo s_globalValueRef = typeof(GlobalInstance).GetProperty(nameof(GlobalInstance.ValueRef))!.GetMethod!;
    static readonly FieldInfo s_valueData = typeof(Value).GetField(nameof(Value.Data))!;
    static readonly FieldInfo[] s_dataFields =
    [
        typeof(DUnion).GetField(nameof(DUnion.Int32))!, typeof(DUnion).GetField(nameof(DUnion.Int64))!,
        typeof(DUnion).GetField(nameof(DUnion.Float32))!, typeof(DUnion).GetField(nameof(DUnion.Float64))!,
    ];

    bool GlobalIsPlain(int index)
    {
        GlobalInstance g = _data.Globals[index];
        return !g.IsShared && !g.IsThreadLocal;
    }

    void LoadGlobalRef(int index)
    {
        _il.Emit(OpCodes.Ldloc, _dataLocal);
        _il.Emit(OpCodes.Ldfld, s_dataGlobals);
        _il.Emit(OpCodes.Ldc_I4, index);
        _il.Emit(OpCodes.Ldelem_Ref);
        _il.Emit(OpCodes.Call, s_globalValueRef);
    }

    void GlobalGet(int index)
    {
        if (!_reachable) return;
        WasmKind kind = WasmKinds.Of(_data.Globals[index].Type.ContentType);
        if (!GlobalIsPlain(index))
        {
            GenericInstruction([], [kind]);
            return;
        }
        LoadGlobalRef(index);
        if (kind < WasmKind.S128)
        {
            _il.Emit(OpCodes.Ldflda, s_valueData);
            _il.Emit(OpCodes.Ldfld, s_dataFields[(int)kind]);
        }
        else
        {
            _il.Emit(OpCodes.Ldobj, typeof(Value));
        }
        _asm.PushStack(kind);
    }

    void GlobalSet(int index)
    {
        if (!_reachable) return;
        WasmKind kind = WasmKinds.Of(_data.Globals[index].Type.ContentType);
        if (!GlobalIsPlain(index))
        {
            GenericInstruction([kind], []);
            return;
        }
        _asm.PopToStack(1);
        LocalBuilder value = _asm.Temp(kind);
        _il.Emit(OpCodes.Stloc, value);
        LoadGlobalRef(index);
        if (kind < WasmKind.S128)
        {
            _il.Emit(OpCodes.Ldflda, s_valueData);
            _il.Emit(OpCodes.Ldloc, value);
            _il.Emit(OpCodes.Stfld, s_dataFields[(int)kind]);
        }
        else
        {
            _il.Emit(OpCodes.Ldloc, value);
            _il.Emit(OpCodes.Stobj, typeof(Value));
        }
    }

    void TableGetSet(bool get, int table)
    {
        WasmKind ak = TableAddressKind(table);
        if (get) GenericInstruction([ak], [WasmKind.Ref]);
        else GenericInstruction([ak, WasmKind.Ref], []);
    }
}
