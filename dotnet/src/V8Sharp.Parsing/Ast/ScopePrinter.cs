// Copyright 2012 the V8 project authors. All rights reserved.
// Use of this source code is governed by a BSD-style license that can be
// found in the LICENSE file.

// Port of the DEBUG printing in src/ast/scopes.cc: Scope::Print,
// DeclarationScope::PrintParameters and their helpers (Header, Indent,
// PrintName, PrintLocation, PrintVar, PrintMap).
//
// V8 prints object addresses with %p. There are no stable addresses here, so
// the printer writes a per-object identity number instead (0x followed by
// RuntimeHelpers.GetHashCode in hex); like V8's addresses it only identifies
// the object within one run. Output goes to a StringBuilder instead of stdout.

using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using V8Sharp.Common;
using static V8Sharp.Common.Globals;

namespace V8Sharp.Ast;

public static class ScopePrinter
{
    private static string Header(ScopeType scope_type, FunctionKind function_kind, bool is_declaration_scope)
    {
        switch (scope_type)
        {
            case ScopeType.EVAL_SCOPE: return "eval";
            case ScopeType.FUNCTION_SCOPE:
                if (IsGeneratorFunction(function_kind)) return "function*";
                if (IsAsyncFunction(function_kind)) return "async function";
                if (IsArrowFunction(function_kind)) return "arrow";
                return "function";
            case ScopeType.MODULE_SCOPE: return "module";
            case ScopeType.REPL_MODE_SCOPE: return "repl";
            case ScopeType.SCRIPT_SCOPE: return "global";
            case ScopeType.CATCH_SCOPE: return "catch";
            case ScopeType.BLOCK_SCOPE: return is_declaration_scope ? "varblock" : "block";
            case ScopeType.CLASS_SCOPE: return "class";
            case ScopeType.WITH_SCOPE: return "with";
            case ScopeType.SHADOW_REALM_SCOPE: return "shadowrealm";
        }
        throw new InvalidOperationException("UNREACHABLE");
    }

    private static void Indent(StringBuilder sb, int n, string str) => sb.Append(' ', n).Append(str);

    private static void PrintName(StringBuilder sb, AstRawString name) => sb.Append(name.Value);

    private static void PrintAddress(StringBuilder sb, object o) =>
        sb.Append("0x").Append(RuntimeHelpers.GetHashCode(o).ToString("x", CultureInfo.InvariantCulture));

    private static void PrintLocation(StringBuilder sb, Variable var)
    {
        switch (var.location())
        {
            case VariableLocation.UNALLOCATED:
                break;
            case VariableLocation.PARAMETER:
                sb.Append("parameter[").Append(var.index()).Append(']');
                break;
            case VariableLocation.LOCAL:
                sb.Append("local[").Append(var.index()).Append(']');
                break;
            case VariableLocation.CONTEXT:
                sb.Append("context[").Append(var.index()).Append(']');
                break;
            case VariableLocation.LOOKUP:
                sb.Append("lookup");
                break;
            case VariableLocation.MODULE:
                sb.Append("module");
                break;
            case VariableLocation.REPL_GLOBAL:
                sb.Append("repl global[").Append(var.index()).Append(']');
                break;
        }
    }

    private static void PrintVar(StringBuilder sb, int indent, Variable var)
    {
        Indent(sb, indent, VariableMode2String(var.mode()));
        sb.Append(' ');
        if (var.raw_name().IsEmpty())
        {
            sb.Append('.');
            PrintAddress(sb, var);
        }
        else
        {
            PrintName(sb, var.raw_name());
        }
        sb.Append(";  // (");
        PrintAddress(sb, var);
        sb.Append(") ");
        PrintLocation(sb, var);
        bool comma = !var.IsUnallocated();
        if (var.has_forced_context_allocation())
        {
            if (comma) sb.Append(", ");
            sb.Append("forced context allocation");
            comma = true;
        }
        if (var.maybe_assigned() == MaybeAssignedFlag.kNotAssigned)
        {
            if (comma) sb.Append(", ");
            sb.Append("never assigned");
            comma = true;
        }
        if (!var.is_used())
        {
            if (comma) sb.Append(", ");
            sb.Append("never used");
            comma = true;
        }
        if (var.initialization_flag() == InitializationFlag.kNeedsInitialization && !var.binding_needs_init())
        {
            if (comma) sb.Append(", ");
            sb.Append("hole initialization elided");
            comma = true;
        }
        if (var.initializer_position() != kNoSourcePosition)
        {
            if (comma) sb.Append(", ");
            sb.Append("init: ").Append(var.initializer_position());
        }
        sb.Append('\n');
    }

    private static void PrintMap(StringBuilder sb, int indent, string label, VariableMap map, bool locals,
                                 Variable? function_var)
    {
        bool printed_label = false;
        foreach (var p in map)
        {
            Variable var = p.Value;
            if (var == function_var) continue;
            bool local = !IsDynamicVariableMode(var.mode());
            if ((locals ? local : !local) && (var.is_used() || !var.IsUnallocated()))
            {
                if (!printed_label)
                {
                    Indent(sb, indent, label);
                    printed_label = true;
                }
                PrintVar(sb, indent, var);
            }
        }
    }

    // DeclarationScope::PrintParameters.
    private static void PrintParameters(StringBuilder sb, DeclarationScope scope)
    {
        sb.Append(" (");
        List<Variable> @params = scope.params_for_printing();
        for (int i = 0; i < @params.Count; i++)
        {
            if (i > 0) sb.Append(", ");
            AstRawString name = @params[i].raw_name();
            if (name.IsEmpty())
            {
                sb.Append('.');
                PrintAddress(sb, @params[i]);
            }
            else
            {
                PrintName(sb, name);
            }
        }
        sb.Append(')');
    }

    // Scope::Print. A negative n suppresses the inner scopes.
    public static void Print(Scope scope, StringBuilder sb, int n)
    {
        int n0 = n > 0 ? n : 0;
        int n1 = n0 + 2;  // indentation

        // Print header.
        FunctionKind function_kind = scope.is_function_scope()
                                         ? scope.AsDeclarationScope().function_kind()
                                         : FunctionKind.NormalFunction;
        Indent(sb, n0, Header(scope.scope_type(), function_kind, scope.is_declaration_scope()));
        AstRawString? scope_name = scope.scope_name();
        if (scope_name != null && !scope_name.IsEmpty())
        {
            sb.Append(' ');
            PrintName(sb, scope_name);
        }

        // Print parameters, if any.
        Variable? function = null;
        if (scope.is_function_scope())
        {
            PrintParameters(sb, scope.AsDeclarationScope());
            function = scope.AsDeclarationScope().function_var();
        }

        sb.Append(" { // (");
        PrintAddress(sb, scope);
        sb.Append(") (").Append(scope.start_position()).Append(", ").Append(scope.end_position()).Append(")\n");
        if (scope.is_hidden())
        {
            Indent(sb, n1, "// is hidden\n");
        }

        // Function name, if any (named function literals, only).
        if (function != null)
        {
            Indent(sb, n1, "// (local) function name: ");
            PrintName(sb, function.raw_name());
            sb.Append('\n');
        }

        // Scope info.
        if (is_strict(scope.language_mode()))
        {
            Indent(sb, n1, "// strict mode scope\n");
        }
        if (scope.is_declaration_scope() && scope.AsDeclarationScope().sloppy_eval_can_extend_vars())
        {
            Indent(sb, n1, "// scope calls sloppy 'eval'\n");
        }
        if (scope.private_name_lookup_skips_outer_class())
        {
            Indent(sb, n1, "// scope skips outer class for #-names\n");
        }
        if (scope.inner_scope_calls_eval()) Indent(sb, n1, "// inner scope calls 'eval'\n");
        if (scope.is_hoisted_in_context()) Indent(sb, n1, "// is hoisted in context\n");
        if (scope.is_declaration_scope())
        {
            DeclarationScope decl = scope.AsDeclarationScope();
            if (decl.was_lazily_parsed()) Indent(sb, n1, "// lazily parsed\n");
            if (decl.ShouldEagerCompile()) Indent(sb, n1, "// will be compiled\n");
            if (decl.needs_private_name_context_chain_recalc())
            {
                Indent(sb, n1, "// needs #-name context chain recalc\n");
            }
            Indent(sb, n1, "// ");
            sb.Append(FunctionKind2String(decl.function_kind())).Append('\n');
            if (decl.class_scope_has_private_brand())
            {
                Indent(sb, n1, "// class scope has private brand\n");
            }
        }
        if (scope.num_stack_slots() > 0)
        {
            Indent(sb, n1, "// ");
            sb.Append(scope.num_stack_slots()).Append(" stack slots\n");
        }
        if (scope.num_heap_slots() > 0)
        {
            Indent(sb, n1, "// ");
            sb.Append(scope.num_heap_slots()).Append(" heap slots\n");
        }

        // Print locals.
        if (function != null)
        {
            Indent(sb, n1, "// function var:\n");
            PrintVar(sb, n1, function);
        }

        // Print temporaries.
        {
            bool printed_header = false;
            foreach (Variable local in scope.locals())
            {
                if (local.mode() != VariableMode.Temporary) continue;
                if (!printed_header)
                {
                    printed_header = true;
                    Indent(sb, n1, "// temporary vars:\n");
                }
                PrintVar(sb, n1, local);
            }
        }

        if (scope.variables_.occupancy() > 0)
        {
            PrintMap(sb, n1, "// local vars:\n", scope.variables_, true, function);
            PrintMap(sb, n1, "// dynamic vars:\n", scope.variables_, false, function);
        }

        if (scope.is_class_scope())
        {
            ClassScope class_scope = scope.AsClassScope();
            VariableMap? private_name_map = class_scope.private_name_map_for_printing();
            if (private_name_map != null)
            {
                PrintMap(sb, n1, "// private name vars:\n", private_name_map, true, function);
                Variable? brand = class_scope.brand();
                if (brand != null)
                {
                    Indent(sb, n1, "// brand var:\n");
                    PrintVar(sb, n1, brand);
                }
            }
            Variable? class_variable = class_scope.class_variable();
            if (class_variable != null)
            {
                Indent(sb, n1, "// class var");
                sb.Append(class_variable.is_used() ? ", used" : ", unused")
                  .Append(class_scope.should_save_class_variable() ? ", saved" : ", not saved")
                  .Append(":\n");
                PrintVar(sb, n1, class_variable);
            }
        }

        // Print inner scopes (disable by providing negative n).
        if (n >= 0)
        {
            for (Scope? inner = scope.inner_scope(); inner != null; inner = inner.sibling())
            {
                sb.Append('\n');
                Print(inner, sb, n1);
            }
        }

        Indent(sb, n0, "}\n");
    }
}
