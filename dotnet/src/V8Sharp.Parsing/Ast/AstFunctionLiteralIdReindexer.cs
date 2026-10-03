// Copyright 2017 the V8 project authors. All rights reserved.
// Use of this source code is governed by a BSD-style license that can be
// found in the LICENSE file.

// Port of src/ast/ast-function-literal-id-reindexer.h and .cc.
//
// The DEBUG-only visited-set verification is not ported.

using System.Runtime.CompilerServices;

namespace V8Sharp.Ast;

// Counts the scopes during which reindexing may happen (the parser's
// max_drift_). V8 holds an int*; the counter is a StrongBox here.
public readonly struct AllowReindexScope : IDisposable
{
    private readonly StrongBox<int>? _counter;

    public AllowReindexScope(StrongBox<int>? counter)
    {
        _counter = counter;
        if (_counter != null) _counter.Value++;
    }

    public void Dispose()
    {
        if (_counter != null) _counter.Value--;
    }
}

// Changes the ID of all FunctionLiterals in the given Expression by adding the
// given delta.
public sealed class AstFunctionLiteralIdReindexer(int delta) : AstTraversalVisitor
{
    private readonly int _delta = delta;

    public void Reindex(Expression pattern, in AllowReindexScope scope)
    {
        Visit(pattern);
    }

    public override void VisitFunctionLiteral(FunctionLiteral lit)
    {
        base.VisitFunctionLiteral(lit);
        lit.set_function_literal_id(lit.function_literal_id() + _delta);
    }

    public override void VisitCall(Call expr)
    {
        base.VisitCall(expr);
        if (expr.is_possibly_eval())
        {
            expr.adjust_eval_scope_info_index(_delta);
        }
    }

    public override void VisitClassLiteral(ClassLiteral expr)
    {
        // Manually visit the class literal so that we can change the property walk.
        // This should be kept in-sync with AstTraversalVisitor::VisitClassLiteral.

        if (expr.extends() != null)
        {
            Visit(expr.extends()!);
        }
        Visit(expr.constructor());
        if (expr.static_initializer() != null)
        {
            Visit(expr.static_initializer()!);
        }
        if (expr.instance_members_initializer_function() != null)
        {
            Visit(expr.instance_members_initializer_function()!);
        }
        List<ClassLiteralProperty> private_members = expr.private_members()!;
        for (int i = 0; i < private_members.Count; ++i)
        {
            ClassLiteralProperty prop = private_members[i];

            // Private fields and auto-accessors have their key and value present in
            // instance_members_initializer_function, so they will
            // already have been visited.
            if (prop.kind() == ClassLiteralProperty.Kind.FIELD ||
                prop.kind() == ClassLiteralProperty.Kind.AUTO_ACCESSOR)
            {
                // CheckVisited(prop->value()) (DEBUG only).
            }
            else
            {
                Visit(prop.value());
            }
        }
        List<ClassLiteralProperty> props = expr.public_members()!;
        for (int i = 0; i < props.Count; ++i)
        {
            ClassLiteralProperty prop = props[i];

            // Public fields and auto-accessors with computed names have their key and
            // value present in instance_members_initializer_function, so they will
            // already have been visited.
            // The value of auto-accessors is always present in
            // instance_members_initializer_function.
            if ((prop.is_computed_name() && prop.kind() == ClassLiteralProperty.Kind.FIELD) ||
                (prop.kind() == ClassLiteralProperty.Kind.AUTO_ACCESSOR))
            {
                // CheckVisited(prop->key()), CheckVisited(prop->value()) (DEBUG only).
            }
            else
            {
                if (!prop.key().IsLiteral())
                {
                    Visit(prop.key());
                }
                Visit(prop.value());
            }
        }
    }
}
