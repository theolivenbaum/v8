// Port of src/runtime/runtime-bigint.cc, src/runtime/runtime-date.cc and
// Runtime_DoubleToStringWithRadix (runtime-internal.cc) as typed static
// methods; the runtime dispatch table adapts its arguments to them.
using V8Sharp.Builtins;

namespace V8Sharp.Runtime;

public static class RuntimeBigInt
{
    /// <summary>ComparisonResultToBool (objects.cc) for the relational operations.</summary>
    public static bool ComparisonResultToBool(Operation op, ComparisonResult result) => op switch
    {
        Operation.LessThan => result == ComparisonResult.LessThan,
        Operation.LessThanOrEqual => result is ComparisonResult.LessThan or ComparisonResult.Equal,
        Operation.GreaterThan => result == ComparisonResult.GreaterThan,
        Operation.GreaterThanOrEqual => result is ComparisonResult.GreaterThan or ComparisonResult.Equal,
        _ => throw new ArgumentOutOfRangeException(nameof(op)),
    };

    /// <summary>Runtime_BigIntCompareToNumber.</summary>
    public static bool BigIntCompareToNumber(Operation mode, BigInt lhs, JSValue rhs) =>
        ComparisonResultToBool(mode, BigInt.CompareToNumber(lhs, rhs));

    /// <summary>Runtime_BigIntCompareToString.</summary>
    public static bool BigIntCompareToString(Isolate isolate, Operation mode, BigInt lhs, JSString rhs) =>
        ComparisonResultToBool(mode, BigInt.CompareToString(isolate, lhs, rhs));

    /// <summary>Runtime_BigIntEqualToBigInt.</summary>
    public static bool BigIntEqualToBigInt(BigInt lhs, BigInt rhs) => BigInt.EqualToBigInt(lhs, rhs);

    /// <summary>Runtime_BigIntEqualToNumber.</summary>
    public static bool BigIntEqualToNumber(BigInt lhs, JSValue rhs) => BigInt.EqualToNumber(lhs, rhs);

    /// <summary>Runtime_BigIntEqualToString.</summary>
    public static bool BigIntEqualToString(Isolate isolate, BigInt lhs, JSString rhs) => BigInt.EqualToString(isolate, lhs, rhs);

    /// <summary>Runtime_BigIntToNumber.</summary>
    public static JSValue BigIntToNumber(Isolate isolate, BigInt x) => BigInt.ToNumber(isolate, x);

    /// <summary>Runtime_ToBigInt.</summary>
    public static BigInt ToBigInt(Isolate isolate, JSValue x) => BigInt.FromObject(isolate, x);

    /// <summary>Runtime_ToBigIntConvertNumber.</summary>
    public static BigInt ToBigIntConvertNumber(Isolate isolate, JSValue x)
    {
        if (x.HeapObjectOrNull is JSReceiver receiver)
        {
            x = JSReceiver.ToPrimitive(isolate, receiver, ToPrimitiveHint.Number);
        }
        return x.IsNumber ? BigInt.FromNumber(isolate, x) : BigInt.FromObject(isolate, x);
    }

    /// <summary>Runtime_BigIntExponentiate.</summary>
    public static JSValue BigIntExponentiate(Isolate isolate, JSValue left, JSValue right) =>
        BuiltinsBigInt.Exponentiate(isolate, left, right);

    /// <summary>Runtime_BigIntUnaryOp.</summary>
    public static BigInt BigIntUnaryOp(Isolate isolate, BigInt x, Operation op) => BuiltinsBigInt.UnaryOp(isolate, x, op);
}

public static class RuntimeDate
{
    /// <summary>Runtime_DateCurrentTime.</summary>
    public static JSValue DateCurrentTime(Isolate isolate) => JSValue.FromNumber(JSDate.CurrentTimeValue(isolate));
}

public static class RuntimeNumbers
{
    /// <summary>Runtime_DoubleToStringWithRadix.</summary>
    public static JSString DoubleToStringWithRadix(Isolate isolate, double number, int radix) =>
        BuiltinsNumber.DoubleToStringWithRadix(isolate, number, radix);
}
