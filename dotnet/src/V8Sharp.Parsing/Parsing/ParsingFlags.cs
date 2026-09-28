// Copyright 2024 the V8 project authors. All rights reserved.
// Use of this source code is governed by a BSD-style license that can be
// found in the LICENSE file.

// The subset of V8's flags (src/flags/flag-definitions.h and
// src/flags/feature-flags.h) that src/parsing and src/ast read through
// v8_flags. Defaults are this revision's. V8 reads them from the process-wide
// v8_flags; here they travel with the ParseInfo (and the UnoptimizedCompileFlags
// built from them), so parses with different flags can run side by side.

namespace V8Sharp.Parsing;

public sealed class ParsingFlags
{
    public static ParsingFlags Default { get; } = new();

    // DEFINE_BOOL(allow_natives_syntax, false, "allow natives syntax")
    public bool allow_natives_syntax { get; init; }
    // DEFINE_BOOL(lazy, true, "use lazy compilation")
    public bool lazy { get; init; } = true;
    // DEFINE_BOOL(max_lazy, false, "ignore eager compilation hints")
    public bool max_lazy { get; init; }
    // DEFINE_BOOL(enable_lazy_source_positions, V8_LAZY_SOURCE_POSITIONS_BOOL, ...)
    // (v8_enable_lazy_source_positions is on by default in GN.)
    public bool enable_lazy_source_positions { get; init; } = true;
    public bool stress_lazy_source_positions { get; init; }
    public bool parallel_compile_tasks_for_eager_toplevel { get; init; }
    public bool parallel_compile_tasks_for_lazy { get; init; }
    public bool fuzzing { get; init; }
    public bool log_function_events { get; init; }
    public bool print_scopes { get; init; }
    // DEFINE_BOOL(script_context_cells, true, ...)
    public bool script_context_cells { get; init; } = true;
    // DEFINE_BOOL(function_context_cells, true, ...)
    public bool function_context_cells { get; init; } = true;
    // DEFINE_INT(function_context_cells_max_size, 2, ...)
    public int function_context_cells_max_size { get; init; } = 2;
    // DEFINE_BOOL(ignition_elide_redundant_tdz_checks, true, ...)
    public bool ignition_elide_redundant_tdz_checks { get; init; } = true;
    // DEFINE_BOOL(enable_experimental_regexp_engine, false, ...): the 'l' flag.
    public bool enable_experimental_regexp_engine { get; init; }
    // DEFINE_BOOL(use_strict, false, "enforce strict mode")
    public bool use_strict { get; init; }

    // Feature flags (feature-flags.h): experimental/staged are off by
    // default, shipped are on.
    public bool js_decorators { get; init; }               // experimental
    public bool js_source_phase_imports { get; init; }     // experimental
    public bool js_defer_import_eval { get; init; }        // staged
    public bool harmony_import_attributes { get; init; } = true; // shipped
    public bool js_esm_ns_reexport { get; init; } = true;  // shipped
}
