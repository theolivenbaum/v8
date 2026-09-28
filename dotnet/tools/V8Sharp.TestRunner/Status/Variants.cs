// Port of tools/testrunner/local/variants.py.
namespace V8Sharp.TestRunner.Status;

/// <summary>The testing variants: each runs every test again with extra
/// flags. The runner currently runs only <c>default</c>, but status files
/// are evaluated per variant, so every variant name has to be known.</summary>
public static class Variants
{
    public const string Default = "default";

    /// <summary>ALL_VARIANT_FLAGS.</summary>
    public static readonly IReadOnlyDictionary<string, string[]> AllVariantFlags = new Dictionary<string, string[]>
    {
        ["assert_types"] = ["--assert-types", "--maglev-assert-types"],
        ["wasm_assert_types"] = ["--wasm-assert-types", "--no-liftoff"],
        ["verify_turboshaft"] = ["--verify-turboshaft"],
        ["code_serializer"] = ["--cache=code"],
        ["default"] = [],
        ["dumpling_test"] = ["--no-sparkplug", "--maglev-dumping", "--turbofan-dumping", "--predictable", "--dump-out-filename=/dev/null"],
        ["dumpling_reference"] = ["--no-maglev", "--no-turbofan", "--sparkplug-dumping", "--interpreter-dumping", "--predictable", "--dump-out-filename=/dev/null"],
        ["future"] = ["--future"],
        ["gc_stats"] = ["--gc-stats=1"],
        ["infra_staging"] = [],
        ["interpreted_regexp"] = ["--regexp-interpret-all"],
        ["stress_regexp_jit"] = ["--regexp-tier-up-ticks=0"],
        ["experimental_regexp"] = ["--default-to-experimental-regexp-engine"],
        ["regexp_assemble_from_bc"] = ["--regexp-assemble-from-bytecode"],
        ["jitless"] = ["--jitless", "--wasm-jitless-if-available-for-testing"],
        ["jit_fuzzing"] = ["--fuzzing", "--jit-fuzzing", "--no-fail"],
        ["jit_fuzzing_maglev"] = ["--fuzzing", "--jit-fuzzing", "--optimize-on-next-call-optimizes-to-maglev", "--no-fail"],
        ["sparkplug"] = ["--sparkplug"],
        ["maglev"] = ["--maglev"],
        ["maglev_future"] = ["--maglev", "--maglev-future"],
        ["maglev_no_turbofan"] = ["--maglev", "--no-turbofan", "--optimize-on-next-call-optimizes-to-maglev"],
        ["maglev_no_turbofan_regexp_from_bc"] = ["--maglev", "--no-turbofan", "--optimize-on-next-call-optimizes-to-maglev", "--regexp-assemble-from-bytecode"],
        ["stress_maglev"] = ["--maglev", "--stress-maglev", "--optimize-on-next-call-optimizes-to-maglev"],
        ["stress_maglev_tracing"] = ["--maglev", "--stress-maglev", "--optimize-on-next-call-optimizes-to-maglev", "--trace-maglev-graph-building"],
        ["stress_maglev_future"] = ["--maglev", "--maglev-future", "--stress-maglev", "--optimize-on-next-call-optimizes-to-maglev"],
        ["stress_maglev_no_turbofan"] = ["--maglev", "--no-turbofan", "--stress-maglev", "--optimize-on-next-call-optimizes-to-maglev"],
        ["stress_maglev_non_eager_inlining"] = ["--maglev", "--stress-maglev", "--maglev-non-eager-inlining", "--max-maglev-inlined-bytecode-size-small=0", "--optimize-on-next-call-optimizes-to-maglev"],
        ["conservative_stack_scanning"] = ["--conservative-stack-scanning", "--scavenger-conservative-object-pinning", "--stress-scavenger-conservative-object-pinning"],
        ["precise_pinning"] = ["--precise-object-pinning", "--scavenger-precise-object-pinning"],
        ["turbolev"] = ["--turbolev"],
        ["turbolev_future"] = ["--turbolev-future"],
        ["stress_turbolev_future"] = ["--turbolev-future", "--max-turbolev-eager-inlined-bytecode-size=0"],
        ["concurrent_sparkplug"] = ["--concurrent-sparkplug", "--sparkplug"],
        ["always_sparkplug"] = ["--always-sparkplug", "--sparkplug"],
        ["always_sparkplug_and_stress_regexp_jit"] = ["--always-sparkplug", "--sparkplug", "--regexp-tier-up-ticks=0"],
        ["minor_ms"] = ["--minor-ms"],
        ["no_lfa"] = ["--no-lazy-feedback-allocation"],
        ["no_memory_protection_keys"] = ["--no-memory-protection-keys"],
        ["nooptimization"] = ["--disable-optimizing-compilers", "--no-wasm-lazy-compilation"],
        ["rehash_snapshot"] = ["--rehash-snapshot"],
        ["slow_path"] = ["--force-slow-path"],
        ["stress"] = ["--no-liftoff", "--stress-lazy-source-positions", "--no-wasm-generic-wrapper", "--no-wasm-lazy-compilation"],
        ["stress_concurrent_allocation"] = ["--stress-concurrent-allocation"],
        ["stress_concurrent_inlining"] = ["--stress-concurrent-inlining"],
        ["stress_js_bg_compile_wasm_code_gc"] = ["--stress-background-compile", "--stress-wasm-code-gc"],
        ["stress_maglev_tests_with_turbofan"] = ["--turbofan", "--optimize-maglev-optimizes-to-turbofan"],
        ["stress_wasm_stack_switching"] = ["--stress-wasm-stack-switching"],
        ["stress_incremental_marking"] = ["--stress-incremental-marking"],
        ["stress_snapshot"] = ["--stress-snapshot"],
        ["scavenger_chaos_mode"] = ["--scavenger-chaos-mode"],
        ["stress_sampling"] = ["--stress-sampling-allocation-profiler=16384"],
        ["no_wasm_traps"] = ["--no-wasm-trap-handler"],
        ["instruction_scheduling"] = ["--turbo-instruction-scheduling", "--no-liftoff"],
        ["stress_instruction_scheduling"] = ["--turbo-stress-instruction-scheduling", "--no-liftoff"],
        ["turbofan_random_rescheduling"] = ["--no-liftoff", "--wasm-random-rescheduling"],
        ["validate_generated_code"] = ["--validate-generated-code"],
        ["google3"] = [],
    };

    /// <summary>ALL_VARIANTS: every variant name, in the order statusfile.py iterates them.</summary>
    public static readonly IReadOnlyList<string> AllVariants = [.. AllVariantFlags.Keys];

    static readonly string[] s_incompatibleForNoTurbofan =
        ["--turbofan", "--liftoff", "--maglev", "--turbolev", "--turbolev-future", "--stress-concurrent-inlining", "--turboshaft"];

    static readonly string[] s_jitlessIncompatible =
    [
        .. s_incompatibleForNoTurbofan,
        "--track-field-types", "--sparkplug", "--concurrent-sparkplug", "--always-sparkplug", "--regexp-tier-up",
        "--no-regexp-interpret-all", "--interpreted-frames-native-stack", "--additive-safe-int-feedback", "--script-context-cells",
        // negate_flag() of the variant's own flags:
        "--no-jitless", "--no-wasm-jitless-if-available-for-testing",
    ];

    /// <summary>INCOMPATIBLE_FLAGS_PER_VARIANT, for the variants the runner can run
    /// (only <c>default</c>, which has none). Extend when more variants are enabled.</summary>
    public static readonly IReadOnlyDictionary<string, string[]> IncompatibleFlagsPerVariant = new Dictionary<string, string[]>
    {
        ["jitless"] = s_jitlessIncompatible,
    };

    /// <summary>INCOMPATIBLE_FLAGS_PER_BUILD_VARIABLE. A leading '!' applies the
    /// rule when the build variable is false.</summary>
    public static readonly IReadOnlyList<KeyValuePair<string, string[]>> IncompatibleFlagsPerBuildVariable =
    [
        new("!code_comments", ["--code-comments"]),
        new("!DEBUG_defined",
        [
            "--check_handle_count", "--code_stats", "--dump_wasm_module", "--enable_testing_opcode_in_wasm", "--no-wasm-opt",
            "--print_ast", "--print_break_location", "--print_global_handles", "--print_handles", "--print_scopes",
            "--regexp_possessive_quantifier", "--trace_backing_store", "--trace_contexts", "--trace_isolates", "--trace_lazy",
            "--trace_liftoff", "--trace_module_status", "--trace_normalization", "--trace_turbo_escape", "--trace_wasm_compiler",
            "--trace_wasm_decoder", "--trace_wasm_instances", "--trace_wasm_lazy_compilation", "--trace_wasm_native_heap",
            "--trace_wasm_serialization", "--trace_wasm_stack_switching", "--trace_wasm_streaming",
        ]),
        new("!verify_heap", ["--verify-heap"]),
        new("!debug_code", ["--debug-code"]),
        new("!disassembler", ["--print_all_code", "--print_code", "--print_opt_code", "--print_code_verbose", "--print_builtin_code", "--print_regexp_code"]),
        new("single_generation", ["--shared-strings", "--shared-heap", "--harmony-struct", "--wasm-shared"]),
        new("!slow_dchecks", ["--enable-slow-asserts"]),
        new("!gdbjit", ["--gdbjit", "--gdbjit_full", "--gdbjit_dump"]),
        new("!has_maglev", ["--maglev"]),
        new("!has_turbofan", s_incompatibleForNoTurbofan),
        new("has_jitless", s_jitlessIncompatible),
        new("lite_mode", s_jitlessIncompatible),
        new("verify_predictable", ["--parallel-compile-tasks-for-eager-toplevel", "--parallel-compile-tasks-for-lazy", "--concurrent-recompilation", "--stress-concurrent-allocation", "--stress-concurrent-inlining"]),
        new("dict_property_const_tracking", ["--stress-concurrent-inlining"]),
    ];

    /// <summary>negate_flag: <c>--foo</c> to <c>--no-foo</c> and back; null for flags with values.</summary>
    public static string? NegateFlag(string flag)
    {
        if (flag.Contains('=')) return null;
        if (flag.StartsWith("--no-", StringComparison.Ordinal)) return "--" + flag["--no-".Length..];
        if (flag.StartsWith("--no", StringComparison.Ordinal)) return "--" + flag["--no".Length..];
        return "--no-" + flag["--".Length..];
    }
}
