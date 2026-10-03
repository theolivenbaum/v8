// Port of src/regexp/special-case.h (the V8_INTL_SUPPORT variant).
//
// For non-unicode ignoreCase matches (aka "i", not "iu"), ECMA 262 defines
// slightly different case-folding rules than Unicode. An input character
// should match a pattern character if the result of the Canonicalize
// algorithm is the same for both characters. For almost all characters, the
// set of matching characters is the simple case closure; IgnoreSet represents
// the remaining exceptions: characters that must match only themselves. The
// IgnoreSet is computed at generation time exactly as
// src/regexp/gen-regexp-special-case.cc does.

namespace V8Sharp.RegExp.Unicode;

internal static class CaseFolding
{
    public enum Mode { kNonUnicode, kUnicode }

    static CodePointSet? s_ignoreSet;

    static CodePointSet IgnoreSet => s_ignoreSet ??= CodePointSet.FromRanges(UnicodeTables.IgnoreSet);

    public static bool IgnoreSetContains(int c) => UnicodeTables.RangesContain(UnicodeTables.IgnoreSet, c);

    /// <summary>
    /// Equal keys denote matching characters, but can diverge from the
    /// specification's Canonicalize operation. Non-unicode inputs are UTF-16
    /// code units; Unicode inputs are code points.
    /// </summary>
    public static int EquivalenceKey(int c, Mode mode)
    {
        if (mode == Mode.kNonUnicode && IgnoreSetContains(c)) return c;
        return UnicodeTables.SimpleFold(c);
    }

    /// <summary>
    /// Close a set of characters under case equivalence, preserving its
    /// original members. Non-unicode inputs must be UTF-16 code units.
    /// </summary>
    public static void CloseOver(CodePointSet set, Mode mode)
    {
        if (mode == Mode.kUnicode)
        {
            set.CloseOverSimpleCaseInsensitive();
            return;
        }
        if (IgnoreSet.ContainsNone(set))
        {
            set.CloseOverSimpleCaseInsensitive();
            set.RemoveAll(IgnoreSet);
            return;
        }
        if (IgnoreSet.ContainsAll(set)) return;
        // Ignored characters neither contribute nor acquire equivalents, but
        // must remain in the result if they were explicitly present.
        var ignored = new CodePointSet(set);
        ignored.RetainAll(IgnoreSet);
        set.RemoveAll(IgnoreSet);
        set.CloseOverSimpleCaseInsensitive();
        set.RemoveAll(IgnoreSet);
        set.AddAll(ignored);
    }
}
