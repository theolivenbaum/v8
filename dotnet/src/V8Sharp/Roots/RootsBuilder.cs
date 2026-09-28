// The allocation side of V8's read-only roots (Heap::CreateInitialObjects and
// the RO-space setup in src/heap/setup-heap-internal.cc): creates the
// internalized string and symbol roots the generated ReadOnlyRoots refers to.
// V8 shares its read-only space between isolates; V8Sharp's read-only roots
// are process-wide statics, and every isolate's StringTable starts with them.
using V8Sharp.Objects;

namespace V8Sharp.Roots;

internal static class RootsBuilder
{
    static readonly Dictionary<string, SeqString> s_strings = new(StringComparer.Ordinal);
    static readonly object s_lock = new();

    /// <summary>The internalized read-only string with this content (deduplicated).</summary>
    public static SeqString String(string value)
    {
        lock (s_lock)
        {
            if (s_strings.TryGetValue(value, out SeqString? existing)) return existing;
            var s = new SeqString(value) { IsInternalized = true };
            s.EnsureRawHash();
            s_strings.Add(value, s);
            return s;
        }
    }

    /// <summary>A private symbol root (PRIVATE_SYMBOL_LIST); V8 gives them no description.</summary>
    public static Symbol PrivateSymbol(string name) =>
        new(JSValue.Undefined) { PrivateSymbolKind = PrivateSymbolKind.Internal };

    /// <summary>A public symbol root (PUBLIC_SYMBOL_LIST / WELL_KNOWN_SYMBOL_LIST).</summary>
    public static Symbol PublicSymbol(string description, bool wellKnown, bool interesting) =>
        new(String(description)) { IsWellKnownSymbol = wellKnown, IsInterestingSymbol = interesting };

    /// <summary>All strings created as roots, for seeding a StringTable.</summary>
    public static SeqString[] AllStrings()
    {
        // Touch ReadOnlyRoots so every root string exists.
        SeqString[] roots = ReadOnlyRoots.InternalizedStringRoots();
        lock (s_lock)
        {
            var all = new SeqString[s_strings.Count];
            s_strings.Values.CopyTo(all, 0);
            return all.Length >= roots.Length ? all : roots;
        }
    }
}
