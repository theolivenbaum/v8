// The operators of the bytecode handlers: the arithmetic, bitwise, compare,
// unary, typeof and conversion parts of src/ic/binary-op-assembler.cc,
// src/ic/unary-op-assembler.cc, CodeStubAssembler::Equal/StrictEqual/
// RelationalComparison and InterpreterAssembler::Typeof, with their type
// feedback (embedded in the bytecode for binary/compare/unary ops, in the
// feedback vector for typeof and ToNumber/ToNumeric), plus the small
// handlers helpers (TestTypeOf, super constructors, module variables).
using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;
using V8Sharp.Base.Numbers;
using V8Sharp.Runtime;
using BOF = V8Sharp.Interpreter.BinaryOperationFeedback;
using COF = V8Sharp.Interpreter.CompareOperationFeedback;

namespace V8Sharp.Interpreter;

public static class InterpreterOps
{

    /// <summary>Whether a double would be a Smi in V8 (31-bit, integral, not -0).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsSmiDouble(double d) => JSValue.IsSmiDouble(d);

    /// <summary>IsSmiDouble, with the Smi's value (V8's TaggedIsSmi then SmiUntag).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool TryGetSmi(double d, out int value)
    {
        int i = Sse2.IsSupported ? Sse2.ConvertToInt32WithTruncation(Vector128.CreateScalarUnsafe(d)) : (int)d;
        value = i;
        return BitConverter.DoubleToInt64Bits(i) == BitConverter.DoubleToInt64Bits(d) && IsSmiRange(i);
    }

    /// <summary>Whether an int (the exact result of an operation on Smis) is in the Smi range.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static bool IsSmiRange(int i) => (uint)(i - JSValue.SmiMinValue) <= (uint)(JSValue.SmiMaxValue - JSValue.SmiMinValue);

    // ---- Embedded feedback ------------------------------------------------------------

    /// <summary>
    /// UpdateEmbeddedFeedback for binary operations: combines the type index in
    /// the bytecode with <paramref name="type"/>, writing only on change.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void UpdateBinaryFeedback(ref byte feedback, BOF.TypeIndex type)
    {
        byte current = feedback;
        if (current != (byte)type && current != (byte)BOF.TypeIndex.Any) UpdateBinaryFeedbackSlow(ref feedback, type);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static void UpdateBinaryFeedbackSlow(ref byte feedback, BOF.TypeIndex type)
    {
        byte current = feedback;
        byte combined = current < BOF.kNumTypeIndices
            ? s_binaryCombine[current * (int)BOF.kNumTypeIndices + (int)type]
            : (byte)BOF.CombineTypeIndex((BOF.TypeIndex)current, type);
        if (combined != current) feedback = combined;
    }

    /// <summary>BOF.CombineTypeIndex for every pair of type indices (the embedded feedback of hot operations changes rarely but is combined on every run).</summary>
    static readonly byte[] s_binaryCombine = BuildCombineTable(BOF.kNumTypeIndices,
        static (a, b) => (byte)BOF.CombineTypeIndex((BOF.TypeIndex)a, (BOF.TypeIndex)b));

    /// <summary>COF.CombineTypeIndex for every pair of type indices.</summary>
    static readonly byte[] s_compareCombine = BuildCombineTable(COF.kNumTypeIndices,
        static (a, b) => (byte)COF.CombineTypeIndex((COF.TypeIndex)a, (COF.TypeIndex)b));

    static byte[] BuildCombineTable(uint count, Func<int, int, byte> combine)
    {
        var table = new byte[count * count];
        for (int a = 0; a < count; a++)
        {
            for (int b = 0; b < count; b++) table[a * count + b] = combine(a, b);
        }
        return table;
    }

    /// <summary>UpdateEmbeddedFeedback for compare operations.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void UpdateCompareFeedback(ref byte feedback, COF.TypeIndex type)
    {
        byte current = feedback;
        if (current != (byte)type && current != (byte)COF.TypeIndex.Any) UpdateCompareFeedbackSlow(ref feedback, type);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static void UpdateCompareFeedbackSlow(ref byte feedback, COF.TypeIndex type)
    {
        byte current = feedback;
        byte combined = current < COF.kNumTypeIndices
            ? s_compareCombine[current * (int)COF.kNumTypeIndices + (int)type]
            : (byte)COF.CombineTypeIndex((COF.TypeIndex)current, type);
        if (combined != current) feedback = combined;
    }

    static BOF.TypeIndex BinaryIndex(BOF.Type type) => BOF.CalculateTypeIndex((uint)type);

    // ---- Feedback the dispatch loop's inline number paths leave unchanged -------------
    //
    // The loop computes a number operation inline only when the embedded
    // feedback already covers it, so nothing is written and nothing is called
    // (a call would keep the operands live across it, and the JIT would spill
    // them on the fast path too); otherwise the handler's slow path updates it.

    /// <summary>Binary feedback that kSignedSmall does not widen: SignedSmall .. NumberOrOddball, Any.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static bool BinaryFeedbackIncludesSmi(byte feedback) =>
        (uint)(feedback - (byte)BOF.TypeIndex.SignedSmall) <= (byte)BOF.TypeIndex.NumberOrOddball - (byte)BOF.TypeIndex.SignedSmall ||
        feedback == (byte)BOF.TypeIndex.Any;

    /// <summary>Compare feedback that kNumber does not widen: Number, NumberOrBoolean, NumberOrOddball, Any.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static bool CompareFeedbackIncludesNumber(byte feedback) =>
        (uint)(feedback - (byte)COF.TypeIndex.Number) <= 2 || feedback == (byte)COF.TypeIndex.Any;

    /// <summary>Compare feedback that kSignedSmall does not widen.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static bool CompareFeedbackIncludesSmi(byte feedback) =>
        (uint)(feedback - (byte)COF.TypeIndex.SignedSmall) <= 3 || feedback == (byte)COF.TypeIndex.Any;

    // ---- Number fast paths -------------------------------------------------------------

    /// <summary>
    /// Embedded binary feedback that no number operation can widen (Number,
    /// NumberOrOddball, Any): the operation then skips computing its operand
    /// types, as the result of UpdateBinaryFeedback would be the same byte.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static bool IsNumberFeedbackSaturated(byte feedback) =>
        (uint)(feedback - (byte)BOF.TypeIndex.Number) <= 1 || feedback == (byte)BOF.TypeIndex.Any;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static JSValue AddNumbers(Isolate isolate, double lhs, double rhs, ref byte feedback)
    {
        double result = lhs + rhs;
        if (IsNumberFeedbackSaturated(feedback)) return JSValue.FromNumber(result);
        // The sum of two Smis is exact (and never -0): a Smi when in range,
        // as the TrySmiAdd overflow check of Generate_AddWithFeedback.
        if (TryGetSmi(lhs, out int l) && TryGetSmi(rhs, out int r) && IsSmiRange(l + r))
        {
            UpdateBinaryFeedback(ref feedback, BOF.TypeIndex.SignedSmall);
        }
        else if (feedback != (byte)BOF.TypeIndex.AdditiveSafeInteger || !IsAdditiveSafeInteger(result) ||
                 !IsAdditiveSafeInteger(lhs) || !IsAdditiveSafeInteger(rhs))
        {
            // (AdditiveSafeInteger feedback stays so while the operands and
            // result are additive safe integers, whatever the flag says.)
            UpdateBinaryFeedback(ref feedback, AddNumberFeedback(isolate, lhs, rhs, result));
        }
        return JSValue.FromNumber(result);
    }

    /// <summary>
    /// The non-Smi feedback of BinaryOpAssembler::Generate_AddWithFeedback:
    /// kAdditiveSafeInteger when --additive-safe-int-feedback is on and the
    /// inputs and the result are additive safe integers (Smis always are),
    /// else kNumber.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    static BOF.TypeIndex AddNumberFeedback(Isolate isolate, double lhs, double rhs, double result) =>
        isolate.Flags.additive_safe_int_feedback && IsAdditiveSafeInteger(lhs) && IsAdditiveSafeInteger(rhs) &&
        IsAdditiveSafeInteger(result)
            ? BOF.TypeIndex.AdditiveSafeInteger
            : BOF.TypeIndex.Number;

    // kMinAdditiveSafeIntegerFeedback and kAdditiveSafeIntegerFeedbackBitLength (globals.h).
    const long kMinAdditiveSafeIntegerFeedback = -(1L << 50);
    const int kAdditiveSafeIntegerFeedbackBitLength = 51;

    /// <summary>CodeStubAssembler::IsAdditiveSafeInteger (TryFloat64ToAdditiveSafeInteger).</summary>
    static bool IsAdditiveSafeInteger(double value)
    {
        if (!(value >= long.MinValue && value < 9.2233720368547758E18)) return false;
        long valueInt64 = (long)value;
        if ((double)valueInt64 != value) return false;
        // -0.0 is not an integer here.
        if (valueInt64 == 0 && BitConverter.DoubleToInt64Bits(value) != 0) return false;
        return (ulong)(valueInt64 - kMinAdditiveSafeIntegerFeedback) >> kAdditiveSafeIntegerFeedbackBitLength == 0;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static JSValue SubtractNumbers(double lhs, double rhs, ref byte feedback)
    {
        double result = lhs - rhs;
        if (IsNumberFeedbackSaturated(feedback)) return JSValue.FromNumber(result);
        // A difference of Smis is exact and never -0 (TrySmiSub).
        UpdateBinaryFeedback(ref feedback,
            TryGetSmi(lhs, out int l) && TryGetSmi(rhs, out int r) && IsSmiRange(l - r) ? BOF.TypeIndex.SignedSmall : BOF.TypeIndex.Number);
        return JSValue.FromNumber(result);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static JSValue MultiplyNumbers(double lhs, double rhs, ref byte feedback)
    {
        double result = lhs * rhs;
        if (IsNumberFeedbackSaturated(feedback)) return JSValue.FromNumber(result);
        UpdateBinaryFeedback(ref feedback,
            IsSmiDouble(lhs) && IsSmiDouble(rhs) && IsSmiDouble(result) ? BOF.TypeIndex.SignedSmall : BOF.TypeIndex.Number);
        return JSValue.FromNumber(result);
    }

    /// <summary>The double result of a number binary operation (JS semantics).</summary>
    public static double NumberBinary(Operation op, double lhs, double rhs) => op switch
    {
        Operation.Add => lhs + rhs,
        Operation.Subtract => lhs - rhs,
        Operation.Multiply => lhs * rhs,
        Operation.Divide => lhs / rhs,
        Operation.Modulus => Modulus(lhs, rhs),
        Operation.Exponentiate => InternalMath.pow(lhs, rhs),
        _ => throw new UnreachableException(),
    };

    /// <summary>
    /// The number modulus: CodeStubAssembler::SmiMod when both operands are
    /// Smis (an integer remainder, -0 for a zero result of a negative
    /// dividend), else Float64Mod (C#'s % on doubles is fmod, which is what
    /// JS wants).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static double Modulus(double lhs, double rhs)
    {
        int a = (int)lhs, b = (int)rhs;
        if (a == lhs && b == rhs && b > 0 && a >= 0 && (a != 0 || !double.IsNegative(lhs))) return a % b;
        return lhs % rhs;
    }

    /// <summary>Div, Mod, Exp and the other arithmetic operators with feedback.</summary>
    public static JSValue Binary(Isolate isolate, Operation op, JSValue lhs, JSValue rhs, ref byte feedback)
    {
        if (lhs.IsNumber && rhs.IsNumber)
        {
            double l = lhs.Number, r = rhs.Number;
            double result = NumberBinary(op, l, r);
            // The smiFunction feedback of the Generate_*WithFeedback helpers:
            // only Divide reports kSignedSmallInputs, and Exponentiate on Smis
            // always reports kNumber.
            UpdateBinaryFeedback(ref feedback,
                !IsSmiDouble(l) || !IsSmiDouble(r) || op == Operation.Exponentiate ? BOF.TypeIndex.Number
                : IsSmiDouble(result) ? BOF.TypeIndex.SignedSmall
                : op == Operation.Divide ? BOF.TypeIndex.SignedSmallInputs
                : BOF.TypeIndex.Number);
            return JSValue.FromNumber(result);
        }
        return BinarySlow(isolate, op, lhs, rhs, ref feedback);
    }

    /// <summary>The generic path of the arithmetic operators (Subtract/Multiply/... builtins).</summary>
    public static JSValue BinarySlow(Isolate isolate, Operation op, JSValue lhs, JSValue rhs, ref byte feedback)
    {
        BOF.Type type = FeedbackForOperands(lhs, rhs);
        UpdateBinaryFeedback(ref feedback, BinaryIndex(type));
        JSValue l = ObjectOps.ToNumeric(isolate, lhs);
        JSValue r = ObjectOps.ToNumeric(isolate, rhs);
        if (l.IsNumber && r.IsNumber) return JSValue.FromNumber(NumberBinary(op, l.Number, r.Number));
        if (l.HeapObjectOrNull is BigInt lb && r.HeapObjectOrNull is BigInt rb)
        {
            return BigIntOperations.BinaryOp(isolate, op, lb, rb);
        }
        return isolate.ThrowTypeError(MessageTemplate.BigIntMixedTypes);
    }

    /// <summary>The binary-op feedback for non-number operands (before conversion).</summary>
    static BOF.Type FeedbackForOperands(in JSValue lhs, in JSValue rhs)
    {
        BOF.Type l = FeedbackForOperand(lhs);
        BOF.Type r = FeedbackForOperand(rhs);
        if (l == BOF.Type.BigInt64 && r == BOF.Type.BigInt64) return BOF.Type.BigInt64;
        if ((l & BOF.Type.BigInt) != 0 && (r & BOF.Type.BigInt) != 0) return BOF.Type.BigInt;
        if ((l | r) == BOF.Type.NumberOrOddball || ((l | r) & ~BOF.Type.NumberOrOddball) == 0) return l | r;
        return BOF.Type.Any;
    }

    static BOF.Type FeedbackForOperand(in JSValue value)
    {
        if (value.IsNumber) return IsSmiDouble(value.Number) ? BOF.Type.SignedSmall : BOF.Type.Number;
        switch (value.HeapObjectOrNull)
        {
            case null:
            case Oddball:
                return BOF.Type.NumberOrOddball;
            case BigInt b:
                return BigIntOperations.FitsInInt64(b) ? BOF.Type.BigInt64 : BOF.Type.BigInt;
            case JSString:
                return BOF.Type.String;
            default:
                return BOF.Type.Any;
        }
    }

    /// <summary>Add with feedback for everything but two numbers (strings, BigInts, objects).</summary>
    public static JSValue AddSlow(Isolate isolate, JSValue lhs, JSValue rhs, ref byte feedback)
    {
        if (lhs.StringOrNull is JSString ls && rhs.StringOrNull is JSString rs)
        {
            UpdateBinaryFeedback(ref feedback, BOF.TypeIndex.String);
            return StringAdd(isolate, ls, rs);
        }
        // A String and a Number: the Add builtin's StringAddConvertRight /
        // StringAddConvertLeft (NumberToString through the number-string
        // cache), with kAny feedback as BinaryOpAssembler records it.
        if (lhs._obj == NumberTag.Instance || rhs._obj == NumberTag.Instance)
        {
            if (lhs.StringOrNull is JSString lstr && rhs._obj == NumberTag.Instance)
            {
                UpdateBinaryFeedback(ref feedback, BOF.TypeIndex.Any);
                return StringAdd(isolate, lstr, isolate.Factory.NumberToString(rhs));
            }
            if (rhs.StringOrNull is JSString rstr && lhs._obj == NumberTag.Instance)
            {
                UpdateBinaryFeedback(ref feedback, BOF.TypeIndex.Any);
                return StringAdd(isolate, isolate.Factory.NumberToString(lhs), rstr);
            }
        }
        if (lhs.HeapObjectOrNull is BigInt lb && rhs.HeapObjectOrNull is BigInt rb)
        {
            UpdateBinaryFeedback(ref feedback,
                BigIntOperations.FitsInInt64(lb) && BigIntOperations.FitsInInt64(rb) ? BOF.TypeIndex.BigInt64 : BOF.TypeIndex.BigInt);
            return BigIntOperations.Add(isolate, lb, rb);
        }
        bool lhsOddballOrNumber = lhs.IsNumber || lhs.IsOddball || lhs.IsUndefined;
        bool rhsOddballOrNumber = rhs.IsNumber || rhs.IsOddball || rhs.IsUndefined;
        if (lhsOddballOrNumber && rhsOddballOrNumber)
        {
            UpdateBinaryFeedback(ref feedback, BOF.TypeIndex.NumberOrOddball);
        }
        else if ((lhs.IsString && IsStringWrapperWithOriginalToPrimitive(isolate, rhs)) ||
                 (rhs.IsString && IsStringWrapperWithOriginalToPrimitive(isolate, lhs)))
        {
            UpdateBinaryFeedback(ref feedback, BOF.TypeIndex.StringOrStringWrapper);
        }
        else
        {
            UpdateBinaryFeedback(ref feedback, BOF.TypeIndex.Any);
        }
        return Add(isolate, lhs, rhs);
    }

    static bool IsStringWrapperWithOriginalToPrimitive(Isolate isolate, in JSValue value) =>
        value.HeapObjectOrNull is JSPrimitiveWrapper w && w.Value.IsString;

    /// <summary>The Add builtin: ToPrimitive, then string concatenation or numeric addition.</summary>
    public static JSValue Add(Isolate isolate, JSValue lhs, JSValue rhs)
    {
        if (lhs.IsNumber && rhs.IsNumber) return JSValue.FromNumber(lhs.Number + rhs.Number);
        if (lhs.StringOrNull is JSString ls0 && rhs.StringOrNull is JSString rs0) return StringAdd(isolate, ls0, rs0);
        JSValue l = ObjectOps.ToPrimitive(isolate, lhs);
        JSValue r = ObjectOps.ToPrimitive(isolate, rhs);
        if (l.IsString || r.IsString)
        {
            JSString ls = ObjectOps.ToString(isolate, l);
            JSString rs = ObjectOps.ToString(isolate, r);
            return StringAdd(isolate, ls, rs);
        }
        JSValue ln = ObjectOps.ToNumeric(isolate, l);
        JSValue rn = ObjectOps.ToNumeric(isolate, r);
        if (ln.IsNumber && rn.IsNumber) return JSValue.FromNumber(ln.Number + rn.Number);
        if (ln.HeapObjectOrNull is BigInt lb && rn.HeapObjectOrNull is BigInt rb) return BigIntOperations.Add(isolate, lb, rb);
        return isolate.ThrowTypeError(MessageTemplate.BigIntMixedTypes);
    }

    /// <summary>StringAdd_CheckNone: concatenation with V8's length check.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static JSValue StringAdd(Isolate isolate, JSString left, JSString right)
    {
        if (left.Length == 0) return right;
        if (right.Length == 0) return left;
        return isolate.Factory.NewConsString(left, right);
    }

    /// <summary>
    /// Add_StringConstant_Internalize: Add where one side is a string constant
    /// and the result is used as a property key, so it is internalized
    /// (BinaryOpAssembler::Generate_Add{Lhs,Rhs}IsStringConstantInternalizeWithFeedback).
    /// </summary>
    public static JSValue AddStringConstantAndInternalize(Isolate isolate, FeedbackVector? fv, int slot, JSValue lhs, JSValue rhs,
        AddStringConstantAndInternalizeVariant variant)
    {
        JSValue result;
        BOF.TypeIndex type;
        if (lhs.StringOrNull is JSString ls && rhs.StringOrNull is JSString rs)
        {
            result = isolate.Factory.InternalizeString(StringAdd(isolate, ls, rs).As<JSString>());
            type = BOF.TypeIndex.String;
        }
        else
        {
            byte dummy = 0;
            result = AddSlow(isolate, lhs, rhs, ref dummy);
            type = (BOF.TypeIndex)dummy;
            if (result.StringOrNull is JSString s) result = isolate.Factory.InternalizeString(s);
        }
        if (fv is not null)
        {
            ref JSValue feedback = ref fv.Slots[slot];
            int current = feedback.IsNumber ? (int)feedback.Number : 0;
            int combined = current | (int)BOF.DecodeTypeIndex(type);
            if (combined != current) feedback = JSValue.FromInt(combined);
        }
        return result;
    }

    // ---- Bitwise --------------------------------------------------------------------

    static int Int32BitwiseOp(Operation op, int l, int r) => op switch
    {
        Operation.BitwiseAnd => l & r,
        Operation.BitwiseOr => l | r,
        Operation.BitwiseXor => l ^ r,
        Operation.ShiftLeft => l << (r & 0x1F),
        Operation.ShiftRight => l >> (r & 0x1F),
        _ => throw new UnreachableException(),
    };

    /// <summary>The bitwise operators (Generate_BitwiseBinaryOpWithFeedback).</summary>
    public static JSValue Bitwise(Isolate isolate, Operation op, JSValue lhs, JSValue rhs, ref byte feedback)
    {
        if (lhs.IsNumber && rhs.IsNumber)
        {
            double ld = lhs.Number, rd = rhs.Number;
            JSValue result = NumberBitwise(op, ld, rd);
            UpdateBinaryFeedback(ref feedback,
                IsSmiDouble(ld) && IsSmiDouble(rd) && IsSmiDouble(result.Number) ? BOF.TypeIndex.SignedSmall
                : BOF.TypeIndex.Number);
            return result;
        }
        return BitwiseSlow(isolate, op, lhs, rhs, ref feedback);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static JSValue NumberBitwise(Operation op, double ld, double rd)
    {
        int l = DoubleToInt32(ld);
        if (op == Operation.ShiftRightLogical)
        {
            return JSValue.FromNumber((uint)l >> (DoubleToInt32(rd) & 0x1F));
        }
        return JSValue.FromInt(Int32BitwiseOp(op, l, DoubleToInt32(rd)));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int DoubleToInt32(double d)
    {
        int i = (int)d;
        if (i == d) return i;
        return Conversions.DoubleToInt32(d);
    }

    static JSValue BitwiseSlow(Isolate isolate, Operation op, JSValue lhs, JSValue rhs, ref byte feedback)
    {
        UpdateBinaryFeedback(ref feedback, BinaryIndex(FeedbackForOperands(lhs, rhs)));
        JSValue l = ObjectOps.ToNumeric(isolate, lhs);
        JSValue r = ObjectOps.ToNumeric(isolate, rhs);
        if (l.IsNumber && r.IsNumber) return NumberBitwise(op, l.Number, r.Number);
        if (l.HeapObjectOrNull is BigInt lb && r.HeapObjectOrNull is BigInt rb) return BigIntOperations.BinaryOp(isolate, op, lb, rb);
        return isolate.ThrowTypeError(MessageTemplate.BigIntMixedTypes);
    }

    // ---- Unary ----------------------------------------------------------------------

    /// <summary>Inc (Generate_IncrementWithFeedback).</summary>
    public static JSValue Increment(Isolate isolate, JSValue value, ref byte feedback) =>
        value.IsNumber ? IncrementNumber(value.Number, ref feedback) : UnarySlow(isolate, Operation.Increment, value, ref feedback);

    /// <summary>Inc of a number.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static JSValue IncrementNumber(double d, ref byte feedback)
    {
        double result = d + 1;
        if (IsNumberFeedbackSaturated(feedback)) return JSValue.FromNumber(result);
        UpdateBinaryFeedback(ref feedback,
            TryGetSmi(d, out int i) && i != JSValue.SmiMaxValue ? BOF.TypeIndex.SignedSmall : BOF.TypeIndex.Number);
        return JSValue.FromNumber(result);
    }

    /// <summary>Dec (Generate_DecrementWithFeedback).</summary>
    public static JSValue Decrement(Isolate isolate, JSValue value, ref byte feedback) =>
        value.IsNumber ? DecrementNumber(value.Number, ref feedback) : UnarySlow(isolate, Operation.Decrement, value, ref feedback);

    /// <summary>Dec of a number.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static JSValue DecrementNumber(double d, ref byte feedback)
    {
        double result = d - 1;
        if (IsNumberFeedbackSaturated(feedback)) return JSValue.FromNumber(result);
        UpdateBinaryFeedback(ref feedback,
            TryGetSmi(d, out int i) && i != JSValue.SmiMinValue ? BOF.TypeIndex.SignedSmall : BOF.TypeIndex.Number);
        return JSValue.FromNumber(result);
    }

    /// <summary>Negate (Generate_NegateWithFeedback).</summary>
    public static JSValue Negate(Isolate isolate, JSValue value, ref byte feedback)
    {
        if (value.IsNumber)
        {
            double d = value.Number;
            double result = -d;
            UpdateBinaryFeedback(ref feedback,
                IsSmiDouble(d) && IsSmiDouble(result) ? BOF.TypeIndex.SignedSmall : BOF.TypeIndex.Number);
            return JSValue.FromNumber(result);
        }
        return UnarySlow(isolate, Operation.Negate, value, ref feedback);
    }

    /// <summary>BitwiseNot (Generate_BitwiseNotWithFeedback).</summary>
    public static JSValue BitwiseNot(Isolate isolate, JSValue value, ref byte feedback)
    {
        if (value.IsNumber)
        {
            UpdateBinaryFeedback(ref feedback, IsSmiDouble(value.Number) ? BOF.TypeIndex.SignedSmall : BOF.TypeIndex.Number);
            return JSValue.FromInt(~DoubleToInt32(value.Number));
        }
        return UnarySlow(isolate, Operation.BitwiseNot, value, ref feedback);
    }

    static JSValue UnarySlow(Isolate isolate, Operation op, JSValue value, ref byte feedback)
    {
        // UnaryOpWithFeedback: BigInts report kBigInt, oddballs
        // kNumberOrOddball, everything else (converted by ToNumeric) kAny.
        UpdateBinaryFeedback(ref feedback, value.HeapObjectOrNull switch
        {
            BigInt => BOF.TypeIndex.BigInt,
            null or Oddball => BOF.TypeIndex.NumberOrOddball,
            _ => BOF.TypeIndex.Any,
        });
        JSValue numeric = ObjectOps.ToNumeric(isolate, value);
        if (numeric.IsNumber)
        {
            double d = numeric.Number;
            return op switch
            {
                Operation.Increment => JSValue.FromNumber(d + 1),
                Operation.Decrement => JSValue.FromNumber(d - 1),
                Operation.Negate => JSValue.FromNumber(-d),
                Operation.BitwiseNot => JSValue.FromInt(~DoubleToInt32(d)),
                _ => throw new UnreachableException(),
            };
        }
        var b = numeric.As<BigInt>();
        return op switch
        {
            Operation.Increment => BigIntOperations.Increment(isolate, b),
            Operation.Decrement => BigIntOperations.Decrement(isolate, b),
            Operation.Negate => BigIntOperations.UnaryMinus(isolate, b),
            Operation.BitwiseNot => BigIntOperations.BitwiseNot(isolate, b),
            _ => throw new UnreachableException(),
        };
    }

    // ---- Comparisons ------------------------------------------------------------------

    /// <summary>The compare feedback of one operand (CodeStubAssembler's CollectFeedbackForString and friends).</summary>
    static COF.Type CompareFeedbackFor(in JSValue value)
    {
        HeapObject? o = value._obj;
        if (o is null) return COF.Type.NullOrUndefined;
        if (ReferenceEquals(o, NumberTag.Instance)) return IsSmiDouble(value._num) ? COF.Type.SignedSmall : COF.Type.Number;
        // Instance type ranges rather than type tests: JSString, BigInt and
        // JSReceiver are not sealed, and a type test walks the class chain.
        InstanceType type = o.InstanceType;
        if (type >= InstanceTypeChecks.FirstJSReceiver) return COF.Type.Receiver;
        if (InstanceTypeChecks.IsString(type))
        {
            return Unsafe.As<JSString>(o).IsInternalized ? COF.Type.InternalizedString : COF.Type.String;
        }
        switch (type)
        {
            case InstanceType.OddballType:
                return Unsafe.As<Oddball>(o).Kind == Oddball.OddballKind.Null ? COF.Type.NullOrUndefined : COF.Type.Boolean;
            case InstanceType.SymbolType:
                return COF.Type.Symbol;
            case InstanceType.BigIntType:
                return BigIntOperations.FitsInInt64(Unsafe.As<BigInt>(o)) ? COF.Type.BigInt64 : COF.Type.BigInt;
            default:
                return COF.Type.Any;
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static void RecordCompareFeedback(in JSValue lhs, in JSValue rhs, ref byte feedback)
    {
        if (feedback == (byte)COF.TypeIndex.Any) return;
        COF.Type type = CompareFeedbackFor(lhs) | CompareFeedbackFor(rhs);
        UpdateCompareFeedback(ref feedback, COF.CalculateTypeIndex((uint)type));
    }

    /// <summary>TestEqual / TestEqualStrict of two numbers, with feedback.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static JSValue EqualNumbers(double l, double r, ref byte feedback)
    {
        UpdateCompareFeedback(ref feedback,
            IsSmiDouble(l) && IsSmiDouble(r) ? COF.TypeIndex.SignedSmall : COF.TypeIndex.Number);
        return JSValue.FromBoolean(l == r);
    }

    /// <summary>TestEqualStrict with feedback.</summary>
    public static JSValue StrictEqual(JSValue lhs, JSValue rhs, ref byte feedback)
    {
        if (lhs.IsNumber && rhs.IsNumber) return EqualNumbers(lhs.Number, rhs.Number, ref feedback);
        RecordCompareFeedback(lhs, rhs, ref feedback);
        return JSValue.FromBoolean(ObjectOps.StrictEquals(lhs, rhs));
    }

    /// <summary>TestEqual with feedback.</summary>
    public static JSValue Equal(Isolate isolate, JSValue lhs, JSValue rhs, ref byte feedback)
    {
        if (lhs.IsNumber && rhs.IsNumber) return EqualNumbers(lhs.Number, rhs.Number, ref feedback);
        RecordCompareFeedback(lhs, rhs, ref feedback);
        if (lhs.IsIdenticalTo(rhs) && !lhs.IsNumber) return JSValue.True;
        return JSValue.FromBoolean(ObjectOps.Equals(isolate, lhs, rhs));
    }

    /// <summary>The feedback of a comparison of two numbers (SignedSmall or Number).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void UpdateCompareFeedbackForNumbers(ref byte feedback, double l, double r)
    {
        // Feedback that two numbers cannot widen (Number, NumberOrBoolean,
        // NumberOrOddball, Any) skips classifying the operands.
        byte current = feedback;
        if ((uint)(current - (byte)COF.TypeIndex.Number) <= 2 || current == (byte)COF.TypeIndex.Any) return;
        UpdateCompareFeedback(ref feedback, IsSmiDouble(l) && IsSmiDouble(r) ? COF.TypeIndex.SignedSmall : COF.TypeIndex.Number);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static JSValue CompareNumbers(Operation op, double l, double r, ref byte feedback)
    {
        UpdateCompareFeedbackForNumbers(ref feedback, l, r);
        bool result = op switch
        {
            Operation.LessThan => l < r,
            Operation.GreaterThan => l > r,
            Operation.LessThanOrEqual => l <= r,
            _ => l >= r,
        };
        return JSValue.FromBoolean(result);
    }

    /// <summary>The relational comparisons for non-number operands.</summary>
    public static JSValue Relational(Isolate isolate, Operation op, JSValue lhs, JSValue rhs, ref byte feedback)
    {
        // CodeStubAssembler::RelationalComparison reports kString for two
        // strings, internalized or not.
        if (lhs.IsString && rhs.IsString) UpdateCompareFeedback(ref feedback, COF.TypeIndex.String);
        else RecordCompareFeedback(lhs, rhs, ref feedback);
        ComparisonResult result = ObjectOps.Compare(isolate, lhs, rhs);
        if (result == ComparisonResult.Undefined) return JSValue.False;
        return JSValue.FromBoolean(EngineGlobals.ComparisonResultToBool(op, result));
    }

    /// <summary>TestInstanceOf: CollectInstanceOfFeedback + InstanceOf.</summary>
    public static JSValue InstanceOf(Isolate isolate, FeedbackVector? fv, int slot, JSValue obj, JSValue callable)
    {
        if (fv is not null)
        {
            // CollectInstanceOfFeedback: remember the constructor if it is always the same.
            ref JSValue feedback = ref fv.Slots[slot];
            if (!feedback.IsIdenticalTo(callable))
            {
                if (ReferenceEquals(feedback.HeapObjectOrNull, ReadOnlyRoots.uninitialized_symbol))
                {
                    feedback = callable.IsJSReceiver ? callable : FeedbackVector.MegamorphicSentinel;
                }
                else if (!ReferenceEquals(feedback.HeapObjectOrNull, ReadOnlyRoots.megamorphic_symbol))
                {
                    feedback = FeedbackVector.MegamorphicSentinel;
                }
            }
        }
        return JSValue.FromBoolean(ObjectOps.InstanceOf(isolate, obj, callable));
    }

    // ---- Conversions --------------------------------------------------------------------

    /// <summary>ToBoolean (BranchIfToBooleanIsTrue).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool ToBoolean(in JSValue value)
    {
        HeapObject? o = value._obj;
        if (ReferenceEquals(o, Oddball.True)) return true;
        if (ReferenceEquals(o, Oddball.False) || o is null) return false;
        if (ReferenceEquals(o, NumberTag.Instance))
        {
            double d = value._num;
            return d != 0 && !double.IsNaN(d);
        }
        return ObjectOps.BooleanValue(value);
    }

    /// <summary>The feedback of ToNumber / ToNumeric on a number (which converts to itself).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void ToNumberFeedbackForNumber(FeedbackVector? fv, int slot, double value)
    {
        if (fv is null) return;
        ref JSValue feedback = ref fv.Slots[slot];
        int current = feedback.IsNumber ? (int)feedback._num : 0;
        int combined = current | (int)(IsSmiDouble(value) ? BOF.Type.SignedSmall : BOF.Type.Number);
        if (combined != current) feedback = JSValue.FromInt(combined);
    }

    /// <summary>ToNumber / ToNumeric with binary-op feedback in the slot (InterpreterAssembler::ToNumberOrNumeric).</summary>
    public static JSValue ToNumberOrNumeric(Isolate isolate, JSValue value, FeedbackVector? fv, int slot, bool numeric)
    {
        BOF.Type feedbackType;
        JSValue result;
        if (value.IsNumber)
        {
            feedbackType = IsSmiDouble(value.Number) ? BOF.Type.SignedSmall : BOF.Type.Number;
            result = value;
        }
        else if (value.IsBigInt && numeric)
        {
            feedbackType = BOF.Type.BigInt;
            result = value;
        }
        else
        {
            feedbackType = value.IsOddball || value.IsUndefined ? BOF.Type.NumberOrOddball : BOF.Type.Any;
            result = numeric ? ObjectOps.ToNumeric(isolate, value) : ObjectOps.ToNumber(isolate, value);
        }
        if (fv is not null)
        {
            ref JSValue feedback = ref fv.Slots[slot];
            int current = feedback.IsNumber ? (int)feedback.Number : 0;
            int combined = current | (int)feedbackType;
            if (combined != current) feedback = JSValue.FromInt(combined);
        }
        return result;
    }

    // ---- typeof ------------------------------------------------------------------------

    /// <summary>TypeOf with feedback (InterpreterAssembler::Typeof).</summary>
    public static JSValue TypeOf(Isolate isolate, JSValue value, FeedbackVector? fv, int slot)
    {
        if (fv is not null)
        {
            TypeOfFeedbackResult type;
            if (value.IsNumber) type = IsSmiDouble(value.Number) ? TypeOfFeedbackResult.kSmi : TypeOfFeedbackResult.kHeapNumber;
            else if (value.IsString) type = TypeOfFeedbackResult.kString;
            else if (value.HeapObjectOrNull is JSReceiver r && r.Map.IsCallable && !r.Map.IsUndetectable)
                type = TypeOfFeedbackResult.kFunction;
            else type = TypeOfFeedbackResult.kAny;
            ref JSValue feedback = ref fv.Slots[slot];
            int current = feedback.IsNumber ? (int)feedback.Number : 0;
            int combined = current | (int)type;
            if (combined != current) feedback = JSValue.FromInt(combined);
        }
        return ObjectOps.TypeOf(isolate, value);
    }

    /// <summary>TestUndetectable.</summary>
    public static bool IsUndetectable(in JSValue value) =>
        value.IsNullOrUndefined || (value.HeapObjectOrNull is JSReceiver r && r.Map.IsUndetectable);

    /// <summary>TestTypeOf.</summary>
    public static bool TestTypeOf(in JSValue value, TestTypeOfFlags.LiteralFlag literal)
    {
        switch (literal)
        {
            case TestTypeOfFlags.LiteralFlag.Number:
                return value.IsNumber;
            case TestTypeOfFlags.LiteralFlag.String:
                return value.IsString;
            case TestTypeOfFlags.LiteralFlag.Symbol:
                return value.IsSymbol;
            case TestTypeOfFlags.LiteralFlag.Boolean:
                return value.IsBoolean;
            case TestTypeOfFlags.LiteralFlag.BigInt:
                return value.IsBigInt;
            case TestTypeOfFlags.LiteralFlag.Undefined:
                // Check it is not null and the map has the undetectable bit set.
                return value.IsUndefined || (value.HeapObjectOrNull is JSReceiver ur && ur.Map.IsUndetectable);
            case TestTypeOfFlags.LiteralFlag.Function:
                return value.HeapObjectOrNull is JSReceiver fr && fr.Map.IsCallable && !fr.Map.IsUndetectable;
            case TestTypeOfFlags.LiteralFlag.Object:
                if (value.IsNull) return true;
                return value.HeapObjectOrNull is JSReceiver or_ && !or_.Map.IsCallable && !or_.Map.IsUndetectable;
            default:
                return false;
        }
    }

    // ---- Contexts, modules, classes -----------------------------------------------------------

    /// <summary>
    /// GotoIfHasContextExtensionUpToDepth: the context at <paramref name="depth"/>
    /// when none of the contexts on the way has an extension object, else null.
    /// </summary>
    public static Context? ContextWithoutExtensionsUpToDepth(Context context, int depth)
    {
        Context current = context;
        while (depth > 0)
        {
            if (current.ScopeInfo.HasContextExtensionSlot && !current.Slots[(int)Context.Field.EXTENSION_INDEX].IsUndefined)
            {
                return null;
            }
            current = current.Previous!;
            depth--;
        }
        return current;
    }

    /// <summary>LdaModuleVariable.</summary>
    public static JSValue LoadModuleVariable(Isolate isolate, Context moduleContext, int cellIndex) =>
        RuntimeModules.LoadVariable(isolate, moduleContext, cellIndex);

    /// <summary>StaModuleVariable.</summary>
    public static void StoreModuleVariable(Isolate isolate, Context moduleContext, int cellIndex, JSValue value) =>
        RuntimeModules.StoreVariable(isolate, moduleContext, cellIndex, value);

    /// <summary>GetSuperConstructor: [[GetPrototypeOf]] of the active function.</summary>
    public static JSValue GetSuperConstructor(Isolate isolate, JSFunction activeFunction)
    {
        JSReceiver? prototype = activeFunction.Map.Prototype;
        return prototype is null ? JSValue.Null : prototype;
    }

    /// <summary>
    /// FindNonDefaultConstructorOrConstruct: walks the super constructors while
    /// they are default derived constructors; at a default base constructor it
    /// constructs the instance directly (FastNewObject).
    /// </summary>
    public static void FindNonDefaultConstructorOrConstruct(Isolate isolate, JSFunction thisFunction, JSValue newTarget,
        out JSValue found, out JSValue constructorOrInstance)
    {
        // CodeStubAssembler::FindNonDefaultConstructor.
        JSValue constructor = GetSuperConstructor(isolate, thisFunction);

        // Disable the optimization if the array iterator has been changed. V8 uses
        // the array iterator for the spread in default ctors, even though it
        // shouldn't, according to the spec. This ensures that omitting default ctors
        // doesn't change the behavior. See crbug.com/v8/13249.
        if (!Protectors.IsArrayIteratorLookupChainIntact(isolate))
        {
            found = JSValue.False;
            constructorOrInstance = constructor;
            return;
        }

        while (true)
        {
            // If it's not a JSFunction, the error will be thrown by the
            // ThrowIfNotSuperConstructor which follows this bytecode.
            if (constructor.HeapObjectOrNull is not JSFunction function ||
                // If there are class fields, bail out.
                function.Shared.RequiresInstanceMembersInitializer ||
                // If there are private methods, bail out.
                function.Context.ScopeInfo.ClassScopeHasPrivateBrand)
            {
                found = JSValue.False;
                constructorOrInstance = constructor;
                return;
            }
            FunctionKind kind = function.Shared.Kind;
            if (kind == FunctionKind.DefaultBaseConstructor)
            {
                // A default base ctor -> stop the search and create an object
                // directly, without calling the default base ctor (FastNewObject).
                found = JSValue.True;
                constructorOrInstance = JSObject.New(isolate, function, newTarget.As<JSReceiver>(), null);
                return;
            }
            // Something else than a default derived ctor (e.g., a non-default base
            // ctor, a non-default derived ctor, or a normal function) -> stop the
            // search.
            if (kind != FunctionKind.DefaultDerivedConstructor)
            {
                found = JSValue.False;
                constructorOrInstance = constructor;
                return;
            }
            constructor = GetSuperConstructor(isolate, function);
        }
    }

    /// <summary>IncBlockCounter: block coverage is not collected (no inspector).</summary>
    public static void IncBlockCounter(Isolate isolate, JSFunction function, int coverageSlot)
    {
    }
}
