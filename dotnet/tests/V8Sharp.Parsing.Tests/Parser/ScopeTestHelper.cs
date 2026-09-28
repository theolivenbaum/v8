// Port of test/unittests/parser/scope-test-helper.h.

using V8Sharp.Ast;
using V8Sharp.Common;

namespace V8Sharp.Parsing.Tests.Parser;

public static class ScopeTestHelper
{
    public static bool MustAllocateInContext(Variable var) => var.scope()!.MustAllocateInContext(var);

    private static List<Variable> Locals(Scope scope)
    {
        var result = new List<Variable>();
        foreach (Variable v in scope.locals()) result.Add(v);
        return result;
    }

    public static void CompareScopes(Scope baseline, Scope scope, bool precise_maybe_assigned)
    {
        Assert.Equal(baseline.scope_type(), scope.scope_type());
        if (baseline.is_declaration_scope())
        {
            Assert.Equal(baseline.AsDeclarationScope().function_kind(), scope.AsDeclarationScope().function_kind());
        }

        if (!PreparseDataBuilder.ScopeNeedsData(baseline)) return;

        if (scope.is_declaration_scope() && scope.AsDeclarationScope().is_skipped_function())
        {
            return;
        }

        if (baseline.is_function_scope())
        {
            Variable? function = baseline.AsDeclarationScope().function_var();
            if (function != null)
            {
                CompareVariables(function, scope.AsDeclarationScope().function_var()!, precise_maybe_assigned);
            }
            else
            {
                Assert.Null(scope.AsDeclarationScope().function_var());
            }
        }

        List<Variable> baseline_locals = Locals(baseline);
        List<Variable> scope_locals = Locals(scope);
        for (int i = 0; i < baseline_locals.Count; i++)
        {
            Variable scope_local = scope_locals[i];
            if (scope_local.mode() == VariableMode.Var || scope_local.mode() == VariableMode.Let ||
                scope_local.mode() == VariableMode.Const)
            {
                CompareVariables(baseline_locals[i], scope_local, precise_maybe_assigned);
            }
        }

        for (Scope? baseline_inner = baseline.inner_scope(), scope_inner = scope.inner_scope();
             scope_inner != null;
             scope_inner = scope_inner.sibling(), baseline_inner = baseline_inner!.sibling())
        {
            CompareScopes(baseline_inner!, scope_inner, precise_maybe_assigned);
        }
    }

    public static void CompareVariables(Variable baseline_local, Variable scope_local, bool precise_maybe_assigned)
    {
        // Sanity check the variable name. If this fails, the variable order
        // is not deterministic.
        Assert.Equal(baseline_local.raw_name().ToString(), scope_local.raw_name().ToString());

        Assert.Equal(baseline_local.location(), scope_local.location());
        if (precise_maybe_assigned)
        {
            Assert.Equal(baseline_local.maybe_assigned(), scope_local.maybe_assigned());
        }
        else
        {
            Assert.True(scope_local.maybe_assigned() >= baseline_local.maybe_assigned());
        }
    }

    // Finds a scope given a start point and directions to it (which inner
    // scope to pick).
    public static Scope FindScope(Scope scope, IReadOnlyList<int> location)
    {
        foreach (int location_n in location)
        {
            int n = location_n;
            scope = scope.inner_scope()!;
            Assert.NotNull(scope);
            while (n-- > 0)
            {
                scope = scope.sibling()!;
                Assert.NotNull(scope);
            }
        }
        return scope;
    }

    public static void MarkInnerFunctionsAsSkipped(Scope scope)
    {
        for (Scope? inner = scope.inner_scope(); inner != null; inner = inner.sibling())
        {
            if (inner.is_function_scope() && !inner.AsDeclarationScope().is_arrow_scope())
            {
                inner.AsDeclarationScope().set_is_skipped_function(true);
            }
            MarkInnerFunctionsAsSkipped(inner);
        }
    }

    public static bool HasSkippedFunctionInside(Scope scope)
    {
        if (scope.is_function_scope() && scope.AsDeclarationScope().is_skipped_function())
        {
            return true;
        }
        for (Scope? inner = scope.inner_scope(); inner != null; inner = inner.sibling())
        {
            if (HasSkippedFunctionInside(inner)) return true;
        }
        return false;
    }
}
