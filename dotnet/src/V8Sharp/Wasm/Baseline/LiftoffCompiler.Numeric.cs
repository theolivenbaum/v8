// LiftoffCompiler::UnOp, BinOp and EmitTypeConversion
// (src/wasm/baseline/liftoff-compiler.cc): the numeric instructions. IL's
// arithmetic is IEEE-754 binary32/binary64 as wasm's; where wasm and IL differ
// (shift counts, division traps, float-to-int conversion, min/max with NaN
// and signed zeros, sign operations on NaN payloads, unsigned 64-bit to float
// rounding) the operation is a helper in RuntimeWasm.Numeric.cs.
using System.Numerics;
using System.Reflection;
using System.Reflection.Emit;

namespace V8Sharp.Wasm.Baseline;

internal sealed partial class LiftoffCompiler
{
    static readonly MethodInfo s_rotl32 = typeof(BitOperations).GetMethod(nameof(BitOperations.RotateLeft), [typeof(uint), typeof(int)])!;
    static readonly MethodInfo s_rotr32 = typeof(BitOperations).GetMethod(nameof(BitOperations.RotateRight), [typeof(uint), typeof(int)])!;
    static readonly MethodInfo s_rotl64 = typeof(BitOperations).GetMethod(nameof(BitOperations.RotateLeft), [typeof(ulong), typeof(int)])!;
    static readonly MethodInfo s_rotr64 = typeof(BitOperations).GetMethod(nameof(BitOperations.RotateRight), [typeof(ulong), typeof(int)])!;
    static readonly MethodInfo s_clz32 = typeof(BitOperations).GetMethod(nameof(BitOperations.LeadingZeroCount), [typeof(uint)])!;
    static readonly MethodInfo s_clz64 = typeof(BitOperations).GetMethod(nameof(BitOperations.LeadingZeroCount), [typeof(ulong)])!;
    static readonly MethodInfo s_ctz32 = typeof(BitOperations).GetMethod(nameof(BitOperations.TrailingZeroCount), [typeof(int)])!;
    static readonly MethodInfo s_ctz64 = typeof(BitOperations).GetMethod(nameof(BitOperations.TrailingZeroCount), [typeof(long)])!;
    static readonly MethodInfo s_popcnt32 = typeof(BitOperations).GetMethod(nameof(BitOperations.PopCount), [typeof(uint)])!;
    static readonly MethodInfo s_popcnt64 = typeof(BitOperations).GetMethod(nameof(BitOperations.PopCount), [typeof(ulong)])!;
    static readonly MethodInfo s_singleToInt32Bits = typeof(BitConverter).GetMethod(nameof(BitConverter.SingleToInt32Bits))!;
    static readonly MethodInfo s_int32BitsToSingle = typeof(BitConverter).GetMethod(nameof(BitConverter.Int32BitsToSingle))!;
    static readonly MethodInfo s_doubleToInt64Bits = typeof(BitConverter).GetMethod(nameof(BitConverter.DoubleToInt64Bits))!;
    static readonly MethodInfo s_int64BitsToDouble = typeof(BitConverter).GetMethod(nameof(BitConverter.Int64BitsToDouble))!;
    static readonly MethodInfo s_ceil32 = typeof(MathF).GetMethod(nameof(MathF.Ceiling), [typeof(float)])!;
    static readonly MethodInfo s_floor32 = typeof(MathF).GetMethod(nameof(MathF.Floor), [typeof(float)])!;
    static readonly MethodInfo s_trunc32 = typeof(MathF).GetMethod(nameof(MathF.Truncate), [typeof(float)])!;
    static readonly MethodInfo s_sqrt32 = typeof(MathF).GetMethod(nameof(MathF.Sqrt), [typeof(float)])!;
    static readonly MethodInfo s_ceil64 = typeof(Math).GetMethod(nameof(Math.Ceiling), [typeof(double)])!;
    static readonly MethodInfo s_floor64 = typeof(Math).GetMethod(nameof(Math.Floor), [typeof(double)])!;
    static readonly MethodInfo s_trunc64 = typeof(Math).GetMethod(nameof(Math.Truncate), [typeof(double)])!;
    static readonly MethodInfo s_sqrt64 = typeof(Math).GetMethod(nameof(Math.Sqrt), [typeof(double)])!;

    void Binary(OpCode op, WasmKind kind)
    {
        _asm.PopToStack(2);
        _il.Emit(op);
        _asm.PushStack(kind);
    }

    void Compare(WasmKind operandKind, OpCode op, bool negate)
    {
        _asm.PopToStack(2);
        _il.Emit(op);
        if (negate)
        {
            _il.Emit(OpCodes.Ldc_I4_0);
            _il.Emit(OpCodes.Ceq);
        }
        _asm.PushStack(WasmKind.I32);
    }

    void Unary(MethodInfo method, WasmKind result, bool widen = false)
    {
        _asm.PopToStack(1);
        _il.Emit(OpCodes.Call, method);
        if (widen) _il.Emit(OpCodes.Conv_I8);
        _asm.PushStack(result);
    }

    void Convert(OpCode op, WasmKind result)
    {
        _asm.PopToStack(1);
        _il.Emit(op);
        _asm.PushStack(result);
    }

    void Convert(OpCode op1, OpCode op2, WasmKind result)
    {
        _asm.PopToStack(1);
        _il.Emit(op1);
        _il.Emit(op2);
        _asm.PushStack(result);
    }

    void Shift(OpCode op, bool is64)
    {
        _asm.PopToStack(2);
        if (is64)
        {
            _il.Emit(OpCodes.Conv_I4);
            _il.Emit(OpCodes.Ldc_I4, 63);
        }
        else
        {
            _il.Emit(OpCodes.Ldc_I4, 31);
        }
        _il.Emit(OpCodes.And);
        _il.Emit(op);
        _asm.PushStack(is64 ? WasmKind.I64 : WasmKind.I32);
    }

    void Rotate(MethodInfo method, bool is64)
    {
        _asm.PopToStack(2);
        if (is64) _il.Emit(OpCodes.Conv_I4);
        _il.Emit(OpCodes.Call, method);
        _asm.PushStack(is64 ? WasmKind.I64 : WasmKind.I32);
    }

    /// <summary>An operation implemented by a RuntimeWasm helper of the same name.</summary>
    void Helper(int operands, string name, WasmKind result, bool withPc = false)
    {
        _asm.PopToStack(operands);
        if (withPc)
        {
            EmitRuntimeCall(name);
        }
        else
        {
            _il.Emit(OpCodes.Call, RuntimeWasm.Method(name));
        }
        _asm.PushStack(result);
    }

    bool TryFoldI32(byte opcode)
    {
        // Constant folding of the commonest integer operations on constants
        // (Liftoff folds constant operands into immediates).
        if (_asm.Height < 2) return false;
        VarState b = _asm.Stack[^1], a = _asm.Stack[^2];
        if (a.Location != VarState.Loc.Const || b.Location != VarState.Loc.Const) return false;
        int x = (int)a.Value, y = (int)b.Value;
        int r;
        switch (opcode)
        {
            case 0x6a: r = x + y; break;
            case 0x6b: r = x - y; break;
            case 0x6c: r = x * y; break;
            case 0x71: r = x & y; break;
            case 0x72: r = x | y; break;
            case 0x73: r = x ^ y; break;
            case 0x74: r = x << (y & 31); break;
            case 0x75: r = x >> (y & 31); break;
            case 0x76: r = (int)((uint)x >> (y & 31)); break;
            default: return false;
        }
        _asm.Drop(2);
        _asm.PushConst(WasmKind.I32, r);
        return true;
    }

    void NumericOp(byte opcode)
    {
        const WasmKind I = WasmKind.I32, L = WasmKind.I64, F = WasmKind.F32, D = WasmKind.F64;
        switch (opcode)
        {
            // i32 comparisons
            case 0x45:
                _asm.PushConst(I, 0);
                Compare(I, OpCodes.Ceq, false);
                return;
            case 0x46: Compare(I, OpCodes.Ceq, false); return;
            case 0x47: Compare(I, OpCodes.Ceq, true); return;
            case 0x48: Compare(I, OpCodes.Clt, false); return;
            case 0x49: Compare(I, OpCodes.Clt_Un, false); return;
            case 0x4a: Compare(I, OpCodes.Cgt, false); return;
            case 0x4b: Compare(I, OpCodes.Cgt_Un, false); return;
            case 0x4c: Compare(I, OpCodes.Cgt, true); return;
            case 0x4d: Compare(I, OpCodes.Cgt_Un, true); return;
            case 0x4e: Compare(I, OpCodes.Clt, true); return;
            case 0x4f: Compare(I, OpCodes.Clt_Un, true); return;
            // i64 comparisons
            case 0x50:
                _asm.PushConst(L, 0);
                Compare(L, OpCodes.Ceq, false);
                return;
            case 0x51: Compare(L, OpCodes.Ceq, false); return;
            case 0x52: Compare(L, OpCodes.Ceq, true); return;
            case 0x53: Compare(L, OpCodes.Clt, false); return;
            case 0x54: Compare(L, OpCodes.Clt_Un, false); return;
            case 0x55: Compare(L, OpCodes.Cgt, false); return;
            case 0x56: Compare(L, OpCodes.Cgt_Un, false); return;
            case 0x57: Compare(L, OpCodes.Cgt, true); return;
            case 0x58: Compare(L, OpCodes.Cgt_Un, true); return;
            case 0x59: Compare(L, OpCodes.Clt, true); return;
            case 0x5a: Compare(L, OpCodes.Clt_Un, true); return;
            // f32 / f64 comparisons (an unordered comparison is false, except ne)
            case 0x5b: Compare(F, OpCodes.Ceq, false); return;
            case 0x5c: Compare(F, OpCodes.Ceq, true); return;
            case 0x5d: Compare(F, OpCodes.Clt, false); return;
            case 0x5e: Compare(F, OpCodes.Cgt, false); return;
            case 0x5f: Compare(F, OpCodes.Cgt_Un, true); return;
            case 0x60: Compare(F, OpCodes.Clt_Un, true); return;
            case 0x61: Compare(D, OpCodes.Ceq, false); return;
            case 0x62: Compare(D, OpCodes.Ceq, true); return;
            case 0x63: Compare(D, OpCodes.Clt, false); return;
            case 0x64: Compare(D, OpCodes.Cgt, false); return;
            case 0x65: Compare(D, OpCodes.Cgt_Un, true); return;
            case 0x66: Compare(D, OpCodes.Clt_Un, true); return;
            // i32 arithmetic
            case 0x67: Unary(s_clz32, I); return;
            case 0x68: Unary(s_ctz32, I); return;
            case 0x69: Unary(s_popcnt32, I); return;
            case 0x6a:
                if (!TryFoldI32(opcode)) Binary(OpCodes.Add, I);
                return;
            case 0x6b:
                if (!TryFoldI32(opcode)) Binary(OpCodes.Sub, I);
                return;
            case 0x6c:
                if (!TryFoldI32(opcode)) Binary(OpCodes.Mul, I);
                return;
            case 0x6d: Helper(2, nameof(RuntimeWasm.I32DivS), I, withPc: true); return;
            case 0x6e: Helper(2, nameof(RuntimeWasm.I32DivU), I, withPc: true); return;
            case 0x6f: Helper(2, nameof(RuntimeWasm.I32RemS), I, withPc: true); return;
            case 0x70: Helper(2, nameof(RuntimeWasm.I32RemU), I, withPc: true); return;
            case 0x71:
                if (!TryFoldI32(opcode)) Binary(OpCodes.And, I);
                return;
            case 0x72:
                if (!TryFoldI32(opcode)) Binary(OpCodes.Or, I);
                return;
            case 0x73:
                if (!TryFoldI32(opcode)) Binary(OpCodes.Xor, I);
                return;
            case 0x74:
                if (!TryFoldI32(opcode)) Shift(OpCodes.Shl, false);
                return;
            case 0x75:
                if (!TryFoldI32(opcode)) Shift(OpCodes.Shr, false);
                return;
            case 0x76:
                if (!TryFoldI32(opcode)) Shift(OpCodes.Shr_Un, false);
                return;
            case 0x77: Rotate(s_rotl32, false); return;
            case 0x78: Rotate(s_rotr32, false); return;
            // i64 arithmetic
            case 0x79: Unary(s_clz64, L, widen: true); return;
            case 0x7a: Unary(s_ctz64, L, widen: true); return;
            case 0x7b: Unary(s_popcnt64, L, widen: true); return;
            case 0x7c: Binary(OpCodes.Add, L); return;
            case 0x7d: Binary(OpCodes.Sub, L); return;
            case 0x7e: Binary(OpCodes.Mul, L); return;
            case 0x7f: Helper(2, nameof(RuntimeWasm.I64DivS), L, withPc: true); return;
            case 0x80: Helper(2, nameof(RuntimeWasm.I64DivU), L, withPc: true); return;
            case 0x81: Helper(2, nameof(RuntimeWasm.I64RemS), L, withPc: true); return;
            case 0x82: Helper(2, nameof(RuntimeWasm.I64RemU), L, withPc: true); return;
            case 0x83: Binary(OpCodes.And, L); return;
            case 0x84: Binary(OpCodes.Or, L); return;
            case 0x85: Binary(OpCodes.Xor, L); return;
            case 0x86: Shift(OpCodes.Shl, true); return;
            case 0x87: Shift(OpCodes.Shr, true); return;
            case 0x88: Shift(OpCodes.Shr_Un, true); return;
            case 0x89: Rotate(s_rotl64, true); return;
            case 0x8a: Rotate(s_rotr64, true); return;
            // f32 arithmetic
            case 0x8b: Helper(1, nameof(RuntimeWasm.F32Abs), F); return;
            case 0x8c: Helper(1, nameof(RuntimeWasm.F32Neg), F); return;
            case 0x8d: Unary(s_ceil32, F); return;
            case 0x8e: Unary(s_floor32, F); return;
            case 0x8f: Unary(s_trunc32, F); return;
            case 0x90: Helper(1, nameof(RuntimeWasm.F32Nearest), F); return;
            case 0x91: Unary(s_sqrt32, F); return;
            case 0x92: Binary(OpCodes.Add, F); return;
            case 0x93: Binary(OpCodes.Sub, F); return;
            case 0x94: Binary(OpCodes.Mul, F); return;
            case 0x95: Binary(OpCodes.Div, F); return;
            case 0x96: Helper(2, nameof(RuntimeWasm.F32Min), F); return;
            case 0x97: Helper(2, nameof(RuntimeWasm.F32Max), F); return;
            case 0x98: Helper(2, nameof(RuntimeWasm.F32CopySign), F); return;
            // f64 arithmetic
            case 0x99: Helper(1, nameof(RuntimeWasm.F64Abs), D); return;
            case 0x9a: Helper(1, nameof(RuntimeWasm.F64Neg), D); return;
            case 0x9b: Unary(s_ceil64, D); return;
            case 0x9c: Unary(s_floor64, D); return;
            case 0x9d: Unary(s_trunc64, D); return;
            case 0x9e: Helper(1, nameof(RuntimeWasm.F64Nearest), D); return;
            case 0x9f: Unary(s_sqrt64, D); return;
            case 0xa0: Binary(OpCodes.Add, D); return;
            case 0xa1: Binary(OpCodes.Sub, D); return;
            case 0xa2: Binary(OpCodes.Mul, D); return;
            case 0xa3: Binary(OpCodes.Div, D); return;
            case 0xa4: Helper(2, nameof(RuntimeWasm.F64Min), D); return;
            case 0xa5: Helper(2, nameof(RuntimeWasm.F64Max), D); return;
            case 0xa6: Helper(2, nameof(RuntimeWasm.F64CopySign), D); return;
            // conversions
            case 0xa7: Convert(OpCodes.Conv_I4, I); return;
            case 0xa8: Helper(1, nameof(RuntimeWasm.I32TruncF32S), I, withPc: true); return;
            case 0xa9: Helper(1, nameof(RuntimeWasm.I32TruncF32U), I, withPc: true); return;
            case 0xaa: Helper(1, nameof(RuntimeWasm.I32TruncF64S), I, withPc: true); return;
            case 0xab: Helper(1, nameof(RuntimeWasm.I32TruncF64U), I, withPc: true); return;
            case 0xac: Convert(OpCodes.Conv_I8, L); return;
            case 0xad: Convert(OpCodes.Conv_U8, L); return;
            case 0xae: Helper(1, nameof(RuntimeWasm.I64TruncF32S), L, withPc: true); return;
            case 0xaf: Helper(1, nameof(RuntimeWasm.I64TruncF32U), L, withPc: true); return;
            case 0xb0: Helper(1, nameof(RuntimeWasm.I64TruncF64S), L, withPc: true); return;
            case 0xb1: Helper(1, nameof(RuntimeWasm.I64TruncF64U), L, withPc: true); return;
            case 0xb2: Convert(OpCodes.Conv_R4, F); return;
            case 0xb3: Convert(OpCodes.Conv_U8, OpCodes.Conv_R4, F); return;
            case 0xb4: Convert(OpCodes.Conv_R4, F); return;
            case 0xb5: Helper(1, nameof(RuntimeWasm.F32ConvertI64U), F); return;
            case 0xb6: Convert(OpCodes.Conv_R4, F); return;
            case 0xb7: Convert(OpCodes.Conv_R8, D); return;
            case 0xb8: Convert(OpCodes.Conv_U8, OpCodes.Conv_R8, D); return;
            case 0xb9: Convert(OpCodes.Conv_R8, D); return;
            case 0xba: Helper(1, nameof(RuntimeWasm.F64ConvertI64U), D); return;
            case 0xbb: Convert(OpCodes.Conv_R8, D); return;
            case 0xbc: Unary(s_singleToInt32Bits, I); return;
            case 0xbd: Unary(s_doubleToInt64Bits, L); return;
            case 0xbe: Unary(s_int32BitsToSingle, F); return;
            case 0xbf: Unary(s_int64BitsToDouble, D); return;
            // sign extension
            case 0xc0: Convert(OpCodes.Conv_I1, I); return;
            case 0xc1: Convert(OpCodes.Conv_I2, I); return;
            case 0xc2: Convert(OpCodes.Conv_I1, OpCodes.Conv_I8, L); return;
            case 0xc3: Convert(OpCodes.Conv_I2, OpCodes.Conv_I8, L); return;
            case 0xc4: Convert(OpCodes.Conv_I4, OpCodes.Conv_I8, L); return;
        }
        Unsupported("numeric opcode");
    }

    /// <summary>The 0xfc prefix: saturating conversions, bulk memory and table instructions.</summary>
    void NumericPrefixedOp(uint index)
    {
        const WasmKind I = WasmKind.I32, L = WasmKind.I64, F = WasmKind.F32, D = WasmKind.F64;
        switch (index)
        {
            case 0: if (_reachable) Helper(1, nameof(RuntimeWasm.I32TruncSatF32S), I); return;
            case 1: if (_reachable) Helper(1, nameof(RuntimeWasm.I32TruncSatF32U), I); return;
            case 2: if (_reachable) Helper(1, nameof(RuntimeWasm.I32TruncSatF64S), I); return;
            case 3: if (_reachable) Helper(1, nameof(RuntimeWasm.I32TruncSatF64U), I); return;
            case 4: if (_reachable) Helper(1, nameof(RuntimeWasm.I64TruncSatF32S), L); return;
            case 5: if (_reachable) Helper(1, nameof(RuntimeWasm.I64TruncSatF32U), L); return;
            case 6: if (_reachable) Helper(1, nameof(RuntimeWasm.I64TruncSatF64S), L); return;
            case 7: if (_reachable) Helper(1, nameof(RuntimeWasm.I64TruncSatF64U), L); return;
            case 8: // memory.init data mem: (d, s, n)
            {
                ReadU32();
                int mem = (int)ReadU32();
                GenericInstruction([AddressKind(mem), I, I], []);
                return;
            }
            case 9: // data.drop
                ReadU32();
                GenericInstruction([], []);
                return;
            case 10: // memory.copy dst src
            {
                int dst = (int)ReadU32();
                int src = (int)ReadU32();
                MemoryCopy(dst, src);
                return;
            }
            case 11: // memory.fill mem
                MemoryFill((int)ReadU32());
                return;
            case 12: // table.init elem table
            {
                ReadU32();
                int table = (int)ReadU32();
                GenericInstruction([TableAddressKind(table), I, I], []);
                return;
            }
            case 13: // elem.drop
                ReadU32();
                GenericInstruction([], []);
                return;
            case 14: // table.copy dst src
            {
                int dst = (int)ReadU32();
                int src = (int)ReadU32();
                WasmKind dk = TableAddressKind(dst), sk = TableAddressKind(src);
                WasmKind nk = dk == L && sk == L ? L : I;
                GenericInstruction([dk, sk, nk], []);
                return;
            }
            case 15: // table.grow
            {
                int table = (int)ReadU32();
                WasmKind ak = TableAddressKind(table);
                GenericInstruction([WasmKind.Ref, ak], [ak]);
                return;
            }
            case 16: // table.size
            {
                int table = (int)ReadU32();
                GenericInstruction([], [TableAddressKind(table)]);
                return;
            }
            case 17: // table.fill
            {
                int table = (int)ReadU32();
                WasmKind ak = TableAddressKind(table);
                GenericInstruction([ak, WasmKind.Ref, ak], []);
                return;
            }
        }
        Unsupported("0xfc opcode " + index);
    }
}
