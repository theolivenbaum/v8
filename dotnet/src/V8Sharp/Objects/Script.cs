// The parser's view of the engine's Script and SharedFunctionInfo
// (IParsingScript, IScriptEvalOrigin, IParsingSharedFunctionInfo): what
// src/parsing reads from them when compiling toplevel code, lazy functions
// and eval code.
using V8Sharp.Ast;
using V8Sharp.Parsing;

namespace V8Sharp.Objects;

public sealed partial class Script : IParsingScript
{
    string? _sourceString;

    /// <summary>The source as a .NET string (flattened once and cached).</summary>
    public string SourceString => _sourceString ??= Source.HeapObjectOrNull is JSString s ? s.ToString() : "";

    string IParsingScript.source() => SourceString;
    int IParsingScript.id() => Id;
    bool IParsingScript.is_wrapped() => IsWrapped;

    IReadOnlyList<string> IParsingScript.wrapped_arguments()
    {
        FixedArray args = WrappedArguments!;
        var result = new string[args.Length];
        for (int i = 0; i < result.Length; i++) result[i] = args[i].As<JSString>().ToString();
        return result;
    }

    int IParsingScript.line_offset() => LineOffset;
    int IParsingScript.column_offset() => ColumnOffset;

    int IScriptEvalOrigin.eval_from_position() => EvalFromPosition;
    bool IScriptEvalOrigin.has_eval_from_shared() => HasEvalFromShared;
    IScriptEvalOrigin? IScriptEvalOrigin.eval_from_shared_script() => EvalFromShared?.Script;

    IScopeInfo? IScriptEvalOrigin.eval_from_scope_info() =>
        EvalFromShared is { } shared && shared.HasScopeInfo ? shared.ScopeInfo : null;
}

/// <summary>A SharedFunctionInfo as the parser reads it when reparsing a function.</summary>
public sealed class ParsingSharedFunctionInfo(SharedFunctionInfo shared) : IParsingSharedFunctionInfo
{
    public SharedFunctionInfo Shared => shared;

    public IParsingScript script() => shared.Script!;
    public int StartPosition() => shared.StartPosition();
    public int EndPosition() => shared.EndPosition();

    public bool HasOuterScopeInfo() =>
        shared.OuterScopeInfo is not null || (shared.HasScopeInfo && shared.ScopeInfo.HasOuterScopeInfo);

    public IScopeInfo GetOuterScopeInfo() =>
        shared.HasScopeInfo && shared.ScopeInfo.HasOuterScopeInfo ? shared.ScopeInfo.OuterScopeInfo() : shared.OuterScopeInfo!;

    public bool is_wrapped() => shared.IsWrapped;
    public int function_literal_id() => shared.FunctionLiteralId;
    public string Name() => shared.Name().ToString();
    public bool private_name_lookup_skips_outer_class() => shared.PrivateNameLookupSkipsOuterClass;
}
