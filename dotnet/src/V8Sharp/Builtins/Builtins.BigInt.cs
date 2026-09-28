// Port of src/builtins/builtins-bigint.cc (the BigInt constructor, asUintN,
// asIntN, toLocaleString without ICU, toString, valueOf), the stub builtins
// of src/builtins/builtins-bigint.tq (BigIntAdd ... BigIntUnaryMinus, with
// their NoThrow variants) and the BigInt runtime functions of
// src/runtime/runtime-bigint.cc (Runtime_BigIntUnaryOp, Runtime_BigIntExponentiate).
using System.Runtime.CompilerServices;

namespace V8Sharp.Builtins;

public static partial class BuiltinRegistry
{
    static partial void RegisterBigInt()
    {
        Register(Builtin.BigIntConstructor, BuiltinsBigInt.BigIntConstructor);
        Register(Builtin.BigIntAsUintN, BuiltinsBigInt.BigIntAsUintN);
        Register(Builtin.BigIntAsIntN, BuiltinsBigInt.BigIntAsIntN);
        Register(Builtin.BigIntPrototypeToLocaleString, BuiltinsBigInt.BigIntPrototypeToLocaleString);
        Register(Builtin.BigIntPrototypeToString, BuiltinsBigInt.BigIntPrototypeToString);
        Register(Builtin.BigIntPrototypeValueOf, BuiltinsBigInt.BigIntPrototypeValueOf);

        Register(Builtin.BigIntAdd, static (Isolate i, in BuiltinArguments a) => BuiltinsBigInt.Binary(i, a, Operation.Add));
        Register(Builtin.BigIntSubtract, static (Isolate i, in BuiltinArguments a) => BuiltinsBigInt.Binary(i, a, Operation.Subtract));
        Register(Builtin.BigIntMultiply, static (Isolate i, in BuiltinArguments a) => BuiltinsBigInt.Binary(i, a, Operation.Multiply));
        Register(Builtin.BigIntDivide, static (Isolate i, in BuiltinArguments a) => BuiltinsBigInt.Binary(i, a, Operation.Divide));
        Register(Builtin.BigIntModulus, static (Isolate i, in BuiltinArguments a) => BuiltinsBigInt.Binary(i, a, Operation.Modulus));
        Register(Builtin.BigIntBitwiseAnd, static (Isolate i, in BuiltinArguments a) => BuiltinsBigInt.Binary(i, a, Operation.BitwiseAnd));
        Register(Builtin.BigIntBitwiseOr, static (Isolate i, in BuiltinArguments a) => BuiltinsBigInt.Binary(i, a, Operation.BitwiseOr));
        Register(Builtin.BigIntBitwiseXor, static (Isolate i, in BuiltinArguments a) => BuiltinsBigInt.Binary(i, a, Operation.BitwiseXor));
        Register(Builtin.BigIntShiftLeft, static (Isolate i, in BuiltinArguments a) => BuiltinsBigInt.Binary(i, a, Operation.ShiftLeft));
        Register(Builtin.BigIntShiftRight, static (Isolate i, in BuiltinArguments a) => BuiltinsBigInt.Binary(i, a, Operation.ShiftRight));
        Register(Builtin.BigIntAddNoThrow, static (Isolate i, in BuiltinArguments a) => BuiltinsBigInt.BinaryNoThrow(i, a, Operation.Add));
        Register(Builtin.BigIntSubtractNoThrow, static (Isolate i, in BuiltinArguments a) => BuiltinsBigInt.BinaryNoThrow(i, a, Operation.Subtract));
        Register(Builtin.BigIntMultiplyNoThrow, static (Isolate i, in BuiltinArguments a) => BuiltinsBigInt.BinaryNoThrow(i, a, Operation.Multiply));
        Register(Builtin.BigIntDivideNoThrow, static (Isolate i, in BuiltinArguments a) => BuiltinsBigInt.BinaryNoThrow(i, a, Operation.Divide));
        Register(Builtin.BigIntModulusNoThrow, static (Isolate i, in BuiltinArguments a) => BuiltinsBigInt.BinaryNoThrow(i, a, Operation.Modulus));
        Register(Builtin.BigIntBitwiseAndNoThrow, static (Isolate i, in BuiltinArguments a) => BuiltinsBigInt.BinaryNoThrow(i, a, Operation.BitwiseAnd));
        Register(Builtin.BigIntBitwiseOrNoThrow, static (Isolate i, in BuiltinArguments a) => BuiltinsBigInt.BinaryNoThrow(i, a, Operation.BitwiseOr));
        Register(Builtin.BigIntBitwiseXorNoThrow, static (Isolate i, in BuiltinArguments a) => BuiltinsBigInt.BinaryNoThrow(i, a, Operation.BitwiseXor));
        Register(Builtin.BigIntShiftLeftNoThrow, static (Isolate i, in BuiltinArguments a) => BuiltinsBigInt.BinaryNoThrow(i, a, Operation.ShiftLeft));
        Register(Builtin.BigIntShiftRightNoThrow, static (Isolate i, in BuiltinArguments a) => BuiltinsBigInt.BinaryNoThrow(i, a, Operation.ShiftRight));
        Register(Builtin.BigIntEqual, static (Isolate i, in BuiltinArguments a) =>
            JSValue.FromBoolean(BigInt.EqualToBigInt(a[1].As<BigInt>(), a[2].As<BigInt>())));
        Register(Builtin.BigIntLessThan, static (Isolate i, in BuiltinArguments a) =>
            JSValue.FromBoolean(BigInt.CompareToBigInt(a[1].As<BigInt>(), a[2].As<BigInt>()) == ComparisonResult.LessThan));
        Register(Builtin.BigIntGreaterThan, static (Isolate i, in BuiltinArguments a) =>
            JSValue.FromBoolean(BigInt.CompareToBigInt(a[1].As<BigInt>(), a[2].As<BigInt>()) == ComparisonResult.GreaterThan));
        Register(Builtin.BigIntLessThanOrEqual, static (Isolate i, in BuiltinArguments a) =>
            JSValue.FromBoolean(BigInt.CompareToBigInt(a[1].As<BigInt>(), a[2].As<BigInt>()) != ComparisonResult.GreaterThan));
        Register(Builtin.BigIntGreaterThanOrEqual, static (Isolate i, in BuiltinArguments a) =>
            JSValue.FromBoolean(BigInt.CompareToBigInt(a[1].As<BigInt>(), a[2].As<BigInt>()) != ComparisonResult.LessThan));
        Register(Builtin.BigIntUnaryMinus, static (Isolate i, in BuiltinArguments a) => BigInt.UnaryMinusBuiltin(i, a[1].As<BigInt>()));
    }
}

/// <summary>The BigInt builtins.</summary>
public static class BuiltinsBigInt
{
    /// <summary>BUILTIN(BigIntConstructor).</summary>
    public static JSValue BigIntConstructor(Isolate isolate, in BuiltinArguments args)
    {
        if (!args.NewTarget.IsUndefined)
        {  // [[Construct]]
            isolate.ThrowTypeError(MessageTemplate.NotConstructor, ReadOnlyRoots.BigInt_string);
        }
        // [[Call]]
        JSValue value = args.AtOrUndefined(1);

        if (value.HeapObjectOrNull is JSReceiver receiver)
        {
            value = JSReceiver.ToPrimitive(isolate, receiver, ToPrimitiveHint.Number);
        }

        return value.IsNumber ? BigInt.FromNumber(isolate, value) : BigInt.FromObject(isolate, value);
    }

    /// <summary>BUILTIN(BigIntAsUintN).</summary>
    public static JSValue BigIntAsUintN(Isolate isolate, in BuiltinArguments args)
    {
        JSValue bitsObj = args.AtOrUndefined(1);
        JSValue bigintObj = args.AtOrUndefined(2);
        JSValue bits = ObjectOps.ToIndex(isolate, bitsObj, MessageTemplate.InvalidIndex);
        BigInt bigint = BigInt.FromObject(isolate, bigintObj);
        return BigInt.AsUintN(isolate, (ulong)bits.Number, bigint);
    }

    /// <summary>BUILTIN(BigIntAsIntN).</summary>
    public static JSValue BigIntAsIntN(Isolate isolate, in BuiltinArguments args)
    {
        JSValue bitsObj = args.AtOrUndefined(1);
        JSValue bigintObj = args.AtOrUndefined(2);
        JSValue bits = ObjectOps.ToIndex(isolate, bitsObj, MessageTemplate.InvalidIndex);
        BigInt bigint = BigInt.FromObject(isolate, bigintObj);
        return BigInt.AsIntN(isolate, (ulong)bits.Number, bigint);
    }

    /// <summary>ThisBigIntValue.</summary>
    public static BigInt ThisBigIntValue(Isolate isolate, JSValue value, string caller)
    {
        // 1. If Type(value) is BigInt, return value.
        if (value.HeapObjectOrNull is BigInt bigint) return bigint;
        // 2. If Type(value) is Object and value has a [[BigIntData]] internal slot:
        if (value.HeapObjectOrNull is JSPrimitiveWrapper wrapper && wrapper.Value.HeapObjectOrNull is BigInt data)
        {
            return data;
        }
        // 3. Throw a TypeError exception.
        isolate.ThrowTypeError(MessageTemplate.NotGeneric, isolate.Factory.NewStringFromAsciiChecked(caller), ReadOnlyRoots.BigInt_string);
        return null!;
    }

    static JSValue BigIntToStringImpl(Isolate isolate, JSValue receiver, JSValue radix, string builtinName)
    {
        // 1. Let x be ? thisBigIntValue(this value).
        BigInt x = ThisBigIntValue(isolate, receiver, builtinName);
        // 2. If radix is not present, let radixNumber be 10.
        // 3. Else if radix is undefined, let radixNumber be 10.
        int radixNumber = 10;
        if (!radix.IsUndefined)
        {
            // 4. Else, let radixNumber be ? ToInteger(radix).
            double radixDouble = ObjectOps.IntegerValue(isolate, radix);
            // 5. If radixNumber < 2 or radixNumber > 36, throw a RangeError exception.
            if (radixDouble < 2 || radixDouble > 36)
            {
                isolate.ThrowRangeError(MessageTemplate.ToRadixFormatRange);
            }
            radixNumber = (int)radixDouble;
        }
        // Return the String representation of this Number value using the radix
        // specified by radixNumber.
        return BigInt.ToString(isolate, x, radixNumber);
    }

    /// <summary>BUILTIN(BigIntPrototypeToLocaleString), the build without V8_INTL_SUPPORT.</summary>
    public static JSValue BigIntPrototypeToLocaleString(Isolate isolate, in BuiltinArguments args) =>
        BigIntToStringImpl(isolate, args.Receiver, JSValue.Undefined, "BigInt.prototype.toLocaleString");

    /// <summary>BUILTIN(BigIntPrototypeToString).</summary>
    public static JSValue BigIntPrototypeToString(Isolate isolate, in BuiltinArguments args) =>
        BigIntToStringImpl(isolate, args.Receiver, args.AtOrUndefined(1), "BigInt.prototype.toString");

    /// <summary>BUILTIN(BigIntPrototypeValueOf).</summary>
    public static JSValue BigIntPrototypeValueOf(Isolate isolate, in BuiltinArguments args) =>
        ThisBigIntValue(isolate, args.Receiver, "BigInt.prototype.valueOf");

    // ---------------------------------------------------------------------
    // builtins-bigint.tq stubs and runtime-bigint.cc.

    /// <summary>
    /// The BigInt binary operators with V8's builtin semantics: both operands
    /// must be BigInts (kBigIntMixedTypes otherwise); kBigIntTooBig and
    /// kBigIntDivZero as RangeErrors. The interpreter's BinaryOp paths call this
    /// once ToNumeric has produced a BigInt operand.
    /// </summary>
    public static BigInt BinaryOp(Isolate isolate, JSValue left, JSValue right, Operation op)
    {
        if (left.HeapObjectOrNull is not BigInt x || right.HeapObjectOrNull is not BigInt y)
        {
            isolate.ThrowTypeError(MessageTemplate.BigIntMixedTypes);
            return null!;
        }
        return op switch
        {
            Operation.Add => BigInt.AddImpl(isolate, x, y),
            Operation.Subtract => BigInt.SubtractImpl(isolate, x, y),
            Operation.Multiply => BigInt.MultiplyImpl(isolate, x, y),
            Operation.Divide => BigInt.DivideImpl(isolate, x, y),
            Operation.Modulus => BigInt.ModulusImpl(isolate, x, y),
            Operation.Exponentiate => BigInt.Exponentiate(isolate, x, y),
            Operation.BitwiseAnd => BigInt.BitwiseAnd(isolate, x, y),
            Operation.BitwiseOr => BigInt.BitwiseOr(isolate, x, y),
            Operation.BitwiseXor => BigInt.BitwiseXor(isolate, x, y),
            Operation.ShiftLeft => BigInt.LeftShift(isolate, x, y),
            Operation.ShiftRight => BigInt.SignedRightShift(isolate, x, y),
            Operation.ShiftRightLogical => BigInt.UnsignedRightShift(isolate, x, y),
            _ => throw new ArgumentOutOfRangeException(nameof(op)),
        };
    }

    /// <summary>Runtime_BigIntUnaryOp.</summary>
    public static BigInt UnaryOp(Isolate isolate, BigInt x, Operation op) => op switch
    {
        Operation.BitwiseNot => BigInt.BitwiseNot(isolate, x),
        Operation.Negate => BigInt.UnaryMinus(isolate, x),
        Operation.Increment => BigInt.Increment(isolate, x),
        Operation.Decrement => BigInt.Decrement(isolate, x),
        _ => throw new ArgumentOutOfRangeException(nameof(op)),
    };

    /// <summary>Runtime_BigIntExponentiate.</summary>
    public static JSValue Exponentiate(Isolate isolate, JSValue left, JSValue right) =>
        BinaryOp(isolate, left, right, Operation.Exponentiate);

    internal static JSValue Binary(Isolate isolate, in BuiltinArguments args, Operation op) =>
        BinaryOp(isolate, args.AtOrUndefined(1), args.AtOrUndefined(2), op);

    /// <summary>
    /// The *NoThrow builtins: Smi 0 signals kBigIntTooBig / kBigIntDivZero,
    /// Smi 1 a termination request.
    /// </summary>
    internal static JSValue BinaryNoThrow(Isolate isolate, in BuiltinArguments args, Operation op)
    {
        try
        {
            return BinaryOp(isolate, args.AtOrUndefined(1), args.AtOrUndefined(2), op);
        }
        catch (JavaScriptException)
        {
            return JSValue.Zero;
        }
        catch (TerminationException)
        {
            return JSValue.FromInt(1);
        }
    }
}
