// Copyright 2018 the V8 project authors. All rights reserved.
// Use of this source code is governed by a BSD-style license that can be
// found in the LICENSE file.

// Port of src/ast/source-range-ast-visitor.h and .cc.

namespace V8Sharp.Ast;

// Post-processes generated source ranges while the AST structure still exists.
//
// In particular, SourceRangeAstVisitor
//
// 1. deduplicates continuation source ranges, only keeping the outermost one.
// See also: https://crbug.com/v8/8539.
//
// 2. removes the source range associated with the final statement in a block
// or function body if the parent itself has a source range associated with it.
// See also: https://crbug.com/v8/8381.
public sealed class SourceRangeAstVisitor(Expression root, SourceRangeMap source_range_map)
    : AstTraversalVisitor(root)
{
    private readonly SourceRangeMap _sourceRangeMap = source_range_map;
    private readonly HashSet<int> _continuationPositions = [];

    public override void VisitBlock(Block stmt)
    {
        base.VisitBlock(stmt);
        List<Statement> stmts = stmt.statements();
        AstNodeSourceRanges? enclosingSourceRanges = _sourceRangeMap.Find(stmt);
        if (enclosingSourceRanges != null)
        {
            if (!enclosingSourceRanges.HasRange(SourceRangeKind.kContinuation))
                throw new InvalidOperationException("CHECK(HasRange(kContinuation))");
            MaybeRemoveLastContinuationRange(stmts);
        }
    }

    public override void VisitSwitchStatement(SwitchStatement stmt)
    {
        base.VisitSwitchStatement(stmt);
        List<CaseClause> clauses = stmt.cases();
        foreach (CaseClause clause in clauses)
        {
            MaybeRemoveLastContinuationRange(clause.statements());
        }
    }

    public override void VisitFunctionLiteral(FunctionLiteral expr)
    {
        base.VisitFunctionLiteral(expr);
        List<Statement> stmts = expr.body();
        MaybeRemoveLastContinuationRange(stmts);
    }

    public override void VisitTryCatchStatement(TryCatchStatement stmt)
    {
        base.VisitTryCatchStatement(stmt);
        MaybeRemoveContinuationRange(stmt.try_block());
        MaybeRemoveContinuationRangeOfAsyncReturn(stmt);
    }

    public override void VisitTryFinallyStatement(TryFinallyStatement stmt)
    {
        base.VisitTryFinallyStatement(stmt);
        MaybeRemoveContinuationRange(stmt.try_block());
    }

    protected override bool VisitNode(AstNode node)
    {
        AstNodeSourceRanges? range = _sourceRangeMap.Find(node);

        if (range == null) return true;
        if (!range.HasRange(SourceRangeKind.kContinuation)) return true;

        // Called in pre-order. In case of conflicting continuation ranges, only the
        // outermost range may survive.

        SourceRange continuation = range.GetRange(SourceRangeKind.kContinuation);
        if (_continuationPositions.Contains(continuation.start))
        {
            range.RemoveContinuationRange();
        }
        else
        {
            _continuationPositions.Add(continuation.start);
        }

        return true;
    }

    private void MaybeRemoveContinuationRange(Statement last_statement)
    {
        AstNodeSourceRanges? last_range;

        if (last_statement.IsExpressionStatement() &&
            ((ExpressionStatement)last_statement).expression().IsThrow())
        {
            // For ThrowStatement, source range is tied to Throw expression not
            // ExpressionStatement.
            last_range = _sourceRangeMap.Find(((ExpressionStatement)last_statement).expression());
        }
        else
        {
            last_range = _sourceRangeMap.Find(last_statement);
        }

        if (last_range == null) return;

        if (last_range.HasRange(SourceRangeKind.kContinuation))
        {
            last_range.RemoveContinuationRange();
        }
    }

    private void MaybeRemoveLastContinuationRange(List<Statement> statements)
    {
        if (statements.Count == 0) return;
        MaybeRemoveContinuationRange(statements[^1]);
    }

    private static Statement? FindLastNonSyntheticStatement(List<Statement> statements)
    {
        for (int i = statements.Count - 1; i >= 0; --i)
        {
            Statement stmt = statements[i];
            if (stmt.IsReturnStatement() && ((ReturnStatement)stmt).is_synthetic_async_return())
            {
                continue;
            }
            return stmt;
        }
        return null;
    }

    private void MaybeRemoveContinuationRangeOfAsyncReturn(TryCatchStatement try_catch_stmt)
    {
        // Detect try-catch inserted by NewTryCatchStatementForAsyncAwait in the
        // parser (issued for async functions, including async generators), and
        // remove the continuation range of the last statement, such that the
        // range of the enclosing function body is used.
        if (try_catch_stmt.is_try_catch_for_async())
        {
            Statement? last_non_synthetic = FindLastNonSyntheticStatement(try_catch_stmt.try_block().statements());
            if (last_non_synthetic != null)
            {
                MaybeRemoveContinuationRange(last_non_synthetic);
            }
        }
    }
}
