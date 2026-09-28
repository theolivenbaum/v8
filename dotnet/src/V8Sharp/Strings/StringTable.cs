// Port of src/objects/string-table.{h,cc}: the isolate's table of
// internalized strings. Property keys are always internalized, so named
// property lookup compares references (architecture.md section 4).
//
// V8's table is an open-addressing hash set keyed by the string hash; V8Sharp
// keys a Dictionary by content (ordinal), which gives the same canonical
// object for equal contents. Strings that are not internalized in place
// remember their canonical copy (V8 turns them into ThinStrings).
using V8Sharp.Objects;
using V8Sharp.Roots;

namespace V8Sharp;

/// <summary>V8's StringTable.</summary>
public sealed class StringTable
{
    readonly Dictionary<string, JSString> _table;
    readonly Dictionary<string, JSString>.AlternateLookup<ReadOnlySpan<char>> _spanLookup;

    public StringTable(Isolate isolate)
    {
        SeqString[] roots = RootsBuilder.AllStrings();
        _table = new Dictionary<string, JSString>(Math.Max(1024, roots.Length * 2), StringComparer.Ordinal);
        foreach (SeqString s in roots) _table[s.Value] = s;
        _spanLookup = _table.GetAlternateLookup<ReadOnlySpan<char>>();
    }

    /// <summary>The number of internalized strings.</summary>
    public int NumberOfElements => _table.Count;

    /// <summary>StringTable::LookupString: the internalized string equal to <paramref name="str"/>.</summary>
    public JSString LookupString(Isolate isolate, JSString str)
    {
        if (str.IsInternalized) return str;
        if (str.InternalizedForward is JSString forward) return forward;
        string flat = str.Flatten();
        if (_table.TryGetValue(flat, out JSString? existing))
        {
            str.InternalizedForward = existing;
            return existing;
        }
        JSString result;
        if (str is SeqString seq)
        {
            // Internalize in place (V8 migrates the map to an internalized one).
            seq.IsInternalized = true;
            result = seq;
        }
        else
        {
            result = new SeqString(flat) { IsInternalized = true };
            str.InternalizedForward = result;
        }
        result.EnsureRawHash();
        _table.Add(flat, result);
        return result;
    }

    /// <summary>Internalizes character data (Factory::InternalizeString over a vector).</summary>
    public JSString LookupString(ReadOnlySpan<char> chars)
    {
        if (_spanLookup.TryGetValue(chars, out JSString? existing)) return existing;
        string value = chars.ToString();
        var result = new SeqString(value) { IsInternalized = true };
        result.EnsureRawHash();
        _table.Add(value, result);
        return result;
    }

    /// <summary>Internalizes a .NET string.</summary>
    public JSString LookupString(string value)
    {
        if (_table.TryGetValue(value, out JSString? existing)) return existing;
        var result = new SeqString(value) { IsInternalized = true };
        result.EnsureRawHash();
        _table.Add(value, result);
        return result;
    }

    /// <summary>StringTable::TryStringToIndexOrLookupExisting: the internalized copy, or null if there is none.</summary>
    public JSString? TryLookupExisting(JSString str)
    {
        if (str.IsInternalized) return str;
        if (str.InternalizedForward is JSString forward) return forward;
        if (_table.TryGetValue(str.Flatten(), out JSString? existing))
        {
            str.InternalizedForward = existing;
            return existing;
        }
        return null;
    }

    public JSString? TryLookupExisting(ReadOnlySpan<char> chars) =>
        _spanLookup.TryGetValue(chars, out JSString? existing) ? existing : null;
}
