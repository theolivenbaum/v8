// Port of src/strings/string-search.h (StringSearch, SearchString) and
// StringMatchBackwards (src/objects/string.cc).
//
// V8 picks one of four strategies per pattern (single char with memchr,
// linear, initial, Boyer-Moore-Horspool and full Boyer-Moore), with the
// "bad character" tables kept on the isolate. The .NET span search
// (MemoryExtensions.IndexOf/LastIndexOf) is a vectorized first/last-character
// filter followed by a vectorized compare, which is faster than BMH on modern
// hardware for every pattern shape V8's heuristics distinguish; the results
// (the first/last match position) are the same. Recorded in deviations.md.
using System.Runtime.CompilerServices;

namespace V8Sharp.Strings;

public static class StringSearch
{
    /// <summary>
    /// SearchString(isolate, subject, pattern, start_index): the index of the
    /// first occurrence of <paramref name="pattern"/> at or after
    /// <paramref name="startIndex"/>, or -1. An empty pattern matches at
    /// <paramref name="startIndex"/>.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int IndexOf(ReadOnlySpan<char> subject, ReadOnlySpan<char> pattern, int startIndex)
    {
        int patternLength = pattern.Length;
        if (patternLength == 0) return startIndex <= subject.Length ? startIndex : -1;
        if ((long)startIndex + patternLength > subject.Length) return -1;
        int r = patternLength == 1
            ? subject[startIndex..].IndexOf(pattern[0])
            : subject[startIndex..].IndexOf(pattern);
        return r < 0 ? -1 : r + startIndex;
    }

    /// <summary>SearchString for a single character (FindFirstCharacter).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int IndexOf(ReadOnlySpan<char> subject, char c, int startIndex)
    {
        if ((uint)startIndex >= (uint)subject.Length) return -1;
        int r = subject[startIndex..].IndexOf(c);
        return r < 0 ? -1 : r + startIndex;
    }

    /// <summary>
    /// StringMatchBackwards: the last occurrence starting at or before
    /// <paramref name="idx"/>, or -1. The pattern is not empty.
    /// </summary>
    public static int LastIndexOf(ReadOnlySpan<char> subject, ReadOnlySpan<char> pattern, int idx)
    {
        int patternLength = pattern.Length;
        Debug.Assert(patternLength >= 1);
        if ((long)idx + patternLength > subject.Length) idx = subject.Length - patternLength;
        if (idx < 0) return -1;
        ReadOnlySpan<char> window = subject[..(idx + patternLength)];
        return patternLength == 1 ? window.LastIndexOf(pattern[0]) : window.LastIndexOf(pattern);
    }
}
