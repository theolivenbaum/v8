// Port of src/objects/string-table.{h,cc}: the isolate's table of
// internalized strings. Property keys are always internalized, so named
// property lookup compares references (architecture.md section 4).
//
// As in V8, an open-addressing hash set of the internalized strings, probed
// by the string's raw hash field (StringHasher) and compared by contents
// (StringTable::LookupKey with a StringTableKey that carries the hash: a
// caller that already has the hash, the AstValueFactory's strings, does not
// hash again). Strings that are not internalized in place remember their
// canonical copy (V8 turns them into ThinStrings). V8 resizes the table
// (EnsureCapacity, at most half full) and never shrinks it while strings are
// alive; V8Sharp does not remove dead strings (the .NET GC cannot tell the
// table which ones died), so the table only grows, as the Dictionary it
// replaces did.
using System.Runtime.CompilerServices;
using V8Sharp.Objects;
using V8Sharp.Roots;

namespace V8Sharp;

/// <summary>V8's StringTable.</summary>
public sealed class StringTable
{
    SeqString?[] _slots;
    int _count;

    public StringTable(Isolate isolate)
    {
        SeqString[] roots = RootsBuilder.AllStrings();
        int capacity = 1024;
        while (capacity < roots.Length * 4) capacity *= 2;
        _slots = new SeqString?[capacity];
        foreach (SeqString s in roots)
        {
            uint hash = s.EnsureRawHash();
            if (Find(s.Value, hash) is null) Add(s, hash);
        }
    }

    /// <summary>The number of internalized strings.</summary>
    public int NumberOfElements => _count;

    /// <summary>The probe start of a raw hash field (Name::HashBits; FirstProbe).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static int FirstProbe(uint rawHashField, int mask) => (int)(rawHashField >> Name.HashShift) & mask;

    /// <summary>StringTable::Data::FindEntry: the internalized string with these contents and hash, or null.</summary>
    SeqString? Find(ReadOnlySpan<char> chars, uint rawHashField)
    {
        SeqString?[] slots = _slots;
        int mask = slots.Length - 1;
        // Linear probing, as V8's NextProbe over a power-of-two table.
        for (int i = FirstProbe(rawHashField, mask); ; i = (i + 1) & mask)
        {
            SeqString? s = slots[i];
            if (s is null) return null;
            if (s.RawHashField == rawHashField && chars.SequenceEqual(s.Value)) return s;
        }
    }

    void Add(SeqString s, uint rawHashField)
    {
        // StringTable::EnsureCapacity: keep the table at most half full.
        if ((_count + 1) * 2 > _slots.Length) Grow();
        Insert(_slots, s, rawHashField);
        _count++;
    }

    static void Insert(SeqString?[] slots, SeqString s, uint rawHashField)
    {
        int mask = slots.Length - 1;
        int i = FirstProbe(rawHashField, mask);
        while (slots[i] is not null) i = (i + 1) & mask;
        slots[i] = s;
    }

    void Grow()
    {
        var slots = new SeqString?[_slots.Length * 2];
        foreach (SeqString? s in _slots)
        {
            if (s is not null) Insert(slots, s, s.RawHashField);
        }
        _slots = slots;
    }

    /// <summary>A new internalized string for these contents, added to the table.</summary>
    SeqString AddNew(string value, uint rawHashField)
    {
        var result = new SeqString(value) { IsInternalized = true, RawHashField = rawHashField };
        Add(result, rawHashField);
        return result;
    }

    /// <summary>StringTable::LookupString: the internalized string equal to <paramref name="str"/>.</summary>
    public JSString LookupString(Isolate isolate, JSString str)
    {
        if (str.IsInternalized) return str;
        if (str.InternalizedForward is JSString forward) return forward;
        string flat = str.Flatten();
        uint hash = str.EnsureRawHash();
        if (Find(flat, hash) is { } existing)
        {
            str.InternalizedForward = existing;
            return existing;
        }
        if (str is SeqString seq)
        {
            // Internalize in place (V8 migrates the map to an internalized one).
            seq.IsInternalized = true;
            Add(seq, hash);
            return seq;
        }
        SeqString result = AddNew(flat, hash);
        str.InternalizedForward = result;
        return result;
    }

    /// <summary>Internalizes character data (Factory::InternalizeString over a vector).</summary>
    public JSString LookupString(ReadOnlySpan<char> chars)
    {
        uint hash = StringHasher.HashSequentialString(chars);
        return Find(chars, hash) ?? AddNew(chars.ToString(), hash);
    }

    /// <summary>Internalizes a .NET string.</summary>
    public JSString LookupString(string value)
    {
        uint hash = StringHasher.HashSequentialString(value);
        return Find(value, hash) ?? AddNew(value, hash);
    }

    /// <summary>
    /// Internalizes a .NET string whose raw hash field the caller computed with
    /// StringHasher (V8: a StringTableKey that carries its hash, such as an
    /// AstRawString's in AstValueFactory::Internalize).
    /// </summary>
    public JSString LookupString(string value, uint rawHashField)
    {
        Debug.Assert(rawHashField == StringHasher.HashSequentialString(value));
        return Find(value, rawHashField) ?? AddNew(value, rawHashField);
    }

    /// <summary>StringTable::TryStringToIndexOrLookupExisting: the internalized copy, or null if there is none.</summary>
    public JSString? TryLookupExisting(JSString str)
    {
        if (str.IsInternalized) return str;
        if (str.InternalizedForward is JSString forward) return forward;
        if (Find(str.Flatten(), str.EnsureRawHash()) is { } existing)
        {
            str.InternalizedForward = existing;
            return existing;
        }
        return null;
    }

    public JSString? TryLookupExisting(ReadOnlySpan<char> chars) => Find(chars, StringHasher.HashSequentialString(chars));
}
