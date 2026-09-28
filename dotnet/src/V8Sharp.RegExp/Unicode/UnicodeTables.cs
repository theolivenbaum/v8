// Runtime side of the generated Unicode tables (UnicodeTables.g.cs).
//
// V8 gets this data from ICU (u_getPropertyValueEnum, UnicodeSet::
// applyIntPropertyValue, u_foldCase, UnicodeSet::closeOver). V8Sharp has no
// ICU; the data is generated from the Unicode Character Database by
// dotnet/tools/unicode/gen_regexp_unicode_tables.py.
// TODO(merge): V8Sharp.Base will carry unibrow's case tables; once it does,
// the simple case folding table could be shared.

namespace V8Sharp.RegExp.Unicode;

internal static partial class UnicodeTables
{
    static byte[]? s_blob;
    static readonly Dictionary<int, int[]> s_rangeCache = [];
    static int[]? s_foldKeys;
    static int[]? s_foldValues;
    static Dictionary<int, int[]>? s_foldClasses;
    static int[]? s_ignoreSet;
    static int[][]? s_classList;

    static byte[] Blob => s_blob ??= Convert.FromBase64String(BlobBase64);

    static int ReadVarint(byte[] blob, ref int pos)
    {
        int result = 0;
        int shift = 0;
        while (true)
        {
            byte b = blob[pos++];
            result |= (b & 0x7F) << shift;
            if ((b & 0x80) == 0) return result;
            shift += 7;
        }
    }

    /// <summary>Returns the inclusive ranges stored at <paramref name="offset"/>
    /// as a flat [from0, to0, from1, to1, ...] array. Cached; do not mutate.</summary>
    public static int[] GetRanges(int offset)
    {
        lock (s_rangeCache)
        {
            if (s_rangeCache.TryGetValue(offset, out int[]? cached)) return cached;
            byte[] blob = Blob;
            int pos = offset;
            int count = ReadVarint(blob, ref pos);
            int[] result = new int[count * 2];
            int prev = 0;
            for (int i = 0; i < count; i++)
            {
                int from = prev + ReadVarint(blob, ref pos);
                int to = from + ReadVarint(blob, ref pos);
                result[2 * i] = from;
                result[2 * i + 1] = to;
                prev = to;
            }
            s_rangeCache[offset] = result;
            return result;
        }
    }

    /// <summary>Returns the strings (as code point arrays) stored at <paramref name="offset"/>.</summary>
    public static int[][] GetStrings(int offset)
    {
        byte[] blob = Blob;
        int pos = offset;
        int count = ReadVarint(blob, ref pos);
        int[][] result = new int[count][];
        for (int i = 0; i < count; i++)
        {
            int len = ReadVarint(blob, ref pos);
            int[] s = new int[len];
            for (int j = 0; j < len; j++) s[j] = ReadVarint(blob, ref pos);
            result[i] = s;
        }
        return result;
    }

    static void EnsureFolding()
    {
        if (s_foldClasses is not null) return;
        lock (s_rangeCache)
        {
            if (s_foldClasses is not null) return;
            byte[] blob = Blob;
            int pos = SimpleCaseFoldingOffset;
            int count = ReadVarint(blob, ref pos);
            int[] keys = new int[count];
            int[] values = new int[count];
            int prev = 0;
            for (int i = 0; i < count; i++)
            {
                int key = prev + ReadVarint(blob, ref pos);
                keys[i] = key;
                values[i] = ReadVarint(blob, ref pos);
                prev = key;
            }
            // Simple case closure classes: every code point with the same
            // simple case folding, plus the folding target itself.
            var members = new Dictionary<int, List<int>>();
            for (int i = 0; i < count; i++)
            {
                int target = values[i];
                if (!members.TryGetValue(target, out List<int>? list))
                {
                    list = [target];
                    members[target] = list;
                }
                list.Add(keys[i]);
            }
            var classes = new Dictionary<int, int[]>();
            var classList = new List<int[]>(members.Count);
            foreach (var kv in members)
            {
                int[] cls = [.. kv.Value];
                Array.Sort(cls);
                classList.Add(cls);
                foreach (int c in cls) classes[c] = cls;
            }
            s_classList = [.. classList];
            s_foldKeys = keys;
            s_foldValues = values;
            s_ignoreSet = GetRanges(IgnoreSetOffset);
            s_foldClasses = classes;
        }
    }

    /// <summary>u_foldCase(c, U_FOLD_CASE_DEFAULT) restricted to simple (C+S) foldings.</summary>
    public static int SimpleFold(int c)
    {
        EnsureFolding();
        int i = Array.BinarySearch(s_foldKeys!, c);
        return i >= 0 ? s_foldValues![i] : c;
    }

    /// <summary>The simple case closure class of <paramref name="c"/>, or null
    /// if c has no case equivalents.</summary>
    public static int[]? SimpleCaseClosure(int c)
    {
        EnsureFolding();
        return s_foldClasses!.TryGetValue(c, out int[]? cls) ? cls : null;
    }

    /// <summary>All simple case closure classes with more than one member.</summary>
    public static int[][] AllCaseClasses()
    {
        EnsureFolding();
        return s_classList!;
    }

    public static int[] IgnoreSet
    {
        get
        {
            EnsureFolding();
            return s_ignoreSet!;
        }
    }

    public static bool RangesContain(int[] ranges, int c)
    {
        int lo = 0, hi = ranges.Length / 2 - 1;
        while (lo <= hi)
        {
            int mid = (lo + hi) >> 1;
            if (c < ranges[2 * mid]) hi = mid - 1;
            else if (c > ranges[2 * mid + 1]) lo = mid + 1;
            else return true;
        }
        return false;
    }

    static int[]? s_idStart;
    static int[]? s_idContinue;

    static int[] BinaryPropertyRanges(string name)
    {
        foreach (var (names, offset) in BinaryProperties)
        {
            if (names[1] == name) return GetRanges(offset);
        }
        throw new InvalidOperationException(name);
    }

    public static bool IsIdStart(int c) => RangesContain(s_idStart ??= BinaryPropertyRanges("ID_Start"), c);
    public static bool IsIdContinue(int c) => RangesContain(s_idContinue ??= BinaryPropertyRanges("ID_Continue"), c);
}
