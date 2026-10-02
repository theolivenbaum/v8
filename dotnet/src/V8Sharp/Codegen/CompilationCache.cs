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
/// SharedFunctionInfo and ages entries whose bytecode was flushed; V8Sharp
/// keeps the SharedFunctionInfo only (each hit gets a new feedback cell) and
/// clears the table when it grows past <see cref="kCapacity"/> entries, as
/// its regexp cache does.
/// </summary>
public sealed class CompilationCacheEval
{
    const int kCapacity = 4096;

    readonly Dictionary<(string Source, SharedFunctionInfo OuterInfo, LanguageMode LanguageMode, int Position),
        SharedFunctionInfo> _table = new();

    static readonly ConditionalWeakTable<Isolate, CompilationCacheEval> s_caches = new();

    /// <summary>The isolate's eval cache, or null when it is disabled (--no-compilation-cache).</summary>
    public static CompilationCacheEval? For(Isolate isolate) =>
        isolate.Flags.compilation_cache ? s_caches.GetValue(isolate, static _ => new CompilationCacheEval()) : null;

    public SharedFunctionInfo? Lookup(string source, SharedFunctionInfo outerInfo, LanguageMode languageMode, int position)
    {
        lock (_table)
        {
            return _table.TryGetValue((source, outerInfo, languageMode, position), out SharedFunctionInfo? shared) &&
                   shared.IsCompiled
                ? shared
                : null;
        }
    }

    public void Put(string source, SharedFunctionInfo outerInfo, LanguageMode languageMode, int position,
                    SharedFunctionInfo shared)
    {
        lock (_table)
        {
            if (_table.Count >= kCapacity) _table.Clear();
            _table[(source, outerInfo, languageMode, position)] = shared;
        }
    }

    /// <summary>CompilationCache::Clear.</summary>
    public void Clear()
    {
        lock (_table) _table.Clear();
    }
}
