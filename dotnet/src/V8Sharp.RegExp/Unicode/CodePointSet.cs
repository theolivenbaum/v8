// A minimal replacement for the parts of icu::UnicodeSet that irregexp uses:
// a set of code points kept as sorted, non-overlapping, non-adjacent ranges.

namespace V8Sharp.RegExp.Unicode;

internal sealed class CodePointSet
{
    const int kMaxCodePoint = 0x10FFFF;

    // Flat inclusive ranges [from0, to0, from1, to1, ...], canonical.
    List<int> _ranges = [];

    public CodePointSet() { }
    public CodePointSet(int from, int to) => Add(from, to);
    public CodePointSet(CodePointSet other) => _ranges = [.. other._ranges];

    public static CodePointSet FromRanges(int[] flat)
    {
        var s = new CodePointSet();
        s._ranges = [.. flat];
        return s;
    }

    public int RangeCount => _ranges.Count / 2;
    public int GetRangeStart(int i) => _ranges[2 * i];
    public int GetRangeEnd(int i) => _ranges[2 * i + 1];
    public bool IsEmpty => _ranges.Count == 0;

    /// <summary>Number of code points in the set.</summary>
    public long Size
    {
        get
        {
            long n = 0;
            for (int i = 0; i < _ranges.Count; i += 2) n += _ranges[i + 1] - _ranges[i] + 1;
            return n;
        }
    }

    public void Clear() => _ranges.Clear();

    /// <summary>UnicodeSet::operator==.</summary>
    public bool SetEquals(CodePointSet other)
    {
        if (_ranges.Count != other._ranges.Count) return false;
        for (int i = 0; i < _ranges.Count; i++)
        {
            if (_ranges[i] != other._ranges[i]) return false;
        }
        return true;
    }

    public void Set(int from, int to)
    {
        _ranges.Clear();
        _ranges.Add(from);
        _ranges.Add(to);
    }

    public bool Contains(int c)
    {
        int lo = 0, hi = RangeCount - 1;
        while (lo <= hi)
        {
            int mid = (lo + hi) >> 1;
            if (c < _ranges[2 * mid]) hi = mid - 1;
            else if (c > _ranges[2 * mid + 1]) lo = mid + 1;
            else return true;
        }
        return false;
    }

    public void Add(int c) => Add(c, c);

    public void Add(int from, int to)
    {
        if (from > to) return;
        // Find insertion point and merge.
        var result = new List<int>(_ranges.Count + 2);
        int i = 0;
        int n = _ranges.Count;
        while (i < n && _ranges[i + 1] + 1 < from)
        {
            result.Add(_ranges[i]);
            result.Add(_ranges[i + 1]);
            i += 2;
        }
        int newFrom = from, newTo = to;
        while (i < n && _ranges[i] <= to + 1)
        {
            newFrom = Math.Min(newFrom, _ranges[i]);
            newTo = Math.Max(newTo, _ranges[i + 1]);
            i += 2;
        }
        result.Add(newFrom);
        result.Add(newTo);
        while (i < n)
        {
            result.Add(_ranges[i]);
            result.Add(_ranges[i + 1]);
            i += 2;
        }
        _ranges = result;
    }

    public void AddAll(CodePointSet other)
    {
        for (int i = 0; i < other._ranges.Count; i += 2) Add(other._ranges[i], other._ranges[i + 1]);
    }

    public void Complement()
    {
        var result = new List<int>(_ranges.Count + 2);
        int next = 0;
        for (int i = 0; i < _ranges.Count; i += 2)
        {
            if (_ranges[i] > next)
            {
                result.Add(next);
                result.Add(_ranges[i] - 1);
            }
            next = _ranges[i + 1] + 1;
        }
        if (next <= kMaxCodePoint)
        {
            result.Add(next);
            result.Add(kMaxCodePoint);
        }
        _ranges = result;
    }

    public void RetainAll(CodePointSet other)
    {
        var result = new List<int>();
        int i = 0, j = 0;
        while (i < _ranges.Count && j < other._ranges.Count)
        {
            int a0 = _ranges[i], a1 = _ranges[i + 1];
            int b0 = other._ranges[j], b1 = other._ranges[j + 1];
            int lo = Math.Max(a0, b0), hi = Math.Min(a1, b1);
            if (lo <= hi)
            {
                result.Add(lo);
                result.Add(hi);
            }
            if (a1 < b1) i += 2; else j += 2;
        }
        _ranges = result;
    }

    public void RemoveAll(CodePointSet other)
    {
        var inverse = new CodePointSet(other);
        inverse.Complement();
        RetainAll(inverse);
    }

    public bool ContainsNone(CodePointSet other)
    {
        var tmp = new CodePointSet(this);
        tmp.RetainAll(other);
        return tmp.IsEmpty;
    }

    public bool ContainsAll(CodePointSet other)
    {
        var tmp = new CodePointSet(other);
        tmp.RemoveAll(this);
        return tmp.IsEmpty;
    }

    /// <summary>
    /// UnicodeSet::closeOver(USET_SIMPLE_CASE_INSENSITIVE): adds every code
    /// point that has the same simple case folding as a member.
    /// </summary>
    public void CloseOverSimpleCaseInsensitive()
    {
        // Iterate over the (small) set of case classes rather than over the
        // (potentially huge) member ranges.
        var toAdd = new List<int>();
        foreach (int[] cls in UnicodeTables.AllCaseClasses())
        {
            bool any = false;
            foreach (int c in cls)
            {
                if (Contains(c)) { any = true; break; }
            }
            if (!any) continue;
            foreach (int c in cls) toAdd.Add(c);
        }
        if (toAdd.Count == 0) return;
        toAdd.Sort();
        var add = new CodePointSet();
        foreach (int c in toAdd) add.AddSorted(c);
        AddAll(add);
    }

    void AddSorted(int c)
    {
        int n = _ranges.Count;
        if (n > 0 && _ranges[n - 1] + 1 >= c)
        {
            if (_ranges[n - 1] < c) _ranges[n - 1] = c;
            return;
        }
        _ranges.Add(c);
        _ranges.Add(c);
    }
}
