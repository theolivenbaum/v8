// Copyright 2016 the V8 project authors. All rights reserved.
// Use of this source code is governed by a BSD-style license that can be
// found in the LICENSE file.

// Port of src/parsing/parse-info.h and parse-info.cc.
//
// The Isolate/Script/SharedFunctionInfo-taking factories become factories
// taking the values they read (script id, script flags, function flags); the
// engine calls them with its objects' fields.

using V8Sharp.Ast;
using V8Sharp.Common;
using static V8Sharp.Common.Globals;

namespace V8Sharp.Parsing;

// The flags for a parse + unoptimized compile operation.
public struct UnoptimizedCompileFlags
{
    // FLAG_FIELDS
    public bool is_toplevel_;
    public bool is_eager_;
    public bool is_eval_;
    public bool is_reparse_;
    public LanguageMode outer_language_mode_;
    public ParseRestriction parse_restriction_;
    public bool is_module_;
    public bool allow_lazy_parsing_;
    public bool is_lazy_compile_;
    public bool coverage_enabled_;
    public bool block_coverage_enabled_;
    public bool class_scope_has_private_brand_;
    public bool private_name_lookup_skips_outer_class_;
    public bool requires_instance_members_initializer_;
    public bool has_static_private_methods_or_accessors_;
    public bool allow_natives_syntax_;
    public bool allow_lazy_compile_;
    public bool post_parallel_compile_tasks_for_eager_toplevel_;
    public bool post_parallel_compile_tasks_for_lazy_;
    public bool collect_source_positions_;
    public bool is_repl_mode_;
    public bool produce_compile_hints_;
    public bool compile_hints_magic_enabled_;
    public bool compile_hints_per_function_magic_enabled_;
    public bool is_hoisted_in_context_;
    public bool allow_heap_allocation_;

    private int _scriptId;
    private FunctionKind _functionKind;
    private FunctionSyntaxKind _functionSyntaxKind;
    private ParsingWhileDebugging _parsingWhileDebugging;

    // UnoptimizedCompileFlags(Isolate*, int script_id). The isolate's code
    // coverage mode and NeedsDetailedOptimizedCodeLineInfo are parameters.
    public UnoptimizedCompileFlags(ParsingFlags flags, int script_id, bool is_best_effort_code_coverage = true,
                                   bool is_block_code_coverage = false,
                                   bool needs_detailed_optimized_code_line_info = false)
    {
        this = default;
        _scriptId = script_id;
        _functionKind = FunctionKind.NormalFunction;
        _functionSyntaxKind = FunctionSyntaxKind.Declaration;
        _parsingWhileDebugging = ParsingWhileDebugging.No;
        set_coverage_enabled(!is_best_effort_code_coverage);
        set_block_coverage_enabled(is_block_code_coverage);
        set_allow_natives_syntax(flags.allow_natives_syntax);
        set_allow_lazy_compile(true);
        set_collect_source_positions(!flags.enable_lazy_source_positions || needs_detailed_optimized_code_line_info);
        set_post_parallel_compile_tasks_for_eager_toplevel(flags.parallel_compile_tasks_for_eager_toplevel);
        set_post_parallel_compile_tasks_for_lazy(flags.parallel_compile_tasks_for_lazy);
        set_allow_heap_allocation(true);
    }

    // Set-up flags for a toplevel compilation.
    public static UnoptimizedCompileFlags ForToplevelCompile(ParsingFlags flags, int script_id, bool is_user_javascript,
                                                             LanguageMode language_mode, REPLMode repl_mode,
                                                             ScriptType type, bool lazy)
    {
        var result = new UnoptimizedCompileFlags(flags, script_id);
        result.SetFlagsForToplevelCompile(is_user_javascript, language_mode, repl_mode, type, lazy);
        return result;
    }

    // The fields of a Script that ForScriptCompile / ForFunctionCompile read.
    public readonly record struct ScriptDetails(
        int id,
        bool is_user_javascript,
        LanguageMode outer_language_mode,
        bool is_repl_mode,
        bool is_module,
        bool is_function_constructor,
        bool is_wrapped,
        bool has_eval_origin);

    // Set-up flags for a full compilation of a given script.
    public static UnoptimizedCompileFlags ForScriptCompile(ParsingFlags flags, ScriptDetails script)
    {
        var result = new UnoptimizedCompileFlags(flags, script.id);

        result.SetFlagsForToplevelCompile(script.is_user_javascript, script.outer_language_mode,
                                          construct_repl_mode(script.is_repl_mode),
                                          script.is_module ? ScriptType.Module : ScriptType.Classic, flags.lazy);
        result.set_outer_language_mode(script.outer_language_mode);
        if (script.is_function_constructor)
        {
            result.set_parse_restriction(ParseRestriction.ONLY_SINGLE_FUNCTION_LITERAL);
        }
        result.SetFlagsForFunctionFromScript(script);
        if (script.is_wrapped)
        {
            result.set_function_syntax_kind(FunctionSyntaxKind.Wrapped);
            result.set_is_eval(true);
        }

        return result;
    }

    // The fields of a SharedFunctionInfo / FunctionLiteral that
    // SetFlagsFromFunction reads.
    public readonly record struct FunctionDetails(
        LanguageMode language_mode,
        FunctionKind kind,
        FunctionSyntaxKind syntax_kind,
        bool requires_instance_members_initializer,
        bool class_scope_has_private_brand,
        bool has_static_private_methods_or_accessors,
        bool private_name_lookup_skips_outer_class,
        bool is_toplevel,
        bool is_hoisted_in_context);

    // Set-up flags for a compiling a particular function (either a lazy compile
    // or a recompile).
    public static UnoptimizedCompileFlags ForFunctionCompile(ParsingFlags flags, FunctionDetails shared, ScriptDetails script)
    {
        var result = new UnoptimizedCompileFlags(flags, script.id);

        result.SetFlagsFromFunction(shared);
        result.SetFlagsForFunctionFromScript(script);
        result.set_allow_lazy_parsing(true);
        result.set_is_lazy_compile(true);

        result.set_is_repl_mode(script.is_repl_mode);

        return result;
    }

    // Set-up flags for a parallel toplevel function compilation, based on the
    // flags of an existing toplevel compilation.
    public static UnoptimizedCompileFlags ForToplevelFunction(UnoptimizedCompileFlags toplevel_flags, FunctionLiteral literal)
    {
        // Replicate the toplevel flags, then setup the function-specific flags.
        UnoptimizedCompileFlags flags = toplevel_flags;
        flags.SetFlagsFromFunction(DetailsOf(literal));
        return flags;
    }

    public static FunctionDetails DetailsOf(FunctionLiteral function) => new(
        function.language_mode(), function.kind(), function.syntax_kind(),
        function.requires_instance_members_initializer(), function.class_scope_has_private_brand(),
        function.has_static_private_methods_or_accessors(), function.private_name_lookup_skips_outer_class(),
        function.is_toplevel(), function.is_hoisted_in_context());

    // Create flags for a test.
    public static UnoptimizedCompileFlags ForTest(ParsingFlags? flags = null) =>
        new(flags ?? ParsingFlags.Default, kTemporaryScriptId);

    private void SetFlagsFromFunction(FunctionDetails function)
    {
        set_outer_language_mode(function.language_mode);
        set_function_kind(function.kind);
        set_function_syntax_kind(function.syntax_kind);
        set_requires_instance_members_initializer(function.requires_instance_members_initializer);
        set_class_scope_has_private_brand(function.class_scope_has_private_brand);
        set_has_static_private_methods_or_accessors(function.has_static_private_methods_or_accessors);
        set_private_name_lookup_skips_outer_class(function.private_name_lookup_skips_outer_class);
        set_is_toplevel(function.is_toplevel);
        set_is_hoisted_in_context(function.is_hoisted_in_context);
    }

    private void SetFlagsForToplevelCompile(bool is_user_javascript, LanguageMode language_mode, REPLMode repl_mode,
                                            ScriptType type, bool lazy)
    {
        set_is_toplevel(true);
        set_allow_lazy_parsing(lazy);
        set_allow_lazy_compile(lazy);
        set_outer_language_mode(stricter_language_mode(outer_language_mode(), language_mode));
        set_is_repl_mode(repl_mode == REPLMode.Yes);
        set_is_module(type == ScriptType.Module);

        set_block_coverage_enabled(block_coverage_enabled() && is_user_javascript);
    }

    private void SetFlagsForFunctionFromScript(ScriptDetails script)
    {
        set_is_eval(is_toplevel() && script.has_eval_origin);
        set_is_module(script.is_module);

        set_block_coverage_enabled(block_coverage_enabled() && script.is_user_javascript);
    }

    public readonly bool is_toplevel() => is_toplevel_;
    public UnoptimizedCompileFlags set_is_toplevel(bool v) { is_toplevel_ = v; return this; }
    public readonly bool is_eager() => is_eager_;
    public UnoptimizedCompileFlags set_is_eager(bool v) { is_eager_ = v; return this; }
    public readonly bool is_eval() => is_eval_;
    public UnoptimizedCompileFlags set_is_eval(bool v) { is_eval_ = v; return this; }
    public readonly bool is_reparse() => is_reparse_;
    public UnoptimizedCompileFlags set_is_reparse(bool v) { is_reparse_ = v; return this; }
    public readonly LanguageMode outer_language_mode() => outer_language_mode_;
    public UnoptimizedCompileFlags set_outer_language_mode(LanguageMode v) { outer_language_mode_ = v; return this; }
    public readonly ParseRestriction parse_restriction() => parse_restriction_;
    public UnoptimizedCompileFlags set_parse_restriction(ParseRestriction v) { parse_restriction_ = v; return this; }
    public readonly bool is_module() => is_module_;
    public UnoptimizedCompileFlags set_is_module(bool v) { is_module_ = v; return this; }
    public readonly bool allow_lazy_parsing() => allow_lazy_parsing_;
    public UnoptimizedCompileFlags set_allow_lazy_parsing(bool v) { allow_lazy_parsing_ = v; return this; }
    public readonly bool is_lazy_compile() => is_lazy_compile_;
    public UnoptimizedCompileFlags set_is_lazy_compile(bool v) { is_lazy_compile_ = v; return this; }
    public readonly bool coverage_enabled() => coverage_enabled_;
    public UnoptimizedCompileFlags set_coverage_enabled(bool v) { coverage_enabled_ = v; return this; }
    public readonly bool block_coverage_enabled() => block_coverage_enabled_;
    public UnoptimizedCompileFlags set_block_coverage_enabled(bool v) { block_coverage_enabled_ = v; return this; }
    public readonly bool class_scope_has_private_brand() => class_scope_has_private_brand_;
    public UnoptimizedCompileFlags set_class_scope_has_private_brand(bool v) { class_scope_has_private_brand_ = v; return this; }
    public readonly bool private_name_lookup_skips_outer_class() => private_name_lookup_skips_outer_class_;
    public UnoptimizedCompileFlags set_private_name_lookup_skips_outer_class(bool v) { private_name_lookup_skips_outer_class_ = v; return this; }
    public readonly bool requires_instance_members_initializer() => requires_instance_members_initializer_;
    public UnoptimizedCompileFlags set_requires_instance_members_initializer(bool v) { requires_instance_members_initializer_ = v; return this; }
    public readonly bool has_static_private_methods_or_accessors() => has_static_private_methods_or_accessors_;
    public UnoptimizedCompileFlags set_has_static_private_methods_or_accessors(bool v) { has_static_private_methods_or_accessors_ = v; return this; }
    public readonly bool allow_natives_syntax() => allow_natives_syntax_;
    public UnoptimizedCompileFlags set_allow_natives_syntax(bool v) { allow_natives_syntax_ = v; return this; }
    public readonly bool allow_lazy_compile() => allow_lazy_compile_;
    public UnoptimizedCompileFlags set_allow_lazy_compile(bool v) { allow_lazy_compile_ = v; return this; }
    public readonly bool post_parallel_compile_tasks_for_eager_toplevel() => post_parallel_compile_tasks_for_eager_toplevel_;
    public UnoptimizedCompileFlags set_post_parallel_compile_tasks_for_eager_toplevel(bool v) { post_parallel_compile_tasks_for_eager_toplevel_ = v; return this; }
    public readonly bool post_parallel_compile_tasks_for_lazy() => post_parallel_compile_tasks_for_lazy_;
    public UnoptimizedCompileFlags set_post_parallel_compile_tasks_for_lazy(bool v) { post_parallel_compile_tasks_for_lazy_ = v; return this; }
    public readonly bool collect_source_positions() => collect_source_positions_;
    public UnoptimizedCompileFlags set_collect_source_positions(bool v) { collect_source_positions_ = v; return this; }
    public readonly bool is_repl_mode() => is_repl_mode_;
    public UnoptimizedCompileFlags set_is_repl_mode(bool v) { is_repl_mode_ = v; return this; }
    public readonly bool produce_compile_hints() => produce_compile_hints_;
    public UnoptimizedCompileFlags set_produce_compile_hints(bool v) { produce_compile_hints_ = v; return this; }
    public readonly bool compile_hints_magic_enabled() => compile_hints_magic_enabled_;
    public UnoptimizedCompileFlags set_compile_hints_magic_enabled(bool v) { compile_hints_magic_enabled_ = v; return this; }
    public readonly bool compile_hints_per_function_magic_enabled() => compile_hints_per_function_magic_enabled_;
    public UnoptimizedCompileFlags set_compile_hints_per_function_magic_enabled(bool v) { compile_hints_per_function_magic_enabled_ = v; return this; }
    public readonly bool is_hoisted_in_context() => is_hoisted_in_context_;
    public UnoptimizedCompileFlags set_is_hoisted_in_context(bool v) { is_hoisted_in_context_ = v; return this; }
    public readonly bool allow_heap_allocation() => allow_heap_allocation_;
    public UnoptimizedCompileFlags set_allow_heap_allocation(bool v) { allow_heap_allocation_ = v; return this; }

    public readonly int script_id() => _scriptId;
    public UnoptimizedCompileFlags set_script_id(int value) { _scriptId = value; return this; }

    public readonly FunctionKind function_kind() => _functionKind;
    public UnoptimizedCompileFlags set_function_kind(FunctionKind value) { _functionKind = value; return this; }

    public readonly FunctionSyntaxKind function_syntax_kind() => _functionSyntaxKind;
    public UnoptimizedCompileFlags set_function_syntax_kind(FunctionSyntaxKind value) { _functionSyntaxKind = value; return this; }

    public readonly ParsingWhileDebugging parsing_while_debugging() => _parsingWhileDebugging;
    public UnoptimizedCompileFlags set_parsing_while_debugging(ParsingWhileDebugging value) { _parsingWhileDebugging = value; return this; }
}

// The mutable state for a parse + unoptimized compile operation.
public sealed class UnoptimizedCompileState
{
    private readonly PendingCompilationErrorHandler _pendingErrorHandler = new();

    public PendingCompilationErrorHandler pending_error_handler() => _pendingErrorHandler;
}

// A container for ParseInfo fields that are reusable across multiple parses and
// unoptimized compiles.
public sealed class ReusableUnoptimizedCompileState
{
    private readonly AstStringConstants _astStringConstants;
    private readonly AstValueFactory _astValueFactory;

    public ReusableUnoptimizedCompileState(AstStringConstants? ast_string_constants = null)
    {
        _astStringConstants = ast_string_constants ?? new AstStringConstants();
        _astValueFactory = new AstValueFactory(_astStringConstants);
    }

    public AstValueFactory ast_value_factory() => _astValueFactory;
    public AstStringConstants ast_string_constants() => _astStringConstants;

    public void NotifySingleParseCompleted() { }
}

// A container for the inputs, configuration options, and outputs of parsing.
public sealed class ParseInfo
{
    //------------- Inputs to parsing and scope analysis -----------------------
    private readonly UnoptimizedCompileFlags _flags;
    private readonly UnoptimizedCompileState _state;
    private readonly ReusableUnoptimizedCompileState _reusableState;
    private readonly ParsingFlags _v8Flags;

    private object? _extension;
    private DeclarationScope? _scriptScope;
    private int _parametersEndPos;
    private int _maxInfoId;

    //----------- Inputs+Outputs of parsing and scope analysis -----------------
    private Utf16CharacterStream? _characterStream;
    private ConsumedPreparseData? _consumedPreparseData;
    private AstRawString? _functionName;
    private SourceRangeMap? _sourceRangeMap; // Used when block coverage is enabled.

    //----------- Output of parsing and scope analysis ------------------------
    private FunctionLiteral? _literal;
    private bool _allowEvalCache;
    private LanguageMode _languageMode;
    private bool _isBackgroundCompilation;
    private bool _isStreamingCompilation;
    private bool _hasModuleInScopeChain;

    public ParseInfo(UnoptimizedCompileFlags flags, UnoptimizedCompileState state,
                     ReusableUnoptimizedCompileState reusable_state, ParsingFlags? v8_flags = null)
    {
        _flags = flags;
        _state = state;
        _reusableState = reusable_state;
        _v8Flags = v8_flags ?? ParsingFlags.Default;
        _extension = null;
        _scriptScope = null;
        _parametersEndPos = kNoSourcePosition;
        _maxInfoId = kInvalidInfoId;
        _characterStream = null;
        _functionName = null;
        _sourceRangeMap = null;
        _literal = null;
        _allowEvalCache = false;
        _languageMode = flags.outer_language_mode();
        _isBackgroundCompilation = false;
        _isStreamingCompilation = false;
        _hasModuleInScopeChain = flags.is_module();
        if (flags.block_coverage_enabled())
        {
            AllocateSourceRangeMap();
        }
    }

    // Convenience: a ParseInfo with fresh state (V8 tests' ParseInfo setup).
    public ParseInfo(UnoptimizedCompileFlags flags, ParsingFlags? v8_flags = null)
        : this(flags, new UnoptimizedCompileState(), new ReusableUnoptimizedCompileState(), v8_flags)
    {
    }

    public UnoptimizedCompileFlags flags() => _flags;

    // The V8 flags this parse runs under (V8: the process-wide v8_flags).
    public ParsingFlags v8_flags() => _v8Flags;

    public AstStringConstants ast_string_constants() => _reusableState.ast_string_constants();

    public UnoptimizedCompileState state() => _state;

    // Getters for state.
    public PendingCompilationErrorHandler pending_error_handler() => _state.pending_error_handler();

    // Accessor methods for output flags.
    public bool allow_eval_cache() => _allowEvalCache;
    public void set_allow_eval_cache(bool value) => _allowEvalCache = value;

    public LanguageMode language_mode() => _languageMode;
    public void set_language_mode(LanguageMode value) => _languageMode = value;

    public Utf16CharacterStream? character_stream() => _characterStream;
    public void set_character_stream(Utf16CharacterStream character_stream) => _characterStream = character_stream;
    public void ResetCharacterStream() => _characterStream = null;

    public object? extension() => _extension;
    public void set_extension(object? extension) => _extension = extension;

    public void set_consumed_preparse_data(ConsumedPreparseData? data) => _consumedPreparseData = data;
    public ConsumedPreparseData? consumed_preparse_data() => _consumedPreparseData;

    public DeclarationScope? script_scope() => _scriptScope;
    public void set_script_scope(DeclarationScope script_scope) => _scriptScope = script_scope;

    public AstValueFactory ast_value_factory() => _reusableState.ast_value_factory();

    public AstRawString? function_name() => _functionName;
    public void set_function_name(AstRawString? function_name) => _functionName = function_name;

    public FunctionLiteral? literal() => _literal;
    public void set_literal(FunctionLiteral? literal) => _literal = literal;

    // Parser::HandleDebugMagicComments stores these on the Script; the parser
    // has no Script object, so ParseProgram leaves them here for the engine.
    public string? source_url_magic_comment { get; set; }
    public string? source_mapping_url_magic_comment { get; set; }

    public DeclarationScope scope() => literal()!.scope();

    public int parameters_end_pos() => _parametersEndPos;
    public void set_parameters_end_pos(int parameters_end_pos) => _parametersEndPos = parameters_end_pos;

    public bool is_wrapped_as_function() => flags().function_syntax_kind() == FunctionSyntaxKind.Wrapped;

    public int max_info_id() => _maxInfoId;
    public void set_max_info_id(int max_info_id) => _maxInfoId = max_info_id;

    public void AllocateSourceRangeMap() => set_source_range_map(new SourceRangeMap());
    public SourceRangeMap? source_range_map() => _sourceRangeMap;
    public void set_source_range_map(SourceRangeMap? source_range_map) => _sourceRangeMap = source_range_map;

    public bool is_background_compilation() => _isBackgroundCompilation;
    public void set_is_background_compilation() => _isBackgroundCompilation = true;

    public bool is_streaming_compilation() => _isStreamingCompilation;
    public void set_is_streaming_compilation() => _isStreamingCompilation = true;

    public bool has_module_in_scope_chain() => _hasModuleInScopeChain;
    public void set_has_module_in_scope_chain() => _hasModuleInScopeChain = true;

    // Compile hints (v8::CompileHintCallback): given a function position,
    // whether the embedder wants it eagerly compiled.
    public Func<int, bool>? compile_hint_callback() => _compileHintCallback;
    public void SetCompileHintCallback(Func<int, bool>? callback) => _compileHintCallback = callback;
    private Func<int, bool>? _compileHintCallback;

    // What the engine supplies for scope deserialization and ScopeInfo creation.
    public IScopeInfoProvider? scope_info_provider() => _scopeInfoProvider;
    public void set_scope_info_provider(IScopeInfoProvider? provider) => _scopeInfoProvider = provider;
    private IScopeInfoProvider? _scopeInfoProvider;

    // RegExp::VerifySyntax for regexp literals (the engine's irregexp). Without
    // one the parser accepts every pattern.
    public IRegExpSyntaxValidator? regexp_syntax_validator() => _regExpSyntaxValidator;
    public void set_regexp_syntax_validator(IRegExpSyntaxValidator? validator) => _regExpSyntaxValidator = validator;
    private IRegExpSyntaxValidator? _regExpSyntaxValidator;
}
