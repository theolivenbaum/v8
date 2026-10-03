// Port of the String builtins: src/builtins/builtins-string.cc (fromCodePoint,
// lastIndexOf, the non-ICU localeCompare and normalize, String.raw),
// builtins-string-gen.cc (fromCharCode, StringToArray, the surrogate helpers)
// and builtins-string.tq, string-at.tq, string-endswith.tq, string-html.tq,
// string-includes.tq, string-indexof.tq, string-iswellformed.tq,
// string-iterator.tq, string-pad.tq, string-repeat.tq, string-slice.tq,
// string-startswith.tq, string-substr.tq, string-substring.tq,
// string-towellformed.tq, string-trim.tq, plus the helpers of base.tq they use
// (ToThisString, ToThisValue, ClampToIndexRange, ConvertAndClampRelativeIndex).
//
// Case conversion is in Builtins.String.Case.cs; replace, replaceAll, match,
// matchAll, search and split in Builtins.String.Regexp.cs.
using System.Runtime.CompilerServices;
using V8Sharp.Base.Numbers;
using V8Sharp.Runtime;

namespace V8Sharp.Builtins;

public static partial class BuiltinRegistry
{
    static partial void RegisterString() => BuiltinsString.Register();
}

/// <summary>The String builtins (String, String.prototype, %StringIteratorPrototype%).</summary>
public static partial class BuiltinsString
{
    internal static void Register()
    {
        BuiltinRegistry.Register(Builtin.StringConstructor, StringConstructor);
        BuiltinRegistry.Register(Builtin.StringFromCharCode, StringFromCharCode);
        BuiltinRegistry.Register(Builtin.StringFromCodePoint, StringFromCodePoint);
        BuiltinRegistry.Register(Builtin.StringRaw, StringRaw);

        BuiltinRegistry.Register(Builtin.StringPrototypeAnchor, StringPrototypeAnchor);
        BuiltinRegistry.Register(Builtin.StringPrototypeAt, StringPrototypeAt);
        BuiltinRegistry.Register(Builtin.StringPrototypeBig, StringPrototypeBig);
        BuiltinRegistry.Register(Builtin.StringPrototypeBlink, StringPrototypeBlink);
        BuiltinRegistry.Register(Builtin.StringPrototypeBold, StringPrototypeBold);
        BuiltinRegistry.Register(Builtin.StringPrototypeCharAt, StringPrototypeCharAt);
        BuiltinRegistry.Register(Builtin.StringPrototypeCharCodeAt, StringPrototypeCharCodeAt);
        BuiltinRegistry.Register(Builtin.StringPrototypeCodePointAt, StringPrototypeCodePointAt);
        BuiltinRegistry.Register(Builtin.StringPrototypeConcat, StringPrototypeConcat);
        BuiltinRegistry.Register(Builtin.StringPrototypeEndsWith, StringPrototypeEndsWith);
        BuiltinRegistry.Register(Builtin.StringPrototypeFontcolor, StringPrototypeFontcolor);
        BuiltinRegistry.Register(Builtin.StringPrototypeFontsize, StringPrototypeFontsize);
        BuiltinRegistry.Register(Builtin.StringPrototypeFixed, StringPrototypeFixed);
        BuiltinRegistry.Register(Builtin.StringPrototypeIncludes, StringPrototypeIncludes);
        BuiltinRegistry.Register(Builtin.StringPrototypeIndexOf, StringPrototypeIndexOf);
        BuiltinRegistry.Register(Builtin.StringPrototypeIsWellFormed, StringPrototypeIsWellFormed);
        BuiltinRegistry.Register(Builtin.StringPrototypeItalics, StringPrototypeItalics);
        BuiltinRegistry.Register(Builtin.StringPrototypeLastIndexOf, StringPrototypeLastIndexOf);
        BuiltinRegistry.Register(Builtin.StringPrototypeLink, StringPrototypeLink);
        BuiltinRegistry.Register(Builtin.StringPrototypeLocaleCompare, StringPrototypeLocaleCompare);
        BuiltinRegistry.Register(Builtin.StringPrototypeMatch, StringPrototypeMatch);
        BuiltinRegistry.Register(Builtin.StringPrototypeMatchAll, StringPrototypeMatchAll);
        BuiltinRegistry.Register(Builtin.StringPrototypeNormalize, StringPrototypeNormalize);
        BuiltinRegistry.Register(Builtin.StringPrototypePadEnd, StringPrototypePadEnd);
        BuiltinRegistry.Register(Builtin.StringPrototypePadStart, StringPrototypePadStart);
        BuiltinRegistry.Register(Builtin.StringPrototypeRepeat, StringPrototypeRepeat);
        BuiltinRegistry.Register(Builtin.StringPrototypeReplace, StringPrototypeReplace);
        BuiltinRegistry.Register(Builtin.StringPrototypeReplaceAll, StringPrototypeReplaceAll);
        BuiltinRegistry.Register(Builtin.StringPrototypeSearch, StringPrototypeSearch);
        BuiltinRegistry.Register(Builtin.StringPrototypeSlice, StringPrototypeSlice);
        BuiltinRegistry.Register(Builtin.StringPrototypeSmall, StringPrototypeSmall);
        BuiltinRegistry.Register(Builtin.StringPrototypeSplit, StringPrototypeSplit);
        BuiltinRegistry.Register(Builtin.StringPrototypeStrike, StringPrototypeStrike);
        BuiltinRegistry.Register(Builtin.StringPrototypeSub, StringPrototypeSub);
        BuiltinRegistry.Register(Builtin.StringPrototypeSubstr, StringPrototypeSubstr);
        BuiltinRegistry.Register(Builtin.StringPrototypeSubstring, StringPrototypeSubstring);
        BuiltinRegistry.Register(Builtin.StringPrototypeSup, StringPrototypeSup);
        BuiltinRegistry.Register(Builtin.StringPrototypeStartsWith, StringPrototypeStartsWith);
        BuiltinRegistry.Register(Builtin.StringPrototypeToString, StringPrototypeToString);
        BuiltinRegistry.Register(Builtin.StringPrototypeToWellFormed, StringPrototypeToWellFormed);
        BuiltinRegistry.Register(Builtin.StringPrototypeTrim, StringPrototypeTrim);
        BuiltinRegistry.Register(Builtin.StringPrototypeTrimStart, StringPrototypeTrimStart);
        BuiltinRegistry.Register(Builtin.StringPrototypeTrimEnd, StringPrototypeTrimEnd);
        BuiltinRegistry.Register(Builtin.StringPrototypeToLocaleLowerCase, StringPrototypeToLocaleLowerCase);
        BuiltinRegistry.Register(Builtin.StringPrototypeToLocaleUpperCase, StringPrototypeToLocaleUpperCase);
        BuiltinRegistry.Register(Builtin.StringPrototypeToLowerCase, StringPrototypeToLowerCase);
        BuiltinRegistry.Register(Builtin.StringPrototypeToUpperCase, StringPrototypeToUpperCase);
        BuiltinRegistry.Register(Builtin.StringPrototypeValueOf, StringPrototypeValueOf);
        BuiltinRegistry.Register(Builtin.StringPrototypeIterator, StringPrototypeIterator);
        BuiltinRegistry.Register(Builtin.StringIteratorPrototypeNext, StringIteratorPrototypeNext);
    }

    // ---- Helpers (base.tq, code-stub-assembler) ---------------------------------------

    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static JSValue ThrowTypeError(Isolate isolate, MessageTemplate template, string arg) =>
        isolate.ThrowTypeError(template, isolate.Factory.NewStringFromAsciiChecked(arg));

    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static JSValue ThrowTypeError(Isolate isolate, MessageTemplate template, string arg, JSValue arg2) =>
        isolate.ThrowTypeError(template, isolate.Factory.NewStringFromAsciiChecked(arg), arg2);

    /// <summary>RequireObjectCoercible: TypeError "% called on null or undefined".</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void RequireObjectCoercible(Isolate isolate, JSValue value, string methodName)
    {
        if (value.IsNullOrUndefined) ThrowTypeError(isolate, MessageTemplate.CalledOnNullOrUndefined, methodName);
    }

    /// <summary>ToThisString: RequireObjectCoercible(receiver), then ToString.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static JSString ToThisString(Isolate isolate, JSValue receiver, string methodName)
    {
        if (receiver.HeapObjectOrNull is JSString s) return s;
        RequireObjectCoercible(isolate, receiver, methodName);
        return ObjectOps.ToString(isolate, receiver);
    }

    /// <summary>ToString_Inline.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static JSString ToStringInline(Isolate isolate, JSValue value) =>
        value.HeapObjectOrNull is JSString s ? s : ObjectOps.ToString(isolate, value);

    /// <summary>ToInteger_Inline as a double (ToIntegerOrInfinity; -0 becomes +0).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static double ToIntegerDouble(Isolate isolate, JSValue value)
    {
        if (value.IsSmi) return value.Number;
        return ObjectOps.DoubleToInteger(ObjectOps.ToNumber(isolate, value).Number);
    }

    /// <summary>ClampToIndexRange (base.tq): min(max(ToInteger(index), 0), limit).</summary>
    internal static int ClampToIndexRange(Isolate isolate, JSValue index, int limit)
    {
        double d = ToIntegerDouble(isolate, index);
        if (d <= 0) return 0;
        if (d >= limit) return limit;
        return (int)d;
    }

    /// <summary>ConvertAndClampRelativeIndex (base.tq): index &lt; 0 ? max(length + index, 0) : min(index, length).</summary>
    internal static int ConvertAndClampRelativeIndex(Isolate isolate, JSValue index, int length)
    {
        double d = ToIntegerDouble(isolate, index);
        if (d < 0)
        {
            d += length;
            return d <= 0 ? 0 : (int)d;
        }
        return d >= length ? length : (int)d;
    }

    /// <summary>StringBuiltinsAssembler::SubString / the SubString builtin.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static JSString SubString(Isolate isolate, JSString s, int from, int to) =>
        isolate.Factory.NewSubString(s, from, to);

    /// <summary>StringFromSingleCharCode: the cached single-character string.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static JSString StringFromSingleCharCode(Isolate isolate, char c) =>
        isolate.Factory.LookupSingleCharacterStringFromCode(c);

    /// <summary>
    /// LoadSurrogatePairAt(UTF16) + StringFromSingleUTF16EncodedCodePoint: the
    /// code point at <paramref name="position"/> as a one- or two-unit string.
    /// </summary>
    internal static JSString StringFromCodePointAt(Isolate isolate, JSString s, ReadOnlySpan<char> flat, int position)
    {
        char lead = flat[position];
        if (char.IsHighSurrogate(lead) && position + 1 < flat.Length && char.IsLowSurrogate(flat[position + 1]))
        {
            return isolate.Factory.NewSubString(s, position, position + 2);
        }
        return isolate.Factory.LookupSingleCharacterStringFromCode(lead);
    }

    /// <summary>LoadSurrogatePairAt(UTF32): the code point at the position.</summary>
    internal static int LoadSurrogatePairAtUtf32(ReadOnlySpan<char> flat, int index)
    {
        char lead = flat[index];
        if ((lead & 0xFC00) == 0xD800 && index + 1 < flat.Length)
        {
            char trail = flat[index + 1];
            if ((trail & 0xFC00) == 0xDC00) return char.ConvertToUtf32(lead, trail);
        }
        return lead;
    }

    /// <summary>AllocateJSIteratorResult: { value, done } from the iterator result map.</summary>
    internal static JSObject CreateIterResultObject(Isolate isolate, JSValue value, bool done)
    {
        JSObject result = isolate.Factory.NewJSObjectFromMap(isolate.NativeContext.IteratorResultMap);
        result.InObjectPropertyRef(0) = value;
        result.InObjectPropertyRef(1) = JSValue.FromBoolean(done);
        return result;
    }

    /// <summary>Symbol::SymbolDescriptiveString: "Symbol(description)".</summary>
    internal static JSString SymbolDescriptiveString(Isolate isolate, Symbol symbol)
    {
        var builder = new IncrementalStringBuilder(isolate);
        builder.AppendCStringLiteral("Symbol(");
        if (symbol.Description.HeapObjectOrNull is JSString description) builder.AppendString(description);
        builder.AppendCharacter(')');
        return builder.Finish();
    }

    // ---- String constructor and statics -----------------------------------------------

    /// <summary>StringConstructor (builtins-string.tq).</summary>
    public static JSValue StringConstructor(Isolate isolate, in BuiltinArguments args)
    {
        JSString s;
        // 1. If no arguments were passed to this function invocation, let s be "".
        if (args.ArgcWithoutReceiver == 0)
        {
            s = ReadOnlyRoots.empty_string;
        }
        else
        {
            JSValue value = args.Arguments[0];
            // 2. a. If NewTarget is undefined and Type(value) is Symbol, return
            // SymbolDescriptiveString(value).
            if (args.NewTarget.IsUndefined && value.HeapObjectOrNull is Symbol symbol)
            {
                return SymbolDescriptiveString(isolate, symbol);
            }
            // 2. b. Let s be ? ToString(value).
            s = ToStringInline(isolate, value);
        }
        // 3. If NewTarget is undefined, return s.
        if (args.NewTarget.IsUndefined) return s;

        // We might be creating a string wrapper with a custom @@toPrimitive.
        if (!ReferenceEquals(args.Target, args.NewTarget.HeapObjectOrNull))
        {
            if (Protectors.IsStringWrapperToPrimitiveIntact(isolate)) Protectors.InvalidateStringWrapperToPrimitive(isolate);
        }

        // 4. Return ! StringCreate(s, ? GetPrototypeFromConstructor(NewTarget,
        // "%String.prototype%")).
        Map map = JSFunction.GetDerivedMap(isolate, args.Target, (JSReceiver)args.NewTarget.Object);
        var obj = (JSPrimitiveWrapper)JSObject.NewFastOrSlowJSObjectFromMap(isolate, map);
        obj.Value = s;
        return obj;
    }

    /// <summary>StringFromCharCode (builtins-string-gen.cc).</summary>
    public static JSValue StringFromCharCode(Isolate isolate, in BuiltinArguments args)
    {
        ReadOnlySpan<JSValue> arguments = args.Arguments;
        int argc = arguments.Length;
        if (argc == 1)
        {
            // Single argument case, perform fast single character string cache lookup
            // for one-byte code units, or fall back to creating a single character
            // string on the fly otherwise.
            return isolate.Factory.LookupSingleCharacterStringFromCode(TruncateToUint16(isolate, arguments[0]));
        }
        if (argc == 0) return ReadOnlyRoots.empty_string;
        // Each ToNumber may run user code (valueOf), so arguments are converted in order.
        char[] buffer = new char[argc];
        for (int i = 0; i < argc; i++) buffer[i] = TruncateToUint16(isolate, arguments[i]);
        return isolate.Factory.NewStringFromUtf16(new string(buffer));
    }

    /// <summary>TruncateTaggedToWord32 + TruncateWord32ToUint16.</summary>
    static char TruncateToUint16(Isolate isolate, JSValue value)
    {
        if (value.IsSmi) return (char)(int)value.Number;
        double d = ObjectOps.ToNumber(isolate, value).Number;
        return (char)Conversions.DoubleToInt32(d);
    }

    const uint kInvalidCodePoint = uint.MaxValue;

    /// <summary>NextCodePoint (builtins-string.cc): RangeError for an invalid code point.</summary>
    static uint NextCodePoint(Isolate isolate, JSValue value)
    {
        value = ObjectOps.ToNumber(isolate, value);
        double number = value.Number;
        // IsValidCodePoint.
        if (ObjectOps.DoubleToInteger(number) != number || number < 0 || number > 0x10FFFF)
        {
            isolate.ThrowRangeError(MessageTemplate.InvalidCodePoint, value);
            return kInvalidCodePoint;
        }
        return Conversions.DoubleToUint32(number);
    }

    /// <summary>StringFromCodePoint (builtins-string.cc).</summary>
    public static JSValue StringFromCodePoint(Isolate isolate, in BuiltinArguments args)
    {
        ReadOnlySpan<JSValue> arguments = args.Arguments;
        int length = arguments.Length;
        if (length == 0) return ReadOnlyRoots.empty_string;
        var builder = new IncrementalStringBuilder(isolate);
        for (int index = 0; index < length; index++)
        {
            uint code = NextCodePoint(isolate, arguments[index]);
            if (code <= 0xFFFF)
            {
                builder.AppendCharacter((char)code);
            }
            else
            {
                builder.AppendCharacter((char)(0xD800 + ((code - 0x10000) >> 10)));
                builder.AppendCharacter((char)(0xDC00 + ((code - 0x10000) & 0x3FF)));
            }
        }
        return builder.Finish();
    }

    /// <summary>StringRaw (builtins-string.cc).</summary>
    public static JSValue StringRaw(Isolate isolate, in BuiltinArguments args)
    {
        JSValue templ = args.AtOrUndefined(1);
        int argc = args.Length;
        JSReceiver cooked = ObjectOps.ToObject(isolate, templ);
        JSValue raw = JSReceiver.GetProperty(isolate, cooked, ReadOnlyRoots.raw_string);
        JSReceiver rawObject = ObjectOps.ToObject(isolate, raw);
        JSValue rawLen = JSReceiver.GetProperty(isolate, rawObject, ReadOnlyRoots.length_string);
        rawLen = ObjectOps.ToLength(isolate, rawLen);

        var resultBuilder = new IncrementalStringBuilder(isolate);
        // Intentional spec violation: we ignore {length} values >= 2^32, because
        // assuming non-empty chunks they would generate too-long strings anyway.
        double rawLenNumber = rawLen.Number;
        uint length = rawLenNumber > uint.MaxValue ? uint.MaxValue : (uint)rawLenNumber;
        if (length > 0)
        {
            JSValue firstElement = ObjectOps.GetElement(isolate, rawObject, 0);
            resultBuilder.AppendString(ObjectOps.ToString(isolate, firstElement));
            for (uint i = 1, argI = 2; i < length; i++, argI++)
            {
                if (argI < argc)
                {
                    resultBuilder.AppendString(ObjectOps.ToString(isolate, args[(int)argI]));
                }
                JSValue element = ObjectOps.GetElement(isolate, rawObject, i);
                resultBuilder.AppendString(ObjectOps.ToString(isolate, element));
            }
        }
        return resultBuilder.Finish();
    }

    // ---- toString / valueOf ---------------------------------------------------------------

    /// <summary>ToThisValue(receiver, PrimitiveType::kString, method).</summary>
    static JSValue ToThisStringValue(Isolate isolate, JSValue receiver, string methodName)
    {
        JSValue value = receiver;
        while (true)
        {
            HeapObject? obj = value.HeapObjectOrNull;
            if (obj is JSString) return value;
            if (obj is JSPrimitiveWrapper wrapper)
            {
                value = wrapper.Value;
                continue;
            }
            return ThrowTypeError(isolate, MessageTemplate.NotGeneric, methodName,
                isolate.Factory.NewStringFromAsciiChecked("String"));
        }
    }

    public static JSValue StringPrototypeToString(Isolate isolate, in BuiltinArguments args) =>
        ToThisStringValue(isolate, args.Receiver, "String.prototype.toString");

    public static JSValue StringPrototypeValueOf(Isolate isolate, in BuiltinArguments args) =>
        ToThisStringValue(isolate, args.Receiver, "String.prototype.valueOf");

    // ---- at / charAt / charCodeAt / codePointAt (string-at.tq, builtins-string.tq) --------

    /// <summary>StringPrototypeAt (string-at.tq).</summary>
    public static JSValue StringPrototypeAt(Isolate isolate, in BuiltinArguments args)
    {
        JSString s = ToThisString(isolate, args.Receiver, "String.prototype.at");
        int len = s.Length;
        double relativeIndex = ToIntegerDouble(isolate, args.AtOrUndefined(1));
        double k = relativeIndex >= 0 ? relativeIndex : len + relativeIndex;
        if (k < 0 || k >= len) return JSValue.Undefined;
        return StringFromSingleCharCode(isolate, s.Get((int)k));
    }

    /// <summary>GenerateStringAt: the string and the in-bounds index, or -1.</summary>
    static int GenerateStringAt(Isolate isolate, JSValue receiver, JSValue position, string methodName, out JSString s)
    {
        s = ToThisString(isolate, receiver, methodName);
        double index = ToIntegerDouble(isolate, position);
        if (index < 0 || index >= s.Length) return -1;
        return (int)index;
    }

    public static JSValue StringPrototypeCharAt(Isolate isolate, in BuiltinArguments args)
    {
        int index = GenerateStringAt(isolate, args.Receiver, args.AtOrUndefined(1), "String.prototype.charAt", out JSString s);
        if (index < 0) return ReadOnlyRoots.empty_string;
        return StringFromSingleCharCode(isolate, s.Get(index));
    }

    public static JSValue StringPrototypeCharCodeAt(Isolate isolate, in BuiltinArguments args)
    {
        int index = GenerateStringAt(isolate, args.Receiver, args.AtOrUndefined(1), "String.prototype.charCodeAt", out JSString s);
        if (index < 0) return JSValue.NaN;
        return JSValue.FromInt(s.Get(index));
    }

    public static JSValue StringPrototypeCodePointAt(Isolate isolate, in BuiltinArguments args)
    {
        int index = GenerateStringAt(isolate, args.Receiver, args.AtOrUndefined(1), "String.prototype.codePointAt", out JSString s);
        if (index < 0) return JSValue.Undefined;
        // This is always a call to a builtin from Javascript, so we need to
        // produce UTF32.
        return JSValue.FromInt(LoadSurrogatePairAtUtf32(s.FlatSpan(), index));
    }

    /// <summary>StringPrototypeConcat (builtins-string.tq).</summary>
    public static JSValue StringPrototypeConcat(Isolate isolate, in BuiltinArguments args)
    {
        JSString s = ToThisString(isolate, args.Receiver, "String.prototype.concat");
        ReadOnlySpan<JSValue> arguments = args.Arguments;
        for (int i = 0; i < arguments.Length; i++)
        {
            JSString temp = ToStringInline(isolate, arguments[i]);
            s = isolate.Factory.NewConsString(s, temp);
        }
        return s;
    }

    // ---- Searching ------------------------------------------------------------------------

    /// <summary>StringIndexOf (builtins-string-gen / Torque): the first match at or after start, or -1.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static int StringIndexOf(JSString s, JSString search, int start) =>
        StringSearch.IndexOf(s.FlatSpan(), search.FlatSpan(), start);

    /// <summary>StringPrototypeIncludes (string-includes.tq).</summary>
    public static JSValue StringPrototypeIncludes(Isolate isolate, in BuiltinArguments args)
    {
        const string methodName = "String.prototype.includes";
        JSValue searchString = args.AtOrUndefined(1);
        JSValue position = args.AtOrUndefined(2);
        JSString s = ToThisString(isolate, args.Receiver, methodName);
        // 3. Let isRegExp be ? IsRegExp(searchString).
        // 4. If isRegExp is true, throw a TypeError exception.
        if (BuiltinsRegExp.IsRegExp(isolate, searchString))
        {
            return ThrowTypeError(isolate, MessageTemplate.FirstArgumentNotRegExp, methodName);
        }
        JSString searchStr = ToStringInline(isolate, searchString);
        int start = 0;
        if (!position.IsUndefined) start = ClampToIndexRange(isolate, position, s.Length);
        return JSValue.FromBoolean(StringIndexOf(s, searchStr, start) != -1);
    }

    /// <summary>StringPrototypeIndexOf (string-indexof.tq).</summary>
    public static JSValue StringPrototypeIndexOf(Isolate isolate, in BuiltinArguments args)
    {
        JSValue searchString = args.AtOrUndefined(1);
        JSValue position = args.AtOrUndefined(2);
        JSString s = ToThisString(isolate, args.Receiver, "String.prototype.indexOf");
        JSString searchStr = ToStringInline(isolate, searchString);
        int start = 0;
        if (!position.IsUndefined) start = ClampToIndexRange(isolate, position, s.Length);
        return JSValue.FromInt(StringIndexOf(s, searchStr, start));
    }

    /// <summary>StringPrototypeLastIndexOf: String::LastIndexOf (objects/string.cc).</summary>
    public static JSValue StringPrototypeLastIndexOf(Isolate isolate, in BuiltinArguments args)
    {
        JSValue receiver = args.Receiver;
        if (receiver.IsNullOrUndefined)
        {
            return ThrowTypeError(isolate, MessageTemplate.CalledOnNullOrUndefined, "String.prototype.lastIndexOf");
        }
        JSString receiverString = ObjectOps.ToString(isolate, receiver);
        JSString searchString = ObjectOps.ToString(isolate, args.AtOrUndefined(1));
        JSValue position = ObjectOps.ToNumber(isolate, args.AtOrUndefined(2));

        uint startIndex;
        if (double.IsNaN(position.Number))
        {
            startIndex = (uint)receiverString.Length;
        }
        else
        {
            // ToValidIndex: clamp ToInteger(position) to [0, length].
            double d = ObjectOps.DoubleToInteger(position.Number);
            startIndex = d < 0 ? 0 : d > receiverString.Length ? (uint)receiverString.Length : (uint)d;
        }

        uint patternLength = (uint)searchString.Length;
        uint receiverLength = (uint)receiverString.Length;
        if (startIndex + patternLength > receiverLength)
        {
            startIndex = receiverLength - patternLength;
        }
        if (patternLength == 0) return JSValue.FromNumber(startIndex);
        if ((int)startIndex < 0) return JSValue.FromInt(-1);
        return JSValue.FromInt(StringSearch.LastIndexOf(receiverString.FlatSpan(), searchString.FlatSpan(), (int)startIndex));
    }

    /// <summary>IsSubstringAt (string-endswith.tq).</summary>
    static bool IsSubstringAt(JSString s, JSString search, int start)
    {
        ReadOnlySpan<char> str = s.FlatSpan();
        ReadOnlySpan<char> sub = search.FlatSpan();
        if (start < 0 || (long)start + sub.Length > str.Length) return false;
        return str.Slice(start, sub.Length).SequenceEqual(sub);
    }

    /// <summary>StringPrototypeStartsWith (string-startswith.tq).</summary>
    public static JSValue StringPrototypeStartsWith(Isolate isolate, in BuiltinArguments args)
    {
        const string kBuiltinName = "String.prototype.startsWith";
        JSValue searchString = args.AtOrUndefined(1);
        JSValue position = args.AtOrUndefined(2);
        JSString s = ToThisString(isolate, args.Receiver, kBuiltinName);
        if (BuiltinsRegExp.IsRegExp(isolate, searchString))
        {
            return ThrowTypeError(isolate, MessageTemplate.FirstArgumentNotRegExp, kBuiltinName);
        }
        JSString searchStr = ToStringInline(isolate, searchString);
        int len = s.Length;
        int start = !position.IsUndefined ? ClampToIndexRange(isolate, position, len) : 0;
        int searchLength = searchStr.Length;
        // 11. If searchLength + start is greater than len, return false.
        if (searchLength > len - start) return JSValue.False;
        return JSValue.FromBoolean(IsSubstringAt(s, searchStr, start));
    }

    /// <summary>StringPrototypeEndsWith (string-endswith.tq).</summary>
    public static JSValue StringPrototypeEndsWith(Isolate isolate, in BuiltinArguments args)
    {
        const string kBuiltinName = "String.prototype.endsWith";
        JSValue searchString = args.AtOrUndefined(1);
        JSValue endPosition = args.AtOrUndefined(2);
        JSString s = ToThisString(isolate, args.Receiver, kBuiltinName);
        if (BuiltinsRegExp.IsRegExp(isolate, searchString))
        {
            return ThrowTypeError(isolate, MessageTemplate.FirstArgumentNotRegExp, kBuiltinName);
        }
        JSString searchStr = ToStringInline(isolate, searchString);
        int len = s.Length;
        int end = !endPosition.IsUndefined ? ClampToIndexRange(isolate, endPosition, len) : len;
        int start = end - searchStr.Length;
        if (start < 0) return JSValue.False;
        return JSValue.FromBoolean(IsSubstringAt(s, searchStr, start));
    }

    // ---- slice / substr / substring ---------------------------------------------------

    /// <summary>StringPrototypeSlice (string-slice.tq).</summary>
    public static JSValue StringPrototypeSlice(Isolate isolate, in BuiltinArguments args)
    {
        JSString s = ToThisString(isolate, args.Receiver, "String.prototype.slice");
        int length = s.Length;
        JSValue arg0 = args.AtOrUndefined(1);
        int start = !arg0.IsUndefined ? ConvertAndClampRelativeIndex(isolate, arg0, length) : 0;
        JSValue arg1 = args.AtOrUndefined(2);
        int end = !arg1.IsUndefined ? ConvertAndClampRelativeIndex(isolate, arg1, length) : length;
        if (end <= start) return ReadOnlyRoots.empty_string;
        return SubString(isolate, s, start, end);
    }

    /// <summary>StringPrototypeSubstr (string-substr.tq).</summary>
    public static JSValue StringPrototypeSubstr(Isolate isolate, in BuiltinArguments args)
    {
        JSString s = ToThisString(isolate, args.Receiver, "String.prototype.substr");
        int size = s.Length;
        JSValue start = args.AtOrUndefined(1);
        int initStart = !start.IsUndefined ? ConvertAndClampRelativeIndex(isolate, start, size) : 0;
        JSValue length = args.AtOrUndefined(2);
        int lengthLimit = size - initStart;
        int resultLength = !length.IsUndefined ? ClampToIndexRange(isolate, length, lengthLimit) : lengthLimit;
        if (resultLength == 0) return ReadOnlyRoots.empty_string;
        return SubString(isolate, s, initStart, initStart + resultLength);
    }

    /// <summary>StringPrototypeSubstring (string-substring.tq).</summary>
    public static JSValue StringPrototypeSubstring(Isolate isolate, in BuiltinArguments args)
    {
        JSString s = ToThisString(isolate, args.Receiver, "String.prototype.substring");
        int length = s.Length;
        JSValue arg0 = args.AtOrUndefined(1);
        int start = !arg0.IsUndefined ? ClampToIndexRange(isolate, arg0, length) : 0;
        JSValue arg1 = args.AtOrUndefined(2);
        int end = !arg1.IsUndefined ? ClampToIndexRange(isolate, arg1, length) : length;
        if (end < start) (start, end) = (end, start);
        return SubString(isolate, s, start, end);
    }

    // ---- padStart / padEnd / repeat -------------------------------------------------------

    const int kStringPadStart = 0;
    const int kStringPadEnd = 1;

    /// <summary>StringPad (string-pad.tq).</summary>
    static JSValue StringPad(Isolate isolate, in BuiltinArguments args, string methodName, int variant)
    {
        JSString receiverString = ToThisString(isolate, args.Receiver, methodName);
        int stringLength = receiverString.Length;
        if (args.ArgcWithoutReceiver == 0) return receiverString;

        JSValue maxLength = ObjectOps.ToLength(isolate, args.Arguments[0]);
        if (maxLength.IsSmi && (int)maxLength.Number <= stringLength) return receiverString;

        JSString fillString = ReadOnlyRoots.SingleCharacterStringTable[' '];
        int fillLength = 1;
        if (args.ArgcWithoutReceiver != 1)
        {
            JSValue fill = args.Arguments[1];
            if (!fill.IsUndefined)
            {
                fillString = ToStringInline(isolate, fill);
                fillLength = fillString.Length;
                if (fillLength == 0) return receiverString;
            }
        }

        // Throw if max_length is greater than String::kMaxLength.
        if (!maxLength.IsSmi || (int)maxLength.Number > JSString.kMaxLength)
        {
            return isolate.Throw(isolate.Factory.NewInvalidStringLengthError());
        }
        int smiMaxLength = (int)maxLength.Number;
        int padLength = smiMaxLength - stringLength;

        // Build the padding flat: V8 builds it with StringRepeat (a cons-string
        // doubling), the result is the same string.
        ReadOnlySpan<char> fill1 = fillString.FlatSpan();
        ReadOnlySpan<char> receiver = receiverString.FlatSpan();
        char[] buffer = new char[smiMaxLength];
        Span<char> padding = variant == kStringPadStart ? buffer.AsSpan(0, padLength) : buffer.AsSpan(stringLength, padLength);
        if (fillLength == 1)
        {
            padding.Fill(fill1[0]);
        }
        else
        {
            for (int i = 0; i < padLength; i += fillLength)
            {
                ReadOnlySpan<char> part = fill1;
                if (part.Length > padLength - i) part = part[..(padLength - i)];
                part.CopyTo(padding[i..]);
            }
        }
        receiver.CopyTo(variant == kStringPadStart ? buffer.AsSpan(padLength) : buffer.AsSpan(0, stringLength));
        return new SeqString(new string(buffer));
    }

    public static JSValue StringPrototypePadStart(Isolate isolate, in BuiltinArguments args) =>
        StringPad(isolate, in args, "String.prototype.padStart", kStringPadStart);

    public static JSValue StringPrototypePadEnd(Isolate isolate, in BuiltinArguments args) =>
        StringPad(isolate, in args, "String.prototype.padEnd", kStringPadEnd);

    /// <summary>StringRepeat (string-repeat.tq): count copies of a non-empty string.</summary>
    internal static JSString StringRepeat(Isolate isolate, JSString s, int count)
    {
        Debug.Assert(count >= 0);
        if (count == 0) return ReadOnlyRoots.empty_string;
        if (count == 1) return s;
        long total = (long)s.Length * count;
        if (total > JSString.kMaxLength) return (JSString)isolate.Throw(isolate.Factory.NewInvalidStringLengthError()).Object;
        ReadOnlySpan<char> part = s.FlatSpan();
        if (part.Length == 1) return new SeqString(new string(part[0], count));
        string result = string.Create((int)total, s, static (span, str) =>
        {
            ReadOnlySpan<char> p = str.FlatSpan();
            p.CopyTo(span);
            // Doubling copies, as V8's powerOfTwoRepeats.
            int filled = p.Length;
            while (filled < span.Length)
            {
                int n = Math.Min(filled, span.Length - filled);
                span[..n].CopyTo(span[filled..]);
                filled += n;
            }
        });
        return new SeqString(result);
    }

    /// <summary>StringPrototypeRepeat (string-repeat.tq).</summary>
    public static JSValue StringPrototypeRepeat(Isolate isolate, in BuiltinArguments args)
    {
        JSValue count = args.AtOrUndefined(1);
        JSString s = ToThisString(isolate, args.Receiver, "String.prototype.repeat");
        double n = ToIntegerDouble(isolate, count);
        if (n >= JSValue.SmiMinValue && n <= JSValue.SmiMaxValue)
        {
            // 4. If n < 0, throw a RangeError exception.
            if (n < 0) return isolate.ThrowRangeError(MessageTemplate.InvalidCountValue, count);
            // 6. If n is 0, return the empty String.
            if (n == 0 || s.Length == 0) return ReadOnlyRoots.empty_string;
            if (n > JSString.kMaxLength) return isolate.Throw(isolate.Factory.NewInvalidStringLengthError());
            return StringRepeat(isolate, s, (int)n);
        }
        // 4. If n < 0, throw a RangeError exception.
        // 5. If n is +∞, throw a RangeError exception.
        if (double.IsPositiveInfinity(n) || n < 0) return isolate.ThrowRangeError(MessageTemplate.InvalidCountValue, count);
        // 6. If n is 0, return the empty String.
        if (s.Length == 0) return ReadOnlyRoots.empty_string;
        return isolate.Throw(isolate.Factory.NewInvalidStringLengthError());
    }

    // ---- trim (string-trim.tq) --------------------------------------------------------------

    enum TrimMode { kTrim, kTrimStart, kTrimEnd }

    /// <summary>IsWhiteSpaceOrLineTerminator (string-trim.tq).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static bool IsWhiteSpaceOrLineTerminator(int charCode)
    {
        // 0x0020 - SPACE (Intentionally out of order to fast path a common case)
        if (charCode == 0x0020) return true;
        // Common Non-whitespace characters from (0x000E, 0x00A0)
        if ((uint)(charCode - 0x000E) < 0x0092) return false;
        // 0x0009 - HORIZONTAL TAB
        if (charCode < 0x0009) return false;
        // 0x000A - LINE FEED OR NEW LINE, 0x000B - VERTICAL TAB, 0x000C - FORMFEED,
        // 0x000D - CARRIAGE RETURN
        if (charCode <= 0x000D) return true;
        // 0x00A0 - NO-BREAK SPACE
        if (charCode == 0x00A0) return true;
        // 0x1680 - Ogham Space Mark
        if (charCode == 0x1680) return true;
        // 0x2000 - EN QUAD
        if (charCode < 0x2000) return false;
        // 0x2001 .. 0x200A: EM QUAD .. HAIR SPACE
        if (charCode <= 0x200A) return true;
        // 0x2028 - LINE SEPARATOR, 0x2029 - PARAGRAPH SEPARATOR,
        // 0x202F - NARROW NO-BREAK SPACE, 0x205F - MEDIUM MATHEMATICAL SPACE,
        // 0xFEFF - BYTE ORDER MARK, 0x3000 - IDEOGRAPHIC SPACE
        return charCode is 0x2028 or 0x2029 or 0x202F or 0x205F or 0xFEFF or 0x3000;
    }

    /// <summary>StringTrim / StringTrimBody (string-trim.tq).</summary>
    static JSValue StringTrim(Isolate isolate, JSValue receiver, string methodName, TrimMode variant)
    {
        JSString s = ToThisString(isolate, receiver, methodName);
        ReadOnlySpan<char> slice = s.FlatSpan();
        int stringLength = slice.Length;
        int startIndex = 0;
        int endIndex = stringLength - 1;
        if (variant is TrimMode.kTrim or TrimMode.kTrimStart)
        {
            while (startIndex != stringLength && IsWhiteSpaceOrLineTerminator(slice[startIndex])) startIndex++;
            if (startIndex == stringLength) return ReadOnlyRoots.empty_string;
        }
        if (variant is TrimMode.kTrim or TrimMode.kTrimEnd)
        {
            while (endIndex != -1 && IsWhiteSpaceOrLineTerminator(slice[endIndex])) endIndex--;
            if (endIndex == -1) return ReadOnlyRoots.empty_string;
        }
        return SubString(isolate, s, startIndex, endIndex + 1);
    }

    public static JSValue StringPrototypeTrim(Isolate isolate, in BuiltinArguments args) =>
        StringTrim(isolate, args.Receiver, "String.prototype.trim", TrimMode.kTrim);

    public static JSValue StringPrototypeTrimStart(Isolate isolate, in BuiltinArguments args) =>
        StringTrim(isolate, args.Receiver, "String.prototype.trimLeft", TrimMode.kTrimStart);

    public static JSValue StringPrototypeTrimEnd(Isolate isolate, in BuiltinArguments args) =>
        StringTrim(isolate, args.Receiver, "String.prototype.trimRight", TrimMode.kTrimEnd);

    // ---- isWellFormed / toWellFormed ------------------------------------------------------

    /// <summary>StringPrototypeIsWellFormed (string-iswellformed.tq).</summary>
    public static JSValue StringPrototypeIsWellFormed(Isolate isolate, in BuiltinArguments args)
    {
        JSString s = ToThisString(isolate, args.Receiver, "String.prototype.isWellFormed");
        return JSValue.FromBoolean(!HasUnpairedSurrogate(s.FlatSpan()));
    }

    /// <summary>StringPrototypeToWellFormed (string-towellformed.tq).</summary>
    public static JSValue StringPrototypeToWellFormed(Isolate isolate, in BuiltinArguments args)
    {
        JSString s = ToThisString(isolate, args.Receiver, "String.prototype.toWellFormed");
        ReadOnlySpan<char> flat = s.FlatSpan();
        int first = FirstUnpairedSurrogate(flat);
        if (first < 0) return s;
        // unibrow::Utf16::ReplaceUnpairedSurrogates.
        char[] result = flat.ToArray();
        for (int i = first; i < result.Length; i++)
        {
            char c = result[i];
            if (char.IsHighSurrogate(c))
            {
                if (i + 1 < result.Length && char.IsLowSurrogate(result[i + 1])) { i++; continue; }
                result[i] = '�';
            }
            else if (char.IsLowSurrogate(c))
            {
                result[i] = '�';
            }
        }
        return new SeqString(new string(result));
    }

    /// <summary>has_unpaired_surrogate (unibrow::Utf16::HasUnpairedSurrogate).</summary>
    internal static bool HasUnpairedSurrogate(ReadOnlySpan<char> s) => FirstUnpairedSurrogate(s) >= 0;

    static int FirstUnpairedSurrogate(ReadOnlySpan<char> s)
    {
        int i = s.IndexOfAnyInRange('\uD800', '\uDFFF');
        if (i < 0) return -1;
        for (; i < s.Length; i++)
        {
            char c = s[i];
            if (char.IsHighSurrogate(c))
            {
                if (i + 1 < s.Length && char.IsLowSurrogate(s[i + 1])) { i++; continue; }
                return i;
            }
            if (char.IsLowSurrogate(c)) return i;
        }
        return -1;
    }

    // ---- localeCompare / normalize (non-ICU) ------------------------------------------

    /// <summary>
    /// StringPrototypeLocaleCompare (builtins-string.cc, !V8_INTL_SUPPORT): code
    /// unit order, returning -1/0/1.
    /// </summary>
    public static JSValue StringPrototypeLocaleCompare(Isolate isolate, in BuiltinArguments args)
    {
        isolate.CountUsage("kStringLocaleCompare");
        JSString str1 = ToThisString(isolate, args.Receiver, "String.prototype.localeCompare");
        JSString str2 = ObjectOps.ToString(isolate, args.AtOrUndefined(1));
        if (ReferenceEquals(str1, str2)) return JSValue.Zero;  // Equal.
        int str1Length = str1.Length;
        int str2Length = str2.Length;
        // Decide trivial cases without flattening.
        if (str1Length == 0) return str2Length == 0 ? JSValue.Zero : JSValue.FromInt(-1);
        if (str2Length == 0) return JSValue.FromInt(1);
        int d = str1.Get(0) - str2.Get(0);
        if (d != 0) return JSValue.FromInt(Math.Sign(d));
        ReadOnlySpan<char> flat1 = str1.FlatSpan();
        ReadOnlySpan<char> flat2 = str2.FlatSpan();
        int end = Math.Min(str1Length, str2Length);
        int mismatch = flat1[..end].CommonPrefixLength(flat2[..end]);
        if (mismatch < end) return JSValue.FromInt(Math.Sign(flat1[mismatch] - flat2[mismatch]));
        return JSValue.FromInt(Math.Sign(str1Length - str2Length));
    }

    /// <summary>
    /// StringPrototypeNormalize (builtins-string.cc, !V8_INTL_SUPPORT): checks
    /// the form argument and returns the string unchanged.
    /// </summary>
    public static JSValue StringPrototypeNormalize(Isolate isolate, in BuiltinArguments args)
    {
        JSString s = ToThisString(isolate, args.Receiver, "String.prototype.normalize");
        JSValue formInput = args.AtOrUndefined(1);
        if (formInput.IsUndefined) return s;
        JSString form = ObjectOps.ToString(isolate, formInput);
        if (!(JSString.Equals(form, ReadOnlyRoots.NFC_string) || JSString.Equals(form, ReadOnlyRoots.NFD_string) ||
              JSString.Equals(form, ReadOnlyRoots.NFKC_string) || JSString.Equals(form, ReadOnlyRoots.NFKD_string)))
        {
            JSString validForms = isolate.Factory.NewStringFromAsciiChecked("NFC, NFD, NFKC, NFKD");
            return isolate.ThrowRangeError(MessageTemplate.NormalizationForm, validForms);
        }
        return s;
    }

    // ---- HTML methods (string-html.tq) -------------------------------------------------

    /// <summary>CreateHTML (string-html.tq).</summary>
    static JSValue CreateHTML(Isolate isolate, JSValue receiver, string methodName, string tagName, string attr,
        JSValue attrValue)
    {
        isolate.CountUsage("kHtmlWrapperMethods");
        JSString tagContents = ToThisString(isolate, receiver, methodName);
        var builder = new IncrementalStringBuilder(isolate);
        builder.AppendCharacter('<');
        builder.AppendCStringLiteral(tagName);
        if (attr.Length != 0)
        {
            JSString attrStringValue = RuntimeStrings.StringEscapeQuotes(isolate, ToStringInline(isolate, attrValue));
            builder.AppendCharacter(' ');
            builder.AppendCStringLiteral(attr);
            builder.AppendCStringLiteral("=\"");
            builder.AppendString(attrStringValue);
            builder.AppendCharacter('"');
        }
        builder.AppendCharacter('>');
        builder.AppendString(tagContents);
        builder.AppendCStringLiteral("</");
        builder.AppendCStringLiteral(tagName);
        builder.AppendCharacter('>');
        return builder.Finish();
    }

    public static JSValue StringPrototypeAnchor(Isolate isolate, in BuiltinArguments args) =>
        CreateHTML(isolate, args.Receiver, "String.prototype.anchor", "a", "name", args.AtOrUndefined(1));

    public static JSValue StringPrototypeBig(Isolate isolate, in BuiltinArguments args) =>
        CreateHTML(isolate, args.Receiver, "String.prototype.big", "big", "", ReadOnlyRoots.empty_string);

    public static JSValue StringPrototypeBlink(Isolate isolate, in BuiltinArguments args) =>
        CreateHTML(isolate, args.Receiver, "String.prototype.blink", "blink", "", ReadOnlyRoots.empty_string);

    public static JSValue StringPrototypeBold(Isolate isolate, in BuiltinArguments args) =>
        CreateHTML(isolate, args.Receiver, "String.prototype.bold", "b", "", ReadOnlyRoots.empty_string);

    public static JSValue StringPrototypeFontcolor(Isolate isolate, in BuiltinArguments args) =>
        CreateHTML(isolate, args.Receiver, "String.prototype.fontcolor", "font", "color", args.AtOrUndefined(1));

    public static JSValue StringPrototypeFontsize(Isolate isolate, in BuiltinArguments args) =>
        CreateHTML(isolate, args.Receiver, "String.prototype.fontsize", "font", "size", args.AtOrUndefined(1));

    public static JSValue StringPrototypeFixed(Isolate isolate, in BuiltinArguments args) =>
        CreateHTML(isolate, args.Receiver, "String.prototype.fixed", "tt", "", ReadOnlyRoots.empty_string);

    public static JSValue StringPrototypeItalics(Isolate isolate, in BuiltinArguments args) =>
        CreateHTML(isolate, args.Receiver, "String.prototype.italics", "i", "", ReadOnlyRoots.empty_string);

    public static JSValue StringPrototypeLink(Isolate isolate, in BuiltinArguments args) =>
        CreateHTML(isolate, args.Receiver, "String.prototype.link", "a", "href", args.AtOrUndefined(1));

    public static JSValue StringPrototypeSmall(Isolate isolate, in BuiltinArguments args) =>
        CreateHTML(isolate, args.Receiver, "String.prototype.small", "small", "", ReadOnlyRoots.empty_string);

    public static JSValue StringPrototypeStrike(Isolate isolate, in BuiltinArguments args) =>
        CreateHTML(isolate, args.Receiver, "String.prototype.strike", "strike", "", ReadOnlyRoots.empty_string);

    public static JSValue StringPrototypeSub(Isolate isolate, in BuiltinArguments args) =>
        CreateHTML(isolate, args.Receiver, "String.prototype.sub", "sub", "", ReadOnlyRoots.empty_string);

    public static JSValue StringPrototypeSup(Isolate isolate, in BuiltinArguments args) =>
        CreateHTML(isolate, args.Receiver, "String.prototype.sup", "sup", "", ReadOnlyRoots.empty_string);

    // ---- Iterator (string-iterator.tq) -----------------------------------------------------

    /// <summary>StringPrototypeIterator.</summary>
    public static JSValue StringPrototypeIterator(Isolate isolate, in BuiltinArguments args)
    {
        JSString name = ToThisString(isolate, args.Receiver, "String.prototype[Symbol.iterator]");
        var iterator = (JSStringIterator)isolate.Factory.NewJSObjectFromMap(isolate.NativeContext.InitialStringIteratorMap);
        iterator.String = name;
        iterator.Index = 0;
        return iterator;
    }

    /// <summary>StringIteratorPrototypeNext.</summary>
    public static JSValue StringIteratorPrototypeNext(Isolate isolate, in BuiltinArguments args)
    {
        if (args.Receiver.HeapObjectOrNull is not JSStringIterator iterator)
        {
            return ThrowTypeError(isolate, MessageTemplate.IncompatibleMethodReceiver, "String Iterator.prototype.next",
                args.Receiver);
        }
        JSString s = iterator.String;
        int position = iterator.Index;
        int length = s.Length;
        if (position >= length) return CreateIterResultObject(isolate, JSValue.Undefined, true);
        // Move to next codepoint.
        JSString value = StringFromCodePointAt(isolate, s, s.FlatSpan(), position);
        iterator.Index = position + value.Length;
        return CreateIterResultObject(isolate, value, false);
    }

    /// <summary>
    /// StringToList (builtins-string.tq): the code points of the string as an
    /// array of strings (the spread/Array.from fast path).
    /// </summary>
    public static JSArray StringToList(Isolate isolate, JSString s)
    {
        ReadOnlySpan<char> flat = s.FlatSpan();
        var elements = new FixedArray(flat.Length);
        int arrayLength = 0;
        int i = 0;
        while (i < flat.Length)
        {
            JSString value = StringFromCodePointAt(isolate, s, flat, i);
            elements[arrayLength++] = value;
            i += value.Length;
        }
        if (arrayLength < flat.Length)
        {
            var trimmed = new FixedArray(arrayLength);
            elements.Data.AsSpan(0, arrayLength).CopyTo(trimmed.Data);
            elements = trimmed;
        }
        return isolate.Factory.NewJSArrayWithElements(elements);
    }
}
