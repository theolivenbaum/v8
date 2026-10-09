// LiftoffCompiler's asm.js opcodes (V8 14.7: LiftoffCompiler::AsmjsLoadMem,
// AsmjsStoreMem and the kExprI32Asmjs* / kExprF64{Acos..Mod} cases of
// BinOp/UnOp in liftoff-compiler.cc; the semantics are in
// src/V8Sharp.Wasm/Instructions/AsmJs.cs). Loads and stores are inline: an
// access out of bounds reads 0 or NaN and a store does nothing, as V8's
// out-of-line code does; the stores leave their value on the stack.
using System.Reflection;
using System.Reflection.Emit;
using V8Sharp.Base;
using V8Sharp.Base.Numbers;
using Wacs.Core.Instructions;
using AsmJsCode = Wacs.Core.OpCodes.AsmJsCode;

using Label = System.Reflection.Emit.Label;

namespace V8Sharp.Wasm.Baseline;

internal sealed partial class LiftoffCompiler
{
    static readonly MethodInfo s_asmDivS = typeof(InstAsmJs).GetMethod(nameof(InstAsmJs.DivS))!;
    static readonly MethodInfo s_asmDivU = typeof(InstAsmJs).GetMethod(nameof(InstAsmJs.DivU))!;
    static readonly MethodInfo s_asmRemS = typeof(InstAsmJs).GetMethod(nameof(InstAsmJs.RemS))!;
    static readonly MethodInfo s_asmRemU = typeof(InstAsmJs).GetMethod(nameof(InstAsmJs.RemU))!;
    static readonly MethodInfo s_doubleToInt32 = typeof(Conversions).GetMethod(nameof(Conversions.DoubleToInt32), [typeof(double)])!;
    static readonly MethodInfo s_pow = typeof(InternalMath).GetMethod(nameof(InternalMath.pow))!;

    static MethodInfo Ieee754Method(string name, int arity) =>
        typeof(Ieee754).GetMethod(name, arity == 1 ? [typeof(double)] : [typeof(double), typeof(double)])!;

    void AsmJsOp(uint index)
    {
        if (!_reachable) return;
        var code = (AsmJsCode)index;
        switch (code)
        {
            case AsmJsCode.F64Acos: Unary(Ieee754Method("acos", 1), WasmKind.F64); return;
            case AsmJsCode.F64Asin: Unary(Ieee754Method("asin", 1), WasmKind.F64); return;
            case AsmJsCode.F64Atan: Unary(Ieee754Method("atan", 1), WasmKind.F64); return;
            case AsmJsCode.F64Cos: Unary(Ieee754Method("cos", 1), WasmKind.F64); return;
            case AsmJsCode.F64Sin: Unary(Ieee754Method("sin", 1), WasmKind.F64); return;
            case AsmJsCode.F64Tan: Unary(Ieee754Method("tan", 1), WasmKind.F64); return;
            case AsmJsCode.F64Exp: Unary(Ieee754Method("exp", 1), WasmKind.F64); return;
            case AsmJsCode.F64Log: Unary(Ieee754Method("log", 1), WasmKind.F64); return;
            case AsmJsCode.F64Atan2:
                _asm.PopToStack(2);
                _il.Emit(OpCodes.Call, Ieee754Method("atan2", 2));
                _asm.PushStack(WasmKind.F64);
                return;
            case AsmJsCode.F64Pow:
                _asm.PopToStack(2);
                _il.Emit(OpCodes.Call, s_pow);
                _asm.PushStack(WasmKind.F64);
                return;
            case AsmJsCode.F64Mod:
                // fmod: IL's rem on floating-point operands.
                Binary(OpCodes.Rem, WasmKind.F64);
                return;
            case AsmJsCode.I32AsmjsDivS: AsmJsDivRem(s_asmDivS); return;
            case AsmJsCode.I32AsmjsDivU: AsmJsDivRem(s_asmDivU); return;
            case AsmJsCode.I32AsmjsRemS: AsmJsDivRem(s_asmRemS); return;
            case AsmJsCode.I32AsmjsRemU: AsmJsDivRem(s_asmRemU); return;
            case AsmJsCode.I32AsmjsSConvertF32:
            case AsmJsCode.I32AsmjsUConvertF32:
                _asm.PopToStack(1);
                _il.Emit(OpCodes.Conv_R8);
                _il.Emit(OpCodes.Call, s_doubleToInt32);
                _asm.PushStack(WasmKind.I32);
                return;
            case AsmJsCode.I32AsmjsSConvertF64:
            case AsmJsCode.I32AsmjsUConvertF64:
                _asm.PopToStack(1);
                _il.Emit(OpCodes.Call, s_doubleToInt32);
                _asm.PushStack(WasmKind.I32);
                return;
            case AsmJsCode.I32AsmjsLoadMem8S: AsmJsLoad(1, OpCodes.Ldind_I1, WasmKind.I32); return;
            case AsmJsCode.I32AsmjsLoadMem8U: AsmJsLoad(1, OpCodes.Ldind_U1, WasmKind.I32); return;
            case AsmJsCode.I32AsmjsLoadMem16S: AsmJsLoad(2, OpCodes.Ldind_I2, WasmKind.I32); return;
            case AsmJsCode.I32AsmjsLoadMem16U: AsmJsLoad(2, OpCodes.Ldind_U2, WasmKind.I32); return;
            case AsmJsCode.I32AsmjsLoadMem: AsmJsLoad(4, OpCodes.Ldind_I4, WasmKind.I32); return;
            case AsmJsCode.F32AsmjsLoadMem: AsmJsLoad(4, OpCodes.Ldind_R4, WasmKind.F32); return;
            case AsmJsCode.F64AsmjsLoadMem: AsmJsLoad(8, OpCodes.Ldind_R8, WasmKind.F64); return;
            case AsmJsCode.I32AsmjsStoreMem8: AsmJsStore(1, OpCodes.Stind_I1, WasmKind.I32); return;
            case AsmJsCode.I32AsmjsStoreMem16: AsmJsStore(2, OpCodes.Stind_I2, WasmKind.I32); return;
            case AsmJsCode.I32AsmjsStoreMem: AsmJsStore(4, OpCodes.Stind_I4, WasmKind.I32); return;
            case AsmJsCode.F32AsmjsStoreMem: AsmJsStore(4, OpCodes.Stind_R4, WasmKind.F32); return;
            case AsmJsCode.F64AsmjsStoreMem: AsmJsStore(8, OpCodes.Stind_R8, WasmKind.F64); return;
            default:
                Unsupported("asmjs opcode 0xfa" + index.ToString("x2", System.Globalization.CultureInfo.InvariantCulture));
                return;
        }
    }

    void AsmJsDivRem(MethodInfo helper)
    {
        _asm.PopToStack(2);
        _il.Emit(OpCodes.Call, helper);
        _asm.PushStack(WasmKind.I32);
    }

    /// <summary>
    /// The index on the IL stack, as an unsigned 64-bit address in
    /// <paramref name="address"/>; branches to <paramref name="outOfBounds"/>
    /// unless <paramref name="size"/> bytes at it are in memory 0.
    /// </summary>
    void AsmJsBoundsCheck(int size, LocalBuilder address, Label outOfBounds)
    {
        _il.Emit(OpCodes.Conv_U8);
        _il.Emit(OpCodes.Stloc, address);
        _il.Emit(OpCodes.Ldloc, address);
        _il.Emit(OpCodes.Ldc_I8, (long)size);
        _il.Emit(OpCodes.Add);
        LoadMemorySize(0);
        _il.Emit(OpCodes.Bgt_Un, outOfBounds);
    }

    void AsmJsLoad(int size, OpCode load, WasmKind kind)
    {
        _asm.PopToStack(1);
        LocalBuilder address = _asm.Temp(WasmKind.I64);
        Label outOfBounds = _il.DefineLabel();
        Label done = _il.DefineLabel();
        AsmJsBoundsCheck(size, address, outOfBounds);
        EmitMemoryPointer(0, address);
        _il.Emit(OpCodes.Unaligned, (byte)1);
        _il.Emit(load);
        _il.Emit(OpCodes.Br, done);
        _il.MarkLabel(outOfBounds);
        // Out of bounds: 0, or NaN for floating-point views (asm.js reads
        // undefined and coerces it).
        switch (kind)
        {
            case WasmKind.I32: _il.Emit(OpCodes.Ldc_I4_0); break;
            case WasmKind.F32: _il.Emit(OpCodes.Ldc_R4, float.NaN); break;
            default: _il.Emit(OpCodes.Ldc_R8, double.NaN); break;
        }
        _il.MarkLabel(done);
        _asm.PushStack(kind);
    }

    void AsmJsStore(int size, OpCode store, WasmKind kind)
    {
        _asm.PopToStack(2);
        LocalBuilder value = _asm.Temp(kind);
        LocalBuilder address = _asm.Temp(WasmKind.I64);
        Label outOfBounds = _il.DefineLabel();
        _il.Emit(OpCodes.Stloc, value);
        AsmJsBoundsCheck(size, address, outOfBounds);
        EmitMemoryPointer(0, address);
        _il.Emit(OpCodes.Ldloc, value);
        _il.Emit(OpCodes.Unaligned, (byte)1);
        _il.Emit(store);
        _il.MarkLabel(outOfBounds);
        _il.Emit(OpCodes.Ldloc, value);
        _asm.PushStack(kind);
    }
}
