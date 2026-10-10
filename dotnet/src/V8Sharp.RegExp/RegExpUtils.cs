// Port of the heap-independent parts of src/regexp/regexp-utils.cc.
// (Last-index and match-info accessors operate on JS objects and live in the
// engine.)

namespace V8Sharp.RegExp;

public static class RegExpUtils
{
    /// <summary>
    /// https://tc39.es/ecma262/#sec-advancestringindex
    /// AdvanceStringIndex ( S, index, unicode )
    /// </summary>
    public static int AdvanceStringIndex(ReadOnlySpan<char> str, int index, bool unicode)
    {
        int stringLength = str.Length;
        if (unicode && index < stringLength)
        {
            char first = str[index];
            if (first >= 0xD800 && first <= 0xDBFF && index + 1 < stringLength)
            {
                char second = str[index + 1];
                if (second >= 0xDC00 && second <= 0xDFFF) return index + 2;
            }
        }
        return index + 1;
    }

    /// <summary>64-bit variant for lastIndex values up to 2^53 - 1.</summary>
    public static long AdvanceStringIndex(ReadOnlySpan<char> str, long index, bool unicode)
    {
        if (index < str.Length) return AdvanceStringIndex(str, (int)index, unicode);
        return index + 1;
    }
}
