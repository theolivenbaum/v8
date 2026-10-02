// Copyright 2011 the V8 project authors. All rights reserved.
// Use of this source code is governed by a BSD-style license that can be
// found in the LICENSE file.

// Port of the eval part of src/codegen/compilation-cache.h/.cc
// (CompilationCacheEval). The script part is not ported: the embedder API
// compiles every script afresh. The regexp part is in JSRegExp.cs.

using System.Runtime.CompilerServices;

namespace V8Sharp.Codegen;

/// <summary>
/// CompilationCacheEval: the SharedFunctionInfo of an eval'd source by
/// (source, outer function, language mode, eval position), so that the same
/// eval (a loop, a function called again, new Function with the same text)
/// is compiled once. V8 keeps a FeedbackCell per native context beside the
/// SharedFunctionInfo and ages entries out with bytecode flushing (old
/// bytecode is dropped after some full GCs). V8Sharp keeps the
/// SharedFunctionInfo only (each hit gets a new feedback cell) and ages the
/// table by full .NET collections: an entry not used since the previous
/// gen-2 collection is dropped at the next one, so a stream of distinct
/// evals (Octane CodeLoad's salted sources) does not keep its scripts alive.
/// The table is also cleared past <see cref="kCapacity"/> entries.
/// </summary>
public sealed class CompilationCacheEval
{
    const int kCapacity = 4096;

    Dictionary<(string Source, SharedFunctionInfo OuterInfo, LanguageMode LanguageMode, int Position),
        SharedFunctionInfo> _table = new();
    // The previous generation: entries not looked up since the last full GC.
    Dictionary<(string Source, SharedFunctionInfo OuterInfo, LanguageMode LanguageMode, int Position),
        SharedFunctionInfo> _old = new();
    int _gen2Count = GC.CollectionCount(2);

    /// <summary>CompilationCacheEval::Age, run when a full collection happened since the last call.</summary>
    void MaybeAge()
    {
        int gen2Count = GC.CollectionCount(2);
        if (gen2Count == _gen2Count) return;
        _gen2Count = gen2Count;
        (_old, _table) = (_table, _old);
        _table.Clear();
    }

    static readonly ConditionalWeakTable<Isolate, CompilationCacheEval> s_caches = new();

    /// <summary>The isolate's eval cache, or null when it is disabled (--no-compilation-cache).</summary>
    public static CompilationCacheEval? For(Isolate isolate) =>
        isolate.Flags.compilation_cache ? s_caches.GetValue(isolate, static _ => new CompilationCacheEval()) : null;

    public SharedFunctionInfo? Lookup(string source, SharedFunctionInfo outerInfo, LanguageMode languageMode, int position)
    {
        lock (this)
        {
            MaybeAge();
            var key = (source, outerInfo, languageMode, position);
            if (!_table.TryGetValue(key, out SharedFunctionInfo? shared))
            {
                if (!_old.Remove(key, out shared)) return null;
                _table[key] = shared;
            }
            return shared.IsCompiled ? shared : null;
        }
    }

    public void Put(string source, SharedFunctionInfo outerInfo, LanguageMode languageMode, int position,
                    SharedFunctionInfo shared)
    {
        lock (this)
        {
            MaybeAge();
            if (_table.Count >= kCapacity) _table.Clear();
            _table[(source, outerInfo, languageMode, position)] = shared;
        }
    }

    /// <summary>CompilationCache::Clear.</summary>
    public void Clear()
    {
        lock (this)
        {
            _table.Clear();
            _old.Clear();
        }
    }
}
