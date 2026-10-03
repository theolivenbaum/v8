// Copyright 2011 the V8 project authors. All rights reserved.
// Use of this source code is governed by a BSD-style license that can be
// found in the LICENSE file.

// Port of the script and eval parts of src/codegen/compilation-cache.h/.cc
// (CompilationCacheScript, CompilationCacheEval) and of the script and eval
// lookups of src/objects/compilation-cache-table.cc (ScriptCacheKey,
// EvalCacheKey). The regexp part is in JSRegExp.cs.
//
// V8 keeps both in a CompilationCacheTable on its heap and ages them with
// bytecode flushing (an entry goes when its SharedFunctionInfo's bytecode is
// flushed, after --bytecode-old-time seconds unused). V8Sharp does not flush
// bytecode; the tables here are .NET dictionaries aged by full .NET
// collections (see the class comments and deviations.md).

using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace V8Sharp.Codegen;

/// <summary>ScriptDetails (src/codegen/script-details.h): the origin a script is compiled with.</summary>
public readonly record struct ScriptDetails(
    JSValue NameObj,
    int LineOffset = 0,
    int ColumnOffset = 0,
    bool IsModule = false,
    bool IsSharedCrossOrigin = false,
    bool IsReplMode = false)
{
    /// <summary>ScriptOriginOptions::Flags (the bits V8Sharp's API can set).</summary>
    public int OriginOptionsFlags => (IsSharedCrossOrigin ? 1 : 0) | (IsModule ? 8 : 0);
}

/// <summary>
/// CompilationCacheScript: the toplevel SharedFunctionInfo of a script by its
/// source and origin (ScriptCacheKey: source, name, line and column offset,
/// origin options), so that compiling the same script again (d8's load() of
/// one file, the same source in another realm) reuses its Script and
/// SharedFunctionInfos. As in V8 the key holds the Script weakly and the value
/// holds the toplevel SharedFunctionInfo strongly until the entry is aged
/// (V8: when bytecode flushing clears it; here: not looked up between two full
/// .NET collections). Deviation: once aged, V8 answers a lookup whose Script
/// is still alive with the Script only (a partial hit: the toplevel is
/// recompiled into it); V8Sharp does not flush bytecode, so the Script's
/// toplevel SharedFunctionInfo is still compiled and is returned (a full hit).
/// </summary>
public sealed class CompilationCacheScript
{
    const int kCapacity = 4096;

    /// <summary>ScriptCacheKey's hash fields. Without a name only the source is compared (MatchesScript).</summary>
    readonly record struct Key(int Hash, int Length, string? Name, int LineOffset, int ColumnOffset, int OriginOptions);

    sealed class Entry(Script script, SharedFunctionInfo toplevel)
    {
        public readonly WeakReference<Script> WeakScript = new(script);
        public SharedFunctionInfo? ToplevelSfi = toplevel;
        public bool Used = true;
        public Entry? Next;
    }

    readonly Dictionary<Key, Entry> _table = [];
    int _gen2Count = GC.CollectionCount(2);

    static readonly ConditionalWeakTable<Isolate, CompilationCacheScript> s_caches = new();

    /// <summary>
    /// The isolate's script cache, or null when it is disabled (--no-compilation-cache)
    /// or the language mode is strict (CompilationCache::IsEnabledScript: the cache
    /// only holds scripts compiled with the default language mode).
    /// </summary>
    public static CompilationCacheScript? For(Isolate isolate, LanguageMode languageMode) =>
        isolate.Flags.compilation_cache && languageMode == LanguageMode.Sloppy
            ? s_caches.GetValue(isolate, static _ => new CompilationCacheScript())
            : null;

    /// <summary>The key of a source and origin, or false when the origin cannot be cached (a non-string name).</summary>
    static bool TryKey(string source, in ScriptDetails details, out Key key)
    {
        int hash = string.GetHashCode(source.AsSpan());
        if (details.NameObj.IsUndefined)
        {
            key = new Key(hash, source.Length, null, 0, 0, 0);
            return true;
        }
        if (details.NameObj.HeapObjectOrNull is JSString name)
        {
            key = new Key(hash, source.Length, name.ToString(), details.LineOffset, details.ColumnOffset,
                details.OriginOptionsFlags);
            return true;
        }
        // V8: a name that is not a string matches no Script (MatchesScript), and
        // PutScript stores the Script without a name, which no lookup finds either.
        key = default;
        return false;
    }

    /// <summary>ScriptCacheKey::MatchesScript, for the parts the key does not hold.</summary>
    static bool MatchesScript(Script script, string source, string? name, bool isModule)
    {
        if (name is null && !script.Name.IsUndefined) return false;
        // Deviation: V8 compares the origin options only for named scripts; a
        // module and a classic script with one source and no name would share
        // an entry. V8Sharp never answers one with the other.
        if (script.OriginOptionsIsModule != isModule) return false;
        // The API compiles without host-defined options or wrapped arguments.
        if (script.IsWrapped) return false;
        if (!script.HostDefinedOptions.IsUndefined && script.HostDefinedOptions.HeapObjectOrNull is not FixedArray { Length: 0 })
            return false;
        return string.Equals(script.SourceString, source, StringComparison.Ordinal);
    }

    /// <summary>
    /// CompilationCacheScript::Lookup: the cached toplevel SharedFunctionInfo
    /// (compiled) of the script with this source and origin, or null.
    /// </summary>
    public SharedFunctionInfo? Lookup(string source, in ScriptDetails details)
    {
        if (!TryKey(source, details, out Key key)) return null;
        lock (this)
        {
            MaybeAge();
            if (!_table.TryGetValue(key, out Entry? entry)) return null;
            for (; entry is not null; entry = entry.Next)
            {
                if (!entry.WeakScript.TryGetTarget(out Script? script) ||
                    !MatchesScript(script, source, key.Name, details.IsModule)) continue;
                SharedFunctionInfo? toplevel = entry.ToplevelSfi ?? script.FindSharedFunctionInfo(0);
                if (toplevel is null || !toplevel.IsCompiled) return null;
                entry.ToplevelSfi = toplevel;
                entry.Used = true;
                return toplevel;
            }
            return null;
        }
    }

    /// <summary>CompilationCacheScript::Put (CompilationCacheTable::PutScript): overwrites a matching entry.</summary>
    public void Put(string source, in ScriptDetails details, SharedFunctionInfo toplevel)
    {
        if (!TryKey(source, details, out Key key)) return;
        Script script = toplevel.Script!;
        lock (this)
        {
            MaybeAge();
            ref Entry? head = ref CollectionsMarshal.GetValueRefOrAddDefault(_table, key, out bool exists);
            if (exists)
            {
                for (Entry? e = head; e is not null; e = e.Next)
                {
                    if (e.WeakScript.TryGetTarget(out Script? other) && MatchesScript(other, source, key.Name, details.IsModule))
                    {
                        e.WeakScript.SetTarget(script);
                        e.ToplevelSfi = toplevel;
                        e.Used = true;
                        return;
                    }
                }
            }
            head = new Entry(script, toplevel) { Next = head };
            if (_table.Count > kCapacity) EnsureCapacity();
        }
    }

    /// <summary>
    /// CompilationCacheTable::EnsureScriptTableCapacity: drops the entries whose
    /// Script died; when that is not enough, the whole table.
    /// </summary>
    void EnsureCapacity()
    {
        RemoveDeadEntries();
        if (_table.Count > kCapacity) _table.Clear();
    }

    void RemoveDeadEntries()
    {
        List<Key>? empty = null;
        List<(Key, Entry)>? moved = null;
        foreach (KeyValuePair<Key, Entry> pair in _table)
        {
            Entry? head = pair.Value, previous = null;
            for (Entry? e = head; e is not null; e = e.Next)
            {
                if (e.ToplevelSfi is null && !e.WeakScript.TryGetTarget(out _))
                {
                    if (previous is null) head = e.Next; else previous.Next = e.Next;
                }
                else previous = e;
            }
            if (head is null) (empty ??= []).Add(pair.Key);
            else if (!ReferenceEquals(head, pair.Value)) (moved ??= []).Add((pair.Key, head));
        }
        if (empty is not null) foreach (Key k in empty) _table.Remove(k);
        if (moved is not null) foreach ((Key k, Entry head) in moved) _table[k] = head;
    }

    /// <summary>
    /// CompilationCacheScript::Age, run when a full collection happened since
    /// the last call: the entries not looked up since the previous age release
    /// their toplevel SharedFunctionInfo (V8: cleared when its bytecode was
    /// flushed) and keep the Script weakly; entries whose Script died go.
    /// </summary>
    void MaybeAge()
    {
        int gen2Count = GC.CollectionCount(2);
        if (gen2Count == _gen2Count) return;
        _gen2Count = gen2Count;
        foreach (Entry head in _table.Values)
        {
            for (Entry? e = head; e is not null; e = e.Next)
            {
                if (!e.Used) e.ToplevelSfi = null;
                e.Used = false;
            }
        }
        RemoveDeadEntries();
    }

    /// <summary>For tests: ages the table as a full collection would.</summary>
    internal void AgeForTesting()
    {
        lock (this)
        {
            _gen2Count = -1;
            MaybeAge();
        }
    }

    /// <summary>CompilationCacheEvalOrScript::Clear.</summary>
    public void Clear()
    {
        lock (this) _table.Clear();
    }
}

/// <summary>
/// CompilationCacheEval: the SharedFunctionInfo of an eval'd source by
/// (source, outer function, language mode, eval position), so that the same
/// eval (a loop, a function called again, new Function with the same text)
/// is compiled once. V8 keeps a FeedbackCell per native context beside the
/// SharedFunctionInfo and ages entries out with bytecode flushing (old
/// bytecode is dropped after --bytecode-old-time seconds unused). V8Sharp
/// keeps the SharedFunctionInfo only (each hit gets a new feedback cell) and
/// ages the table by full .NET collections: an entry not used since the
/// previous gen-2 collection is demoted at the next one to a weak reference,
/// which still hits while the eval's code is alive (its closures keep the
/// Script and its toplevel SharedFunctionInfo), so a stream of distinct
/// evals (Octane CodeLoad's salted sources) does not keep its scripts alive.
/// </summary>
public sealed class CompilationCacheEval
{
    const int kCapacity = 4096;

    /// <summary>
    /// Deviation: V8 caches evals of any length strongly. An entry keeps the
    /// script, its source and bytecode alive until it is aged out, and with the
    /// .NET GC that makes every gen-2 collection in between mark it: compiling
    /// large distinct sources through eval (the compile micro-benchmarks on
    /// Octane's PdfJS and TypeScript sources) ran 25-30% slower with them
    /// cached, and 7% slower on PdfJS with them held weakly. A source longer
    /// than this is only marked as seen when first compiled and cached
    /// strongly when compiled again before the next full collection, so a
    /// repeated large eval hits from its third evaluation (V8: its second).
    /// </summary>
    public const int kMaxSourceLength = 16 * 1024;

    /// <summary>EvalCacheKey, with the source as its hash and length (an entry compares its Script's source).</summary>
    public readonly record struct Key(int Hash, int Length, SharedFunctionInfo OuterInfo, LanguageMode LanguageMode, int Position);

    sealed class Entry
    {
        public SharedFunctionInfo? Shared;
        public WeakReference<SharedFunctionInfo>? Weak;
        public bool Used = true;
        public Entry? Next;

        public SharedFunctionInfo? Target => Shared ?? (Weak is not null && Weak.TryGetTarget(out var s) ? s : null);
    }

    readonly Dictionary<Key, Entry> _table = [];
    int _gen2Count = GC.CollectionCount(2);

    static readonly ConditionalWeakTable<Isolate, CompilationCacheEval> s_caches = new();

    /// <summary>The isolate's eval cache, or null when it is disabled (--no-compilation-cache).</summary>
    public static CompilationCacheEval? For(Isolate isolate) =>
        isolate.Flags.compilation_cache ? s_caches.GetValue(isolate, static _ => new CompilationCacheEval()) : null;

    /// <summary>
    /// EvalCacheKey for the source string and its flat contents, computed once
    /// per eval and passed to Lookup and Put. V8 hashes the source with its
    /// cached string hash (source->EnsureHash()): computed once and kept on
    /// the string for up to String::kMaxHashCalcLength characters, the length
    /// alone above that. A short source uses the JSString's cached hash here as
    /// well. Deviation: a longer source hashes its first and last
    /// kLongSourceHashChars characters with its length, so that the seen
    /// markers of distinct large sources of one length (CodeLoad's salted
    /// evals) do not match each other and get the next one held strongly (see
    /// kMaxSourceLength). A lookup compares the whole source either way.
    /// </summary>
    public static Key KeyOf(JSString source, string flat, SharedFunctionInfo outerInfo, LanguageMode languageMode, int position)
    {
        int hash = flat.Length <= JSString.kMaxHashCalcLength
            ? (int)source.EnsureHash()
            : LongSourceHash(flat);
        return new Key(hash, flat.Length, outerInfo, languageMode, position);
    }

    const int kLongSourceHashChars = 2048;

    static int LongSourceHash(string flat)
    {
        ReadOnlySpan<char> span = flat.AsSpan();
        return HashCode.Combine(flat.Length, string.GetHashCode(span[..kLongSourceHashChars]),
            string.GetHashCode(span[^kLongSourceHashChars..]));
    }

    static Key KeyOf(string source, SharedFunctionInfo outerInfo, LanguageMode languageMode, int position) =>
        new(source.Length <= JSString.kMaxHashCalcLength
                ? (int)(StringHasher.HashSequentialString(source.AsSpan()) >> Name.HashShift)
                : LongSourceHash(source),
            source.Length, outerInfo, languageMode, position);

    static bool SourceEquals(SharedFunctionInfo shared, string source) =>
        shared.Script is { } script && string.Equals(script.SourceString, source, StringComparison.Ordinal);

    public SharedFunctionInfo? Lookup(string source, SharedFunctionInfo outerInfo, LanguageMode languageMode, int position) =>
        Lookup(KeyOf(source, outerInfo, languageMode, position), source);

    public SharedFunctionInfo? Lookup(in Key key, string source)
    {
        lock (this)
        {
            MaybeAge();
            if (!_table.TryGetValue(key, out Entry? entry)) return null;
            for (; entry is not null; entry = entry.Next)
            {
                if (entry.Target is not { } shared || !SourceEquals(shared, source)) continue;
                if (!shared.IsCompiled) return null;
                // Used again: held strongly until it is unused for a full collection.
                entry.Shared = shared;
                entry.Weak = null;
                entry.Used = true;
                return shared;
            }
            return null;
        }
    }

    public void Put(string source, SharedFunctionInfo outerInfo, LanguageMode languageMode, int position,
                    SharedFunctionInfo shared) =>
        Put(KeyOf(source, outerInfo, languageMode, position), source, shared);

    public void Put(in Key key, string source, SharedFunctionInfo shared)
    {
        lock (this)
        {
            MaybeAge();
            ref Entry? head = ref CollectionsMarshal.GetValueRefOrAddDefault(_table, key, out bool exists);
            if (exists)
            {
                // A matching entry, a seen marker or one whose code died (this
                // source, or one with the same hash, was compiled before): hold the
                // new result strongly.
                for (Entry? e = head; e is not null; e = e.Next)
                {
                    if (e.Target is { } other && !SourceEquals(other, source)) continue;
                    e.Shared = shared;
                    e.Weak = null;
                    e.Used = true;
                    return;
                }
            }
            // A large source is only marked as seen (an entry without a target,
            // dropped at the next age); compiled again, it is held strongly.
            var entry = new Entry { Next = head };
            if (source.Length <= kMaxSourceLength) entry.Shared = shared;
            head = entry;
            if (_table.Count > kCapacity) EnsureCapacity();
        }
    }

    void EnsureCapacity()
    {
        RemoveDeadEntries();
        if (_table.Count > kCapacity) _table.Clear();
    }

    void RemoveDeadEntries()
    {
        List<Key>? empty = null;
        List<(Key, Entry)>? moved = null;
        foreach (KeyValuePair<Key, Entry> pair in _table)
        {
            Entry? head = pair.Value, previous = null;
            for (Entry? e = head; e is not null; e = e.Next)
            {
                // Dead: a weak entry whose code died, or a seen marker unused
                // since the previous age.
                if (e.Target is null && !(e.Weak is null && e.Used))
                {
                    if (previous is null) head = e.Next; else previous.Next = e.Next;
                }
                else previous = e;
            }
            if (head is null) (empty ??= []).Add(pair.Key);
            else if (!ReferenceEquals(head, pair.Value)) (moved ??= []).Add((pair.Key, head));
        }
        if (empty is not null) foreach (Key k in empty) _table.Remove(k);
        if (moved is not null) foreach ((Key k, Entry head) in moved) _table[k] = head;
    }

    /// <summary>
    /// CompilationCacheEval::Age, run when a full collection happened since the
    /// last call: entries whose code died and seen markers unused since the
    /// previous age go; entries not used since the previous age are demoted to
    /// weak references.
    /// </summary>
    void MaybeAge()
    {
        int gen2Count = GC.CollectionCount(2);
        if (gen2Count == _gen2Count) return;
        _gen2Count = gen2Count;
        RemoveDeadEntries();
        foreach (Entry head in _table.Values)
        {
            for (Entry? e = head; e is not null; e = e.Next)
            {
                if (!e.Used && e.Shared is { } shared)
                {
                    e.Weak = new WeakReference<SharedFunctionInfo>(shared);
                    e.Shared = null;
                }
                e.Used = false;
            }
        }
    }

    /// <summary>For tests: ages the table as a full collection would.</summary>
    internal void AgeForTesting()
    {
        lock (this)
        {
            _gen2Count = -1;
            MaybeAge();
        }
    }

    /// <summary>CompilationCache::Clear.</summary>
    public void Clear()
    {
        lock (this) _table.Clear();
    }
}
