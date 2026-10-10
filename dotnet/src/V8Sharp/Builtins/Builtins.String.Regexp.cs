// Port of the String.prototype methods that dispatch to a regexp:
// replace, matchAll and split (src/builtins/builtins-string-gen.cc, with
// MaybeCallFunctionAtSymbol), match and search (string-match-search.tq) and
// replaceAll (string-replaceall.tq).
using V8Sharp.RegExp;
using V8Sharp.Runtime;

namespace V8Sharp.Builtins;

public static partial class BuiltinsString
{
    /// <summary>Which of the regexp fast paths MaybeCallFunctionAtSymbol takes.</summary>
    enum SymbolCall { Replace, MatchAll, Split }

    /// <summary>
    /// StringBuiltinsAssembler::MaybeCallFunctionAtSymbol: if <paramref name="obj"/>
    /// is an object with a callable <paramref name="symbol"/> method, calls it
    /// (directly into the regexp builtin when obj is an unmodified regexp and
    /// <paramref name="maybeString"/> a string) and returns true.
    /// </summary>
    static bool MaybeCallFunctionAtSymbol(Isolate isolate, JSValue obj, JSValue maybeString, Symbol symbol,
        in BuiltinsRegExp.DescriptorIndexNameValue additionalPropertyToCheck, SymbolCall kind, JSValue arg2,
        out JSValue result)
    {
        result = default;
        if (obj.HeapObjectOrNull is not JSReceiver receiver) return false;

        // Take the fast path for RegExps.
        // There's two conditions: {object} needs to be a fast regexp, and
        // {maybe_string} must be a string (we can't call ToString on the fast path
        // since it may mutate {object}).
        if (maybeString.HeapObjectOrNull is JSString s &&
            BuiltinsRegExp.IsFastRegExp(isolate, obj, BuiltinsRegExp.PrototypeCheck.kCheckPrototypePropertyConstness,
                additionalPropertyToCheck))
        {
            var regexp = (JSRegExp)receiver;
            result = kind switch
            {
                SymbolCall.Replace => BuiltinsRegExp.RegExpReplace(isolate, regexp, s, arg2),
                SymbolCall.Split => BuiltinsRegExp.RegExpSplit(isolate, regexp, s, arg2),
                // MaybeCallFunctionAtSymbol guarantees fast path is chosen only if
                // maybe_regexp is a fast regexp and receiver is a string.
                _ => BuiltinsRegExp.RegExpPrototypeMatchAllImpl(isolate, regexp, s),
            };
            return true;
        }

        // Fall back to a slow lookup of {heap_object[symbol]}.
        //
        // The spec uses GetMethod({heap_object}, {symbol}), which has a few quirks:
        // * null values are turned into undefined, and
        // * an exception is thrown if the value is not undefined, null, or callable.
        // We handle the former by jumping to {out} for null values as well, while
        // the latter is already handled by the Call({maybe_func}) operation.
        JSValue maybeFunc = JSReceiver.GetProperty(isolate, receiver, symbol);
        if (maybeFunc.IsNullOrUndefined) return false;

        // Attempt to call the function.
        result = kind == SymbolCall.MatchAll
            ? Execution.Call(isolate, maybeFunc, obj, [maybeString])
            : Execution.Call(isolate, maybeFunc, obj, [maybeString, arg2]);
        return true;
    }

    /// <summary>StringPrototypeReplace (builtins-string-gen.cc).</summary>
    public static JSValue StringPrototypeReplace(Isolate isolate, in BuiltinArguments args)
    {
        JSValue receiver = args.Receiver;
        JSValue search = args.AtOrUndefined(1);
        JSValue replace = args.AtOrUndefined(2);
        RequireObjectCoercible(isolate, receiver, "String.prototype.replace");

        // Redirect to replacer method if {search} is an Object and
        // {search[@@replace]} is not undefined.
        if (MaybeCallFunctionAtSymbol(isolate, search, receiver, ReadOnlyRoots.replace_symbol,
                BuiltinsRegExp.kReplaceDescriptor, SymbolCall.Replace, replace, out JSValue result))
        {
            return result;
        }

        // Convert {receiver} and {search} to strings.
        JSString subjectString = ToStringInline(isolate, receiver);
        JSString searchString = ToStringInline(isolate, search);
        int subjectLength = subjectString.Length;
        int searchLength = searchString.Length;

        // Fast-path single-char {search}, long cons {receiver}, and simple string
        // {replace}.
        if (searchLength == 1 && subjectLength > 0xFF && replace.HeapObjectOrNull is JSString replaceStr &&
            subjectString is ConsString { IsFlat: false } &&
            StringSearch.IndexOf(replaceStr.FlatSpan(), '$', 0) < 0)
        {
            // Searching by traversing a cons string tree and replace with cons of
            // slices works only when the replaced string is a single character, being
            // replaced by a simple string and only pays off for long strings.
            return RuntimeStrings.StringReplaceOneCharWithString(isolate, subjectString, searchString, replaceStr);
        }

        int matchStartIndex = StringIndexOf(subjectString, searchString, 0);

        // Early exit if no match found.
        if (matchStartIndex < 0)
        {
            // The spec requires to perform ToString(replace) if the {replace} is not
            // callable even if we are going to exit here.
            // Since ToString() being applied to Smi does not have side effects for
            // numbers we can skip it.
            if (!replace.IsSmi && !ObjectOps.IsCallable(replace)) ToStringInline(isolate, replace);
            return subjectString;
        }

        int matchEndIndex = matchStartIndex + searchLength;
        Factory factory = isolate.Factory;
        JSString varResult = ReadOnlyRoots.empty_string;

        // Compute the prefix.
        if (matchStartIndex != 0) varResult = SubString(isolate, subjectString, 0, matchStartIndex);

        // Compute the string to replace with.
        if (ObjectOps.IsCallable(replace))
        {
            JSValue replacement = Execution.Call(isolate, replace, JSValue.Undefined,
                [searchString, JSValue.FromInt(matchStartIndex), subjectString]);
            JSString replacementString = ToStringInline(isolate, replacement);
            varResult = factory.NewConsString(varResult, replacementString);
        }
        else
        {
            JSString replaceString = ToStringInline(isolate, replace);
            JSString replacement = RuntimeStrings.GetSubstitution(isolate, subjectString, matchStartIndex, matchEndIndex,
                replaceString);
            varResult = factory.NewConsString(varResult, replacement);
        }

        JSString suffix = SubString(isolate, subjectString, matchEndIndex, subjectLength);
        return factory.NewConsString(varResult, suffix);
    }

    /// <summary>ThrowIfNotGlobal (string-replaceall.tq).</summary>
    static void ThrowIfNotGlobal(Isolate isolate, JSValue searchValue)
    {
        bool shouldThrow;
        if (BuiltinsRegExp.IsFastRegExpPermissive(isolate, searchValue))
        {
            shouldThrow = (((JSRegExp)searchValue.Object).Flags & RegExpFlags.Global) == 0;
        }
        else
        {
            JSValue flags = ObjectOps.GetProperty(isolate, searchValue, ReadOnlyRoots.flags_string);
            RequireObjectCoercible(isolate, flags, "String.prototype.replaceAll");
            shouldThrow = !ToStringInline(isolate, flags).FlatSpan().Contains('g');
        }
        if (shouldThrow)
        {
            ThrowTypeError(isolate, MessageTemplate.RegExpGlobalInvokedOnNonGlobal, "String.prototype.replaceAll");
        }
    }

    /// <summary>StringPrototypeReplaceAll (string-replaceall.tq).</summary>
    public static JSValue StringPrototypeReplaceAll(Isolate isolate, in BuiltinArguments args)
    {
        isolate.CountUsage("kStringReplaceAll");
        JSValue receiver = args.Receiver;
        JSValue searchValue = args.AtOrUndefined(1);
        JSValue replaceValue = args.AtOrUndefined(2);
        // 1. Let O be ? RequireObjectCoercible(this value).
        RequireObjectCoercible(isolate, receiver, "String.prototype.replaceAll");

        // 2. If searchValue is an Object, then
        if (searchValue.HeapObjectOrNull is JSReceiver searchReceiver)
        {
            // a. Let isRegExp be ? IsRegExp(searchString).
            // b. If isRegExp is true, then
            //   i. Let flags be ? Get(searchValue, "flags").
            //  ii. Perform ? RequireObjectCoercible(flags).
            // iii. If ? ToString(flags) does not contain "g", throw a
            //      TypeError exception.
            if (BuiltinsRegExp.IsRegExp(isolate, searchValue)) ThrowIfNotGlobal(isolate, searchValue);

            // c. Let replacer be ? GetMethod(searchValue, %Symbol.replace%).
            // d. If replacer is not undefined, then
            //   i. Return ? Call(replacer, searchValue, « O, replaceValue »).
            JSValue replacer = ObjectOps.GetMethod(isolate, searchReceiver, ReadOnlyRoots.replace_symbol);
            if (!replacer.IsUndefined) return Execution.Call(isolate, replacer, searchValue, [receiver, replaceValue]);
        }

        // 3. Let string be ? ToString(O).
        JSString s = ToStringInline(isolate, receiver);
        // 4. Let searchString be ? ToString(searchValue).
        JSString searchString = ToStringInline(isolate, searchValue);
        // 5. Let functionalReplace be IsCallable(replaceValue).
        bool functionalReplace = ObjectOps.IsCallable(replaceValue);
        // 6. If functionalReplace is false, then
        //   a. Let replaceValue be ? ToString(replaceValue).
        JSString? replaceValueString = functionalReplace ? null : ToStringInline(isolate, replaceValue);

        // 7. Let searchLength be the length of searchString.
        int searchLength = searchString.Length;
        // 8. Let advanceBy be max(1, searchLength).
        int advanceBy = Math.Max(1, searchLength);

        // We combine the two loops from the spec into one to avoid
        // needing a growable array.
        ReadOnlySpan<char> sChars = s.FlatSpan();
        ReadOnlySpan<char> searchChars = searchString.FlatSpan();
        int endOfLastMatch = 0;
        var result = new IncrementalStringBuilder(isolate);
        int position = StringSearch.IndexOf(sChars, searchChars, 0);
        while (position != -1)
        {
            // a. If functionalReplace is true, then
            // b. Else,
            JSString replacement;
            if (functionalReplace)
            {
                // i. Let replacement be ? ToString(? Call(replaceValue, undefined,
                //    « searchString, position, string »).
                replacement = ToStringInline(isolate, Execution.Call(isolate, replaceValue, JSValue.Undefined,
                    [searchString, JSValue.FromInt(position), s]));
                sChars = s.FlatSpan();
                searchChars = searchString.FlatSpan();
            }
            else
            {
                // iii. Let replacement be GetSubstitution(searchString, string,
                //      position, captures, undefined, replaceValue).
                // Note: Instead we just call a simpler GetSubstitution primitive.
                int matchEndPosition = position + searchLength;
                replacement = RuntimeStrings.GetSubstitution(isolate, s, position, matchEndPosition, replaceValueString!);
            }

            // c. Let stringSlice be the substring of string from endOfLastMatch
            //    up to position.
            // d. Let result be the string-concatenation of result, stringSlice,
            //    and replacement.
            result.AppendChars(sChars[endOfLastMatch..position]);
            result.AppendString(replacement);

            // e. Let endOfLastMatch be position + searchLength.
            endOfLastMatch = position + searchLength;
            position = StringSearch.IndexOf(sChars, searchChars, position + advanceBy);
        }

        // 15. If endOfLastMatch < the length of string, then append the rest.
        if (endOfLastMatch < sChars.Length) result.AppendChars(sChars[endOfLastMatch..]);

        // 16. Return result.
        return result.Finish();
    }

    /// <summary>StringMatchSearch (string-match-search.tq).</summary>
    static JSValue StringMatchSearch(Isolate isolate, JSValue receiver, JSValue regexp, bool isMatch, string methodName)
    {
        // 1. Let O be ? RequireObjectCoercible(this value).
        RequireObjectCoercible(isolate, receiver, methodName);

        // 3. Let string be ? ToString(O).
        if (receiver.HeapObjectOrNull is JSString fastString && regexp.HeapObjectOrNull is not null &&
            (isMatch ? BuiltinsRegExp.IsFastRegExpForMatch(isolate, regexp) : BuiltinsRegExp.IsFastRegExpForSearch(isolate, regexp)))
        {
            var fastRegExp = (JSRegExp)regexp.Object;
            return isMatch
                ? BuiltinsRegExp.RegExpMatchFast(isolate, fastRegExp, fastString)
                : BuiltinsRegExp.RegExpSearchFast(isolate, fastRegExp, fastString);
        }

        Symbol fnSymbol = isMatch ? ReadOnlyRoots.match_symbol : ReadOnlyRoots.search_symbol;
        // 2. If regexp is an Object, then
        if (regexp.HeapObjectOrNull is JSReceiver regexpReceiver)
        {
            // a. Let fn be ? GetMethod(regexp, %Symbol.match%/%Symbol.search%).
            // b. If fn is not undefined, then
            JSValue fn = ObjectOps.GetMethod(isolate, regexpReceiver, fnSymbol);
            //   i. Return ? Call(fn, regexp, « O »).
            if (!fn.IsUndefined) return Execution.Call(isolate, fn, regexp, [receiver]);
        }

        // 3. Let string be ? ToString(O).
        JSString s = ToStringInline(isolate, receiver);
        // 4. Let rx be ? RegExpCreate(regexp, undefined).
        JSRegExp rx = BuiltinsRegExp.RegExpCreate(isolate, regexp, ReadOnlyRoots.empty_string);
        // 5. Return ? Invoke(rx, %Symbol.match%/%Symbol.search%, « string »).
        JSValue fnValue = JSReceiver.GetProperty(isolate, rx, fnSymbol);
        return Execution.Call(isolate, fnValue, rx, [s]);
    }

    public static JSValue StringPrototypeMatch(Isolate isolate, in BuiltinArguments args) =>
        StringMatchSearch(isolate, args.Receiver, args.AtOrUndefined(1), true, "String.prototype.match");

    public static JSValue StringPrototypeSearch(Isolate isolate, in BuiltinArguments args) =>
        StringMatchSearch(isolate, args.Receiver, args.AtOrUndefined(1), false, "String.prototype.search");

    /// <summary>StringPrototypeMatchAll (builtins-string-gen.cc).</summary>
    public static JSValue StringPrototypeMatchAll(Isolate isolate, in BuiltinArguments args)
    {
        const string methodName = "String.prototype.matchAll";
        JSValue receiver = args.Receiver;
        JSValue maybeRegExp = args.AtOrUndefined(1);
        // 1. Let O be ? RequireObjectCoercible(this value).
        RequireObjectCoercible(isolate, receiver, methodName);

        // 2. If regexp is an Object, then
        //   a. Let isRegExp be ? IsRegExp(regexp).
        //   b. If isRegExp is true, then
        //     i. Let flags be ? Get(regexp, "flags").
        //    ii. Perform ? RequireObjectCoercible(flags).
        //   iii. If ? ToString(flags) does not contain "g", throw a
        //        TypeError exception.
        if (maybeRegExp.IsJSReceiver)
        {
            if (BuiltinsRegExp.IsFastRegExpForMatch(isolate, maybeRegExp))
            {
                if ((((JSRegExp)maybeRegExp.Object).Flags & RegExpFlags.Global) == 0)
                {
                    ThrowTypeError(isolate, MessageTemplate.RegExpGlobalInvokedOnNonGlobal, methodName);
                }
            }
            else if (BuiltinsRegExp.IsRegExp(isolate, maybeRegExp))
            {
                JSValue flags = ObjectOps.GetProperty(isolate, maybeRegExp, ReadOnlyRoots.flags_string);
                if (flags.IsNullOrUndefined) isolate.ThrowTypeError(MessageTemplate.StringMatchAllNullOrUndefinedFlags);
                JSString flagsString = ToStringInline(isolate, flags);
                if (!flagsString.FlatSpan().Contains('g'))
                {
                    ThrowTypeError(isolate, MessageTemplate.RegExpGlobalInvokedOnNonGlobal, methodName);
                }
            }

            //   a. Let matcher be ? GetMethod(regexp, %Symbol.matchAll%).
            //   b. If matcher is not undefined, then
            //     i. Return ? Call(matcher, regexp, « O »).
            if (MaybeCallFunctionAtSymbol(isolate, maybeRegExp, receiver, ReadOnlyRoots.match_all_symbol,
                    BuiltinsRegExp.kMatchAllDescriptor, SymbolCall.MatchAll, JSValue.Undefined, out JSValue result))
            {
                return result;
            }
        }

        // 3. Let S be ? ToString(O).
        JSString s = ToStringInline(isolate, receiver);
        // 4. Let rx be ? RegExpCreate(R, "g").
        JSRegExp rx = BuiltinsRegExp.RegExpCreate(isolate, maybeRegExp, isolate.Factory.LookupSingleCharacterStringFromCode('g'));
        // 5. Return ? Invoke(rx, %Symbol.matchAll%, « S »).
        JSValue matchAllFunc = JSReceiver.GetProperty(isolate, rx, ReadOnlyRoots.match_all_symbol);
        return Execution.Call(isolate, matchAllFunc, rx, [s]);
    }

    /// <summary>StringPrototypeSplit (builtins-string-gen.cc).</summary>
    public static JSValue StringPrototypeSplit(Isolate isolate, in BuiltinArguments args)
    {
        JSValue receiver = args.Receiver;
        JSValue separator = args.AtOrUndefined(1);
        JSValue limit = args.AtOrUndefined(2);
        RequireObjectCoercible(isolate, receiver, "String.prototype.split");

        // Redirect to splitter method if {separator} is an Object and
        // {separator[@@split]} is not undefined.
        if (MaybeCallFunctionAtSymbol(isolate, separator, receiver, ReadOnlyRoots.split_symbol,
                BuiltinsRegExp.kSplitDescriptor, SymbolCall.Split, limit, out JSValue result))
        {
            return result;
        }

        // String and integer conversions.
        JSString subjectString = ToStringInline(isolate, receiver);
        uint limitNumber = limit.IsUndefined
            ? uint.MaxValue
            : V8Sharp.Base.Numbers.Conversions.DoubleToUint32(ObjectOps.ToNumber(isolate, limit).Number);
        JSString separatorString = ToStringInline(isolate, separator);
        Factory factory = isolate.Factory;

        // Shortcut for {limit} == 0.
        if (limitNumber == 0) return factory.NewJSArray(ElementsKind.PACKED_ELEMENTS);

        // ECMA-262 says that if {separator} is undefined, the result should
        // be an array of size 1 containing the entire string.
        if (!separator.IsUndefined)
        {
            // If the separator string is empty then return the elements in the subject.
            if (separatorString.Length == 0)
            {
                if (subjectString.Length == 0) return factory.NewJSArray(ElementsKind.PACKED_ELEMENTS);
                return RuntimeStrings.StringToArray(isolate, subjectString, limitNumber);
            }

            // Fast path for a separator that does not occur in the subject string.
            int firstIndex = StringIndexOf(subjectString, separatorString, 0);
            if (firstIndex != -1)
            {
                return RuntimeRegExp.StringSplit(isolate, subjectString, separatorString, limitNumber, firstIndex);
            }
        }

        var single = new FixedArray(1);
        single[0] = subjectString;
        return factory.NewJSArrayWithElements(single);
    }
}
