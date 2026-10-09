// The numeric operations of compiled wasm code that are not a single IL
// instruction: integer division and remainder with their traps, float to
// integer truncation (trapping and saturating), unsigned 64-bit integer to
// float conversion, and the float sign, rounding and min/max operations with
// wasm's NaN and signed-zero rules. V8: the out-of-line code of
// LiftoffAssembler::emit_i32_divs etc., the conversions of
// LiftoffCompiler::EmitTypeConversion, and wasm-external-refs.cc
// (float32_nearest_int_wrapper, uint64_to_float32_wrapper, ...).
using System.Runtime.CompilerServices;

namespace V8Sharp.Wasm;

public static partial class RuntimeWasm
{
    const MethodImplOptions Inline = MethodImplOptions.AggressiveInlining;

    // ---- Integer division --------------------------------------------------------------

    [MethodImpl(Inline)]
    public static int I32DivS(int a, int b, WasmCode code, int pc)
    {
        if (b == 0) Trap(MessageTemplate.WasmTrapDivByZero, code, pc);
        if (b == -1 && a == int.MinValue) Trap(MessageTemplate.WasmTrapDivUnrepresentable, code, pc);
        return a / b;
    }

    [MethodImpl(Inline)]
    public static int I32DivU(int a, int b, WasmCode code, int pc)
    {
        if (b == 0) Trap(MessageTemplate.WasmTrapDivByZero, code, pc);
        return (int)((uint)a / (uint)b);
    }

    [MethodImpl(Inline)]
    public static int I32RemS(int a, int b, WasmCode code, int pc)
    {
        if (b == 0) Trap(MessageTemplate.WasmTrapRemByZero, code, pc);
        return b == -1 ? 0 : a % b;
    }

    [MethodImpl(Inline)]
    public static int I32RemU(int a, int b, WasmCode code, int pc)
    {
        if (b == 0) Trap(MessageTemplate.WasmTrapRemByZero, code, pc);
        return (int)((uint)a % (uint)b);
    }

    [MethodImpl(Inline)]
    public static long I64DivS(long a, long b, WasmCode code, int pc)
    {
        if (b == 0) Trap(MessageTemplate.WasmTrapDivByZero, code, pc);
        if (b == -1 && a == long.MinValue) Trap(MessageTemplate.WasmTrapDivUnrepresentable, code, pc);
        return a / b;
    }

    [MethodImpl(Inline)]
    public static long I64DivU(long a, long b, WasmCode code, int pc)
    {
        if (b == 0) Trap(MessageTemplate.WasmTrapDivByZero, code, pc);
        return (long)((ulong)a / (ulong)b);
    }

    [MethodImpl(Inline)]
    public static long I64RemS(long a, long b, WasmCode code, int pc)
    {
        if (b == 0) Trap(MessageTemplate.WasmTrapRemByZero, code, pc);
        return b == -1 ? 0 : a % b;
    }

    [MethodImpl(Inline)]
    public static long I64RemU(long a, long b, WasmCode code, int pc)
    {
        if (b == 0) Trap(MessageTemplate.WasmTrapRemByZero, code, pc);
        return (long)((ulong)a % (ulong)b);
    }

    // ---- Float to integer truncation ------------------------------------------------------
    // A value is in range when its truncation is; NaN compares false and traps.

    [MethodImpl(Inline)]
    public static int I32TruncF32S(float x, WasmCode code, int pc)
    {
        if (!(x >= -2147483648f && x < 2147483648f)) Trap(MessageTemplate.WasmTrapFloatUnrepresentable, code, pc);
        return (int)x;
    }

    [MethodImpl(Inline)]
    public static int I32TruncF32U(float x, WasmCode code, int pc)
    {
        if (!(x > -1f && x < 4294967296f)) Trap(MessageTemplate.WasmTrapFloatUnrepresentable, code, pc);
        return (int)(uint)x;
    }

    [MethodImpl(Inline)]
    public static int I32TruncF64S(double x, WasmCode code, int pc)
    {
        if (!(x > -2147483649.0 && x < 2147483648.0)) Trap(MessageTemplate.WasmTrapFloatUnrepresentable, code, pc);
        return (int)x;
    }

    [MethodImpl(Inline)]
    public static int I32TruncF64U(double x, WasmCode code, int pc)
    {
        if (!(x > -1.0 && x < 4294967296.0)) Trap(MessageTemplate.WasmTrapFloatUnrepresentable, code, pc);
        return (int)(uint)x;
    }

    [MethodImpl(Inline)]
    public static long I64TruncF32S(float x, WasmCode code, int pc)
    {
        if (!(x >= -9223372036854775808f && x < 9223372036854775808f)) Trap(MessageTemplate.WasmTrapFloatUnrepresentable, code, pc);
        return (long)x;
    }

    [MethodImpl(Inline)]
    public static long I64TruncF32U(float x, WasmCode code, int pc)
    {
        if (!(x > -1f && x < 18446744073709551616f)) Trap(MessageTemplate.WasmTrapFloatUnrepresentable, code, pc);
        return (long)(ulong)x;
    }

    [MethodImpl(Inline)]
    public static long I64TruncF64S(double x, WasmCode code, int pc)
    {
        if (!(x >= -9223372036854775808.0 && x < 9223372036854775808.0)) Trap(MessageTemplate.WasmTrapFloatUnrepresentable, code, pc);
        return (long)x;
    }

    [MethodImpl(Inline)]
    public static long I64TruncF64U(double x, WasmCode code, int pc)
    {
        if (!(x > -1.0 && x < 18446744073709551616.0)) Trap(MessageTemplate.WasmTrapFloatUnrepresentable, code, pc);
        return (long)(ulong)x;
    }

    // ---- Saturating truncation (nontrapping float-to-int) ------------------------------------

    [MethodImpl(Inline)]
    public static int I32TruncSatF32S(float x) =>
        float.IsNaN(x) ? 0 : x < -2147483648f ? int.MinValue : x >= 2147483648f ? int.MaxValue : (int)x;

    [MethodImpl(Inline)]
    public static int I32TruncSatF32U(float x) =>
        !(x > -1f) ? 0 : x >= 4294967296f ? -1 : (int)(uint)x;

    [MethodImpl(Inline)]
    public static int I32TruncSatF64S(double x) =>
        double.IsNaN(x) ? 0 : x <= -2147483649.0 ? int.MinValue : x >= 2147483648.0 ? int.MaxValue : (int)x;

    [MethodImpl(Inline)]
    public static int I32TruncSatF64U(double x) =>
        !(x > -1.0) ? 0 : x >= 4294967296.0 ? -1 : (int)(uint)x;

    [MethodImpl(Inline)]
    public static long I64TruncSatF32S(float x) =>
        float.IsNaN(x) ? 0 : x < -9223372036854775808f ? long.MinValue : x >= 9223372036854775808f ? long.MaxValue : (long)x;

    [MethodImpl(Inline)]
    public static long I64TruncSatF32U(float x) =>
        !(x > -1f) ? 0 : x >= 18446744073709551616f ? -1L : (long)(ulong)x;

    [MethodImpl(Inline)]
    public static long I64TruncSatF64S(double x) =>
        double.IsNaN(x) ? 0 : x < -9223372036854775808.0 ? long.MinValue : x >= 9223372036854775808.0 ? long.MaxValue : (long)x;

    [MethodImpl(Inline)]
    public static long I64TruncSatF64U(double x) =>
        !(x > -1.0) ? 0 : x >= 18446744073709551616.0 ? -1L : (long)(ulong)x;

    // ---- Unsigned 64-bit integer to float (one rounding) ---------------------------------------

    [MethodImpl(Inline)]
    public static float F32ConvertI64U(long x)
    {
        if (x >= 0) return x;
        // Halve, keeping the lost bit sticky, convert, double.
        ulong u = (ulong)x;
        return (float)(long)((u >> 1) | (u & 1)) * 2f;
    }

    [MethodImpl(Inline)]
    public static double F64ConvertI64U(long x)
    {
        if (x >= 0) return x;
        ulong u = (ulong)x;
        return (double)(long)((u >> 1) | (u & 1)) * 2.0;
    }

    // ---- Float operations ------------------------------------------------------------------------
    // abs, neg and copysign change only the sign bit (a NaN keeps its
    // payload); min and max return NaN for a NaN operand (quieted) and order
    // -0 below +0.

    [MethodImpl(Inline)]
    public static float F32Abs(float x) => BitConverter.Int32BitsToSingle(BitConverter.SingleToInt32Bits(x) & 0x7fffffff);

    [MethodImpl(Inline)]
    public static float F32Neg(float x) => BitConverter.Int32BitsToSingle(BitConverter.SingleToInt32Bits(x) ^ int.MinValue);

    [MethodImpl(Inline)]
    public static float F32CopySign(float x, float y) => BitConverter.Int32BitsToSingle(
        (BitConverter.SingleToInt32Bits(x) & 0x7fffffff) | (BitConverter.SingleToInt32Bits(y) & int.MinValue));

    [MethodImpl(Inline)]
    public static float F32Min(float x, float y) => float.IsNaN(x) || float.IsNaN(y) ? x + y : MathF.Min(x, y);

    [MethodImpl(Inline)]
    public static float F32Max(float x, float y) => float.IsNaN(x) || float.IsNaN(y) ? x + y : MathF.Max(x, y);

    [MethodImpl(Inline)]
    public static float F32Nearest(float x) => MathF.Round(x, MidpointRounding.ToEven);

    [MethodImpl(Inline)]
    public static double F64Abs(double x) => BitConverter.Int64BitsToDouble(BitConverter.DoubleToInt64Bits(x) & long.MaxValue);

    [MethodImpl(Inline)]
    public static double F64Neg(double x) => BitConverter.Int64BitsToDouble(BitConverter.DoubleToInt64Bits(x) ^ long.MinValue);

    [MethodImpl(Inline)]
    public static double F64CopySign(double x, double y) => BitConverter.Int64BitsToDouble(
        (BitConverter.DoubleToInt64Bits(x) & long.MaxValue) | (BitConverter.DoubleToInt64Bits(y) & long.MinValue));

    [MethodImpl(Inline)]
    public static double F64Min(double x, double y) => double.IsNaN(x) || double.IsNaN(y) ? x + y : Math.Min(x, y);

    [MethodImpl(Inline)]
    public static double F64Max(double x, double y) => double.IsNaN(x) || double.IsNaN(y) ? x + y : Math.Max(x, y);

    [MethodImpl(Inline)]
    public static double F64Nearest(double x) => Math.Round(x, MidpointRounding.ToEven);
}
