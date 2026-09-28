// Port of the static Object:: operations of src/objects/objects.cc,
// objects.h and objects-inl.h: the abstract operations (ToNumber, ToString,
// ToPropertyKey, ToObject ...), equality and comparison, typeof, instanceof,
// [[Get]] / [[Set]] through a LookupIterator (GetProperty, SetProperty,
// SetSuperProperty, SetDataProperty, AddDataProperty), accessor calls,
// SpeciesConstructor, hashing and the representation helpers used by maps.
//
// V8 declares these as static members of class Object. V8Sharp's values are
// the JSValue struct, so they live in this static class. Errors are thrown as
// JavaScriptException; Maybe<bool> operations return bool.
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using V8Sharp.Base.Numbers;
using V8Sharp.Common;
using V8Sharp.Roots;

namespace V8Sharp.Objects;

public static class ObjectOps
{
    // ---- ShouldThrow ------------------------------------------------------------------

    /// <summary>
    /// GetShouldThrow: the explicit value, or the language mode of the current
    /// context (V8 also walks to the topmost JavaScript frame's closure).
    /// </summary>
    public static ShouldThrow GetShouldThrow(Isolate isolate, ShouldThrow? shouldThrow)
    {
        if (shouldThrow.HasValue) return shouldThrow.Value;

        LanguageMode mode = isolate.Context is { } context ? context.ScopeInfo.LanguageMode : LanguageMode.Sloppy;
        if (mode == LanguageMode.Strict) return ShouldThrow.ThrowOnError;

        if (isolate.Frames is { } frames && frames.TryGetFrame(0, out JavaScriptFrameSummary frame))
        {
            // Get the language mode from closure.
            if (frame.Function.Shared.LanguageMode > mode) mode = frame.Function.Shared.LanguageMode;
        }
        return mode == LanguageMode.Sloppy ? ShouldThrow.DontThrow : ShouldThrow.ThrowOnError;
    }

    /// <summary>
    /// V8's RETURN_FAILURE macro: throws a TypeError built from the template
    /// when <paramref name="shouldThrow"/> is ThrowOnError, otherwise returns false.
    /// </summary>
    public static bool ReturnFailure(Isolate isolate, ShouldThrow shouldThrow, MessageTemplate template,
        params ReadOnlySpan<JSValue> args)
    {
        if (shouldThrow == ShouldThrow.ThrowOnError) isolate.Throw(isolate.Factory.NewTypeError(template, args));
        return false;
    }

    // ---- Predicates -------------------------------------------------------------------

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsPrimitive(in JSValue value) => !value.IsJSReceiver;

    /// <summary>IsCallable: the map's is_callable bit.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsCallable(in JSValue value) => AsReceiverOrNull(value) is { } r && r.Map.IsCallable;

    /// <summary>
    /// The value as a JSReceiver, or null. The instance type range test, where
    /// `is JSReceiver` on the abstract class is a cast helper call.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static JSReceiver? AsReceiverOrNull(in JSValue value)
    {
        HeapObject? o = value._obj;
        return o is not null && o.InstanceType >= InstanceTypeChecks.FirstJSReceiver ? Unsafe.As<JSReceiver>(o) : null;
    }

    /// <summary>IsConstructor: the map's is_constructor bit.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsConstructor(in JSValue value) => AsReceiverOrNull(value) is { } r && r.Map.IsConstructor;

    /// <summary>IsUndetectable (document.all-like objects).</summary>
    public static bool IsUndetectable(in JSValue value) => value.HeapObjectOrNull is JSReceiver r && r.Map.IsUndetectable;

    /// <summary>IsStringWrapper: a JSPrimitiveWrapper around a string.</summary>
    public static bool IsStringWrapper(in JSValue value) =>
        value.HeapObjectOrNull is JSPrimitiveWrapper w && w.Value.IsString;

    /// <summary>IsSpecialIndex (lookup.cc): a string key that JSTypedArray treats as a canonical numeric string.</summary>
    public static bool IsSpecialIndex(JSString str)
    {
        // Maximum length of a canonical double: -X.XXXXXXXXXXXXXXXXXe-XXX
        const int kBufferSize = 24;
        int length = str.Length;
        if (length == 0 || length > kBufferSize) return false;
        ReadOnlySpan<char> chars = str.FlatSpan();
        // Compare text directly: "Infinity", "-Infinity", "NaN", "-0".
        if (chars.SequenceEqual("Infinity") || chars.SequenceEqual("-Infinity") || chars.SequenceEqual("NaN") ||
            chars.SequenceEqual("-0"))
        {
            return true;
        }
        char c = chars[0];
        if (c != '-' && (c < '0' || c > '9')) return false;
        double d = Conversions.StringToDouble(chars, ConversionFlag.NoConversionFlag);
        if (double.IsNaN(d)) return false;
        // Compare to the canonical string form (NumberToString).
        string canonical = Conversions.DoubleToCString(d);
        return chars.SequenceEqual(canonical);
    }

    /// <summary>Object::IsArray (ES #sec-isarray).</summary>
    public static bool IsArray(Isolate isolate, JSValue obj)
    {
        HeapObject? heapObject = obj.HeapObjectOrNull;
        if (heapObject is JSArray) return true;
        if (heapObject is not JSProxy proxy) return false;
        return JSProxy.IsArray(isolate, proxy);
    }

    /// <summary>Object::FilterKey: whether <paramref name="obj"/> is excluded by <paramref name="filter"/>.</summary>
    public static bool FilterKey(in JSValue obj, PropertyFilter filter)
    {
        if (filter == PropertyFilter.PRIVATE_NAMES_ONLY)
        {
            if (obj.HeapObjectOrNull is not Symbol s) return true;
            return !s.IsAnyPrivateName;
        }
        if (obj.HeapObjectOrNull is Symbol symbol)
        {
            if ((filter & PropertyFilter.SKIP_SYMBOLS) != 0) return true;
            if (symbol.IsAnyPrivate) return true;
        }
        else if ((filter & PropertyFilter.SKIP_STRINGS) != 0)
        {
            return true;
        }
        return false;
    }

    // ---- Representations ---------------------------------------------------------------

    /// <summary>Object::OptimalRepresentation.</summary>
    public static (Representation, PropertyConstness) OptimalRepresentation(in JSValue obj, PropertyConstness constness)
    {
        if (obj.IsSmi) return (Representation.Smi, constness);
        if (ReferenceEquals(obj.HeapObjectOrNull, Oddball.Uninitialized)) return (Representation.None, constness);
        if (obj.IsNumber)
        {
            if (constness == PropertyConstness.Const && FixedDoubleArray.IsHoleBits(obj.Number))
            {
                // Make sure that even an initializing store of a double value with
                // the hole NaN pattern removes constness, otherwise it wouldn't be
                // possible to distinguish whether subsequent stores to a double field
                // is initializing or not.
                constness = PropertyConstness.Mutable;
            }
            return (Representation.Double, constness);
        }
        return (Representation.HeapObject, constness);
    }

    /// <summary>Object::OptimalElementsKind.</summary>
    public static ElementsKind OptimalElementsKind(in JSValue obj)
    {
        if (obj.IsSmi) return ElementsKind.PACKED_SMI_ELEMENTS;
        if (obj.IsNumber) return ElementsKind.PACKED_DOUBLE_ELEMENTS;
        return ElementsKind.PACKED_ELEMENTS;
    }

    /// <summary>Object::FitsRepresentation.</summary>
    public static bool FitsRepresentation(in JSValue obj, Representation representation, bool allowCoercion = true)
    {
        if (representation.IsSmi) return obj.IsSmi;
        if (representation.IsDouble) return allowCoercion ? obj.IsNumber : obj.IsNumber && !obj.IsSmi;
        // Deviation: V8 returns IsHeapObject(obj), which holds for a HeapNumber.
        // JSValue numbers are unboxed, so a non-Smi number does not fit HeapObject
        // and storing one generalizes the field to Tagged instead.
        if (representation.IsHeapObject) return !obj.IsNumber;
        if (representation.IsNone) return false;
        return true;
    }

    /// <summary>Object::OptimalType.</summary>
    public static HeapObject OptimalType(in JSValue obj, Isolate isolate, Representation representation)
    {
        if (representation.IsNone) return FieldType.None;
        if (isolate.Flags.track_field_types)
        {
            if (representation.IsHeapObject && obj.HeapObjectOrNull is JSReceiver receiver)
            {
                // We can track only JavaScript objects with stable maps.
                Map map = receiver.Map;
                if (map.IsStable && Map.IsJSReceiverMap(map)) return FieldType.Class(map);
            }
        }
        return FieldType.Any;
    }

    // ---- Number helpers ---------------------------------------------------------------

    /// <summary>DoubleToInteger (conversions-inl.h): ToIntegerOrInfinity on a double.</summary>
    public static double DoubleToInteger(double x)
    {
        // ToIntegerOrInfinity normalizes -0 to +0. Special case 0 for performance.
        if (double.IsNaN(x) || x == 0) return 0;
        if (!double.IsFinite(x)) return x;
        // Add 0.0 in the truncation case to ensure this doesn't return -0.
        return (x > 0 ? Math.Floor(x) : Math.Ceiling(x)) + 0.0;
    }

    /// <summary>FastD2IChecked: saturating double to int.</summary>
    public static int FastD2IChecked(double x)
    {
        if (!(x >= int.MinValue)) return int.MinValue;  // Negation to catch NaNs.
        if (x > int.MaxValue) return int.MaxValue;
        return (int)x;
    }

    /// <summary>Object::NumberValue.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static double NumberValue(in JSValue number) => number.Number;

    /// <summary>Object::SameNumberValue: bitwise equal, or both NaN.</summary>
    public static bool SameNumberValue(double value1, double value2)
    {
        // Compare values bitwise, to cover -0 being different from 0 -- we'd need to
        // look at sign bits anyway if we'd done a double comparison, so we may as
        // well compare bitwise immediately.
        if (BitConverter.DoubleToInt64Bits(value1) == BitConverter.DoubleToInt64Bits(value2)) return true;
        // SameNumberValue(NaN, NaN) is true even for NaNs with different bit
        // representations.
        return double.IsNaN(value1) && double.IsNaN(value2);
    }

    /// <summary>Object::ToArrayLength: a number that is a valid array length.</summary>
    public static bool ToArrayLength(in JSValue obj, out uint index) => ToUint32(obj, out index);

    /// <summary>Object::ToArrayIndex: a number that is a valid array index.</summary>
    public static bool ToArrayIndex(in JSValue obj, out uint index) => ToUint32(obj, out index) && index != uint.MaxValue;

    /// <summary>Object::ToUint32(obj, &amp;value): an integral number in uint32 range.</summary>
    public static bool ToUint32(in JSValue obj, out uint value)
    {
        value = 0;
        if (!obj.IsNumber) return false;
        double num = obj.Number;
        // Check range before conversion to avoid undefined behavior.
        if (num >= 0 && num <= uint.MaxValue)
        {
            uint u = (uint)num;
            if (u == num)
            {
                value = u;
                return true;
            }
        }
        return false;
    }

    /// <summary>Object::ToInt32(obj, &amp;value): an integral number in int32 range.</summary>
    public static bool ToInt32(in JSValue obj, out int value)
    {
        value = 0;
        if (!obj.IsNumber) return false;
        double num = obj.Number;
        if (num >= int.MinValue && num <= int.MaxValue && (int)num == num)
        {
            value = (int)num;
            return true;
        }
        return false;
    }

    /// <summary>Object::ToIntegerIndex: an integral number usable as a typed-array or element index.</summary>
    public static bool ToIntegerIndex(in JSValue obj, out ulong index)
    {
        index = 0;
        if (!obj.IsNumber) return false;
        double num = obj.Number;
        // Check range before conversion to avoid undefined behavior.
        if (num >= 0 && num <= EngineGlobals.kMaxSafeInteger)
        {
            ulong u = (ulong)num;
            if (u == num)
            {
                index = u;
                return true;
            }
        }
        return false;
    }

    // ---- Conversions ---------------------------------------------------------------

    /// <summary>Object::ToObject.</summary>
    public static JSReceiver ToObject(Isolate isolate, JSValue obj, string? methodName = null)
    {
        if (obj.HeapObjectOrNull is JSReceiver receiver) return receiver;
        return ToObjectImpl(isolate, obj, methodName);
    }

    /// <summary>Object::ToObjectImpl: wraps a primitive in its wrapper object.</summary>
    public static JSReceiver ToObjectImpl(Isolate isolate, JSValue obj, string? methodName = null) =>
        ToObjectImpl(isolate, obj, isolate.NativeContext, methodName);

    /// <summary>Object::ToObjectImpl with an explicit native context (the callee's, for sloppy receivers).</summary>
    public static JSReceiver ToObjectImpl(Isolate isolate, JSValue obj, NativeContext nativeContext, string? methodName = null)
    {
        Debug.Assert(!obj.IsJSReceiver);
        JSFunction constructor;
        if (obj.IsNumber)
        {
            constructor = nativeContext.NumberFunction;
        }
        else
        {
            HeapObject? heapObject = obj.HeapObjectOrNull;
            switch (heapObject)
            {
                case JSString:
                    constructor = nativeContext.StringFunction;
                    break;
                case Symbol:
                    constructor = nativeContext.SymbolFunction;
                    break;
                case BigInt:
                    constructor = nativeContext.BigIntFunction;
                    break;
                case Oddball o when o.Kind is Oddball.OddballKind.True or Oddball.OddballKind.False:
                    constructor = nativeContext.BooleanFunction;
                    break;
                default:
                    // undefined, null and the holes have no constructor function index.
                    if (methodName is not null)
                    {
                        isolate.Throw(isolate.Factory.NewTypeError(MessageTemplate.CalledOnNullOrUndefined,
                            isolate.Factory.NewStringFromAsciiChecked(methodName)));
                    }
                    isolate.Throw(isolate.Factory.NewTypeError(MessageTemplate.UndefinedOrNullToObject));
                    return null!;
            }
        }
        JSObject result = isolate.Factory.NewJSObject(constructor);
        ((JSPrimitiveWrapper)result).Value = obj;
        return result;
    }

    /// <summary>Object::ConvertReceiver: OrdinaryCallBindThis for a sloppy callee (ES6 9.2.1.2).</summary>
    public static JSReceiver ConvertReceiver(Isolate isolate, JSValue obj)
    {
        if (obj.HeapObjectOrNull is JSReceiver receiver) return receiver;
        if (obj.IsNullOrUndefined) return isolate.NativeContext.GlobalProxyObject;
        return ToObject(isolate, obj);
    }

    /// <summary>Object::ToPrimitive.</summary>
    public static JSValue ToPrimitive(Isolate isolate, JSValue input, ToPrimitiveHint hint = ToPrimitiveHint.Default)
    {
        if (input.HeapObjectOrNull is not JSReceiver receiver) return input;
        return JSReceiver.ToPrimitive(isolate, receiver, hint);
    }

    /// <summary>Object::ToNumber.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static JSValue ToNumber(Isolate isolate, JSValue input)
    {
        if (input.IsNumber) return input;  // Shortcut.
        return ConvertToNumber(isolate, input);
    }

    /// <summary>ToNumber, returning the double.</summary>
    public static double ToNumberValue(Isolate isolate, JSValue input) => ToNumber(isolate, input).Number;

    /// <summary>Object::ConvertToNumber.</summary>
    public static JSValue ConvertToNumber(Isolate isolate, JSValue input)
    {
        while (true)
        {
            if (input.IsNumber) return input;
            if (input.IsUndefined) return JSValue.NaN;
            HeapObject obj = input.Object;
            switch (obj)
            {
                case JSString s:
                    return JSValue.FromNumber(JSString.ToNumber(s));
                case Oddball o:
                    return JSValue.FromNumber(o.ToNumberValue);
                case Symbol:
                    return isolate.Throw(isolate.Factory.NewTypeError(MessageTemplate.SymbolToNumber));
                case BigInt:
                    return isolate.Throw(isolate.Factory.NewTypeError(MessageTemplate.BigIntToNumber));
            }
            input = JSReceiver.ToPrimitive(isolate, (JSReceiver)obj, ToPrimitiveHint.Number);
        }
    }

    /// <summary>Object::ToNumeric.</summary>
    public static JSValue ToNumeric(Isolate isolate, JSValue input)
    {
        if (input.IsNumber || input.IsBigInt) return input;  // Shortcut.
        return ConvertToNumeric(isolate, input);
    }

    /// <summary>Object::ConvertToNumeric.</summary>
    public static JSValue ConvertToNumeric(Isolate isolate, JSValue input)
    {
        while (true)
        {
            if (input.IsNumber) return input;
            if (input.IsUndefined) return JSValue.NaN;
            HeapObject obj = input.Object;
            switch (obj)
            {
                case JSString s:
                    return JSValue.FromNumber(JSString.ToNumber(s));
                case Oddball o:
                    return JSValue.FromNumber(o.ToNumberValue);
                case Symbol:
                    return isolate.Throw(isolate.Factory.NewTypeError(MessageTemplate.SymbolToNumber));
                case BigInt:
                    return input;
            }
            input = JSReceiver.ToPrimitive(isolate, (JSReceiver)obj, ToPrimitiveHint.Number);
        }
    }

    /// <summary>Object::ToInteger (ToIntegerOrInfinity).</summary>
    public static JSValue ToInteger(Isolate isolate, JSValue input)
    {
        if (input.IsSmi) return input;
        input = ConvertToNumber(isolate, input);
        if (input.IsSmi) return input;
        return JSValue.FromNumber(DoubleToInteger(input.Number));
    }

    /// <summary>Object::IntegerValue: ToIntegerOrInfinity as a double.</summary>
    public static double IntegerValue(Isolate isolate, JSValue input) =>
        DoubleToInteger(ToNumber(isolate, input).Number);

    /// <summary>Object::ToInt32.</summary>
    public static JSValue ToInt32(Isolate isolate, JSValue input)
    {
        if (input.IsSmi) return input;
        input = ConvertToNumber(isolate, input);
        return JSValue.FromInt(Conversions.DoubleToInt32(input.Number));
    }

    /// <summary>Object::ToUint32.</summary>
    public static JSValue ToUint32(Isolate isolate, JSValue input)
    {
        input = ToNumber(isolate, input);
        return JSValue.FromNumber(Conversions.DoubleToUint32(input.Number));
    }

    /// <summary>Object::ToName.</summary>
    public static Name ToName(Isolate isolate, JSValue input)
    {
        if (input.HeapObjectOrNull is Name name) return name;
        return ConvertToName(isolate, input);
    }

    /// <summary>Object::ConvertToName.</summary>
    public static Name ConvertToName(Isolate isolate, JSValue input)
    {
        input = ToPrimitive(isolate, input, ToPrimitiveHint.String);
        if (input.HeapObjectOrNull is Name name) return name;
        return ToString(isolate, input);
    }

    /// <summary>Object::ToPropertyKey: a Name, or a Smi-valued number (an element index).</summary>
    public static JSValue ToPropertyKey(Isolate isolate, JSValue value)
    {
        if (value.IsSmi || value.HeapObjectOrNull is Name) return value;
        return ConvertToPropertyKey(isolate, value);
    }

    /// <summary>Object::ConvertToPropertyKey (ES6 7.1.14).</summary>
    public static JSValue ConvertToPropertyKey(Isolate isolate, JSValue value)
    {
        // 1. Let key be ToPrimitive(argument, hint String).
        JSValue key = ToPrimitive(isolate, value, ToPrimitiveHint.String);
        // 3. If Type(key) is Symbol, then return key.
        if (key.IsSymbol) return key;
        // 4. Return ToString(key).
        // Extending spec'ed behavior, we'd be happy to return an element index.
        if (key.IsSmi) return key;
        if (key.IsNumber)
        {
            if (ToArrayLength(value, out uint uintValue) && uintValue <= (uint)JSValue.SmiMaxValue)
            {
                return JSValue.FromInt((int)uintValue);
            }
        }
        return ToString(isolate, key);
    }

    /// <summary>Object::ToString.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static JSString ToString(Isolate isolate, JSValue input)
    {
        if (input.HeapObjectOrNull is JSString s) return s;
        return ConvertToString(isolate, input);
    }

    /// <summary>Object::ConvertToString.</summary>
    public static JSString ConvertToString(Isolate isolate, JSValue input)
    {
        while (true)
        {
            if (input.IsUndefined) return ReadOnlyRoots.undefined_string;
            if (input.IsNumber) return isolate.Factory.NumberToString(input);
            HeapObject obj = input.Object;
            switch (obj)
            {
                case Oddball o:
                    return OddballToString(o);
                case Symbol:
                    isolate.Throw(isolate.Factory.NewTypeError(MessageTemplate.SymbolToString));
                    return null!;
                case BigInt b:
                    return BigInt.ToString(isolate, b);
                case JSString s:
                    return s;
            }
            input = JSReceiver.ToPrimitive(isolate, (JSReceiver)obj, ToPrimitiveHint.String);
            // The previous isString() check happened in Object::ToString and thus we
            // put it at the end of the loop in this helper.
            if (input.HeapObjectOrNull is JSString str) return str;
        }
    }

    /// <summary>Oddball::to_string.</summary>
    public static JSString OddballToString(Oddball o) => o.Kind switch
    {
        Oddball.OddballKind.Null => ReadOnlyRoots.null_string,
        Oddball.OddballKind.True => ReadOnlyRoots.true_string,
        Oddball.OddballKind.False => ReadOnlyRoots.false_string,
        _ => ReadOnlyRoots.undefined_string,
    };

    /// <summary>Oddball::type_of.</summary>
    static JSString OddballTypeOf(Oddball o) => o.Kind switch
    {
        Oddball.OddballKind.Null => ReadOnlyRoots.object_string,
        Oddball.OddballKind.True or Oddball.OddballKind.False => ReadOnlyRoots.boolean_string,
        _ => ReadOnlyRoots.undefined_string,
    };

    /// <summary>Object::ToLength.</summary>
    public static JSValue ToLength(Isolate isolate, JSValue input)
    {
        if (input.IsSmi) return JSValue.FromInt(Math.Max((int)input.Number, 0));
        return ConvertToLength(isolate, input);
    }

    /// <summary>Object::ConvertToLength.</summary>
    public static JSValue ConvertToLength(Isolate isolate, JSValue input)
    {
        input = ToNumber(isolate, input);
        if (input.IsSmi) return JSValue.FromInt(Math.Max((int)input.Number, 0));
        double len = DoubleToInteger(input.Number);
        if (len <= 0.0)
        {
            return JSValue.Zero;
        }
        else if (len >= EngineGlobals.kMaxSafeInteger)
        {
            len = EngineGlobals.kMaxSafeInteger;
        }
        return JSValue.FromNumber(len);
    }

    /// <summary>Object::ToIndex.</summary>
    public static JSValue ToIndex(Isolate isolate, JSValue input, MessageTemplate errorIndex)
    {
        if (input.IsSmi && input.Number >= 0) return input;
        return ConvertToIndex(isolate, input, errorIndex);
    }

    /// <summary>Object::ConvertToIndex.</summary>
    public static JSValue ConvertToIndex(Isolate isolate, JSValue input, MessageTemplate errorIndex)
    {
        if (input.IsUndefined) return JSValue.Zero;
        input = ToNumber(isolate, input);
        if (input.IsSmi && input.Number >= 0) return input;
        double len = DoubleToInteger(input.Number);
        JSValue jsLen = JSValue.FromNumber(len);
        if (len < 0.0 || len > EngineGlobals.kMaxSafeInteger)
        {
            isolate.Throw(isolate.Factory.NewRangeError(errorIndex, jsLen));
        }
        return jsLen;
    }

    /// <summary>Object::BooleanValue (ToBoolean).</summary>
    public static bool BooleanValue(in JSValue obj)
    {
        if (obj.IsNumber)
        {
            double d = obj.Number;
            // DoubleToBoolean: false for 0, -0 and NaN.
            return d != 0 && !double.IsNaN(d);
        }
        HeapObject? o = obj.HeapObjectOrNull;
        switch (o)
        {
            case null:
                return false;
            case Oddball oddball:
                return oddball.Kind == Oddball.OddballKind.True;
            case JSString s:
                return s.Length != 0;
            case BigInt b:
                return !b.IsZero;
            case JSReceiver r:
                return !r.Map.IsUndetectable;  // Undetectable object is false.
            default:
                return true;
        }
    }

    /// <summary>Object::BooleanValue(obj, isolate).</summary>
    public static bool BooleanValue(in JSValue obj, Isolate isolate) => BooleanValue(obj);

    /// <summary>Object::ToBoolean.</summary>
    public static JSValue ToBoolean(in JSValue obj) => obj.IsBoolean ? obj : JSValue.FromBoolean(BooleanValue(obj));

    // ---- Equality and comparison ------------------------------------------------------

    static ComparisonResult StrictNumberCompare(double x, double y)
    {
        if (double.IsNaN(x) || double.IsNaN(y)) return ComparisonResult.Undefined;
        if (x < y) return ComparisonResult.LessThan;
        if (x > y) return ComparisonResult.GreaterThan;
        return ComparisonResult.Equal;
    }

    // See Number case of ES6#sec-strict-equality-comparison
    // Returns false if x or y is NaN, treats -0.0 as equal to 0.0.
    static bool StrictNumberEquals(double x, double y)
    {
        // Must check explicitly for NaN's on Windows, but -0 works fine.
        if (double.IsNaN(x) || double.IsNaN(y)) return false;
        return x == y;
    }

    static ComparisonResult Reverse(ComparisonResult result) => result switch
    {
        ComparisonResult.LessThan => ComparisonResult.GreaterThan,
        ComparisonResult.GreaterThan => ComparisonResult.LessThan,
        _ => result,
    };

    /// <summary>Object::Compare (ES6 7.2.11 Abstract Relational Comparison).</summary>
    public static ComparisonResult Compare(Isolate isolate, JSValue x, JSValue y)
    {
        // ES6 section 7.2.11 Abstract Relational Comparison step 3 and 4.
        x = ToPrimitive(isolate, x, ToPrimitiveHint.Number);
        y = ToPrimitive(isolate, y, ToPrimitiveHint.Number);
        if (x.HeapObjectOrNull is JSString xs && y.HeapObjectOrNull is JSString ys)
        {
            // ES6 section 7.2.11 Abstract Relational Comparison step 5.
            return JSString.Compare(xs, ys);
        }
        if (x.HeapObjectOrNull is BigInt xb && y.HeapObjectOrNull is JSString ys2)
        {
            return BigInt.CompareToString(isolate, xb, ys2);
        }
        if (x.HeapObjectOrNull is JSString xs2 && y.HeapObjectOrNull is BigInt yb)
        {
            return Reverse(BigInt.CompareToString(isolate, yb, xs2));
        }
        // ES6 section 7.2.11 Abstract Relational Comparison step 6.
        x = ToNumeric(isolate, x);
        y = ToNumeric(isolate, y);

        bool xIsNumber = x.IsNumber;
        bool yIsNumber = y.IsNumber;
        if (xIsNumber && yIsNumber) return StrictNumberCompare(x.Number, y.Number);
        if (!xIsNumber && !yIsNumber) return BigInt.CompareToBigInt(x.As<BigInt>(), y.As<BigInt>());
        if (xIsNumber) return Reverse(BigInt.CompareToNumber(y.As<BigInt>(), x));
        return BigInt.CompareToNumber(x.As<BigInt>(), y);
    }

    /// <summary>Object::Equals (ES6 7.2.12 Abstract Equality Comparison).</summary>
    public static bool Equals(Isolate isolate, JSValue x, JSValue y)
    {
        // This is the generic version of Abstract Equality Comparison. Must be in
        // sync with CodeStubAssembler::Equal.
        while (true)
        {
            if (x.IsNumber)
            {
                if (y.IsNumber) return StrictNumberEquals(x.Number, y.Number);
                if (y.IsBoolean) return StrictNumberEquals(x.Number, y.IsTrue ? 1 : 0);
                if (y.HeapObjectOrNull is JSString ys) return StrictNumberEquals(x.Number, JSString.ToNumber(ys));
                if (y.HeapObjectOrNull is BigInt yb) return BigInt.EqualToNumber(yb, x);
                if (y.HeapObjectOrNull is JSReceiver yr)
                {
                    y = JSReceiver.ToPrimitive(isolate, yr);
                }
                else
                {
                    return false;
                }
            }
            else if (x.HeapObjectOrNull is JSString xs)
            {
                if (y.HeapObjectOrNull is JSString ys) return JSString.Equals(xs, ys);
                if (y.IsNumber) return StrictNumberEquals(JSString.ToNumber(xs), y.Number);
                if (y.IsBoolean) return StrictNumberEquals(JSString.ToNumber(xs), y.IsTrue ? 1 : 0);
                if (y.HeapObjectOrNull is BigInt yb) return BigInt.EqualToString(isolate, yb, xs);
                if (y.HeapObjectOrNull is JSReceiver yr)
                {
                    y = JSReceiver.ToPrimitive(isolate, yr);
                }
                else
                {
                    return false;
                }
            }
            else if (x.IsBoolean)
            {
                double xn = x.IsTrue ? 1 : 0;
                if (y.IsOddball || y.IsUndefined) return x.IsIdenticalTo(y);
                if (y.IsNumber) return StrictNumberEquals(xn, y.Number);
                if (y.HeapObjectOrNull is JSString ys) return StrictNumberEquals(xn, JSString.ToNumber(ys));
                if (y.HeapObjectOrNull is BigInt yb) return BigInt.EqualToNumber(yb, JSValue.FromNumber(xn));
                if (y.HeapObjectOrNull is JSReceiver yr)
                {
                    y = JSReceiver.ToPrimitive(isolate, yr);
                    x = JSValue.FromNumber(xn);
                }
                else
                {
                    return false;
                }
            }
            else if (x.IsSymbol)
            {
                if (y.IsSymbol) return x.IsIdenticalTo(y);
                if (y.HeapObjectOrNull is JSReceiver yr)
                {
                    y = JSReceiver.ToPrimitive(isolate, yr);
                }
                else
                {
                    return false;
                }
            }
            else if (x.HeapObjectOrNull is BigInt xb)
            {
                if (y.HeapObjectOrNull is BigInt yb) return BigInt.EqualToBigInt(xb, yb);
                return Equals(isolate, y, x);
            }
            else if (x.HeapObjectOrNull is JSReceiver xr)
            {
                if (y.IsJSReceiver) return x.IsIdenticalTo(y);
                // V8: null and undefined are undetectable oddballs.
                if (IsUndetectableOrNullish(y)) return IsUndetectable(x);
                if (y.IsBoolean)
                {
                    y = JSValue.FromNumber(y.IsTrue ? 1 : 0);
                }
                else
                {
                    x = JSReceiver.ToPrimitive(isolate, xr);
                }
            }
            else
            {
                // x is undefined or null (V8: both are undetectable oddballs).
                return IsUndetectableOrNullish(x) && IsUndetectableOrNullish(y);
            }
        }
    }

    static bool IsUndetectableOrNullish(in JSValue v) => v.IsNullOrUndefined || IsUndetectable(v);

    /// <summary>Object::StrictEquals (===).</summary>
    public static bool StrictEquals(in JSValue obj, in JSValue that)
    {
        if (obj.IsNumber)
        {
            if (!that.IsNumber) return false;
            return StrictNumberEquals(obj.Number, that.Number);
        }
        if (obj.HeapObjectOrNull is JSString s)
        {
            if (that.HeapObjectOrNull is not JSString t) return false;
            return JSString.Equals(s, t);
        }
        if (obj.HeapObjectOrNull is BigInt b)
        {
            if (that.HeapObjectOrNull is not BigInt c) return false;
            return BigInt.EqualToBigInt(b, c);
        }
        return obj.IsIdenticalTo(that);
    }

    /// <summary>Object::SameValue.</summary>
    public static bool SameValue(in JSValue obj, in JSValue other)
    {
        if (other.IsIdenticalTo(obj)) return true;
        if (obj.IsNumber && other.IsNumber) return SameNumberValue(obj.Number, other.Number);
        if (obj.HeapObjectOrNull is JSString s && other.HeapObjectOrNull is JSString t) return JSString.Equals(s, t);
        if (obj.HeapObjectOrNull is BigInt b && other.HeapObjectOrNull is BigInt c) return BigInt.EqualToBigInt(b, c);
        return false;
    }

    /// <summary>Object::SameValueZero.</summary>
    public static bool SameValueZero(in JSValue obj, in JSValue other)
    {
        if (other.IsIdenticalTo(obj)) return true;
        if (obj.IsNumber && other.IsNumber)
        {
            double thisValue = obj.Number;
            double otherValue = other.Number;
            // +0 == -0 is true
            return thisValue == otherValue || (double.IsNaN(thisValue) && double.IsNaN(otherValue));
        }
        if (obj.HeapObjectOrNull is JSString s && other.HeapObjectOrNull is JSString t) return JSString.Equals(s, t);
        if (obj.HeapObjectOrNull is BigInt b && other.HeapObjectOrNull is BigInt c) return BigInt.EqualToBigInt(b, c);
        return false;
    }

    /// <summary>Object::TypeOf.</summary>
    public static JSString TypeOf(Isolate isolate, in JSValue obj)
    {
        if (obj.IsNumber) return ReadOnlyRoots.number_string;
        HeapObject? o = obj.HeapObjectOrNull;
        switch (o)
        {
            case null:
                return ReadOnlyRoots.undefined_string;
            case Oddball oddball:
                return OddballTypeOf(oddball);
            case JSString:
                return ReadOnlyRoots.string_string;
            case Symbol:
                return ReadOnlyRoots.symbol_string;
            case BigInt:
                return ReadOnlyRoots.bigint_string;
            case JSReceiver r:
                if (r.Map.IsUndetectable) return ReadOnlyRoots.undefined_string;
                if (r.Map.IsCallable) return ReadOnlyRoots.function_string;
                return ReadOnlyRoots.object_string;
            default:
                return ReadOnlyRoots.object_string;
        }
    }

    /// <summary>Object::Add (the generic + of the runtime).</summary>
    public static JSValue Add(Isolate isolate, JSValue lhs, JSValue rhs)
    {
        if (lhs.IsNumber && rhs.IsNumber) return JSValue.FromNumber(lhs.Number + rhs.Number);
        if (lhs.HeapObjectOrNull is JSString ls && rhs.HeapObjectOrNull is JSString rs)
        {
            return isolate.Factory.NewConsString(ls, rs);
        }
        lhs = ToPrimitive(isolate, lhs);
        rhs = ToPrimitive(isolate, rhs);
        if (lhs.IsString || rhs.IsString)
        {
            JSString r = ToString(isolate, rhs);
            JSString l = ToString(isolate, lhs);
            return isolate.Factory.NewConsString(l, r);
        }
        JSValue rhsNumber = ToNumber(isolate, rhs);
        JSValue lhsNumber = ToNumber(isolate, lhs);
        return JSValue.FromNumber(lhsNumber.Number + rhsNumber.Number);
    }

    // ---- instanceof ------------------------------------------------------------------

    /// <summary>Object::OrdinaryHasInstance (ES #sec-ordinaryhasinstance).</summary>
    public static bool OrdinaryHasInstance(Isolate isolate, JSValue callable, JSValue obj)
    {
        // The {callable} must have a [[Call]] internal method.
        if (!IsCallable(callable)) return false;

        // Check if {callable} is a bound function, and if so retrieve its
        // [[BoundTargetFunction]] and use that instead of {callable}.
        if (callable.HeapObjectOrNull is JSBoundFunction bound)
        {
            // Since there is a mutual recursion here, we might run out of stack
            // space for long chains of bound functions.
            isolate.StackGuard.StackCheck(isolate);
            return InstanceOf(isolate, obj, bound.BoundTargetFunction);
        }

        // If {object} is not a receiver, return false.
        if (AsReceiverOrNull(obj) is not { } receiver) return false;

        // CodeStubAssembler::OrdinaryHasInstance: for a JSFunction whose
        // "prototype" is the function prototype accessor, the prototype comes
        // from prototype_or_initial_map, and the chain walk loads map prototypes
        // until it meets a receiver that needs the full [[GetPrototypeOf]].
        if (callable.HeapObjectOrNull is JSFunction function && !function.Map.IsDictionaryMap)
        {
            Map functionMap = function.Map;
            DescriptorArray descriptors = functionMap.InstanceDescriptors;
            InternalIndex index = descriptors.Search(ReadOnlyRoots.prototype_string, functionMap);
            JSReceiver? functionPrototype = function.PrototypeOrInitialMap switch
            {
                Map initialMap => initialMap.Prototype,
                JSReceiver instancePrototype => instancePrototype,
                _ => null,
            };
            if (functionPrototype is not null && index.IsFound &&
                ReferenceEquals(descriptors.GetStrongValue(index).HeapObjectOrNull, Builtins.Accessors.FunctionPrototypeAccessor))
            {
                JSReceiver current = receiver;
                while (!Map.IsSpecialReceiverMap(current.Map))
                {
                    JSReceiver? next = current.Map.Prototype;
                    if (next is null) return false;
                    if (ReferenceEquals(next, functionPrototype)) return true;
                    current = next;
                }
                return JSReceiver.HasInPrototypeChain(isolate, current, functionPrototype);
            }
        }

        // Get the "prototype" of {callable}; raise an error if it's not a receiver.
        JSValue prototype = GetProperty(isolate, callable, ReadOnlyRoots.prototype_string);
        if (!prototype.IsJSReceiver)
        {
            isolate.Throw(isolate.Factory.NewTypeError(MessageTemplate.InstanceofNonobjectProto, prototype));
        }

        // Return whether or not {prototype} is in the prototype chain of {object}.
        return JSReceiver.HasInPrototypeChain(isolate, receiver, prototype);
    }

    /// <summary>Object::InstanceOf (ES #sec-instanceofoperator).</summary>
    public static bool InstanceOf(Isolate isolate, JSValue obj, JSValue callable)
    {
        // The {callable} must be a receiver.
        if (AsReceiverOrNull(callable) is not { } callableReceiver)
        {
            isolate.Throw(isolate.Factory.NewTypeError(MessageTemplate.NonObjectInInstanceOfCheck));
            return false;
        }

        // Lookup the @@hasInstance method on {callable}.
        JSValue instOfHandler;
        if (callableReceiver is JSFunction function && !function.Map.IsDictionaryMap &&
            ReferenceEquals(function.Map.Prototype, function.Context.NativeContext.FunctionPrototype) &&
            function.Map.InstanceDescriptors.Search(ReadOnlyRoots.has_instance_symbol, function.Map).IsNotFound)
        {
            // The lookup of an ordinary function without an own @@hasInstance
            // whose prototype is %Function.prototype%: that property is
            // non-writable and non-configurable, so it is the original.
            instOfHandler = function.Context.NativeContext.FunctionHasInstance;
        }
        else
        {
            instOfHandler = GetMethod(isolate, callableReceiver, ReadOnlyRoots.has_instance_symbol);
        }
        if (isolate.Context is { } current && ReferenceEquals(instOfHandler._obj, current.NativeContext.FunctionHasInstance))
        {
            // CodeStubAssembler::InstanceOf: Function.prototype[@@hasInstance]
            // is called directly (CallJSBuiltin), without Builtins::Call; its
            // frame still shows in stack traces.
            JSValue result = V8Sharp.Interpreter.InterpreterCalls.CallBuiltin(isolate, Unsafe.As<JSFunction>(instOfHandler._obj!),
                callable, [obj], JSValue.Undefined);
            return ReferenceEquals(result._obj, Oddball.True);
        }
        if (!instOfHandler.IsUndefined)
        {
            // Call the {inst_of_handler} on the {callable}.
            JSValue result = Execution.Call(isolate, instOfHandler, callable, [obj]);
            return BooleanValue(result);
        }

        // The {callable} must have a [[Call]] internal method.
        if (!IsCallable(callable))
        {
            isolate.Throw(isolate.Factory.NewTypeError(MessageTemplate.NonCallableInInstanceOfCheck));
        }

        // Fall back to OrdinaryHasInstance with {callable} and {object}.
        return OrdinaryHasInstance(isolate, callable, obj);
    }

    /// <summary>Object::GetMethod (ES #sec-getmethod).</summary>
    public static JSValue GetMethod(Isolate isolate, JSReceiver receiver, Name name)
    {
        JSValue func = JSReceiver.GetProperty(isolate, receiver, name);
        if (func.IsNullOrUndefined) return JSValue.Undefined;
        if (!IsCallable(func))
        {
            isolate.Throw(isolate.Factory.NewTypeError(MessageTemplate.PropertyNotFunction, func, name, receiver));
        }
        return func;
    }

    static FixedArray? CreateListFromArrayLikeFastPath(Isolate isolate, JSValue obj, ElementTypes elementTypes)
    {
        if (elementTypes == ElementTypes.All)
        {
            if (obj.HeapObjectOrNull is JSArray array)
            {
                uint arrayLength = 0;
                if (!array.HasArrayPrototype(isolate) || !ToUint32(array.Length, out arrayLength) ||
                    !array.HasFastElements || !JSObject.PrototypeHasNoElements(isolate, array))
                {
                    return null;
                }
                return array.GetElementsAccessor().CreateListFromArrayLike(isolate, array, arrayLength);
            }
            if (obj.HeapObjectOrNull is JSTypedArray typedArray)
            {
                ulong length = typedArray.GetLength();
                if (typedArray.IsDetachedOrOutOfBounds || length > FixedArrayBase.kMaxLength) return null;
                return typedArray.GetElementsAccessor().CreateListFromArrayLike(isolate, typedArray, (uint)length);
            }
        }
        return null;
    }

    /// <summary>Object::CreateListFromArrayLike (ES #sec-createlistfromarraylike).</summary>
    public static FixedArray CreateListFromArrayLike(Isolate isolate, JSValue obj, ElementTypes elementTypes)
    {
        // Fast-path for JSArray and JSTypedArray.
        FixedArray? fastResult = CreateListFromArrayLikeFastPath(isolate, obj, elementTypes);
        if (fastResult is not null) return fastResult;
        // 3. If Type(obj) is not Object, throw a TypeError exception.
        if (obj.HeapObjectOrNull is not JSReceiver receiver)
        {
            isolate.Throw(isolate.Factory.NewTypeError(MessageTemplate.CalledOnNonObject,
                isolate.Factory.NewStringFromAsciiChecked("CreateListFromArrayLike")));
            return null!;
        }

        // 4. Let len be ? ToLength(? Get(obj, "length")).
        JSValue rawLengthNumber = GetLengthFromArrayLike(isolate, receiver);
        if (!ToUint32(rawLengthNumber, out uint len) || len > FixedArrayBase.kMaxLength)
        {
            isolate.Throw(isolate.Factory.NewRangeError(MessageTemplate.InvalidArrayLength));
        }
        // 5. Let list be an empty List.
        FixedArray list = isolate.Factory.NewFixedArray((int)len);
        // 7. Repeat while index < len:
        for (uint index = 0; index < len; ++index)
        {
            // 7a. Let indexName be ToString(index).
            // 7b. Let next be ? Get(obj, indexName).
            JSValue next = JSReceiver.GetElement(isolate, receiver, index);
            switch (elementTypes)
            {
                case ElementTypes.All:
                    // Nothing to do.
                    break;
                case ElementTypes.StringAndSymbol:
                    // 7c. If Type(next) is not an element of elementTypes, throw a
                    //     TypeError exception.
                    if (next.HeapObjectOrNull is not Name nextName)
                    {
                        isolate.Throw(isolate.Factory.NewTypeError(MessageTemplate.NotPropertyName, next));
                        return null!;
                    }
                    // 7d. Append next as the last element of list.
                    // Internalize on the fly so we can use pointer identity later.
                    next = isolate.Factory.InternalizeName(nextName);
                    break;
            }
            list.Set((int)index, next);
        }
        // 8. Return list.
        return list;
    }

    /// <summary>Object::GetLengthFromArrayLike: ToLength(Get(obj, "length")).</summary>
    public static JSValue GetLengthFromArrayLike(Isolate isolate, JSReceiver obj)
    {
        JSValue val = JSReceiver.GetProperty(isolate, obj, ReadOnlyRoots.length_string);
        return ToLength(isolate, val);
    }

    // ---- [[Get]] ----------------------------------------------------------------------

    /// <summary>Object::GetProperty(LookupIterator*).</summary>
    public static JSValue GetProperty(ref LookupIterator it, bool isGlobalReference = false)
    {
        for (;; it.Next())
        {
            switch (it.State)
            {
                case LookupIterator.StateKind.TRANSITION:
                    throw new InvalidOperationException("unreachable");
                case LookupIterator.StateKind.JSPROXY:
                {
                    JSValue receiver = it.GetReceiver();
                    // In case of global IC, the receiver is the global object. Replace by
                    // the global proxy.
                    if (receiver.HeapObjectOrNull is JSGlobalObject global) receiver = global.GlobalProxy!;
                    if (isGlobalReference)
                    {
                        if (!JSProxy.HasProperty(it.Isolate, it.GetHolder<JSProxy>(), it.GetName()))
                        {
                            it.NotFound();
                            return JSValue.Undefined;
                        }
                    }
                    JSValue result = JSProxy.GetProperty(it.Isolate, it.GetHolder<JSProxy>(), it.GetName(), receiver,
                        out bool wasFound);
                    if (!wasFound && !isGlobalReference) it.NotFound();
                    return result;
                }
                case LookupIterator.StateKind.WASM_OBJECT:
                    continue;  // Continue to the prototype, if present.
                case LookupIterator.StateKind.INTERCEPTOR:
                    continue;
                case LookupIterator.StateKind.ACCESS_CHECK:
                    if (it.HasAccess()) continue;
                    // JSObject::GetPropertyWithFailedAccessCheck (no interceptors).
                    return it.Isolate.ReportFailedAccessCheck(it.GetHolder<JSObject>());
                case LookupIterator.StateKind.MODULE_NAMESPACE:
                    continue;
                case LookupIterator.StateKind.ACCESSOR:
                    return GetPropertyWithAccessor(ref it);
                case LookupIterator.StateKind.TYPED_ARRAY_INDEX_NOT_FOUND:
                    return JSValue.Undefined;
                case LookupIterator.StateKind.DATA:
                    return it.GetDataValue();
                case LookupIterator.StateKind.STRING_LOOKUP_START_OBJECT:
                    return it.GetStringPropertyValue();
                case LookupIterator.StateKind.NOT_FOUND:
                    if (it.IsAnyPrivateName())
                    {
                        var privateSymbol = (Symbol)it.GetName();
                        var nameString = (JSString)privateSymbol.Description.Object;
                        if (privateSymbol.IsPrivateBrand)
                        {
                            JSString className = nameString.Length == 0 ? ReadOnlyRoots.anonymous_string : nameString;
                            it.Isolate.Throw(it.Isolate.Factory.NewTypeError(MessageTemplate.InvalidPrivateBrandInstance,
                                className));
                        }
                        it.Isolate.Throw(it.Isolate.Factory.NewTypeError(MessageTemplate.InvalidPrivateMemberRead, nameString));
                    }
                    return JSValue.Undefined;
            }
            throw new InvalidOperationException("unreachable");
        }
    }

    /// <summary>Object::GetProperty(isolate, object, name).</summary>
    public static JSValue GetProperty(Isolate isolate, JSValue obj, Name name)
    {
        var it = new LookupIterator(isolate, obj, name);
        if (!it.IsFound) return JSValue.Undefined;
        return GetProperty(ref it);
    }

    /// <summary>Object::GetPropertyOrElement(isolate, object, name).</summary>
    public static JSValue GetPropertyOrElement(Isolate isolate, JSValue obj, Name name)
    {
        var key = new PropertyKey(isolate, name);
        var it = new LookupIterator(isolate, obj, key);
        return GetProperty(ref it);
    }

    /// <summary>Object::GetProperty with a PropertyKey (the LookupIterator overload).</summary>
    public static JSValue GetPropertyOrElement(Isolate isolate, JSValue obj, in PropertyKey key)
    {
        var it = new LookupIterator(isolate, obj, key);
        return GetProperty(ref it);
    }

    /// <summary>Object::GetPropertyOrElement(isolate, receiver, name, holder).</summary>
    public static JSValue GetPropertyOrElement(Isolate isolate, JSValue receiver, Name name, JSReceiver holder)
    {
        var key = new PropertyKey(isolate, name);
        var it = new LookupIterator(isolate, receiver, key, holder);
        return GetProperty(ref it);
    }

    /// <summary>Object::GetElement.</summary>
    public static JSValue GetElement(Isolate isolate, JSValue obj, uint index)
    {
        var it = new LookupIterator(isolate, obj, index);
        return GetProperty(ref it);
    }

    /// <summary>Object::SetElement.</summary>
    public static JSValue SetElement(Isolate isolate, JSValue obj, uint index, JSValue value, ShouldThrow shouldThrow)
    {
        var it = new LookupIterator(isolate, obj, index);
        SetProperty(ref it, value, StoreOrigin.MaybeKeyed, shouldThrow);
        return value;
    }

    /// <summary>Object::GetPropertyWithAccessor.</summary>
    public static JSValue GetPropertyWithAccessor(ref LookupIterator it)
    {
        Isolate isolate = it.Isolate;
        HeapObject structure = it.GetAccessors();

        // API style callbacks.
        if (structure is AccessorInfo info)
        {
            Name name = it.GetName();
            if (!info.HasGetter) return JSValue.Undefined;

            JSObject holder = it.GetHolder<JSObject>();
            JSValue result = info.Getter!(isolate, it.GetReceiver(), holder, name);
            if (info.ReplaceOnAccess)
            {
                Accessors.ReplaceAccessorWithDataProperty(isolate, holder, name, result);
            }
            return result;
        }

        var accessorPair = (AccessorPair)structure;

        // Regular accessor.
        JSValue getter = accessorPair.Getter;

        JSValue receiver = it.GetReceiver();
        // In case of global IC, the receiver is the global object. Replace by the
        // global proxy.
        if (receiver.HeapObjectOrNull is JSGlobalObject global) receiver = global.GlobalProxy!;

        if (IsCallable(getter)) return GetPropertyWithDefinedGetter(isolate, receiver, getter.As<JSReceiver>());
        // Getter is not a function.
        return JSValue.Undefined;
    }

    /// <summary>Object::SetPropertyWithAccessor.</summary>
    public static bool SetPropertyWithAccessor(ref LookupIterator it, JSValue value, ShouldThrow? shouldThrow)
    {
        Isolate isolate = it.Isolate;
        HeapObject structure = it.GetAccessors();

        // API style callbacks.
        if (structure is AccessorInfo info)
        {
            Name name = it.GetName();
            if (!info.HasSetter)
            {
                // TODO(verwaest): We should not get here anymore once all AccessorInfos
                // are marked as special_data_property. They cannot both be writable and
                // not have a setter.
                return true;
            }

            JSObject holder = it.GetHolder<JSObject>();
            bool result = info.Setter!(isolate, it.GetReceiver(), holder, name, value, shouldThrow);
            if (!result)
            {
                // Make sure TypeError is thrown if necessary in case the callback
                // failed to set the property.
                return ReturnFailure(isolate, GetShouldThrow(isolate, shouldThrow), MessageTemplate.StrictCannotSetProperty,
                    it.GetName(), holder);
            }
            return result;
        }

        // Regular accessor.
        JSValue setter = ((AccessorPair)structure).Setter;

        JSValue receiver = it.GetReceiver();
        // In case of global IC, the receiver is the global object. Replace by the
        // global proxy.
        if (receiver.HeapObjectOrNull is JSGlobalObject global) receiver = global.GlobalProxy!;

        if (IsCallable(setter))
        {
            return SetPropertyWithDefinedSetter(isolate, receiver, setter.As<JSReceiver>(), value, shouldThrow);
        }

        return ReturnFailure(isolate, GetShouldThrow(isolate, shouldThrow), MessageTemplate.NoSetterInCallback,
            it.GetName(), it.GetHolder<JSReceiver>());
    }

    /// <summary>Object::GetPropertyWithDefinedGetter.</summary>
    public static JSValue GetPropertyWithDefinedGetter(Isolate isolate, JSValue receiver, JSReceiver getter)
    {
        // Break possible unbounded recursion through getters with a stack check.
        isolate.StackGuard.StackCheck(isolate);
        return Execution.Call(isolate, getter, receiver, []);
    }

    /// <summary>Object::SetPropertyWithDefinedSetter.</summary>
    public static bool SetPropertyWithDefinedSetter(Isolate isolate, JSValue receiver, JSReceiver setter, JSValue value,
        ShouldThrow? shouldThrow)
    {
        Execution.Call(isolate, setter, receiver, [value]);
        return true;
    }

    /// <summary>Object::GetPrototypeChainRootMap.</summary>
    public static Map GetPrototypeChainRootMap(in JSValue obj, Isolate isolate)
    {
        if (obj.HeapObjectOrNull is JSReceiver receiver) return receiver.Map.GetPrototypeChainRootMap(isolate);
        return GetPrototypeChainRoot(obj, isolate).InitialMap;
    }

    /// <summary>
    /// The constructor function whose initial map roots the prototype chain of a
    /// primitive (V8: map()->GetConstructorFunctionIndex() of the primitive's map).
    /// </summary>
    public static JSFunction GetPrototypeChainRoot(in JSValue obj, Isolate isolate)
    {
        NativeContext nativeContext = isolate.NativeContext;
        if (obj.IsNumber) return nativeContext.NumberFunction;
        return obj.HeapObjectOrNull switch
        {
            JSString => nativeContext.StringFunction,
            Symbol => nativeContext.SymbolFunction,
            BigInt => nativeContext.BigIntFunction,
            Oddball o when o.Kind is Oddball.OddballKind.True or Oddball.OddballKind.False => nativeContext.BooleanFunction,
            _ => nativeContext.ObjectFunction,
        };
    }

    // ---- Hashing ----------------------------------------------------------------------

    /// <summary>Object::GetSimpleHash: the hash of a non-receiver, or -1 for a receiver.</summary>
    public static long GetSimpleHash(in JSValue obj)
    {
        if (obj.IsNumber)
        {
            double num = obj.Number;
            if (double.IsNaN(num)) return JSValue.SmiMaxValue;
            // For values in Signed32 range, including -0 (which is considered equal
            // to 0 because collections use SameValueZero), hash the integer form to
            // match the Smi branch above.
            if (num >= int.MinValue && num <= int.MaxValue && (int)num == num)
            {
                return Hashing.SmiHash32(unchecked((uint)(int)num));
            }
            return Hashing.SmiHash64((ulong)BitConverter.DoubleToInt64Bits(num));
        }
        switch (obj.HeapObjectOrNull)
        {
            case null:
                return ReadOnlyRoots.undefined_string.EnsureHash();
            case Name name:
                return name.EnsureHash();
            case Oddball oddball:
                return OddballToString(oddball).EnsureHash();
            case BigInt bigint:
                return BigInt.Hash(bigint) & (uint)JSValue.SmiMaxValue;
            case Script script:
                return Hashing.SmiHash32((uint)script.Id);
            case JSReceiver:
                return -1;
            default:
                return (uint)System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(obj.Object) & (uint)JSValue.SmiMaxValue;
        }
    }

    /// <summary>Object::GetHash: the hash, or undefined for a receiver without an identity hash.</summary>
    public static JSValue GetHash(in JSValue obj)
    {
        long hash = GetSimpleHash(obj);
        if (hash >= 0) return JSValue.FromNumber(hash);
        return ((JSReceiver)obj.Object).GetIdentityHash();
    }

    /// <summary>Object::GetOrCreateHash, as a uint (hash tables key on it).</summary>
    public static uint GetOrCreateHashRaw(in JSValue obj)
    {
        long hash = GetSimpleHash(obj);
        if (hash >= 0) return (uint)hash;
        return (uint)((JSReceiver)obj.Object).GetOrCreateIdentityHash(null);
    }

    /// <summary>Object::GetOrCreateHash.</summary>
    public static JSValue GetOrCreateHash(in JSValue obj, Isolate isolate) => JSValue.FromNumber(GetOrCreateHashRaw(obj));

    // ---- Species ----------------------------------------------------------------------

    /// <summary>Object::ArraySpeciesConstructor (ES #sec-arrayspeciescreate steps 4-8).</summary>
    public static JSValue ArraySpeciesConstructor(Isolate isolate, JSValue originalArray)
    {
        JSValue defaultSpecies = isolate.NativeContext.ArrayFunction;
        if (!isolate.Flags.builtin_subclassing) return defaultSpecies;
        if (originalArray.HeapObjectOrNull is JSArray array && array.HasArrayPrototype(isolate) &&
            Protectors.IsArraySpeciesLookupChainIntact(isolate))
        {
            return defaultSpecies;
        }
        JSValue constructor = JSValue.Undefined;
        bool isArray = IsArray(isolate, originalArray);
        if (isArray)
        {
            constructor = GetProperty(isolate, originalArray, ReadOnlyRoots.constructor_string);
            if (IsConstructor(constructor))
            {
                NativeContext constructorContext = JSReceiver.GetFunctionRealm(isolate, constructor.As<JSReceiver>());
                if (!ReferenceEquals(constructorContext, isolate.NativeContext) &&
                    ReferenceEquals(constructor.Object, constructorContext.ArrayFunction))
                {
                    constructor = JSValue.Undefined;
                }
            }
            if (constructor.HeapObjectOrNull is JSReceiver ctorReceiver)
            {
                constructor = JSReceiver.GetProperty(isolate, ctorReceiver, ReadOnlyRoots.species_symbol);
                if (constructor.IsNull) constructor = JSValue.Undefined;
            }
        }
        if (constructor.IsUndefined) return defaultSpecies;
        if (!IsConstructor(constructor))
        {
            isolate.Throw(isolate.Factory.NewTypeError(MessageTemplate.SpeciesNotConstructor));
        }
        return constructor;
    }

    /// <summary>Object::SpeciesConstructor (ES6 section 7.3.20).</summary>
    public static JSValue SpeciesConstructor(Isolate isolate, JSReceiver recv, JSFunction defaultCtor)
    {
        JSValue ctorObj = JSReceiver.GetProperty(isolate, recv, ReadOnlyRoots.constructor_string);

        if (ctorObj.IsUndefined) return defaultCtor;

        if (ctorObj.HeapObjectOrNull is not JSReceiver ctor)
        {
            isolate.Throw(isolate.Factory.NewTypeError(MessageTemplate.ConstructorNotReceiver));
            return default;
        }

        JSValue species = JSReceiver.GetProperty(isolate, ctor, ReadOnlyRoots.species_symbol);

        if (species.IsNullOrUndefined) return defaultCtor;

        if (IsConstructor(species)) return species;

        return isolate.Throw(isolate.Factory.NewTypeError(MessageTemplate.SpeciesNotConstructor));
    }

    /// <summary>Object::IterationHasObservableEffects.</summary>
    public static bool IterationHasObservableEffects(Isolate isolate, in JSValue obj)
    {
        // Check that this object is an array.
        if (obj.HeapObjectOrNull is not JSArray array) return true;

        // Check that we have the original ArrayPrototype.
        JSReceiver? arrayProto = array.Map.Prototype;
        if (arrayProto is not JSObject) return true;
        NativeContext? nativeContext = array.GetCreationContext();
        if (nativeContext is null || !ReferenceEquals(nativeContext.InitialArrayPrototype, arrayProto)) return true;

        // Check that the ArrayPrototype hasn't been modified in a way that would
        // affect iteration.
        if (!Protectors.IsArrayIteratorLookupChainIntact(isolate)) return true;

        // For FastPacked kinds, iteration will have the same effect as simply
        // accessing each property in order.
        ElementsKind arrayKind = array.GetElementsKind();
        if (ElementsKinds.IsFastPackedElementsKind(arrayKind)) return false;

        // For FastHoley kinds, an element access on a hole would cause a lookup on
        // the prototype. This could have different results if the prototype has been
        // changed.
        if (ElementsKinds.IsHoleyElementsKind(arrayKind) && Protectors.IsNoElementsIntact(isolate)) return false;
        return true;
    }

    // ---- NoSideEffectsToString --------------------------------------------------------

    static bool IsErrorObject(Isolate isolate, in JSValue obj) =>
        obj.HeapObjectOrNull is JSObject o && ErrorUtils.HasErrorStackSymbolOwnProperty(isolate, o);

    static JSString AsStringOrEmpty(in JSValue obj) => obj.HeapObjectOrNull as JSString ?? ReadOnlyRoots.empty_string;

    static JSString NoSideEffectsErrorToString(Isolate isolate, JSReceiver error)
    {
        JSValue name = JSReceiver.GetDataProperty(isolate, error, ReadOnlyRoots.name_string);
        JSString nameStr = AsStringOrEmpty(name);

        JSValue msg = JSReceiver.GetDataProperty(isolate, error, ReadOnlyRoots.message_string);
        JSString msgStr = AsStringOrEmpty(msg);

        if (nameStr.Length == 0) return msgStr;
        if (msgStr.Length == 0) return nameStr;

        const string errorSuffix = "<a very large string>";
        const int errorSuffixSize = 22;  // sizeof(error_suffix), including the NUL.
        int suffixSize = Math.Min(errorSuffixSize, msgStr.Length);

        var builder = new IncrementalStringBuilder(isolate);
        if (nameStr.Length + suffixSize + 2 /* ": " */ > JSString.kMaxLength)
        {
            const string connector = "... : ";
            const int connectorSize = 7;  // sizeof(connector), including the NUL.
            JSString truncatedName = isolate.Factory.NewProperSubString(nameStr, 0,
                nameStr.Length - errorSuffixSize - connectorSize);
            builder.AppendString(truncatedName);
            builder.AppendCStringLiteral(connector);
            builder.AppendCStringLiteral(errorSuffix);
        }
        else
        {
            builder.AppendString(nameStr);
            builder.AppendCStringLiteral(": ");
            if (builder.Length + msgStr.Length <= JSString.kMaxLength)
            {
                builder.AppendString(msgStr);
            }
            else
            {
                builder.AppendCStringLiteral(errorSuffix);
            }
        }
        return builder.Finish();
    }

    /// <summary>Object::NoSideEffectsToMaybeString: a string without calling user code, or null.</summary>
    public static JSString? NoSideEffectsToMaybeString(Isolate isolate, JSValue input)
    {
        HeapObject? obj = input.HeapObjectOrNull;
        if (obj is Oddball hole && hole.IsHole)
        {
            return isolate.Factory.NewStringFromAsciiChecked(hole.Kind switch
            {
                Oddball.OddballKind.TheHole => "TheHole",
                Oddball.OddballKind.PropertyCellHole => "PropertyCellHole",
                _ => "HashTableHole",
            });
        }
        if (input.IsUndefined || input.IsNumber || obj is JSString || obj is Oddball)
        {
            return ToString(isolate, input);
        }
        if (obj is JSProxy proxy)
        {
            JSValue currInput = proxy.Target;
            while (currInput.HeapObjectOrNull is JSProxy p) currInput = p.Target;
            return NoSideEffectsToMaybeString(isolate, currInput);
        }
        if (obj is BigInt bigint) return BigInt.NoSideEffectsToString(isolate, bigint);
        if (obj is JSFunctionOrBoundFunctionOrWrappedFunction)
        {
            // -- F u n c t i o n
            JSString funStr = obj switch
            {
                JSBoundFunction bound => JSBoundFunction.ToString(isolate, bound),
                JSWrappedFunction wrapped => JSWrappedFunction.ToString(isolate, wrapped),
                _ => JSFunction.ToString(isolate, (JSFunction)obj),
            };

            if (funStr.Length > 128)
            {
                var builder = new IncrementalStringBuilder(isolate);
                builder.AppendString(isolate.Factory.NewSubString(funStr, 0, 111));
                builder.AppendCStringLiteral("...<omitted>...");
                builder.AppendString(isolate.Factory.NewSubString(funStr, funStr.Length - 2, funStr.Length));
                return builder.Finish();
            }
            return funStr;
        }
        if (obj is Symbol symbol)
        {
            // -- S y m b o l
            if (symbol.IsAnyPrivateName) return (JSString)symbol.Description.Object;

            var builder = new IncrementalStringBuilder(isolate);
            builder.AppendCStringLiteral("Symbol(");
            if (symbol.Description.HeapObjectOrNull is JSString description)
            {
                if (description.Length > 128)
                {
                    builder.AppendString(isolate.Factory.NewSubString(description, 0, 56));
                    builder.AppendCStringLiteral("...<omitted>...");
                    builder.AppendString(isolate.Factory.NewSubString(description, description.Length - 56,
                        description.Length));
                }
                else
                {
                    builder.AppendString(description);
                }
            }
            builder.AppendCharacter(')');
            return builder.Finish();
        }
        if (obj is JSReceiver receiver)
        {
            // -- J S R e c e i v e r
            JSValue toString = JSReceiver.GetDataProperty(isolate, receiver, ReadOnlyRoots.toString_string);

            if (IsErrorObject(isolate, input) || toString.IsIdenticalTo(isolate.NativeContext.ErrorToString))
            {
                // When internally formatting error objects, use a side-effects-free
                // version of Error.prototype.toString independent of the actually
                // installed toString method.
                return NoSideEffectsErrorToString(isolate, receiver);
            }
            if (toString.IsIdenticalTo(isolate.NativeContext.ObjectToString))
            {
                JSValue ctor = JSReceiver.GetDataProperty(isolate, receiver, ReadOnlyRoots.constructor_string);
                if (ctor.HeapObjectOrNull is JSFunctionOrBoundFunctionOrWrappedFunction)
                {
                    JSString ctorName;
                    try
                    {
                        ctorName = ctor.HeapObjectOrNull switch
                        {
                            JSBoundFunction bound => JSBoundFunction.GetName(isolate, bound),
                            JSFunction fun => JSFunction.GetName(isolate, fun),
                            JSWrappedFunction wrapped => JSWrappedFunction.GetName(isolate, wrapped),
                            _ => throw new InvalidOperationException("unreachable"),
                        };
                    }
                    catch (JavaScriptException)
                    {
                        return null;
                    }

                    if (ctorName.Length != 0)
                    {
                        var builder = new IncrementalStringBuilder(isolate);
                        builder.AppendCStringLiteral("#<");
                        builder.AppendString(ctorName);
                        builder.AppendCharacter('>');
                        try
                        {
                            return builder.Finish();
                        }
                        catch (JavaScriptException)
                        {
                            return null;
                        }
                    }
                }
            }
        }
        return null;
    }

    /// <summary>Object::NoSideEffectsToString.</summary>
    public static JSString NoSideEffectsToString(Isolate isolate, JSValue input)
    {
        // Try to convert input to a meaningful string.
        JSString? maybeString = NoSideEffectsToMaybeString(isolate, input);
        if (maybeString is not null) return maybeString;

        // At this point, input is either none of the above or a JSReceiver.
        JSReceiver receiver;
        if (input.HeapObjectOrNull is JSReceiver r)
        {
            receiver = r;
        }
        else
        {
            // This is the only case where Object::ToObject throws. Every primitive
            // with a wrapper constructor was handled above, so what is left are
            // internal objects without a constructor function index.
            if (input.HeapObjectOrNull is not (JSString or Symbol or BigInt) && !input.IsBoolean && !input.IsNumber)
            {
                return isolate.Factory.NewStringFromAsciiChecked("[object Unknown]");
            }
            receiver = ToObjectImpl(isolate, input);
        }

        JSString builtinTag = receiver.ClassName();
        JSValue tagObj = JSReceiver.GetDataProperty(isolate, receiver, ReadOnlyRoots.to_string_tag_symbol);
        JSString tag = tagObj.HeapObjectOrNull as JSString ?? builtinTag;

        var builder = new IncrementalStringBuilder(isolate);
        builder.AppendCStringLiteral("[object ");
        // This threshold must be sufficiently far below String::kMaxLength that
        // the {builder}'s result can never exceed that limit.
        const int kMaxPrintedStringLength = 1000;
        builder.AppendStringCapped(tag, kMaxPrintedStringLength);
        builder.AppendCharacter(']');
        return builder.Finish();
    }

    // ---- [[Set]] ----------------------------------------------------------------------

    /// <summary>Object::SetProperty(isolate, object, name, value, store_origin, should_throw).</summary>
    public static JSValue SetProperty(Isolate isolate, JSValue obj, Name name, JSValue value,
        StoreOrigin storeOrigin = StoreOrigin.MaybeKeyed, ShouldThrow? shouldThrow = null)
    {
        var it = new LookupIterator(isolate, obj, name);
        SetProperty(ref it, value, storeOrigin, shouldThrow);
        return value;
    }

    /// <summary>
    /// Object::SetPropertyInternal. Returns null for V8's "found = false" (the
    /// caller continues by adding a data property); otherwise the result.
    /// </summary>
    static bool? SetPropertyInternal(ref LookupIterator it, JSValue value, ShouldThrow? shouldThrow, StoreOrigin storeOrigin)
    {
        it.UpdateProtector();
        Debug.Assert(it.IsFound);

        for (;; it.Next())
        {
            switch (it.State)
            {
                case LookupIterator.StateKind.ACCESS_CHECK:
                    if (it.HasAccess()) continue;
                    // JSObject::SetPropertyWithFailedAccessCheck (no interceptors).
                    it.Isolate.ReportFailedAccessCheck(it.GetHolder<JSObject>());
                    return true;

                case LookupIterator.StateKind.JSPROXY:
                {
                    JSValue receiver = it.GetReceiver();
                    // In case of global IC, the receiver is the global object. Replace by
                    // the global proxy.
                    if (receiver.HeapObjectOrNull is JSGlobalObject global) receiver = global.GlobalProxy!;
                    return JSProxy.SetProperty(it.Isolate, it.GetHolder<JSProxy>(), it.GetName(), value, receiver, shouldThrow);
                }

                case LookupIterator.StateKind.WASM_OBJECT:
                    continue;  // Continue to the prototype, if present.

                case LookupIterator.StateKind.INTERCEPTOR:
                    continue;

                case LookupIterator.StateKind.MODULE_NAMESPACE:
                {
                    Isolate isolate = it.Isolate;
                    return ReturnFailure(isolate, GetShouldThrow(isolate, shouldThrow), MessageTemplate.StrictCannotSetProperty,
                        it.GetName(), it.GetReceiver());
                }
                case LookupIterator.StateKind.ACCESSOR:
                {
                    if (it.IsReadOnly) return WriteToReadOnlyProperty(ref it, value, shouldThrow);
                    HeapObject accessors = it.GetAccessors();
                    if (accessors is AccessorInfo && !it.HolderIsReceiverOrHiddenPrototype()) return null;
                    return SetPropertyWithAccessor(ref it, value, shouldThrow);
                }
                case LookupIterator.StateKind.TYPED_ARRAY_INDEX_NOT_FOUND:
                {
                    // IntegerIndexedElementSet converts value to a Number/BigInt prior to
                    // the bounds check. The bounds check has already happened here, but
                    // perform the possibly effectful ToNumber (or ToBigInt) operation
                    // anyways.
                    JSTypedArray holder = it.GetHolder<JSTypedArray>();

                    // The index found case uses HolderIsReceiverOrHiddenPrototype()
                    // in below "case LookupIterator::DATA" to check
                    // "i. If SameValue(O, Receiver) is true..." of the spec.
                    // https://tc39.es/ecma262/#sec-typedarray-set
                    if (it.HolderIsReceiver())
                    {
                        JSValue convertedValue = holder.IsBigIntArray
                            ? BigInt.FromObject(it.Isolate, value)
                            : ToNumber(it.Isolate, value);
                        // For RAB/GSABs, the above conversion might grow the buffer so that
                        // the index is no longer out of bounds. Redo the bounds check and try
                        // again.
                        it.RecheckTypedArrayBounds();
                        value = convertedValue;
                    }
                    if (it.State != LookupIterator.StateKind.DATA)
                    {
                        // Still out of bounds.
                        // TODO(verwaest): Per spec, we should return false here (steps 6-9
                        // in IntegerIndexedElementSet), resulting in an exception being
                        // thrown on OOB accesses in strict code. Historically, v8 has not
                        // done made this change due to uncertainty about web compat.
                        // (v8:4901)
                        return true;
                    }
                    goto case LookupIterator.StateKind.DATA;
                }

                case LookupIterator.StateKind.DATA:
                    if (it.IsReadOnly) return WriteToReadOnlyProperty(ref it, value, shouldThrow);
                    if (it.HolderIsReceiverOrHiddenPrototype()) return SetDataProperty(ref it, value);
                    return null;
                case LookupIterator.StateKind.NOT_FOUND:
                case LookupIterator.StateKind.TRANSITION:
                    return null;

                case LookupIterator.StateKind.STRING_LOOKUP_START_OBJECT:
                    return WriteToReadOnlyProperty(ref it, value, shouldThrow);
            }
            throw new InvalidOperationException("unreachable");
        }
    }

    /// <summary>Object::CheckContextualStoreToJSGlobalObject.</summary>
    public static bool CheckContextualStoreToJSGlobalObject(ref LookupIterator it, ShouldThrow? shouldThrow)
    {
        Isolate isolate = it.Isolate;

        if (it.GetReceiver().HeapObjectOrNull is JSGlobalObject &&
            GetShouldThrow(isolate, shouldThrow) == ShouldThrow.ThrowOnError)
        {
            if (it.State == LookupIterator.StateKind.TRANSITION)
            {
                // The property cell that we have created is garbage because we are going
                // to throw now instead of putting it into the global dictionary. However,
                // the cell might already have been stored into the feedback vector, so
                // we must invalidate it nevertheless.
                it.TransitionCell.ClearAndInvalidate(isolate);
            }
            isolate.Throw(isolate.Factory.NewReferenceError(MessageTemplate.NotDefined, it.GetName()));
            return false;
        }
        return true;
    }

    /// <summary>Object::SetProperty(LookupIterator*, ...).</summary>
    public static bool SetProperty(ref LookupIterator it, JSValue value, StoreOrigin storeOrigin, ShouldThrow? shouldThrow = null)
    {
        if (it.IsFound)
        {
            bool? result = SetPropertyInternal(ref it, value, shouldThrow, storeOrigin);
            if (result.HasValue) return result.Value;
        }

        if (!CheckContextualStoreToJSGlobalObject(ref it, shouldThrow)) return false;
        return AddDataProperty(ref it, value, PropertyAttributes.NONE, shouldThrow, storeOrigin);
    }

    /// <summary>Object::SetSuperProperty.</summary>
    public static bool SetSuperProperty(ref LookupIterator it, JSValue value, StoreOrigin storeOrigin, ShouldThrow? shouldThrow = null)
    {
        Isolate isolate = it.Isolate;

        if (it.IsFound)
        {
            bool? result = SetPropertyInternal(ref it, value, shouldThrow, storeOrigin);
            if (result.HasValue) return result.Value;
        }

        it.UpdateProtector();

        // The property either doesn't exist on the holder or exists there as a data
        // property.

        if (it.GetReceiver().HeapObjectOrNull is not JSReceiver receiver) return WriteToReadOnlyProperty(ref it, value, shouldThrow);

        // Note, the callers rely on the fact that this code is redoing the full own
        // lookup from scratch.
        var ownLookup = new LookupIterator(isolate, receiver, it.GetKey(), LookupIterator.Configuration.OWN);
        for (;; ownLookup.Next())
        {
            switch (ownLookup.State)
            {
                case LookupIterator.StateKind.ACCESS_CHECK:
                    if (ownLookup.HasAccess()) continue;
                    isolate.ReportFailedAccessCheck(ownLookup.GetHolder<JSObject>());
                    return true;

                case LookupIterator.StateKind.ACCESSOR:
                    if (ownLookup.GetAccessors() is AccessorInfo)
                    {
                        if (ownLookup.IsReadOnly) return WriteToReadOnlyProperty(ref ownLookup, value, shouldThrow);
                        return SetPropertyWithAccessor(ref ownLookup, value, shouldThrow);
                    }
                    return RedefineIncompatibleProperty(isolate, it.GetName(), value, shouldThrow);
                case LookupIterator.StateKind.TYPED_ARRAY_INDEX_NOT_FOUND:
                    return RedefineIncompatibleProperty(isolate, it.GetName(), value, shouldThrow);

                case LookupIterator.StateKind.STRING_LOOKUP_START_OBJECT:
                    throw new InvalidOperationException("unreachable");

                case LookupIterator.StateKind.DATA:
                    if (ownLookup.IsReadOnly) return WriteToReadOnlyProperty(ref ownLookup, value, shouldThrow);
                    return SetDataProperty(ref ownLookup, value);

                case LookupIterator.StateKind.INTERCEPTOR:
                case LookupIterator.StateKind.JSPROXY:
                case LookupIterator.StateKind.MODULE_NAMESPACE:
                {
                    var desc = new PropertyDescriptor();
                    bool owned = JSReceiver.GetOwnPropertyDescriptor(ref ownLookup, ref desc);
                    if (!owned)
                    {
                        if (ownLookup.State == LookupIterator.StateKind.MODULE_NAMESPACE)
                        {
                            // Property not found on namespace (non-exported key).
                            // Spec: OrdinarySetWithOwnDescriptor step 2.d.ii calls
                            // CreateDataProperty(Receiver, P, V), which invokes
                            // Receiver.[[DefineOwnProperty]] ->
                            // JSModuleNamespace::DefineOwnProperty
                            // -> throws kRedefineDisallowed TypeError.
                            // We shortcut to RedefineIncompatibleProperty for the same result.
                            return RedefineIncompatibleProperty(isolate, it.GetName(), value, shouldThrow);
                        }
                        // |own_lookup| might become outdated at this point anyway.
                        ownLookup.Restart();
                        if (!CheckContextualStoreToJSGlobalObject(ref ownLookup, shouldThrow)) return false;
                        return JSReceiver.CreateDataProperty(isolate, receiver, it.GetKey(), value, shouldThrow);
                    }
                    if (PropertyDescriptor.IsAccessorDescriptor(in desc) || !desc.Writable)
                    {
                        return RedefineIncompatibleProperty(isolate, it.GetName(), value, shouldThrow);
                    }

                    var valueDesc = new PropertyDescriptor();
                    valueDesc.SetValue(value);
                    return JSReceiver.DefineOwnProperty(isolate, receiver, it.GetName(), ref valueDesc, shouldThrow);
                }

                case LookupIterator.StateKind.NOT_FOUND:
                    if (!CheckContextualStoreToJSGlobalObject(ref ownLookup, shouldThrow)) return false;
                    return AddDataProperty(ref ownLookup, value, PropertyAttributes.NONE, shouldThrow, storeOrigin);

                case LookupIterator.StateKind.WASM_OBJECT:
                    return ReturnFailure(isolate, ShouldThrow.ThrowOnError, MessageTemplate.WasmObjectsAreOpaque);

                case LookupIterator.StateKind.TRANSITION:
                    throw new InvalidOperationException("unreachable");
            }
            throw new InvalidOperationException("unreachable");
        }
    }

    /// <summary>Object::CannotCreateProperty.</summary>
    public static bool CannotCreateProperty(Isolate isolate, JSValue receiver, JSValue name, ShouldThrow? shouldThrow) =>
        ReturnFailure(isolate, GetShouldThrow(isolate, shouldThrow), MessageTemplate.StrictCannotCreateProperty, name,
            TypeOf(isolate, receiver), receiver);

    /// <summary>Object::WriteToReadOnlyProperty(LookupIterator*, ...).</summary>
    public static bool WriteToReadOnlyProperty(ref LookupIterator it, JSValue value, ShouldThrow? maybeShouldThrow)
    {
        ShouldThrow shouldThrow = GetShouldThrow(it.Isolate, maybeShouldThrow);
        if (it.IsFound && it.State != LookupIterator.StateKind.STRING_LOOKUP_START_OBJECT && !it.HolderIsReceiver())
        {
            // "Override mistake" attempted, record a use count to track this per
            // v8:8175
            it.Isolate.CountUsage(shouldThrow == ShouldThrow.ThrowOnError
                ? UseCounterFeature.kAttemptOverrideReadOnlyOnPrototypeStrict
                : UseCounterFeature.kAttemptOverrideReadOnlyOnPrototypeSloppy);
        }
        return WriteToReadOnlyProperty(it.Isolate, it.GetReceiver(), it.GetName(), value, shouldThrow);
    }

    /// <summary>Object::WriteToReadOnlyProperty(isolate, receiver, name, value, should_throw).</summary>
    public static bool WriteToReadOnlyProperty(Isolate isolate, JSValue receiver, JSValue name, JSValue value,
        ShouldThrow shouldThrow) =>
        ReturnFailure(isolate, shouldThrow, MessageTemplate.StrictReadOnlyProperty, name, TypeOf(isolate, receiver), receiver);

    /// <summary>Object::RedefineIncompatibleProperty.</summary>
    public static bool RedefineIncompatibleProperty(Isolate isolate, JSValue name, JSValue value, ShouldThrow? shouldThrow) =>
        ReturnFailure(isolate, GetShouldThrow(isolate, shouldThrow), MessageTemplate.RedefineDisallowed, name);

    /// <summary>Object::SetDataProperty.</summary>
    public static bool SetDataProperty(ref LookupIterator it, JSValue value)
    {
        Isolate isolate = it.Isolate;
        var receiver = (JSReceiver)it.GetReceiver().Object;

        // Store on the holder which may be hidden behind the receiver.
        Debug.Assert(it.HolderIsReceiverOrHiddenPrototype());

        JSValue toAssign = value;
        // Convert the incoming value to a number for storing into typed arrays.
        if (it.IsElement() && receiver is JSObject receiverObj && receiverObj.HasTypedArrayOrRabGsabTypedArrayElements)
        {
            var receiverTa = (JSTypedArray)receiver;
            ElementsKind elementsKind = receiverObj.GetElementsKind();
            if (ElementsKinds.IsBigIntTypedArrayElementsKind(elementsKind))
            {
                toAssign = BigInt.FromObject(isolate, value);
                if (receiverTa.IsDetachedOrOutOfBounds || it.Index >= receiverTa.GetLength()) return true;
            }
            else if (!value.IsNumber && !value.IsUndefined)
            {
                toAssign = ToNumber(isolate, value);
                if (receiverTa.IsDetachedOrOutOfBounds || it.Index >= receiverTa.GetLength()) return true;
            }
        }

        // Possibly migrate to the most up-to-date map that will be able to store
        // |value| under it->name().
        it.PrepareForDataProperty(toAssign);

        // Write the property value.
        it.WriteDataValue(toAssign, false);
        return true;
    }

    /// <summary>Object::AddDataProperty.</summary>
    public static bool AddDataProperty(ref LookupIterator it, JSValue value, PropertyAttributes attributes,
        ShouldThrow? shouldThrow, StoreOrigin storeOrigin, EnforceDefineSemantics semantics = EnforceDefineSemantics.Set)
    {
        if (!it.GetReceiver().IsJSReceiver)
        {
            return CannotCreateProperty(it.Isolate, it.GetReceiver(), it.GetName(), shouldThrow);
        }

        // Private symbols should be installed on JSProxy using
        // JSProxy::SetPrivateSymbol.
        if (it.GetReceiver().HeapObjectOrNull is JSProxy && it.GetName().IsPrivateInternal)
        {
            return ReturnFailure(it.Isolate, GetShouldThrow(it.Isolate, shouldThrow), MessageTemplate.ProxyPrivate);
        }

        Debug.Assert(it.State != LookupIterator.StateKind.TYPED_ARRAY_INDEX_NOT_FOUND);

        JSReceiver receiver = it.GetStoreTarget<JSReceiver>();

        // If the receiver is a JSGlobalProxy, store on the prototype (JSGlobalObject)
        // instead. If the prototype is Null, the proxy is detached.
        if (receiver is JSGlobalProxy) return true;

        Isolate isolate = it.Isolate;

        if (it.ExtendingNonExtensible(receiver))
        {
            return ReturnFailure(isolate, GetShouldThrow(it.Isolate, shouldThrow),
                semantics == EnforceDefineSemantics.Define ? MessageTemplate.DefineDisallowed : MessageTemplate.ObjectNotExtensible,
                it.GetName());
        }

        if (it.IsElement(receiver))
        {
            if (receiver is JSArray array)
            {
                if (JSArray.WouldChangeReadOnlyLength(isolate, array, it.ArrayIndex))
                {
                    return ReturnFailure(isolate, GetShouldThrow(isolate, shouldThrow), MessageTemplate.StrictReadOnlyProperty,
                        ReadOnlyRoots.length_string, TypeOf(isolate, array), array);
                }
            }

            var receiverObj = (JSObject)receiver;
            JSObject.AddDataElement(isolate, receiverObj, it.ArrayIndex, value, attributes);
            return true;
        }

        return TransitionAndWriteDataProperty(ref it, value, attributes, shouldThrow, storeOrigin);
    }

    /// <summary>Object::TransitionAndWriteDataProperty.</summary>
    public static bool TransitionAndWriteDataProperty(ref LookupIterator it, JSValue value, PropertyAttributes attributes,
        ShouldThrow? shouldThrow, StoreOrigin storeOrigin)
    {
        JSReceiver receiver = it.GetStoreTarget<JSReceiver>();
        it.UpdateProtector();
        // Migrate to the most up-to-date map that will be able to store |value|
        // under it->name() with |attributes|.
        it.PrepareTransitionToDataProperty(receiver, value, attributes, storeOrigin);
        Debug.Assert(it.State == LookupIterator.StateKind.TRANSITION);

        // Apply the transitions -- this can fail if there are too many properties.
        if (!it.ApplyTransitionToDataProperty(receiver)) return false;

        // Write the property value.
        it.WriteDataValue(value, true);
        return true;
    }
}
