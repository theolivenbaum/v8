// Copyright 2016 the V8 project authors. All rights reserved.
// Use of this source code is governed by a BSD-style license that can be
// found in the LICENSE file.

// Port of src/parsing/parsing.h and parsing.cc (namespace v8::internal::parsing).

#nullable disable

using V8Sharp.Ast;
using V8Sharp.Common;

namespace V8Sharp.Parsing;

public static class ParsingEntry
{
    // Parses the top-level source code represented by the parse info and sets its
    // function literal. Allows passing an |outer_scope| for programs that exist in
    // another scope (e.g. eval). Returns false if parsing failed.
    //
    // V8's ReportStatisticsMode (moving use counters to the Isolate) is left to
    // the caller: use_counts, when given, receives the counters.
    public static bool ParseProgram(ParseInfo info, IParsingScript script, IScopeInfo maybe_outer_scope_info = null,
                                    List<UseCounterFeature> use_counts = null)
    {
        // Create a character stream for the parser.
        info.set_character_stream(ScannerStream.For(script.source()));

        Parser parser = new(info);

        parser.ParseProgram(script, info, maybe_outer_scope_info);
        if (use_counts != null) parser.UpdateStatistics(script, use_counts, out _);
        return info.literal() != null;
    }

    // Like ParseProgram but for an individual function which already has a
    // allocated shared function info.
    public static bool ParseFunction(ParseInfo info, IParsingSharedFunctionInfo shared_info,
                                     List<UseCounterFeature> use_counts = null)
    {
        // Create a character stream for the parser.
        IParsingScript script = shared_info.script();
        string source = script.source();
        int start_pos = shared_info.StartPosition();
        int end_pos = shared_info.EndPosition();
        if (end_pos > source.Length)
        {
            throw new InvalidOperationException("shared function end position beyond source length");
        }
        info.set_character_stream(ScannerStream.For(source, start_pos, end_pos));

        Parser parser = new(info);

        parser.ParseFunction(info, shared_info);
        if (use_counts != null) parser.UpdateStatistics(script, use_counts, out _);
        return info.literal() != null;
    }

    // If you don't know whether info->is_toplevel() is true or not, use this method
    // to dispatch to either of the above functions. Prefer to use the above methods
    // whenever possible.
    public static bool ParseAny(ParseInfo info, IParsingSharedFunctionInfo shared_info,
                                List<UseCounterFeature> use_counts = null)
    {
        if (info.flags().is_toplevel())
        {
            IScopeInfo maybe_outer_scope_info = null;
            if (shared_info.HasOuterScopeInfo())
            {
                maybe_outer_scope_info = shared_info.GetOuterScopeInfo();
            }
            return ParseProgram(info, shared_info.script(), maybe_outer_scope_info, use_counts);
        }
        return ParseFunction(info, shared_info, use_counts);
    }
}

// A plain script for parsing source that has no heap-side Script (tests, the
// corpus runner, tools).
public sealed class SourceScript(string source, int id = 0) : IParsingScript
{
    private readonly string _source = source;
    private readonly int _id = id;

    public string source() => _source;
    public int id() => _id;
    public bool is_wrapped() => wrapped_arguments_ != null;
    public IReadOnlyList<string> wrapped_arguments() => wrapped_arguments_;
    public int line_offset() => 0;
    public int column_offset() => 0;
    public int eval_from_position() => -1;
    public bool has_eval_from_shared() => false;
    public IScriptEvalOrigin eval_from_shared_script() => null;
    public IScopeInfo eval_from_scope_info() => null;

    public IReadOnlyList<string> wrapped_arguments_;
}
