// Port of src/builtins/builtins-number.cc (toExponential, toFixed,
// toLocaleString without ICU, toPrecision), the Number builtins of
// src/builtins/number.tq (toString(radix), isFinite, isInteger, isNaN,
// isSafeInteger, valueOf, parseFloat, parseInt), NumberConstructor from
// src/builtins/constructor.tq, and the runtime functions they call
// (Runtime_StringParseInt, Runtime_StringParseFloat,
// Runtime_DoubleToStringWithRadix).
using System.Runtime.CompilerServices;
using V8Sharp.Base.Numbers;

namespace V8Sharp.Builtins;

public static partial class BuiltinRegistry
{
    static partial void RegisterNumber()
    {
        Register(Builtin.NumberConstructor, BuiltinsNumber.NumberConstructor);
        Register(Builtin.NumberPrototypeToExponential, BuiltinsNumber.NumberPrototypeToExponential);
        Register(Builtin.NumberPrototypeToFixed, BuiltinsNumber.NumberPrototypeToFixed);
        Register(Builtin.NumberPrototypeToLocaleString, BuiltinsNumber.NumberPrototypeToLocaleString);
        Register(Builtin.NumberPrototypeToPrecision, BuiltinsNumber.NumberPrototypeToPrecision);
        Register(Builtin.NumberPrototypeToString, BuiltinsNumber.NumberPrototypeToString);
        Register(Builtin.NumberPrototypeValueOf, BuiltinsNumber.NumberPrototypeValueOf);
        Register(Builtin.NumberIsFinite, BuiltinsNumber.NumberIsFinite);
        Register(Builtin.NumberIsInteger, BuiltinsNumber.NumberIsInteger);
        Register(Builtin.NumberIsNaN, BuiltinsNumber.NumberIsNaN);
        Register(Builtin.NumberIsSafeInteger, BuiltinsNumber.NumberIsSafeInteger);
        Register(Builtin.NumberParseFloat, BuiltinsNumber.NumberParseFloat);
        Register(Builtin.NumberParseInt, BuiltinsNumber.NumberParseInt);
    }
}

/// <summary>The Number builtins.</summary>
public static class BuiltinsNumber
{
    /// <summary>kMaxFractionDigits (src/numbers/conversions.h).</summary>
    public const int kMaxFractionDigits = 100;

    // Buffer sizes of src/numbers/conversions.h.
    const int kDoubleToFixedMaxChars = kMaxFractionDigits + 21 + 1 + 1;
    const int kDoubleToExponentialMaxChars = kMaxFractionDigits + 1 + 1 + 1 + 1 + 3;
    const int kDoubleToPrecisionMaxChars = kMaxFractionDigits + 1 + 1 + 1 + 1 + 1 + 3 + 1;
    const int kDoubleToRadixMaxChars = 2200;

    /// <summary>
    /// CodeStubAssembler::ToThisValue for PrimitiveType::kNumber: the number,
    /// or the value of a Number wrapper; throws kNotGeneric otherwise.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static double ThisNumberValue(Isolate isolate, JSValue receiver, string method)
    {
        if (receiver.IsNumber) return receiver.Number;
        if (receiver.HeapObjectOrNull is JSPrimitiveWrapper wrapper && wrapper.Value.IsNumber) return wrapper.Value.Number;
        ThrowNotGeneric(isolate, method);
        return 0;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static void ThrowNotGeneric(Isolate isolate, string method) =>
        isolate.ThrowTypeError(MessageTemplate.NotGeneric, isolate.Factory.NewStringFromAsciiChecked(method),
            ReadOnlyRoots.Number_string);

    /// <summary>Unwraps a Number wrapper receiver the way the C++ builtins do (IsJSPrimitiveWrapper, then IsNumber).</summary>
    static double UnwrapReceiver(Isolate isolate, JSValue value, string method)
    {
        if (value.HeapObjectOrNull is JSPrimitiveWrapper wrapper) value = wrapper.Value;
        if (!value.IsNumber) ThrowNotGeneric(isolate, method);
        return value.Number;
    }

    // ---------------------------------------------------------------------
    // constructor.tq

    /// <summary>NumberConstructor (ES #sec-number-constructor).</summary>
    public static JSValue NumberConstructor(Isolate isolate, in BuiltinArguments args)
    {
        // 1. If no arguments were passed to this function invocation, let n be +0.
        JSValue n = JSValue.Zero;
        if (args.ArgcWithoutReceiver > 0)
        {
            // 2. a. Let prim be ? ToNumeric(value).
            //    b. If Type(prim) is BigInt, let n be the Number value for prim.
            //    c. Otherwise, let n be prim.
            JSValue prim = ObjectOps.ToNumeric(isolate, args.AtOrUndefined(1));
            n = prim.HeapObjectOrNull is BigInt bigint ? BigInt.ToNumber(isolate, bigint) : prim;
        }

        // 3. If NewTarget is undefined, return n.
        if (args.NewTarget.IsUndefined) return n;

        // 4. Let O be ? OrdinaryCreateFromConstructor(NewTarget, "%NumberPrototype%", « [[NumberData]] »).
        // 5. Set O.[[NumberData]] to n.
        var result = (JSPrimitiveWrapper)JSObject.New(isolate, args.Target, (JSReceiver)args.NewTarget.Object);
        result.Value = n;
        return result;
    }

    // ---------------------------------------------------------------------
    // builtins-number.cc

    /// <summary>ES6 section 20.1.3.2 Number.prototype.toExponential ( fractionDigits ).</summary>
    public static JSValue NumberPrototypeToExponential(Isolate isolate, in BuiltinArguments args)
    {
        JSValue fractionDigits = args.AtOrUndefined(1);

        // Unwrap the receiver {value}.
        double valueNumber = UnwrapReceiver(isolate, args.Receiver, "Number.prototype.toExponential");

        // Convert the {fraction_digits} to an integer first.
        double fractionDigitsNumber = ObjectOps.IntegerValue(isolate, fractionDigits);

        if (double.IsNaN(valueNumber)) return ReadOnlyRoots.NaN_string;
        if (double.IsInfinity(valueNumber))
        {
            return valueNumber < 0.0 ? ReadOnlyRoots.minus_Infinity_string : ReadOnlyRoots.Infinity_string;
        }
        if (fractionDigitsNumber < 0.0 || fractionDigitsNumber > kMaxFractionDigits)
        {
            isolate.ThrowRangeError(MessageTemplate.NumberFormatRange, isolate.Factory.NewStringFromAsciiChecked("toExponential()"));
        }
        int f = fractionDigits.IsUndefined ? -1 : (int)fractionDigitsNumber;
        Span<char> buffer = stackalloc char[kDoubleToExponentialMaxChars];
        return isolate.Factory.NewStringFromUtf16(Conversions.DoubleToExponentialStringView(valueNumber, f, buffer));
    }

    /// <summary>ES6 section 20.1.3.3 Number.prototype.toFixed ( fractionDigits ).</summary>
    public static JSValue NumberPrototypeToFixed(Isolate isolate, in BuiltinArguments args)
    {
        JSValue fractionDigits = args.AtOrUndefined(1);

        // Unwrap the receiver {value}.
        double valueNumber = UnwrapReceiver(isolate, args.Receiver, "Number.prototype.toFixed");

        // Convert the {fraction_digits} to an integer first.
        double fractionDigitsNumber = ObjectOps.IntegerValue(isolate, fractionDigits);

        // Check if the {fraction_digits} are in the supported range.
        if (fractionDigitsNumber < 0.0 || fractionDigitsNumber > kMaxFractionDigits)
        {
            isolate.ThrowRangeError(MessageTemplate.NumberFormatRange, isolate.Factory.NewStringFromAsciiChecked("toFixed() digits"));
        }

        if (double.IsNaN(valueNumber)) return ReadOnlyRoots.NaN_string;
        if (double.IsInfinity(valueNumber))
        {
            return valueNumber < 0.0 ? ReadOnlyRoots.minus_Infinity_string : ReadOnlyRoots.Infinity_string;
        }
        Span<char> buffer = stackalloc char[kDoubleToFixedMaxChars];
        return isolate.Factory.NewStringFromUtf16(Conversions.DoubleToFixedStringView(valueNumber, (int)fractionDigitsNumber, buffer));
    }

    /// <summary>
    /// ES6 section 20.1.3.4 Number.prototype.toLocaleString ( [ r1 [ , r2 ] ] ),
    /// the build without V8_INTL_SUPPORT: ToString of the number.
    /// </summary>
    public static JSValue NumberPrototypeToLocaleString(Isolate isolate, in BuiltinArguments args)
    {
        const string methodName = "Number.prototype.toLocaleString";
        isolate.CountUsage("kNumberToLocaleString");

        // 1. Let x be ? thisNumberValue(this value)
        double value = UnwrapReceiver(isolate, args.Receiver, methodName);

        // Turn the {value} into a String.
        return isolate.Factory.NumberToString(JSValue.FromNumber(value));
    }

    /// <summary>ES6 section 20.1.3.5 Number.prototype.toPrecision ( precision ).</summary>
    public static JSValue NumberPrototypeToPrecision(Isolate isolate, in BuiltinArguments args)
    {
        JSValue precision = args.AtOrUndefined(1);

        // Unwrap the receiver {value}.
        double valueNumber = UnwrapReceiver(isolate, args.Receiver, "Number.prototype.toPrecision");

        // If no {precision} was specified, just return ToString of {value}.
        if (precision.IsUndefined) return isolate.Factory.NumberToString(JSValue.FromNumber(valueNumber));

        // Convert the {precision} to an integer first.
        double precisionNumber = ObjectOps.IntegerValue(isolate, precision);

        if (double.IsNaN(valueNumber)) return ReadOnlyRoots.NaN_string;
        if (double.IsInfinity(valueNumber))
        {
            return valueNumber < 0.0 ? ReadOnlyRoots.minus_Infinity_string : ReadOnlyRoots.Infinity_string;
        }
        if (precisionNumber < 1.0 || precisionNumber > kMaxFractionDigits)
        {
            isolate.ThrowRangeError(MessageTemplate.ToPrecisionFormatRange);
        }
        Span<char> buffer = stackalloc char[kDoubleToPrecisionMaxChars];
        return isolate.Factory.NewStringFromUtf16(Conversions.DoubleToPrecisionStringView(valueNumber, (int)precisionNumber, buffer));
    }

    // ---------------------------------------------------------------------
    // number.tq

    /// <summary>NumberPrototypeToString (https://tc39.es/ecma262/#sec-number.prototype.tostring).</summary>
    public static JSValue NumberPrototypeToString(Isolate isolate, in BuiltinArguments args)
    {
        // 1. Let x be ? thisNumberValue(this value).
        double x = ThisNumberValue(isolate, args.Receiver, "Number.prototype.toString");

        // 2.-4. Let radixNumber be 10 if radix is undefined, else ? ToInteger(radix).
        JSValue radix = args.AtOrUndefined(1);
        double radixNumber = radix.IsUndefined ? 10 : ObjectOps.ToInteger(isolate, radix).Number;

        // 5. If radixNumber < 2 or radixNumber > 36, throw a RangeError exception.
        if (radixNumber < 2 || radixNumber > 36)
        {
            isolate.ThrowRangeError(MessageTemplate.ToRadixFormatRange);
        }

        // 6. If radixNumber = 10, return ! ToString(x).
        if (radixNumber == 10) return isolate.Factory.NumberToString(JSValue.FromNumber(x));

        // 7. Return the String representation of this Number value using the
        //    radix specified by radixNumber.
        JSValue xValue = JSValue.FromNumber(x);
        if (xValue.IsSmi) return IntToString(isolate, (int)x, (uint)radixNumber);

        if (x == 0) return ReadOnlyRoots.zero_string;  // -0
        if (double.IsNaN(x)) return ReadOnlyRoots.NaN_string;
        if (x == double.PositiveInfinity) return ReadOnlyRoots.Infinity_string;
        if (x == double.NegativeInfinity) return ReadOnlyRoots.minus_Infinity_string;

        return DoubleToStringWithRadix(isolate, x, (int)radixNumber);
    }

    /// <summary>number.tq IntToString (radix != 10).</summary>
    public static JSString IntToString(Isolate isolate, int x, uint radix)
    {
        bool isNegative = x < 0;
        uint n;
        if (!isNegative)
        {
            // Fast case where the result is a one character string.
            n = (uint)x;
            if (n < radix)
            {
                if (n == 0) return ReadOnlyRoots.zero_string;
                return isolate.Factory.LookupSingleCharacterStringFromCode(ToCharCode(n));
            }
        }
        else
        {
            n = (uint)(0 - x);
        }

        // Calculate length and pre-allocate the result string.
        uint temp = n;
        int length = isNegative ? 1 : 0;
        while (temp > 0)
        {
            temp /= radix;
            length++;
        }
        Span<char> chars = stackalloc char[33];
        chars = chars[..length];
        int cursor = length - 1;
        while (n > 0)
        {
            uint digit = n % radix;
            n /= radix;
            chars[cursor--] = ToCharCode(digit);
        }
        if (isNegative) chars[0] = '-';
        return isolate.Factory.NewStringFromUtf16(chars);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static char ToCharCode(uint input) => input < 10 ? (char)(input + '0') : (char)(input - 10 + 'a');

    /// <summary>Runtime_DoubleToStringWithRadix.</summary>
    public static JSString DoubleToStringWithRadix(Isolate isolate, double number, int radix)
    {
        Span<char> buffer = stackalloc char[kDoubleToRadixMaxChars];
        return isolate.Factory.NewStringFromUtf16(Conversions.DoubleToRadixStringView(number, radix, buffer));
    }

    /// <summary>NumberIsFinite (https://tc39.es/ecma262/#sec-number.isfinite).</summary>
    public static JSValue NumberIsFinite(Isolate isolate, in BuiltinArguments args)
    {
        JSValue value = args.AtOrUndefined(1);
        if (!value.IsNumber) return JSValue.False;
        double number = value.Number;
        return JSValue.FromBoolean(!double.IsNaN(number - number));
    }

    /// <summary>NumberIsInteger (https://tc39.es/ecma262/#sec-number.isinteger).</summary>
    public static JSValue NumberIsInteger(Isolate isolate, in BuiltinArguments args) =>
        JSValue.FromBoolean(IsInteger(args.AtOrUndefined(1)));

    /// <summary>NumberIsNaN (https://tc39.es/ecma262/#sec-number.isnan).</summary>
    public static JSValue NumberIsNaN(Isolate isolate, in BuiltinArguments args)
    {
        JSValue value = args.AtOrUndefined(1);
        return JSValue.FromBoolean(value.IsNumber && double.IsNaN(value.Number));
    }

    /// <summary>NumberIsSafeInteger (https://tc39.es/ecma262/#sec-number.issafeinteger).</summary>
    public static JSValue NumberIsSafeInteger(Isolate isolate, in BuiltinArguments args) =>
        JSValue.FromBoolean(IsSafeInteger(args.AtOrUndefined(1)));

    /// <summary>CodeStubAssembler::IsInteger.</summary>
    public static bool IsInteger(JSValue value)
    {
        if (!value.IsNumber) return false;
        double number = value.Number;
        if (!double.IsFinite(number)) return false;
        return Math.Truncate(number) == number;
    }

    /// <summary>CodeStubAssembler::IsSafeInteger.</summary>
    public static bool IsSafeInteger(JSValue value)
    {
        if (!value.IsNumber) return false;
        double number = value.Number;
        if (!double.IsFinite(number)) return false;
        double integer = Math.Truncate(number);
        if (integer != number) return false;
        return Math.Abs(integer) <= EngineGlobals.kMaxSafeInteger;
    }

    /// <summary>NumberPrototypeValueOf (https://tc39.es/ecma262/#sec-number.prototype.valueof).</summary>
    public static JSValue NumberPrototypeValueOf(Isolate isolate, in BuiltinArguments args) =>
        JSValue.FromNumber(ThisNumberValue(isolate, args.Receiver, "Number.prototype.valueOf"));

    /// <summary>NumberParseFloat (https://tc39.es/ecma262/#sec-number.parsefloat).</summary>
    public static JSValue NumberParseFloat(Isolate isolate, in BuiltinArguments args)
    {
        JSValue value = args.AtOrUndefined(1);
        if (value.IsNumber)
        {
            // The input is already a Number. Take care of -0.
            // The sense of comparison is important for the NaN case.
            return value.Number == 0 ? JSValue.Zero : value;
        }
        JSString s = value.HeapObjectOrNull as JSString ?? ObjectOps.ToString(isolate, value);
        return JSValue.FromNumber(ParseFloat(s));
    }

    /// <summary>The String label of NumberParseFloat plus Runtime_StringParseFloat.</summary>
    public static double ParseFloat(JSString s)
    {
        // Check if the string is a cached array index.
        uint hash = s.RawHashField;
        if (Name.IsIntegerIndex(hash) && Name.ContainsCachedArrayIndex(hash))
        {
            return (hash >> Name.ArrayIndexValueShift) & Name.kArrayIndexValueMask;
        }
        // Fall back to the runtime to convert string to a number.
        return Conversions.StringToDouble(s.FlatSpan(), ConversionFlag.AllowTrailingJunk, double.NaN);
    }

    /// <summary>NumberParseInt (https://tc39.es/ecma262/#sec-number.parseint).</summary>
    public static JSValue NumberParseInt(Isolate isolate, in BuiltinArguments args) =>
        ParseInt(isolate, args.AtOrUndefined(1), args.AtOrUndefined(2));

    /// <summary>number.tq ParseInt.</summary>
    public static JSValue ParseInt(Isolate isolate, JSValue input, JSValue radix)
    {
        // Check if radix should be 10 (i.e. undefined, 0 or 10).
        if (radix.IsUndefined || (radix.IsSmi && radix.Number is 10 or 0))
        {
            if (input.IsSmi) return input;
            if (input.IsNumber)
            {
                // Check if the input value is in Signed32 range.
                double asFloat64 = input.Number;
                int asInt32 = Conversions.DoubleToInt32(asFloat64);
                // The sense of comparison is important for the NaN case.
                if (asFloat64 == asInt32) return JSValue.FromInt(asInt32);

                // Check if the absolute value of input is in the [1,1<<31[ range. Call
                // the runtime for the range [0,1[ because the result could be -0.
                const double kMaxAbsValue = 2147483648.0;
                double absInput = Math.Abs(asFloat64);
                if (absInput < kMaxAbsValue && absInput >= 1.0) return JSValue.FromInt(asInt32);
            }
            else if (input.HeapObjectOrNull is JSString s)
            {
                // Check if the string is a cached array index.
                uint hash = s.RawHashField;
                if (Name.IsIntegerIndex(hash) && Name.ContainsCachedArrayIndex(hash))
                {
                    return JSValue.FromNumber((hash >> Name.ArrayIndexValueShift) & Name.kArrayIndexValueMask);
                }
            }
        }
        return StringParseInt(isolate, input, radix);
    }

    /// <summary>Runtime_StringParseInt.</summary>
    public static JSValue StringParseInt(Isolate isolate, JSValue @string, JSValue radix)
    {
        // Convert {string} to a String first, and flatten it.
        JSString subject = ObjectOps.ToString(isolate, @string);

        // Convert {radix} to Int32.
        if (!radix.IsNumber) radix = ObjectOps.ToNumber(isolate, radix);
        int radix32 = Conversions.DoubleToInt32(radix.Number);
        if (radix32 != 0 && (radix32 < 2 || radix32 > 36)) return JSValue.NaN;

        return JSValue.FromNumber(Conversions.StringToInt(subject.FlatSpan(), radix32));
    }
}
