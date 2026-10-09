// The SIMD instructions of compiled wasm code (V8: the emit_* functions of
// src/wasm/baseline/x64/liftoff-assembler-x64-inl.h and the SIMD lowering of
// the wasm compiler), on System.Runtime.Intrinsics.Vector128. Each method is
// named after V8's opcode (wasm-opcodes.h), takes the operands in order and
// returns the result; the compiler calls the method of the opcode's name and
// leaves instructions without one (the relaxed SIMD instructions, fp16) to
// the interpreter. A v128 is a Vector128<byte>; the methods reinterpret it as
// the lanes they work on.
//
// Where a Vector128 operation is not defined exactly as wasm's (saturation,
// float min/max with NaN and signed zeros, conversions, rounding), the
// method works lane by lane with the scalar operations of RuntimeWasm.Numeric.cs.
using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;
using Wacs.Core.Runtime;

namespace V8Sharp.Wasm;

public static class WasmSimd
{
    const MethodImplOptions Inline = MethodImplOptions.AggressiveInlining;
    static Vector128<byte> B<T>(Vector128<T> v) => v.AsByte();

    // ---- Conversions with the interpreter's representation ---------------------------------

    public static Vector128<byte> FromValue(Value value)
    {
        MV128 v = ((VecRef)value.GcRef!).V128;
        return Unsafe.As<MV128, Vector128<byte>>(ref v);
    }

    public static Value ToValue(Vector128<byte> value) => new(Unsafe.As<Vector128<byte>, V128>(ref value));

    // ---- Splats, lanes, constants ------------------------------------------------------------

    [MethodImpl(Inline)] public static Vector128<byte> I8x16Splat(int x) => Vector128.Create((byte)x);
    [MethodImpl(Inline)] public static Vector128<byte> I16x8Splat(int x) => B(Vector128.Create((short)x));
    [MethodImpl(Inline)] public static Vector128<byte> I32x4Splat(int x) => B(Vector128.Create(x));
    [MethodImpl(Inline)] public static Vector128<byte> I64x2Splat(long x) => B(Vector128.Create(x));
    [MethodImpl(Inline)] public static Vector128<byte> F32x4Splat(float x) => B(Vector128.Create(x));
    [MethodImpl(Inline)] public static Vector128<byte> F64x2Splat(double x) => B(Vector128.Create(x));

    [MethodImpl(Inline)] public static int I8x16ExtractLaneS(Vector128<byte> v, int lane) => v.AsSByte().GetElement(lane);
    [MethodImpl(Inline)] public static int I8x16ExtractLaneU(Vector128<byte> v, int lane) => v.GetElement(lane);
    [MethodImpl(Inline)] public static int I16x8ExtractLaneS(Vector128<byte> v, int lane) => v.AsInt16().GetElement(lane);
    [MethodImpl(Inline)] public static int I16x8ExtractLaneU(Vector128<byte> v, int lane) => v.AsUInt16().GetElement(lane);
    [MethodImpl(Inline)] public static int I32x4ExtractLane(Vector128<byte> v, int lane) => v.AsInt32().GetElement(lane);
    [MethodImpl(Inline)] public static long I64x2ExtractLane(Vector128<byte> v, int lane) => v.AsInt64().GetElement(lane);
    [MethodImpl(Inline)] public static float F32x4ExtractLane(Vector128<byte> v, int lane) => v.AsSingle().GetElement(lane);
    [MethodImpl(Inline)] public static double F64x2ExtractLane(Vector128<byte> v, int lane) => v.AsDouble().GetElement(lane);

    [MethodImpl(Inline)] public static Vector128<byte> I8x16ReplaceLane(Vector128<byte> v, int x, int lane) => v.WithElement(lane, (byte)x);
    [MethodImpl(Inline)] public static Vector128<byte> I16x8ReplaceLane(Vector128<byte> v, int x, int lane) => B(v.AsInt16().WithElement(lane, (short)x));
    [MethodImpl(Inline)] public static Vector128<byte> I32x4ReplaceLane(Vector128<byte> v, int x, int lane) => B(v.AsInt32().WithElement(lane, x));
    [MethodImpl(Inline)] public static Vector128<byte> I64x2ReplaceLane(Vector128<byte> v, long x, int lane) => B(v.AsInt64().WithElement(lane, x));
    [MethodImpl(Inline)] public static Vector128<byte> F32x4ReplaceLane(Vector128<byte> v, float x, int lane) => B(v.AsSingle().WithElement(lane, x));
    [MethodImpl(Inline)] public static Vector128<byte> F64x2ReplaceLane(Vector128<byte> v, double x, int lane) => B(v.AsDouble().WithElement(lane, x));

    [MethodImpl(Inline)] public static Vector128<byte> Const(long low, long high) => B(Vector128.Create(low, high));

    /// <summary>i8x16.shuffle: lanes of a (indices 0-15) or b (16-31).</summary>
    [MethodImpl(Inline)]
    public static Vector128<byte> I8x16Shuffle(Vector128<byte> a, Vector128<byte> b, Vector128<byte> indices) =>
        Vector128.Shuffle(a, indices) | Vector128.Shuffle(b, indices - Vector128.Create((byte)16));

    /// <summary>i8x16.swizzle: lanes of a, 0 for indices above 15.</summary>
    [MethodImpl(Inline)] public static Vector128<byte> I8x16Swizzle(Vector128<byte> a, Vector128<byte> s) => Vector128.Shuffle(a, s);

    // ---- Bitwise -------------------------------------------------------------------------------

    [MethodImpl(Inline)] public static Vector128<byte> S128Not(Vector128<byte> a) => ~a;
    [MethodImpl(Inline)] public static Vector128<byte> S128And(Vector128<byte> a, Vector128<byte> b) => a & b;
    [MethodImpl(Inline)] public static Vector128<byte> S128AndNot(Vector128<byte> a, Vector128<byte> b) => a & ~b;
    [MethodImpl(Inline)] public static Vector128<byte> S128Or(Vector128<byte> a, Vector128<byte> b) => a | b;
    [MethodImpl(Inline)] public static Vector128<byte> S128Xor(Vector128<byte> a, Vector128<byte> b) => a ^ b;
    [MethodImpl(Inline)] public static Vector128<byte> S128Select(Vector128<byte> a, Vector128<byte> b, Vector128<byte> c) => (a & c) | (b & ~c);
    [MethodImpl(Inline)] public static int V128AnyTrue(Vector128<byte> a) => a != Vector128<byte>.Zero ? 1 : 0;

    // ---- Integer comparisons (lanes of all ones or zeros) ------------------------------------------

    [MethodImpl(Inline)] public static Vector128<byte> I8x16Eq(Vector128<byte> a, Vector128<byte> b) => Vector128.Equals(a, b);
    [MethodImpl(Inline)] public static Vector128<byte> I8x16Ne(Vector128<byte> a, Vector128<byte> b) => ~Vector128.Equals(a, b);
    [MethodImpl(Inline)] public static Vector128<byte> I8x16LtS(Vector128<byte> a, Vector128<byte> b) => B(Vector128.LessThan(a.AsSByte(), b.AsSByte()));
    [MethodImpl(Inline)] public static Vector128<byte> I8x16LtU(Vector128<byte> a, Vector128<byte> b) => Vector128.LessThan(a, b);
    [MethodImpl(Inline)] public static Vector128<byte> I8x16GtS(Vector128<byte> a, Vector128<byte> b) => B(Vector128.GreaterThan(a.AsSByte(), b.AsSByte()));
    [MethodImpl(Inline)] public static Vector128<byte> I8x16GtU(Vector128<byte> a, Vector128<byte> b) => Vector128.GreaterThan(a, b);
    [MethodImpl(Inline)] public static Vector128<byte> I8x16LeS(Vector128<byte> a, Vector128<byte> b) => B(Vector128.LessThanOrEqual(a.AsSByte(), b.AsSByte()));
    [MethodImpl(Inline)] public static Vector128<byte> I8x16LeU(Vector128<byte> a, Vector128<byte> b) => Vector128.LessThanOrEqual(a, b);
    [MethodImpl(Inline)] public static Vector128<byte> I8x16GeS(Vector128<byte> a, Vector128<byte> b) => B(Vector128.GreaterThanOrEqual(a.AsSByte(), b.AsSByte()));
    [MethodImpl(Inline)] public static Vector128<byte> I8x16GeU(Vector128<byte> a, Vector128<byte> b) => Vector128.GreaterThanOrEqual(a, b);
    [MethodImpl(Inline)] public static Vector128<byte> I16x8Eq(Vector128<byte> a, Vector128<byte> b) => B(Vector128.Equals(a.AsInt16(), b.AsInt16()));
    [MethodImpl(Inline)] public static Vector128<byte> I16x8Ne(Vector128<byte> a, Vector128<byte> b) => ~B(Vector128.Equals(a.AsInt16(), b.AsInt16()));
    [MethodImpl(Inline)] public static Vector128<byte> I16x8LtS(Vector128<byte> a, Vector128<byte> b) => B(Vector128.LessThan(a.AsInt16(), b.AsInt16()));
    [MethodImpl(Inline)] public static Vector128<byte> I16x8LtU(Vector128<byte> a, Vector128<byte> b) => B(Vector128.LessThan(a.AsUInt16(), b.AsUInt16()));
    [MethodImpl(Inline)] public static Vector128<byte> I16x8GtS(Vector128<byte> a, Vector128<byte> b) => B(Vector128.GreaterThan(a.AsInt16(), b.AsInt16()));
    [MethodImpl(Inline)] public static Vector128<byte> I16x8GtU(Vector128<byte> a, Vector128<byte> b) => B(Vector128.GreaterThan(a.AsUInt16(), b.AsUInt16()));
    [MethodImpl(Inline)] public static Vector128<byte> I16x8LeS(Vector128<byte> a, Vector128<byte> b) => B(Vector128.LessThanOrEqual(a.AsInt16(), b.AsInt16()));
    [MethodImpl(Inline)] public static Vector128<byte> I16x8LeU(Vector128<byte> a, Vector128<byte> b) => B(Vector128.LessThanOrEqual(a.AsUInt16(), b.AsUInt16()));
    [MethodImpl(Inline)] public static Vector128<byte> I16x8GeS(Vector128<byte> a, Vector128<byte> b) => B(Vector128.GreaterThanOrEqual(a.AsInt16(), b.AsInt16()));
    [MethodImpl(Inline)] public static Vector128<byte> I16x8GeU(Vector128<byte> a, Vector128<byte> b) => B(Vector128.GreaterThanOrEqual(a.AsUInt16(), b.AsUInt16()));
    [MethodImpl(Inline)] public static Vector128<byte> I32x4Eq(Vector128<byte> a, Vector128<byte> b) => B(Vector128.Equals(a.AsInt32(), b.AsInt32()));
    [MethodImpl(Inline)] public static Vector128<byte> I32x4Ne(Vector128<byte> a, Vector128<byte> b) => ~B(Vector128.Equals(a.AsInt32(), b.AsInt32()));
    [MethodImpl(Inline)] public static Vector128<byte> I32x4LtS(Vector128<byte> a, Vector128<byte> b) => B(Vector128.LessThan(a.AsInt32(), b.AsInt32()));
    [MethodImpl(Inline)] public static Vector128<byte> I32x4LtU(Vector128<byte> a, Vector128<byte> b) => B(Vector128.LessThan(a.AsUInt32(), b.AsUInt32()));
    [MethodImpl(Inline)] public static Vector128<byte> I32x4GtS(Vector128<byte> a, Vector128<byte> b) => B(Vector128.GreaterThan(a.AsInt32(), b.AsInt32()));
    [MethodImpl(Inline)] public static Vector128<byte> I32x4GtU(Vector128<byte> a, Vector128<byte> b) => B(Vector128.GreaterThan(a.AsUInt32(), b.AsUInt32()));
    [MethodImpl(Inline)] public static Vector128<byte> I32x4LeS(Vector128<byte> a, Vector128<byte> b) => B(Vector128.LessThanOrEqual(a.AsInt32(), b.AsInt32()));
    [MethodImpl(Inline)] public static Vector128<byte> I32x4LeU(Vector128<byte> a, Vector128<byte> b) => B(Vector128.LessThanOrEqual(a.AsUInt32(), b.AsUInt32()));
    [MethodImpl(Inline)] public static Vector128<byte> I32x4GeS(Vector128<byte> a, Vector128<byte> b) => B(Vector128.GreaterThanOrEqual(a.AsInt32(), b.AsInt32()));
    [MethodImpl(Inline)] public static Vector128<byte> I32x4GeU(Vector128<byte> a, Vector128<byte> b) => B(Vector128.GreaterThanOrEqual(a.AsUInt32(), b.AsUInt32()));
    [MethodImpl(Inline)] public static Vector128<byte> I64x2Eq(Vector128<byte> a, Vector128<byte> b) => B(Vector128.Equals(a.AsInt64(), b.AsInt64()));
    [MethodImpl(Inline)] public static Vector128<byte> I64x2Ne(Vector128<byte> a, Vector128<byte> b) => ~B(Vector128.Equals(a.AsInt64(), b.AsInt64()));
    [MethodImpl(Inline)] public static Vector128<byte> I64x2LtS(Vector128<byte> a, Vector128<byte> b) => B(Vector128.LessThan(a.AsInt64(), b.AsInt64()));
    [MethodImpl(Inline)] public static Vector128<byte> I64x2GtS(Vector128<byte> a, Vector128<byte> b) => B(Vector128.GreaterThan(a.AsInt64(), b.AsInt64()));
    [MethodImpl(Inline)] public static Vector128<byte> I64x2LeS(Vector128<byte> a, Vector128<byte> b) => B(Vector128.LessThanOrEqual(a.AsInt64(), b.AsInt64()));
    [MethodImpl(Inline)] public static Vector128<byte> I64x2GeS(Vector128<byte> a, Vector128<byte> b) => B(Vector128.GreaterThanOrEqual(a.AsInt64(), b.AsInt64()));

    // ---- Float comparisons (an unordered comparison is false, except ne) ------------------------------

    [MethodImpl(Inline)] public static Vector128<byte> F32x4Eq(Vector128<byte> a, Vector128<byte> b) => B(Vector128.Equals(a.AsSingle(), b.AsSingle()));
    [MethodImpl(Inline)] public static Vector128<byte> F32x4Ne(Vector128<byte> a, Vector128<byte> b) => ~B(Vector128.Equals(a.AsSingle(), b.AsSingle()));
    [MethodImpl(Inline)] public static Vector128<byte> F32x4Lt(Vector128<byte> a, Vector128<byte> b) => B(Vector128.LessThan(a.AsSingle(), b.AsSingle()));
    [MethodImpl(Inline)] public static Vector128<byte> F32x4Gt(Vector128<byte> a, Vector128<byte> b) => B(Vector128.GreaterThan(a.AsSingle(), b.AsSingle()));
    [MethodImpl(Inline)] public static Vector128<byte> F32x4Le(Vector128<byte> a, Vector128<byte> b) => B(Vector128.LessThanOrEqual(a.AsSingle(), b.AsSingle()));
    [MethodImpl(Inline)] public static Vector128<byte> F32x4Ge(Vector128<byte> a, Vector128<byte> b) => B(Vector128.GreaterThanOrEqual(a.AsSingle(), b.AsSingle()));
    [MethodImpl(Inline)] public static Vector128<byte> F64x2Eq(Vector128<byte> a, Vector128<byte> b) => B(Vector128.Equals(a.AsDouble(), b.AsDouble()));
    [MethodImpl(Inline)] public static Vector128<byte> F64x2Ne(Vector128<byte> a, Vector128<byte> b) => ~B(Vector128.Equals(a.AsDouble(), b.AsDouble()));
    [MethodImpl(Inline)] public static Vector128<byte> F64x2Lt(Vector128<byte> a, Vector128<byte> b) => B(Vector128.LessThan(a.AsDouble(), b.AsDouble()));
    [MethodImpl(Inline)] public static Vector128<byte> F64x2Gt(Vector128<byte> a, Vector128<byte> b) => B(Vector128.GreaterThan(a.AsDouble(), b.AsDouble()));
    [MethodImpl(Inline)] public static Vector128<byte> F64x2Le(Vector128<byte> a, Vector128<byte> b) => B(Vector128.LessThanOrEqual(a.AsDouble(), b.AsDouble()));
    [MethodImpl(Inline)] public static Vector128<byte> F64x2Ge(Vector128<byte> a, Vector128<byte> b) => B(Vector128.GreaterThanOrEqual(a.AsDouble(), b.AsDouble()));

    // ---- Integer arithmetic --------------------------------------------------------------------------

    [MethodImpl(Inline)] public static Vector128<byte> I8x16Add(Vector128<byte> a, Vector128<byte> b) => a + b;
    [MethodImpl(Inline)] public static Vector128<byte> I8x16Sub(Vector128<byte> a, Vector128<byte> b) => a - b;
    [MethodImpl(Inline)] public static Vector128<byte> I8x16Neg(Vector128<byte> a) => Vector128<byte>.Zero - a;
    [MethodImpl(Inline)] public static Vector128<byte> I8x16Abs(Vector128<byte> a) => B(Vector128.Abs(a.AsSByte()));
    [MethodImpl(Inline)] public static Vector128<byte> I16x8Add(Vector128<byte> a, Vector128<byte> b) => B(a.AsInt16() + b.AsInt16());
    [MethodImpl(Inline)] public static Vector128<byte> I16x8Sub(Vector128<byte> a, Vector128<byte> b) => B(a.AsInt16() - b.AsInt16());
    [MethodImpl(Inline)] public static Vector128<byte> I16x8Mul(Vector128<byte> a, Vector128<byte> b) => B(a.AsInt16() * b.AsInt16());
    [MethodImpl(Inline)] public static Vector128<byte> I16x8Neg(Vector128<byte> a) => B(Vector128<short>.Zero - a.AsInt16());
    [MethodImpl(Inline)] public static Vector128<byte> I16x8Abs(Vector128<byte> a) => B(Vector128.Abs(a.AsInt16()));
    [MethodImpl(Inline)] public static Vector128<byte> I32x4Add(Vector128<byte> a, Vector128<byte> b) => B(a.AsInt32() + b.AsInt32());
    [MethodImpl(Inline)] public static Vector128<byte> I32x4Sub(Vector128<byte> a, Vector128<byte> b) => B(a.AsInt32() - b.AsInt32());
    [MethodImpl(Inline)] public static Vector128<byte> I32x4Mul(Vector128<byte> a, Vector128<byte> b) => B(a.AsInt32() * b.AsInt32());
    [MethodImpl(Inline)] public static Vector128<byte> I32x4Neg(Vector128<byte> a) => B(Vector128<int>.Zero - a.AsInt32());
    [MethodImpl(Inline)] public static Vector128<byte> I32x4Abs(Vector128<byte> a) => B(Vector128.Abs(a.AsInt32()));
    [MethodImpl(Inline)] public static Vector128<byte> I64x2Add(Vector128<byte> a, Vector128<byte> b) => B(a.AsInt64() + b.AsInt64());
    [MethodImpl(Inline)] public static Vector128<byte> I64x2Sub(Vector128<byte> a, Vector128<byte> b) => B(a.AsInt64() - b.AsInt64());
    [MethodImpl(Inline)] public static Vector128<byte> I64x2Mul(Vector128<byte> a, Vector128<byte> b) => B(a.AsInt64() * b.AsInt64());
    [MethodImpl(Inline)] public static Vector128<byte> I64x2Neg(Vector128<byte> a) => B(Vector128<long>.Zero - a.AsInt64());
    [MethodImpl(Inline)] public static Vector128<byte> I64x2Abs(Vector128<byte> a) => B(Vector128.Abs(a.AsInt64()));

    [MethodImpl(Inline)] public static Vector128<byte> I8x16MinS(Vector128<byte> a, Vector128<byte> b) => B(Vector128.Min(a.AsSByte(), b.AsSByte()));
    [MethodImpl(Inline)] public static Vector128<byte> I8x16MinU(Vector128<byte> a, Vector128<byte> b) => Vector128.Min(a, b);
    [MethodImpl(Inline)] public static Vector128<byte> I8x16MaxS(Vector128<byte> a, Vector128<byte> b) => B(Vector128.Max(a.AsSByte(), b.AsSByte()));
    [MethodImpl(Inline)] public static Vector128<byte> I8x16MaxU(Vector128<byte> a, Vector128<byte> b) => Vector128.Max(a, b);
    [MethodImpl(Inline)] public static Vector128<byte> I16x8MinS(Vector128<byte> a, Vector128<byte> b) => B(Vector128.Min(a.AsInt16(), b.AsInt16()));
    [MethodImpl(Inline)] public static Vector128<byte> I16x8MinU(Vector128<byte> a, Vector128<byte> b) => B(Vector128.Min(a.AsUInt16(), b.AsUInt16()));
    [MethodImpl(Inline)] public static Vector128<byte> I16x8MaxS(Vector128<byte> a, Vector128<byte> b) => B(Vector128.Max(a.AsInt16(), b.AsInt16()));
    [MethodImpl(Inline)] public static Vector128<byte> I16x8MaxU(Vector128<byte> a, Vector128<byte> b) => B(Vector128.Max(a.AsUInt16(), b.AsUInt16()));
    [MethodImpl(Inline)] public static Vector128<byte> I32x4MinS(Vector128<byte> a, Vector128<byte> b) => B(Vector128.Min(a.AsInt32(), b.AsInt32()));
    [MethodImpl(Inline)] public static Vector128<byte> I32x4MinU(Vector128<byte> a, Vector128<byte> b) => B(Vector128.Min(a.AsUInt32(), b.AsUInt32()));
    [MethodImpl(Inline)] public static Vector128<byte> I32x4MaxS(Vector128<byte> a, Vector128<byte> b) => B(Vector128.Max(a.AsInt32(), b.AsInt32()));
    [MethodImpl(Inline)] public static Vector128<byte> I32x4MaxU(Vector128<byte> a, Vector128<byte> b) => B(Vector128.Max(a.AsUInt32(), b.AsUInt32()));

    /// <summary>avgr_u: (a + b + 1) / 2 without overflow.</summary>
    [MethodImpl(Inline)] public static Vector128<byte> I8x16RoundingAverageU(Vector128<byte> a, Vector128<byte> b) => (a | b) - ((a ^ b) >> 1);
    [MethodImpl(Inline)]
    public static Vector128<byte> I16x8RoundingAverageU(Vector128<byte> a, Vector128<byte> b)
    {
        Vector128<ushort> x = a.AsUInt16(), y = b.AsUInt16();
        return B((x | y) - ((x ^ y) >> 1));
    }

    // Shifts: the count modulo the lane width.
    [MethodImpl(Inline)] public static Vector128<byte> I8x16Shl(Vector128<byte> a, int n) => Vector128.ShiftLeft(a, n & 7);
    [MethodImpl(Inline)] public static Vector128<byte> I8x16ShrS(Vector128<byte> a, int n) => B(Vector128.ShiftRightArithmetic(a.AsSByte(), n & 7));
    [MethodImpl(Inline)] public static Vector128<byte> I8x16ShrU(Vector128<byte> a, int n) => Vector128.ShiftRightLogical(a, n & 7);
    [MethodImpl(Inline)] public static Vector128<byte> I16x8Shl(Vector128<byte> a, int n) => B(Vector128.ShiftLeft(a.AsInt16(), n & 15));
    [MethodImpl(Inline)] public static Vector128<byte> I16x8ShrS(Vector128<byte> a, int n) => B(Vector128.ShiftRightArithmetic(a.AsInt16(), n & 15));
    [MethodImpl(Inline)] public static Vector128<byte> I16x8ShrU(Vector128<byte> a, int n) => B(Vector128.ShiftRightLogical(a.AsUInt16(), n & 15));
    [MethodImpl(Inline)] public static Vector128<byte> I32x4Shl(Vector128<byte> a, int n) => B(Vector128.ShiftLeft(a.AsInt32(), n & 31));
    [MethodImpl(Inline)] public static Vector128<byte> I32x4ShrS(Vector128<byte> a, int n) => B(Vector128.ShiftRightArithmetic(a.AsInt32(), n & 31));
    [MethodImpl(Inline)] public static Vector128<byte> I32x4ShrU(Vector128<byte> a, int n) => B(Vector128.ShiftRightLogical(a.AsUInt32(), n & 31));
    [MethodImpl(Inline)] public static Vector128<byte> I64x2Shl(Vector128<byte> a, int n) => B(Vector128.ShiftLeft(a.AsInt64(), n & 63));
    [MethodImpl(Inline)] public static Vector128<byte> I64x2ShrS(Vector128<byte> a, int n) => B(Vector128.ShiftRightArithmetic(a.AsInt64(), n & 63));
    [MethodImpl(Inline)] public static Vector128<byte> I64x2ShrU(Vector128<byte> a, int n) => B(Vector128.ShiftRightLogical(a.AsUInt64(), n & 63));

    // Saturating arithmetic.
    [MethodImpl(Inline)]
    public static Vector128<byte> I8x16AddSatS(Vector128<byte> a, Vector128<byte> b) =>
        Sse2.IsSupported ? B(Sse2.AddSaturate(a.AsSByte(), b.AsSByte())) : Lanes8S(a, b, static (x, y) => x + y);
    [MethodImpl(Inline)]
    public static Vector128<byte> I8x16AddSatU(Vector128<byte> a, Vector128<byte> b) =>
        Sse2.IsSupported ? Sse2.AddSaturate(a, b) : Lanes8U(a, b, static (x, y) => x + y);
    [MethodImpl(Inline)]
    public static Vector128<byte> I8x16SubSatS(Vector128<byte> a, Vector128<byte> b) =>
        Sse2.IsSupported ? B(Sse2.SubtractSaturate(a.AsSByte(), b.AsSByte())) : Lanes8S(a, b, static (x, y) => x - y);
    [MethodImpl(Inline)]
    public static Vector128<byte> I8x16SubSatU(Vector128<byte> a, Vector128<byte> b) =>
        Sse2.IsSupported ? Sse2.SubtractSaturate(a, b) : Lanes8U(a, b, static (x, y) => x - y);
    [MethodImpl(Inline)]
    public static Vector128<byte> I16x8AddSatS(Vector128<byte> a, Vector128<byte> b) =>
        Sse2.IsSupported ? B(Sse2.AddSaturate(a.AsInt16(), b.AsInt16())) : Lanes16S(a, b, static (x, y) => x + y);
    [MethodImpl(Inline)]
    public static Vector128<byte> I16x8AddSatU(Vector128<byte> a, Vector128<byte> b) =>
        Sse2.IsSupported ? B(Sse2.AddSaturate(a.AsUInt16(), b.AsUInt16())) : Lanes16U(a, b, static (x, y) => x + y);
    [MethodImpl(Inline)]
    public static Vector128<byte> I16x8SubSatS(Vector128<byte> a, Vector128<byte> b) =>
        Sse2.IsSupported ? B(Sse2.SubtractSaturate(a.AsInt16(), b.AsInt16())) : Lanes16S(a, b, static (x, y) => x - y);
    [MethodImpl(Inline)]
    public static Vector128<byte> I16x8SubSatU(Vector128<byte> a, Vector128<byte> b) =>
        Sse2.IsSupported ? B(Sse2.SubtractSaturate(a.AsUInt16(), b.AsUInt16())) : Lanes16U(a, b, static (x, y) => x - y);

    static Vector128<byte> Lanes8S(Vector128<byte> a, Vector128<byte> b, Func<int, int, int> f)
    {
        Span<sbyte> r = stackalloc sbyte[16];
        for (int i = 0; i < 16; i++) r[i] = (sbyte)Math.Clamp(f(a.AsSByte().GetElement(i), b.AsSByte().GetElement(i)), sbyte.MinValue, sbyte.MaxValue);
        return B(Vector128.Create<sbyte>(r));
    }

    static Vector128<byte> Lanes8U(Vector128<byte> a, Vector128<byte> b, Func<int, int, int> f)
    {
        Span<byte> r = stackalloc byte[16];
        for (int i = 0; i < 16; i++) r[i] = (byte)Math.Clamp(f(a.GetElement(i), b.GetElement(i)), 0, 255);
        return Vector128.Create<byte>(r);
    }

    static Vector128<byte> Lanes16S(Vector128<byte> a, Vector128<byte> b, Func<int, int, int> f)
    {
        Span<short> r = stackalloc short[8];
        for (int i = 0; i < 8; i++) r[i] = (short)Math.Clamp(f(a.AsInt16().GetElement(i), b.AsInt16().GetElement(i)), short.MinValue, short.MaxValue);
        return B(Vector128.Create<short>(r));
    }

    static Vector128<byte> Lanes16U(Vector128<byte> a, Vector128<byte> b, Func<int, int, int> f)
    {
        Span<ushort> r = stackalloc ushort[8];
        for (int i = 0; i < 8; i++) r[i] = (ushort)Math.Clamp(f(a.AsUInt16().GetElement(i), b.AsUInt16().GetElement(i)), 0, ushort.MaxValue);
        return B(Vector128.Create<ushort>(r));
    }

    /// <summary>i16x8.q15mulr_sat_s: (a * b + 0x4000) >> 15, saturated.</summary>
    public static Vector128<byte> I16x8Q15MulRSatS(Vector128<byte> a, Vector128<byte> b)
    {
        Span<short> r = stackalloc short[8];
        for (int i = 0; i < 8; i++)
        {
            int p = (a.AsInt16().GetElement(i) * b.AsInt16().GetElement(i) + 0x4000) >> 15;
            r[i] = (short)Math.Clamp(p, short.MinValue, short.MaxValue);
        }
        return B(Vector128.Create<short>(r));
    }

    /// <summary>i32x4.dot_i16x8_s: sums of adjacent products.</summary>
    public static Vector128<byte> I32x4DotI16x8S(Vector128<byte> a, Vector128<byte> b)
    {
        if (Sse2.IsSupported) return B(Sse2.MultiplyAddAdjacent(a.AsInt16(), b.AsInt16()));
        Span<int> r = stackalloc int[4];
        for (int i = 0; i < 4; i++)
        {
            r[i] = a.AsInt16().GetElement(2 * i) * b.AsInt16().GetElement(2 * i) +
                   a.AsInt16().GetElement(2 * i + 1) * b.AsInt16().GetElement(2 * i + 1);
        }
        return B(Vector128.Create<int>(r));
    }

    public static Vector128<byte> I8x16Popcnt(Vector128<byte> a)
    {
        // Nibble lookup (V8's pshufb sequence).
        Vector128<byte> table = Vector128.Create((byte)0, 1, 1, 2, 1, 2, 2, 3, 1, 2, 2, 3, 2, 3, 3, 4);
        Vector128<byte> mask = Vector128.Create((byte)0x0f);
        return Vector128.Shuffle(table, a & mask) + Vector128.Shuffle(table, Vector128.ShiftRightLogical(a, 4) & mask);
    }

    // ---- Lane tests -----------------------------------------------------------------------------

    [MethodImpl(Inline)] public static int I8x16AllTrue(Vector128<byte> a) => Vector128.EqualsAny(a, Vector128<byte>.Zero) ? 0 : 1;
    [MethodImpl(Inline)] public static int I16x8AllTrue(Vector128<byte> a) => Vector128.EqualsAny(a.AsInt16(), Vector128<short>.Zero) ? 0 : 1;
    [MethodImpl(Inline)] public static int I32x4AllTrue(Vector128<byte> a) => Vector128.EqualsAny(a.AsInt32(), Vector128<int>.Zero) ? 0 : 1;
    [MethodImpl(Inline)] public static int I64x2AllTrue(Vector128<byte> a) => Vector128.EqualsAny(a.AsInt64(), Vector128<long>.Zero) ? 0 : 1;
    [MethodImpl(Inline)] public static int I8x16BitMask(Vector128<byte> a) => (int)a.ExtractMostSignificantBits();
    [MethodImpl(Inline)] public static int I16x8BitMask(Vector128<byte> a) => (int)a.AsInt16().ExtractMostSignificantBits();
    [MethodImpl(Inline)] public static int I32x4BitMask(Vector128<byte> a) => (int)a.AsInt32().ExtractMostSignificantBits();
    [MethodImpl(Inline)] public static int I64x2BitMask(Vector128<byte> a) => (int)a.AsInt64().ExtractMostSignificantBits();

    // ---- Widening and narrowing --------------------------------------------------------------------

    [MethodImpl(Inline)] public static Vector128<byte> I16x8SConvertI8x16Low(Vector128<byte> a) => B(Vector128.WidenLower(a.AsSByte()));
    [MethodImpl(Inline)] public static Vector128<byte> I16x8SConvertI8x16High(Vector128<byte> a) => B(Vector128.WidenUpper(a.AsSByte()));
    [MethodImpl(Inline)] public static Vector128<byte> I16x8UConvertI8x16Low(Vector128<byte> a) => B(Vector128.WidenLower(a));
    [MethodImpl(Inline)] public static Vector128<byte> I16x8UConvertI8x16High(Vector128<byte> a) => B(Vector128.WidenUpper(a));
    [MethodImpl(Inline)] public static Vector128<byte> I32x4SConvertI16x8Low(Vector128<byte> a) => B(Vector128.WidenLower(a.AsInt16()));
    [MethodImpl(Inline)] public static Vector128<byte> I32x4SConvertI16x8High(Vector128<byte> a) => B(Vector128.WidenUpper(a.AsInt16()));
    [MethodImpl(Inline)] public static Vector128<byte> I32x4UConvertI16x8Low(Vector128<byte> a) => B(Vector128.WidenLower(a.AsUInt16()));
    [MethodImpl(Inline)] public static Vector128<byte> I32x4UConvertI16x8High(Vector128<byte> a) => B(Vector128.WidenUpper(a.AsUInt16()));
    [MethodImpl(Inline)] public static Vector128<byte> I64x2SConvertI32x4Low(Vector128<byte> a) => B(Vector128.WidenLower(a.AsInt32()));
    [MethodImpl(Inline)] public static Vector128<byte> I64x2SConvertI32x4High(Vector128<byte> a) => B(Vector128.WidenUpper(a.AsInt32()));
    [MethodImpl(Inline)] public static Vector128<byte> I64x2UConvertI32x4Low(Vector128<byte> a) => B(Vector128.WidenLower(a.AsUInt32()));
    [MethodImpl(Inline)] public static Vector128<byte> I64x2UConvertI32x4High(Vector128<byte> a) => B(Vector128.WidenUpper(a.AsUInt32()));

    public static Vector128<byte> I8x16SConvertI16x8(Vector128<byte> a, Vector128<byte> b)
    {
        if (Sse2.IsSupported) return B(Sse2.PackSignedSaturate(a.AsInt16(), b.AsInt16()));
        Vector128<short> lo = Vector128.Create((short)sbyte.MinValue), hi = Vector128.Create((short)sbyte.MaxValue);
        return B(Vector128.Narrow(Vector128.Clamp(a.AsInt16(), lo, hi), Vector128.Clamp(b.AsInt16(), lo, hi)));
    }

    public static Vector128<byte> I8x16UConvertI16x8(Vector128<byte> a, Vector128<byte> b)
    {
        if (Sse2.IsSupported) return Sse2.PackUnsignedSaturate(a.AsInt16(), b.AsInt16());
        Vector128<short> lo = Vector128<short>.Zero, hi = Vector128.Create((short)byte.MaxValue);
        return B(Vector128.Narrow(Vector128.Clamp(a.AsInt16(), lo, hi), Vector128.Clamp(b.AsInt16(), lo, hi)));
    }

    public static Vector128<byte> I16x8SConvertI32x4(Vector128<byte> a, Vector128<byte> b)
    {
        if (Sse2.IsSupported) return B(Sse2.PackSignedSaturate(a.AsInt32(), b.AsInt32()));
        Vector128<int> lo = Vector128.Create((int)short.MinValue), hi = Vector128.Create((int)short.MaxValue);
        return B(Vector128.Narrow(Vector128.Clamp(a.AsInt32(), lo, hi), Vector128.Clamp(b.AsInt32(), lo, hi)));
    }

    public static Vector128<byte> I16x8UConvertI32x4(Vector128<byte> a, Vector128<byte> b)
    {
        if (Sse41.IsSupported) return B(Sse41.PackUnsignedSaturate(a.AsInt32(), b.AsInt32()));
        Vector128<int> lo = Vector128<int>.Zero, hi = Vector128.Create((int)ushort.MaxValue);
        return B(Vector128.Narrow(Vector128.Clamp(a.AsInt32(), lo, hi), Vector128.Clamp(b.AsInt32(), lo, hi)));
    }

    // Extended multiplication and pairwise addition.
    public static Vector128<byte> I16x8ExtMulLowI8x16S(Vector128<byte> a, Vector128<byte> b) => B(Vector128.WidenLower(a.AsSByte()) * Vector128.WidenLower(b.AsSByte()));
    public static Vector128<byte> I16x8ExtMulHighI8x16S(Vector128<byte> a, Vector128<byte> b) => B(Vector128.WidenUpper(a.AsSByte()) * Vector128.WidenUpper(b.AsSByte()));
    public static Vector128<byte> I16x8ExtMulLowI8x16U(Vector128<byte> a, Vector128<byte> b) => B(Vector128.WidenLower(a) * Vector128.WidenLower(b));
    public static Vector128<byte> I16x8ExtMulHighI8x16U(Vector128<byte> a, Vector128<byte> b) => B(Vector128.WidenUpper(a) * Vector128.WidenUpper(b));
    public static Vector128<byte> I32x4ExtMulLowI16x8S(Vector128<byte> a, Vector128<byte> b) => B(Vector128.WidenLower(a.AsInt16()) * Vector128.WidenLower(b.AsInt16()));
    public static Vector128<byte> I32x4ExtMulHighI16x8S(Vector128<byte> a, Vector128<byte> b) => B(Vector128.WidenUpper(a.AsInt16()) * Vector128.WidenUpper(b.AsInt16()));
    public static Vector128<byte> I32x4ExtMulLowI16x8U(Vector128<byte> a, Vector128<byte> b) => B(Vector128.WidenLower(a.AsUInt16()) * Vector128.WidenLower(b.AsUInt16()));
    public static Vector128<byte> I32x4ExtMulHighI16x8U(Vector128<byte> a, Vector128<byte> b) => B(Vector128.WidenUpper(a.AsUInt16()) * Vector128.WidenUpper(b.AsUInt16()));
    public static Vector128<byte> I64x2ExtMulLowI32x4S(Vector128<byte> a, Vector128<byte> b) => B(Vector128.WidenLower(a.AsInt32()) * Vector128.WidenLower(b.AsInt32()));
    public static Vector128<byte> I64x2ExtMulHighI32x4S(Vector128<byte> a, Vector128<byte> b) => B(Vector128.WidenUpper(a.AsInt32()) * Vector128.WidenUpper(b.AsInt32()));
    public static Vector128<byte> I64x2ExtMulLowI32x4U(Vector128<byte> a, Vector128<byte> b) => B(Vector128.WidenLower(a.AsUInt32()) * Vector128.WidenLower(b.AsUInt32()));
    public static Vector128<byte> I64x2ExtMulHighI32x4U(Vector128<byte> a, Vector128<byte> b) => B(Vector128.WidenUpper(a.AsUInt32()) * Vector128.WidenUpper(b.AsUInt32()));

    public static Vector128<byte> I16x8ExtAddPairwiseI8x16S(Vector128<byte> a)
    {
        Span<short> r = stackalloc short[8];
        for (int i = 0; i < 8; i++) r[i] = (short)(a.AsSByte().GetElement(2 * i) + a.AsSByte().GetElement(2 * i + 1));
        return B(Vector128.Create<short>(r));
    }

    public static Vector128<byte> I16x8ExtAddPairwiseI8x16U(Vector128<byte> a)
    {
        Span<short> r = stackalloc short[8];
        for (int i = 0; i < 8; i++) r[i] = (short)(a.GetElement(2 * i) + a.GetElement(2 * i + 1));
        return B(Vector128.Create<short>(r));
    }

    public static Vector128<byte> I32x4ExtAddPairwiseI16x8S(Vector128<byte> a)
    {
        Span<int> r = stackalloc int[4];
        for (int i = 0; i < 4; i++) r[i] = a.AsInt16().GetElement(2 * i) + a.AsInt16().GetElement(2 * i + 1);
        return B(Vector128.Create<int>(r));
    }

    public static Vector128<byte> I32x4ExtAddPairwiseI16x8U(Vector128<byte> a)
    {
        Span<int> r = stackalloc int[4];
        for (int i = 0; i < 4; i++) r[i] = a.AsUInt16().GetElement(2 * i) + a.AsUInt16().GetElement(2 * i + 1);
        return B(Vector128.Create<int>(r));
    }

    // ---- Float arithmetic -----------------------------------------------------------------------------

    [MethodImpl(Inline)] public static Vector128<byte> F32x4Add(Vector128<byte> a, Vector128<byte> b) => B(a.AsSingle() + b.AsSingle());
    [MethodImpl(Inline)] public static Vector128<byte> F32x4Sub(Vector128<byte> a, Vector128<byte> b) => B(a.AsSingle() - b.AsSingle());
    [MethodImpl(Inline)] public static Vector128<byte> F32x4Mul(Vector128<byte> a, Vector128<byte> b) => B(a.AsSingle() * b.AsSingle());
    [MethodImpl(Inline)] public static Vector128<byte> F32x4Div(Vector128<byte> a, Vector128<byte> b) => B(a.AsSingle() / b.AsSingle());
    [MethodImpl(Inline)] public static Vector128<byte> F32x4Sqrt(Vector128<byte> a) => B(Vector128.Sqrt(a.AsSingle()));
    [MethodImpl(Inline)] public static Vector128<byte> F32x4Abs(Vector128<byte> a) => a & B(Vector128.Create(0x7fffffff));
    [MethodImpl(Inline)] public static Vector128<byte> F32x4Neg(Vector128<byte> a) => a ^ B(Vector128.Create(int.MinValue));
    [MethodImpl(Inline)] public static Vector128<byte> F64x2Add(Vector128<byte> a, Vector128<byte> b) => B(a.AsDouble() + b.AsDouble());
    [MethodImpl(Inline)] public static Vector128<byte> F64x2Sub(Vector128<byte> a, Vector128<byte> b) => B(a.AsDouble() - b.AsDouble());
    [MethodImpl(Inline)] public static Vector128<byte> F64x2Mul(Vector128<byte> a, Vector128<byte> b) => B(a.AsDouble() * b.AsDouble());
    [MethodImpl(Inline)] public static Vector128<byte> F64x2Div(Vector128<byte> a, Vector128<byte> b) => B(a.AsDouble() / b.AsDouble());
    [MethodImpl(Inline)] public static Vector128<byte> F64x2Sqrt(Vector128<byte> a) => B(Vector128.Sqrt(a.AsDouble()));
    [MethodImpl(Inline)] public static Vector128<byte> F64x2Abs(Vector128<byte> a) => a & B(Vector128.Create(long.MaxValue));
    [MethodImpl(Inline)] public static Vector128<byte> F64x2Neg(Vector128<byte> a) => a ^ B(Vector128.Create(long.MinValue));

    /// <summary>pmin: b &lt; a ? b : a (no NaN or zero-sign rules).</summary>
    [MethodImpl(Inline)]
    public static Vector128<byte> F32x4Pmin(Vector128<byte> a, Vector128<byte> b) =>
        B(Vector128.ConditionalSelect(Vector128.LessThan(b.AsSingle(), a.AsSingle()), b.AsSingle(), a.AsSingle()));
    [MethodImpl(Inline)]
    public static Vector128<byte> F32x4Pmax(Vector128<byte> a, Vector128<byte> b) =>
        B(Vector128.ConditionalSelect(Vector128.LessThan(a.AsSingle(), b.AsSingle()), b.AsSingle(), a.AsSingle()));
    [MethodImpl(Inline)]
    public static Vector128<byte> F64x2Pmin(Vector128<byte> a, Vector128<byte> b) =>
        B(Vector128.ConditionalSelect(Vector128.LessThan(b.AsDouble(), a.AsDouble()), b.AsDouble(), a.AsDouble()));
    [MethodImpl(Inline)]
    public static Vector128<byte> F64x2Pmax(Vector128<byte> a, Vector128<byte> b) =>
        B(Vector128.ConditionalSelect(Vector128.LessThan(a.AsDouble(), b.AsDouble()), b.AsDouble(), a.AsDouble()));

    // Lane by lane with wasm's scalar rules.
    public static Vector128<byte> F32x4Min(Vector128<byte> a, Vector128<byte> b) => MapF32(a, b, 0);
    public static Vector128<byte> F32x4Max(Vector128<byte> a, Vector128<byte> b) => MapF32(a, b, 1);
    public static Vector128<byte> F64x2Min(Vector128<byte> a, Vector128<byte> b) => MapF64(a, b, 0);
    public static Vector128<byte> F64x2Max(Vector128<byte> a, Vector128<byte> b) => MapF64(a, b, 1);
    public static Vector128<byte> F32x4Ceil(Vector128<byte> a) => MapF32(a, a, 2);
    public static Vector128<byte> F32x4Floor(Vector128<byte> a) => MapF32(a, a, 3);
    public static Vector128<byte> F32x4Trunc(Vector128<byte> a) => MapF32(a, a, 4);
    public static Vector128<byte> F32x4NearestInt(Vector128<byte> a) => MapF32(a, a, 5);
    public static Vector128<byte> F64x2Ceil(Vector128<byte> a) => MapF64(a, a, 2);
    public static Vector128<byte> F64x2Floor(Vector128<byte> a) => MapF64(a, a, 3);
    public static Vector128<byte> F64x2Trunc(Vector128<byte> a) => MapF64(a, a, 4);
    public static Vector128<byte> F64x2NearestInt(Vector128<byte> a) => MapF64(a, a, 5);

    static Vector128<byte> MapF32(Vector128<byte> a, Vector128<byte> b, int op)
    {
        Span<float> r = stackalloc float[4];
        for (int i = 0; i < 4; i++)
        {
            float x = a.AsSingle().GetElement(i), y = b.AsSingle().GetElement(i);
            r[i] = op switch
            {
                0 => RuntimeWasm.F32Min(x, y),
                1 => RuntimeWasm.F32Max(x, y),
                2 => MathF.Ceiling(x),
                3 => MathF.Floor(x),
                4 => MathF.Truncate(x),
                _ => RuntimeWasm.F32Nearest(x),
            };
        }
        return B(Vector128.Create<float>(r));
    }

    static Vector128<byte> MapF64(Vector128<byte> a, Vector128<byte> b, int op)
    {
        Span<double> r = stackalloc double[2];
        for (int i = 0; i < 2; i++)
        {
            double x = a.AsDouble().GetElement(i), y = b.AsDouble().GetElement(i);
            r[i] = op switch
            {
                0 => RuntimeWasm.F64Min(x, y),
                1 => RuntimeWasm.F64Max(x, y),
                2 => Math.Ceiling(x),
                3 => Math.Floor(x),
                4 => Math.Truncate(x),
                _ => RuntimeWasm.F64Nearest(x),
            };
        }
        return B(Vector128.Create<double>(r));
    }

    // ---- Conversions ------------------------------------------------------------------------------------

    public static Vector128<byte> F32x4SConvertI32x4(Vector128<byte> a) => B(Vector128.ConvertToSingle(a.AsInt32()));

    public static Vector128<byte> F32x4UConvertI32x4(Vector128<byte> a)
    {
        Span<float> r = stackalloc float[4];
        for (int i = 0; i < 4; i++) r[i] = (float)(long)a.AsUInt32().GetElement(i);
        return B(Vector128.Create<float>(r));
    }

    public static Vector128<byte> I32x4SConvertF32x4(Vector128<byte> a)
    {
        Span<int> r = stackalloc int[4];
        for (int i = 0; i < 4; i++) r[i] = RuntimeWasm.I32TruncSatF32S(a.AsSingle().GetElement(i));
        return B(Vector128.Create<int>(r));
    }

    public static Vector128<byte> I32x4UConvertF32x4(Vector128<byte> a)
    {
        Span<int> r = stackalloc int[4];
        for (int i = 0; i < 4; i++) r[i] = RuntimeWasm.I32TruncSatF32U(a.AsSingle().GetElement(i));
        return B(Vector128.Create<int>(r));
    }

    public static Vector128<byte> I32x4TruncSatF64x2SZero(Vector128<byte> a) =>
        B(Vector128.Create(RuntimeWasm.I32TruncSatF64S(a.AsDouble().GetElement(0)), RuntimeWasm.I32TruncSatF64S(a.AsDouble().GetElement(1)), 0, 0));

    public static Vector128<byte> I32x4TruncSatF64x2UZero(Vector128<byte> a) =>
        B(Vector128.Create(RuntimeWasm.I32TruncSatF64U(a.AsDouble().GetElement(0)), RuntimeWasm.I32TruncSatF64U(a.AsDouble().GetElement(1)), 0, 0));

    public static Vector128<byte> F64x2ConvertLowI32x4S(Vector128<byte> a) =>
        B(Vector128.Create((double)a.AsInt32().GetElement(0), a.AsInt32().GetElement(1)));

    public static Vector128<byte> F64x2ConvertLowI32x4U(Vector128<byte> a) =>
        B(Vector128.Create((double)a.AsUInt32().GetElement(0), a.AsUInt32().GetElement(1)));

    public static Vector128<byte> F32x4DemoteF64x2Zero(Vector128<byte> a) =>
        B(Vector128.Create((float)a.AsDouble().GetElement(0), (float)a.AsDouble().GetElement(1), 0f, 0f));

    public static Vector128<byte> F64x2PromoteLowF32x4(Vector128<byte> a) =>
        B(Vector128.Create((double)a.AsSingle().GetElement(0), a.AsSingle().GetElement(1)));

    // ---- Memory -----------------------------------------------------------------------------------------

    public static Vector128<byte> S128Load8Splat(int x) => I8x16Splat(x);
    public static Vector128<byte> S128Load16Splat(int x) => I16x8Splat(x);
    public static Vector128<byte> S128Load32Splat(int x) => I32x4Splat(x);
    public static Vector128<byte> S128Load64Splat(long x) => I64x2Splat(x);
    public static Vector128<byte> S128Load32Zero(int x) => B(Vector128.Create(x, 0, 0, 0));
    public static Vector128<byte> S128Load64Zero(long x) => B(Vector128.Create(x, 0L));
    public static Vector128<byte> S128Load8x8S(long x) => I16x8SConvertI8x16Low(B(Vector128.Create(x, 0L)));
    public static Vector128<byte> S128Load8x8U(long x) => I16x8UConvertI8x16Low(B(Vector128.Create(x, 0L)));
    public static Vector128<byte> S128Load16x4S(long x) => I32x4SConvertI16x8Low(B(Vector128.Create(x, 0L)));
    public static Vector128<byte> S128Load16x4U(long x) => I32x4UConvertI16x8Low(B(Vector128.Create(x, 0L)));
    public static Vector128<byte> S128Load32x2S(long x) => I64x2SConvertI32x4Low(B(Vector128.Create(x, 0L)));
    public static Vector128<byte> S128Load32x2U(long x) => I64x2UConvertI32x4Low(B(Vector128.Create(x, 0L)));
}
