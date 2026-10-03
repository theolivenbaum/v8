// Copyright 2017 the V8 project authors. All rights reserved.
// Use of this source code is governed by a BSD-style license that can be
// found in the LICENSE file.

// Port of src/ast/ast-source-ranges.h (block coverage source ranges).

using static V8Sharp.Common.Globals;

namespace V8Sharp.Ast;

public struct SourceRange(int start, int end)
{
    public int start = start;
    public int end = end;

    public SourceRange() : this(kNoSourcePosition, kNoSourcePosition) { }

    public readonly bool IsEmpty() => start == kNoSourcePosition;
    public static SourceRange Empty() => new();
    public static SourceRange OpenEnded(int start) => new(start, kNoSourcePosition);
    public static SourceRange ContinuationOf(SourceRange that, int end = kNoSourcePosition) =>
        that.IsEmpty() ? Empty() : new SourceRange(that.end, end);

    public const int kFunctionLiteralSourcePosition = -2;

    // Source ranges associated with a function literal do not contain real
    // source positions; instead, they are created with special marker values.
    // These are later recognized and rewritten during processing in
    // Coverage::Collect().
    public static SourceRange FunctionLiteralMarkerRange() => new(kFunctionLiteralSourcePosition, kFunctionLiteralSourcePosition);

    public override readonly string ToString() => $"[{start}, {end})";
}

public enum SourceRangeKind
{
    kBody,
    kCatch,
    kContinuation,
    kElse,
    kFinally,
    kRight,
    kThen,
}

public abstract class AstNodeSourceRanges
{
    public abstract SourceRange GetRange(SourceRangeKind kind);
    public abstract bool HasRange(SourceRangeKind kind);
    public virtual void RemoveContinuationRange() => throw new InvalidOperationException("UNREACHABLE");
}

public sealed class BinaryOperationSourceRanges(SourceRange right_range) : AstNodeSourceRanges
{
    public override SourceRange GetRange(SourceRangeKind kind) => right_range;
    public override bool HasRange(SourceRangeKind kind) => kind == SourceRangeKind.kRight;
}

public class ContinuationSourceRanges(int continuation_position) : AstNodeSourceRanges
{
    private int _continuationPosition = continuation_position;

    public override SourceRange GetRange(SourceRangeKind kind) => SourceRange.OpenEnded(_continuationPosition);
    public override bool HasRange(SourceRangeKind kind) => kind == SourceRangeKind.kContinuation;
    public override void RemoveContinuationRange() => _continuationPosition = kNoSourcePosition;
}

public sealed class BlockSourceRanges(int continuation_position) : ContinuationSourceRanges(continuation_position)
{
}

public sealed class CaseClauseSourceRanges(SourceRange body_range) : AstNodeSourceRanges
{
    public override SourceRange GetRange(SourceRangeKind kind) => body_range;
    public override bool HasRange(SourceRangeKind kind) => kind == SourceRangeKind.kBody;
}

public sealed class ConditionalChainSourceRanges : AstNodeSourceRanges
{
    private readonly List<SourceRange> _thenRanges = [];
    private readonly List<SourceRange> _elseRanges = [];

    public SourceRange GetRangeAtIndex(SourceRangeKind kind, int index) =>
        kind == SourceRangeKind.kThen ? _thenRanges[index] : _elseRanges[index];

    public void AddThenRanges(SourceRange range) => _thenRanges.Add(range);
    public void AddElseRange(SourceRange else_range) => _elseRanges.Add(else_range);
    public int RangeCount() => _thenRanges.Count;

    public override SourceRange GetRange(SourceRangeKind kind) => throw new InvalidOperationException("UNREACHABLE");
    public override bool HasRange(SourceRangeKind kind) => false;
}

public sealed class ConditionalSourceRanges(SourceRange then_range, SourceRange else_range) : AstNodeSourceRanges
{
    public override SourceRange GetRange(SourceRangeKind kind) => kind switch
    {
        SourceRangeKind.kThen => then_range,
        SourceRangeKind.kElse => else_range,
        _ => throw new InvalidOperationException("UNREACHABLE"),
    };

    public override bool HasRange(SourceRangeKind kind) => kind == SourceRangeKind.kThen || kind == SourceRangeKind.kElse;
}

public sealed class FunctionLiteralSourceRanges : AstNodeSourceRanges
{
    public override SourceRange GetRange(SourceRangeKind kind) => SourceRange.FunctionLiteralMarkerRange();
    public override bool HasRange(SourceRangeKind kind) => kind == SourceRangeKind.kBody;
}

public sealed class IfStatementSourceRanges(SourceRange then_range, SourceRange else_range) : AstNodeSourceRanges
{
    private bool _hasContinuation = true;

    public override SourceRange GetRange(SourceRangeKind kind)
    {
        switch (kind)
        {
            case SourceRangeKind.kElse:
                return else_range;
            case SourceRangeKind.kThen:
                return then_range;
            case SourceRangeKind.kContinuation:
            {
                if (!_hasContinuation) return SourceRange.Empty();
                SourceRange trailing_range = else_range.IsEmpty() ? then_range : else_range;
                return SourceRange.ContinuationOf(trailing_range);
            }
            default:
                throw new InvalidOperationException("UNREACHABLE");
        }
    }

    public override bool HasRange(SourceRangeKind kind) =>
        kind == SourceRangeKind.kThen || kind == SourceRangeKind.kElse || kind == SourceRangeKind.kContinuation;

    public override void RemoveContinuationRange() => _hasContinuation = false;
}

public sealed class IterationStatementSourceRanges(SourceRange body_range) : AstNodeSourceRanges
{
    private bool _hasContinuation = true;

    public override SourceRange GetRange(SourceRangeKind kind) => kind switch
    {
        SourceRangeKind.kBody => body_range,
        SourceRangeKind.kContinuation => _hasContinuation ? SourceRange.ContinuationOf(body_range) : SourceRange.Empty(),
        _ => throw new InvalidOperationException("UNREACHABLE"),
    };

    public override bool HasRange(SourceRangeKind kind) => kind == SourceRangeKind.kBody || kind == SourceRangeKind.kContinuation;

    public override void RemoveContinuationRange() => _hasContinuation = false;
}

public sealed class JumpStatementSourceRanges(int continuation_position) : ContinuationSourceRanges(continuation_position)
{
}

public sealed class NaryOperationSourceRanges : AstNodeSourceRanges
{
    private readonly List<SourceRange> _ranges = [];

    public NaryOperationSourceRanges(SourceRange range) => AddRange(range);

    public SourceRange GetRangeAtIndex(int index) => _ranges[index];
    public void AddRange(SourceRange range) => _ranges.Add(range);
    public int RangeCount() => _ranges.Count;

    public override SourceRange GetRange(SourceRangeKind kind) => throw new InvalidOperationException("UNREACHABLE");
    public override bool HasRange(SourceRangeKind kind) => false;
}

public sealed class ExpressionSourceRanges(SourceRange right_range) : AstNodeSourceRanges
{
    public override SourceRange GetRange(SourceRangeKind kind) => right_range;
    public override bool HasRange(SourceRangeKind kind) => kind == SourceRangeKind.kRight;
}

public sealed class SuspendSourceRanges(int continuation_position) : ContinuationSourceRanges(continuation_position)
{
}

public sealed class SwitchStatementSourceRanges(int continuation_position) : ContinuationSourceRanges(continuation_position)
{
}

public sealed class ThrowSourceRanges(int continuation_position) : ContinuationSourceRanges(continuation_position)
{
}

public sealed class TryCatchStatementSourceRanges(SourceRange catch_range) : AstNodeSourceRanges
{
    private bool _hasContinuation = true;

    public override SourceRange GetRange(SourceRangeKind kind) => kind switch
    {
        SourceRangeKind.kCatch => catch_range,
        SourceRangeKind.kContinuation => _hasContinuation ? SourceRange.ContinuationOf(catch_range) : SourceRange.Empty(),
        _ => throw new InvalidOperationException("UNREACHABLE"),
    };

    public override bool HasRange(SourceRangeKind kind) => kind == SourceRangeKind.kCatch || kind == SourceRangeKind.kContinuation;

    public override void RemoveContinuationRange() => _hasContinuation = false;
}

public sealed class TryFinallyStatementSourceRanges(SourceRange finally_range) : AstNodeSourceRanges
{
    private bool _hasContinuation = true;

    public override SourceRange GetRange(SourceRangeKind kind) => kind switch
    {
        SourceRangeKind.kFinally => finally_range,
        SourceRangeKind.kContinuation => _hasContinuation ? SourceRange.ContinuationOf(finally_range) : SourceRange.Empty(),
        _ => throw new InvalidOperationException("UNREACHABLE"),
    };

    public override bool HasRange(SourceRangeKind kind) => kind == SourceRangeKind.kFinally || kind == SourceRangeKind.kContinuation;

    public override void RemoveContinuationRange() => _hasContinuation = false;
}

public sealed class SourceRangeMap
{
    private readonly Dictionary<object, AstNodeSourceRanges> _map = new(ReferenceEqualityComparer.Instance);

    public AstNodeSourceRanges? Find(object node) => _map.GetValueOrDefault(node);

    // map_.emplace: an existing entry is kept.
    public void Insert(BinaryOperation node, BinaryOperationSourceRanges ranges) => _map.TryAdd(node, ranges);
    public void Insert(Block node, BlockSourceRanges ranges) => _map.TryAdd(node, ranges);
    public void Insert(CaseClause node, CaseClauseSourceRanges ranges) => _map.TryAdd(node, ranges);
    public void Insert(ConditionalChain node, ConditionalChainSourceRanges ranges) => _map.TryAdd(node, ranges);
    public void Insert(Conditional node, ConditionalSourceRanges ranges) => _map.TryAdd(node, ranges);
    public void Insert(Expression node, ExpressionSourceRanges ranges) => _map.TryAdd(node, ranges);
    public void Insert(FunctionLiteral node, FunctionLiteralSourceRanges ranges) => _map.TryAdd(node, ranges);
    public void Insert(IfStatement node, IfStatementSourceRanges ranges) => _map.TryAdd(node, ranges);
    public void Insert(IterationStatement node, IterationStatementSourceRanges ranges) => _map.TryAdd(node, ranges);
    public void Insert(JumpStatement node, JumpStatementSourceRanges ranges) => _map.TryAdd(node, ranges);
    public void Insert(NaryOperation node, NaryOperationSourceRanges ranges) => _map.TryAdd(node, ranges);
    public void Insert(Suspend node, SuspendSourceRanges ranges) => _map.TryAdd(node, ranges);
    public void Insert(SwitchStatement node, SwitchStatementSourceRanges ranges) => _map.TryAdd(node, ranges);
    public void Insert(Throw node, ThrowSourceRanges ranges) => _map.TryAdd(node, ranges);
    public void Insert(TryCatchStatement node, TryCatchStatementSourceRanges ranges) => _map.TryAdd(node, ranges);
    public void Insert(TryFinallyStatement node, TryFinallyStatementSourceRanges ranges) => _map.TryAdd(node, ranges);

    public int Count => _map.Count;
}
