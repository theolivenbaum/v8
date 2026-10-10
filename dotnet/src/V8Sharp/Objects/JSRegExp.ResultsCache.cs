// Port of regexp::ResultsCache and ResultsCache_MatchGlobalAtom
// (src/regexp/regexp.{h,cc}): caches of split results and of the matches of
// global regexps, keyed on an internalized subject and the pattern (the
// separator string, or the RegExpData). Hits return copy-on-write arrays.
//
// V8 keeps the caches as heap roots and clears them on GC; V8Sharp keeps one
// set per isolate (a ConditionalWeakTable entry) and never clears them, which
// only affects memory, not results (deviations.md).
using System.Runtime.CompilerServices;

namespace V8Sharp.Objects;

public static class RegExpResultsCache
{
    /// <summary>ResultsCache::ResultsCacheType.</summary>
    public enum ResultsCacheType
    {
        REGEXP_MULTIPLE_INDICES,
        STRING_SPLIT_SUBSTRINGS,
        REGEXP_SPLIT_SUBSTRINGS,
    }

    public const int kRegExpResultsCacheSize = 0x100;
    // Splits reuse many more distinct (subject, pattern) pairs than the other
    // cache types.
    public const int kRegExpSplitResultsCacheSize = 0x400;
    // One entry per (string, pattern) pair; V8's kArrayEntriesPerCacheEntry
    // slots are the fields of Entry here, so the indices step by one.
    const int kArrayEntriesPerCacheEntry = 1;

    struct Entry
    {
        public JSString? KeyString;
        public object? KeyPattern;
        public FixedArray? Array;
        public int[]? LastMatch;
    }

    sealed class Caches
    {
        public readonly Entry[] StringSplit = new Entry[kRegExpResultsCacheSize];
        public readonly Entry[] RegExpMultiple = new Entry[kRegExpResultsCacheSize];
        public Entry[]? RegExpSplit;

        // ResultsCache_MatchGlobalAtom.
        public SlicedString? AtomSubject;
        public JSString? AtomPattern;
        public uint AtomNumberOfMatches;
        public int AtomLastMatchIndex;
    }

    static readonly ConditionalWeakTable<Isolate, Caches> s_caches = new();

    static Caches Get(Isolate isolate) => s_caches.GetValue(isolate, static _ => new Caches());

    static Entry[]? CacheFor(Caches caches, ResultsCacheType type, bool create) => type switch
    {
        ResultsCacheType.STRING_SPLIT_SUBSTRINGS => caches.StringSplit,
        ResultsCacheType.REGEXP_MULTIPLE_INDICES => caches.RegExpMultiple,
        // The split cache is by far the largest of the regexp caches, so it is
        // allocated on first use.
        _ => create ? caches.RegExpSplit ??= new Entry[kRegExpSplitResultsCacheSize] : caches.RegExpSplit,
    };

    /// <summary>
    /// ResultsCache::Lookup: the cached copy-on-write array, or null. The last
    /// match registers of the cached run are returned in <paramref name="lastMatch"/>.
    /// </summary>
    public static FixedArray? Lookup(Isolate isolate, JSString keyString, object keyPattern, out int[]? lastMatch,
        ResultsCacheType type)
    {
        lastMatch = null;
        if (!isolate.Flags.regexp_results_cache) return null;
        if (!keyString.IsInternalized) return null;
        if (type == ResultsCacheType.STRING_SPLIT_SUBSTRINGS && keyPattern is JSString { IsInternalized: false }) return null;
        Entry[]? cache = CacheFor(Get(isolate), type, false);
        if (cache is null) return null;
        int cacheSize = cache.Length;
        uint hash = keyString.EnsureHash();
        int index = (int)(hash & (uint)(cacheSize - 1)) & ~(kArrayEntriesPerCacheEntry - 1);
        if (!ReferenceEquals(cache[index].KeyString, keyString) || !ReferenceEquals(cache[index].KeyPattern, keyPattern))
        {
            index = (index + kArrayEntriesPerCacheEntry) & (cacheSize - 1);
            if (!ReferenceEquals(cache[index].KeyString, keyString) || !ReferenceEquals(cache[index].KeyPattern, keyPattern))
            {
                return null;
            }
        }
        lastMatch = cache[index].LastMatch;
        return cache[index].Array;
    }

    /// <summary>
    /// ResultsCache::Enter: caches <paramref name="valueArray"/> and turns it
    /// into a copy-on-write array (short split results are internalized first).
    /// </summary>
    public static void Enter(Isolate isolate, JSString keyString, object keyPattern, FixedArray valueArray, int[] lastMatch,
        ResultsCacheType type)
    {
        if (!isolate.Flags.regexp_results_cache) return;
        if (!keyString.IsInternalized) return;
        if (type == ResultsCacheType.STRING_SPLIT_SUBSTRINGS && keyPattern is JSString { IsInternalized: false }) return;
        Entry[] cache = CacheFor(Get(isolate), type, true)!;
        int cacheSize = cache.Length;
        uint hash = keyString.EnsureHash();
        int index = (int)(hash & (uint)(cacheSize - 1)) & ~(kArrayEntriesPerCacheEntry - 1);
        var entry = new Entry { KeyString = keyString, KeyPattern = keyPattern, Array = valueArray, LastMatch = lastMatch };
        if (cache[index].KeyString is null)
        {
            cache[index] = entry;
        }
        else
        {
            int index2 = (index + kArrayEntriesPerCacheEntry) & (cacheSize - 1);
            if (cache[index2].KeyString is null)
            {
                cache[index2] = entry;
            }
            else
            {
                cache[index2] = default;
                cache[index] = entry;
            }
        }
        // If the array is a reasonably short list of substrings, convert it into a
        // list of internalized strings.
        int valueArrayLength = valueArray.Length;
        if (type == ResultsCacheType.STRING_SPLIT_SUBSTRINGS && valueArrayLength < 100)
        {
            for (int i = 0; i < valueArrayLength; i++)
            {
                valueArray[i] = isolate.Factory.InternalizeString((JSString)valueArray[i].Object);
            }
        }
        // Convert backing store to a copy-on-write array.
        valueArray.IsCowArray = true;
    }

    /// <summary>ResultsCache_MatchGlobalAtom::TryInsert.</summary>
    public static void MatchGlobalAtomTryInsert(Isolate isolate, JSString subject, JSString pattern, uint numberOfMatches,
        int lastMatchIndex)
    {
        if (subject is not SlicedString sliced) return;
        Caches caches = Get(isolate);
        caches.AtomSubject = sliced;
        caches.AtomPattern = pattern;
        caches.AtomNumberOfMatches = numberOfMatches;
        caches.AtomLastMatchIndex = lastMatchIndex;
    }

    /// <summary>
    /// ResultsCache_MatchGlobalAtom::TryGet: a cached count for a slice that
    /// starts where the cached one does and is at least as long.
    /// </summary>
    public static bool MatchGlobalAtomTryGet(Isolate isolate, JSString subject, JSString pattern, out uint numberOfMatches,
        out int lastMatchIndex)
    {
        numberOfMatches = 0;
        lastMatchIndex = -1;
        if (subject is not SlicedString sliced) return false;
        Caches caches = Get(isolate);
        if (!ReferenceEquals(pattern, caches.AtomPattern)) return false;
        SlicedString? cached = caches.AtomSubject;
        if (cached is null) return false;
        if (!ReferenceEquals(cached.Parent, sliced.Parent)) return false;
        if (cached.Offset != sliced.Offset) return false;
        if (cached.Length > sliced.Length) return false;
        numberOfMatches = caches.AtomNumberOfMatches;
        lastMatchIndex = caches.AtomLastMatchIndex;
        return true;
    }
}
