// Port of the string runtime functions of src/runtime/runtime-strings.cc used
// by the String builtins: GetSubstitution, StringReplaceOneCharWithString,
// StringToArray, StringEscapeQuotes, StringIsWellFormed, StringToWellFormed,
// StringLastIndexOf.
//
// Plain static methods with typed parameters; the runtime dispatch table
// adapts its arguments to them.
using V8Sharp.Builtins;

namespace V8Sharp.Runtime;

public static class RuntimeStrings
{
    /// <summary>
    /// Runtime_GetSubstitution: expands the '$' patterns of
    /// <paramref name="replacement"/> for a match without captures, starting at
    /// the first '$' (<paramref name="startIndex"/>).
    /// </summary>
    public static JSString GetSubstitution(Isolate isolate, JSString matched, JSString subject, int position,
        JSString replacement, int startIndex)
    {
        Factory factory = isolate.Factory;
        JSString prefix = factory.NewSubString(subject, 0, position);
        JSString suffix = factory.NewSubString(subject, position + matched.Length, subject.Length);
        var match = new RuntimeRegExp.SimpleMatch(matched, prefix, suffix);
        return JSString.GetSubstitution(isolate, match, replacement, startIndex);
    }

    /// <summary>
    /// StringBuiltinsAssembler::GetSubstitution: the replacement itself when it
    /// has no '$', else Runtime_GetSubstitution.
    /// </summary>
    public static JSString GetSubstitution(Isolate isolate, JSString subject, int matchStartIndex, int matchEndIndex,
        JSString replaceString)
    {
        int dollarIndex = StringSearch.IndexOf(replaceString.FlatSpan(), '$', 0);
        if (dollarIndex < 0) return replaceString;
        JSString matched = isolate.Factory.NewSubString(subject, matchStartIndex, matchEndIndex);
        return GetSubstitution(isolate, matched, subject, matchStartIndex, replaceString, dollarIndex);
    }

    /// <summary>
    /// StringReplaceOneCharWithString: replaces the first occurrence of a
    /// one-character search string, rebuilding only the rope path that holds it.
    /// Returns null when the recursion limit is reached.
    /// </summary>
    static JSString? StringReplaceOneCharWithString(Isolate isolate, JSString subject, JSString search, JSString replace,
        ref bool found, int recursionLimit)
    {
        if (!System.Runtime.CompilerServices.RuntimeHelpers.TryEnsureSufficientExecutionStack() || recursionLimit == 0)
        {
            return null;
        }
        recursionLimit--;
        if (subject is ConsString { IsFlat: false } cons)
        {
            JSString first = cons.First;
            JSString second = cons.Second!;
            JSString? newFirst = StringReplaceOneCharWithString(isolate, first, search, replace, ref found, recursionLimit);
            if (newFirst is null) return null;
            if (found) return isolate.Factory.NewConsString(newFirst, second);

            JSString? newSecond = StringReplaceOneCharWithString(isolate, second, search, replace, ref found, recursionLimit);
            if (newSecond is null) return null;
            if (found) return isolate.Factory.NewConsString(first, newSecond);
            return subject;
        }
        int index = StringSearch.IndexOf(subject.FlatSpan(), search.Get(0), 0);
        if (index == -1) return subject;
        found = true;
        JSString firstPart = isolate.Factory.NewSubString(subject, 0, index);
        JSString cons1 = isolate.Factory.NewConsString(firstPart, replace);
        JSString secondPart = isolate.Factory.NewSubString(subject, index + 1, subject.Length);
        return isolate.Factory.NewConsString(cons1, secondPart);
    }

    /// <summary>Runtime_StringReplaceOneCharWithString.</summary>
    public static JSString StringReplaceOneCharWithString(Isolate isolate, JSString subject, JSString search, JSString replace)
    {
        // If the cons string tree is too deep, we simply abort the recursion and
        // retry with a flattened subject string.
        const int kRecursionLimit = 0x1000;
        bool found = false;
        JSString? result = StringReplaceOneCharWithString(isolate, subject, search, replace, ref found, kRecursionLimit);
        if (result is not null) return result;
        subject = JSString.Flatten(isolate, subject);
        result = StringReplaceOneCharWithString(isolate, subject, search, replace, ref found, kRecursionLimit);
        if (result is not null) return result;
        // In case of empty handle and no exception we have stack overflow.
        return (JSString)isolate.StackOverflow().Object;
    }

    /// <summary>Runtime_StringToArray: the first <paramref name="limit"/> code units as one-character strings.</summary>
    public static JSArray StringToArray(Isolate isolate, JSString s, uint limit)
    {
        ReadOnlySpan<char> chars = s.FlatSpan();
        int length = (int)Math.Min((uint)chars.Length, limit);
        var elements = new FixedArray(length);
        Factory factory = isolate.Factory;
        for (int i = 0; i < length; i++) elements[i] = factory.LookupSingleCharacterStringFromCode(chars[i]);
        return factory.NewJSArrayWithElements(elements);
    }

    /// <summary>
    /// Runtime_StringEscapeQuotes: replaces '"' with "&amp;quot;" without
    /// touching the regexp match info.
    /// </summary>
    public static JSString StringEscapeQuotes(Isolate isolate, JSString s)
    {
        ReadOnlySpan<char> chars = s.FlatSpan();
        int quoteIndex = chars.IndexOf('"');
        // No quotes, nothing to do.
        if (quoteIndex == -1) return s;
        int count = chars[quoteIndex..].Count('"');
        const string replacement = "&quot;";
        long resultLength = chars.Length + (long)count * (replacement.Length - 1);
        if (resultLength > JSString.kMaxLength) return (JSString)isolate.Throw(isolate.Factory.NewInvalidStringLengthError()).Object;
        var builder = new IncrementalStringBuilder(isolate);
        int from = 0;
        while (quoteIndex >= 0)
        {
            builder.AppendChars(chars[from..quoteIndex]);
            builder.AppendCStringLiteral(replacement);
            from = quoteIndex + 1;
            int next = chars[from..].IndexOf('"');
            quoteIndex = next < 0 ? -1 : next + from;
        }
        builder.AppendChars(chars[from..]);
        return builder.Finish();
    }

    /// <summary>Runtime_StringIsWellFormed.</summary>
    public static bool StringIsWellFormed(JSString s) => s.IsWellFormedUnicode();

    /// <summary>Runtime_StringToWellFormed.</summary>
    public static JSValue StringToWellFormed(Isolate isolate, JSString source)
    {
        if (source.IsWellFormedUnicode()) return source;
        var args = new BuiltinArguments(isolate.NativeContext.StringFunction, JSValue.Undefined, source, []);
        return BuiltinsString.StringPrototypeToWellFormed(isolate, in args);
    }
}
