// Port of src/interpreter/control-flow-builders.h/.cc and
// block-coverage-builder.h.
//
// V8's builders do their final work in destructors; here they are
// IDisposable and the bytecode generator uses them with `using`.
// AST nodes are only needed for block coverage and are passed as `object`
// keys to an ISourceRangeMap.
// TODO(merge): the BytecodeGenerator port supplies ISourceRangeMap from the
// parser's SourceRangeMap (src/ast/ast-source-ranges.h).
using V8Sharp.Codegen;

namespace V8Sharp.Interpreter;

/// <summary>A source range (src/ast/ast-source-ranges.h SourceRange).</summary>
public readonly record struct SourceRange(int Start, int End)
{
    public const int kNoSourcePosition = InterpreterConstants.kNoSourcePosition;
    public const int kFunctionLiteralSourceRangeKey = -2;

    public static SourceRange Empty => new(kNoSourcePosition, kNoSourcePosition);
    public bool IsEmpty() => Start == kNoSourcePosition;
}

/// <summary>The AST's SourceRangeMap as the block coverage builder sees it.</summary>
public interface ISourceRangeMap
{
    /// <summary>The range of |kind| recorded for |node|, or null if the node has no ranges.</summary>
    SourceRange? GetRange(object node, SourceRangeKind kind);

    /// <summary>NaryOperationSourceRanges::GetRangeAtIndex, or null if the node has no ranges.</summary>
    SourceRange? GetNaryRangeAtIndex(object node, int index);

    /// <summary>ConditionalChainSourceRanges::GetRangeAtIndex, or null if the node has no ranges.</summary>
    SourceRange? GetConditionalChainRangeAtIndex(object node, SourceRangeKind kind, int index);
}

/// <summary>The FeedbackVectorSpec as the loop builder sees it.</summary>
// TODO(merge): implemented by the feedback-vector port's FeedbackVectorSpec.
public interface IFeedbackVectorSpec
{
    /// <summary>AddJumpLoopSlot().ToInt().</summary>
    int AddJumpLoopSlot();
}

/// <summary>Generates IncBlockCounter bytecodes and the {source range, slot}
/// mapping for block coverage.</summary>
public sealed class BlockCoverageBuilder
{
    public const int kNoCoverageArraySlot = -1;

    // Contains source range information for allocated block coverage counter
    // slots. Slot i covers range slots_[i].
    readonly List<SourceRange> _slots = [];
    readonly BytecodeArrayBuilder _builder;
    readonly ISourceRangeMap _sourceRangeMap;

    public BlockCoverageBuilder(BytecodeArrayBuilder builder, ISourceRangeMap sourceRangeMap)
    {
        _builder = builder;
        _sourceRangeMap = sourceRangeMap;
    }

    int AllocateSlot(SourceRange? range)
    {
        if (range is not { } r || r.IsEmpty()) return kNoCoverageArraySlot;
        int slot = _slots.Count;
        _slots.Add(r);
        return slot;
    }

    public int AllocateBlockCoverageSlot(object node, SourceRangeKind kind) =>
        AllocateSlot(_sourceRangeMap.GetRange(node, kind));

    public int AllocateNaryBlockCoverageSlot(object node, int index) =>
        AllocateSlot(_sourceRangeMap.GetNaryRangeAtIndex(node, index));

    public int AllocateConditionalChainBlockCoverageSlot(object node, SourceRangeKind kind, int index) =>
        AllocateSlot(_sourceRangeMap.GetConditionalChainRangeAtIndex(node, kind, index));

    public void IncrementBlockCounter(int coverageArraySlot)
    {
        if (coverageArraySlot == kNoCoverageArraySlot) return;
        _builder.IncBlockCounter(coverageArraySlot);
    }

    public void IncrementBlockCounter(object node, SourceRangeKind kind)
    {
        int slot = AllocateBlockCoverageSlot(node, kind);
        IncrementBlockCounter(slot);
    }

    public IReadOnlyList<SourceRange> Slots => _slots;
}

public abstract class ControlFlowBuilder(BytecodeArrayBuilder builder) : IDisposable
{
    protected BytecodeArrayBuilder Builder => builder;

    /// <summary>The work V8 does in the destructor.</summary>
    public virtual void Dispose() => GC.SuppressFinalize(this);
}

public abstract class BreakableControlFlowBuilder(BytecodeArrayBuilder builder,
                                                  BlockCoverageBuilder? blockCoverageBuilder, object? node)
    : ControlFlowBuilder(builder)
{
    // Unbound labels that identify jumps for break statements in the code.
    protected readonly BytecodeLabels _breakLabels = new();

    // A continuation counter (for block coverage) is needed e.g. when
    // encountering a break statement.
    protected readonly object? _node = node;
    protected readonly BlockCoverageBuilder? _blockCoverageBuilder = blockCoverageBuilder;

    public override void Dispose()
    {
        BindBreakTarget();
        Debug.Assert(_breakLabels.Empty || _breakLabels.IsBound);
        if (_blockCoverageBuilder is not null && _node is not null)
        {
            _blockCoverageBuilder.IncrementBlockCounter(_node, SourceRangeKind.Continuation);
        }
        base.Dispose();
    }

    /// <summary>Called when visiting break statements in the AST. Inserts a jump
    /// to an unbound label that is patched when BindBreakTarget is called.</summary>
    public void Break() => EmitJump(_breakLabels);

    public void BreakIfTrue(BytecodeArrayBuilder.ToBooleanMode mode) => EmitJumpIfTrue(mode, _breakLabels);

    public void BreakIfForInDone(Register index, Register cacheLength) =>
        EmitJumpIfForInDone(_breakLabels, index, cacheLength);

    public BytecodeLabels BreakLabels => _breakLabels;

    protected void EmitJump(BytecodeLabels sites) => Builder.Jump(sites.New());

    protected void EmitJumpIfTrue(BytecodeArrayBuilder.ToBooleanMode mode, BytecodeLabels sites) =>
        Builder.JumpIfTrue(mode, sites.New());

    protected void EmitJumpIfFalse(BytecodeArrayBuilder.ToBooleanMode mode, BytecodeLabels sites) =>
        Builder.JumpIfFalse(mode, sites.New());

    protected void EmitJumpIfUndefined(BytecodeLabels sites) => Builder.JumpIfUndefined(sites.New());

    protected void EmitJumpIfForInDone(BytecodeLabels sites, Register index, Register cacheLength) =>
        Builder.JumpIfForInDone(sites.New(), index, cacheLength);

    /// <summary>Called from Dispose to update sites that emit jumps for break.</summary>
    protected void BindBreakTarget() => _breakLabels.Bind(Builder);
}

/// <summary>Tracks control flow for block statements (which can break in JS).</summary>
public sealed class BlockBuilder(BytecodeArrayBuilder builder, BlockCoverageBuilder? blockCoverageBuilder,
                                 object? statement)
    : BreakableControlFlowBuilder(builder, blockCoverageBuilder, statement);

/// <summary>Co-ordinates break and continue statements with their loop.</summary>
public sealed class LoopBuilder : BreakableControlFlowBuilder
{
    readonly BytecodeLoopHeader _loopHeader = new();

    // Unbound labels that identify jumps for continue statements in the code and
    // jumps from checking the loop condition to the header for do-while loops.
    readonly BytecodeLabels _continueLabels = new();

    // Unbound labels that identify jumps for nested inner loops which share the
    // same header offset as this loop. Said inner loops will Jump to our end
    // label, which could be a JumpLoop or, iff we are a nested inner loop too, a
    // Jump to our parent's end label.
    readonly BytecodeLabels _endLabels = new();

    readonly int _blockCoverageBodySlot = BlockCoverageBuilder.kNoCoverageArraySlot;
    readonly int _sourcePosition;
    readonly IFeedbackVectorSpec _feedbackVectorSpec;

    /// <param name="nodePosition">node->position(), or kNoSourcePosition when there is no node.</param>
    public LoopBuilder(BytecodeArrayBuilder builder, BlockCoverageBuilder? blockCoverageBuilder, object? node,
                       int nodePosition, IFeedbackVectorSpec feedbackVectorSpec)
        : base(builder, blockCoverageBuilder, node)
    {
        _feedbackVectorSpec = feedbackVectorSpec;
        if (_blockCoverageBuilder is not null && node is not null)
        {
            _blockCoverageBodySlot = _blockCoverageBuilder.AllocateBlockCoverageSlot(node, SourceRangeKind.Body);
        }
        _sourcePosition = node is not null ? nodePosition : InterpreterConstants.kNoSourcePosition;
    }

    public override void Dispose()
    {
        Debug.Assert(_continueLabels.Empty || _continueLabels.IsBound);
        Debug.Assert(_endLabels.Empty || _endLabels.IsBound);
        base.Dispose();
    }

    public void LoopHeader()
    {
        // Jumps from before the loop header into the loop violate ordering
        // requirements of bytecode basic blocks. The only entry into a loop
        // must be the loop header. Surely breaks is okay? Not if nested
        // and misplaced between the headers.
        Debug.Assert(_breakLabels.Empty && _continueLabels.Empty && _endLabels.Empty);
        Builder.Bind(_loopHeader);
    }

    public void LoopBody() => _blockCoverageBuilder?.IncrementBlockCounter(_blockCoverageBodySlot);

    public void JumpToHeader(int loopDepth, LoopBuilder? parentLoop)
    {
        BindLoopEnd();
        if (parentLoop is not null && _loopHeader.Offset == parentLoop._loopHeader.Offset)
        {
            // TurboFan can't cope with multiple loops that have the same loop header
            // bytecode offset. If we have an inner loop with the same header offset
            // than its parent loop, we do not create a JumpLoop bytecode. Instead, we
            // Jump to our parent's JumpToHeader which in turn can be a JumpLoop or, iff
            // they are a nested inner loop too, a Jump to its parent's JumpToHeader.
            parentLoop.JumpToLoopEnd();
        }
        else
        {
            // Pass the proper loop depth to the backwards branch for triggering OSR.
            // For purposes of OSR, the loop depth is capped at `kMaxOsrUrgency - 1`.
            // Once that urgency is reached, all loops become OSR candidates.
            //
            // The loop must have closed form, i.e. all loop elements are within the
            // loop, the loop header precedes the body and next elements in the loop.
            int slot_index = _feedbackVectorSpec.AddJumpLoopSlot();
            Builder.JumpLoop(_loopHeader, Math.Min(loopDepth, InterpreterConstants.kMaxOsrUrgency - 1),
                             _sourcePosition, slot_index);
        }
    }

    public void BindContinueTarget() => _continueLabels.Bind(Builder);

    /// <summary>Called when visiting continue statements in the AST. Inserts a jump
    /// to an unbound label that is patched when BindContinueTarget is called.</summary>
    public void Continue() => EmitJump(_continueLabels);

    public void ContinueIfUndefined() => EmitJumpIfUndefined(_continueLabels);

    // Emit a Jump to our parent_loop_'s end label which could be a JumpLoop or,
    // iff they are a nested inner loop with the same loop header bytecode offset
    // as their parent's, a Jump to its parent's end label.
    void JumpToLoopEnd() => EmitJump(_endLabels);

    void BindLoopEnd() => _endLabels.Bind(Builder);
}

/// <summary>Co-ordinates break statements with their switch.</summary>
public sealed class SwitchBuilder : BreakableControlFlowBuilder
{
    // Unbound labels that identify jumps for case statements in the code.
    readonly BytecodeLabel[] _caseSites;
    readonly BytecodeLabels _default = new();
    readonly BytecodeLabels _fallThrough = new();
    readonly BytecodeJumpTable? _jumpTable;

    public SwitchBuilder(BytecodeArrayBuilder builder, BlockCoverageBuilder? blockCoverageBuilder, object? statement,
                         int numberOfCases, BytecodeJumpTable? jumpTable)
        : base(builder, blockCoverageBuilder, statement)
    {
        _caseSites = new BytecodeLabel[numberOfCases];
        for (int i = 0; i < numberOfCases; i++) _caseSites[i] = new BytecodeLabel();
        _jumpTable = jumpTable;
    }

    public override void Dispose()
    {
#if DEBUG
        foreach (BytecodeLabel site in _caseSites)
        {
            Debug.Assert(!site.HasReferrerJump || site.IsBound);
        }
#endif
        base.Dispose();
    }

    public void BindCaseTargetForJumpTable(int caseValue, object? clause)
    {
        Builder.Bind(_jumpTable!, caseValue);
        BuildBlockCoverage(clause);
    }

    public void BindCaseTargetForCompareJump(int index, object? clause)
    {
        Builder.Bind(_caseSites[index]);
        BuildBlockCoverage(clause);
    }

    /// <summary>Called when visiting a case comparison operation for |index|.
    /// Inserts a JumpIfTrue with ToBooleanMode |mode| to an unbound label that is
    /// patched when the corresponding SetCaseTarget is called.</summary>
    public void JumpToCaseIfTrue(BytecodeArrayBuilder.ToBooleanMode mode, int index) =>
        Builder.JumpIfTrue(mode, _caseSites[index]);

    /// <summary>Precondition: tag is in the accumulator.</summary>
    public void EmitJumpTableIfExists(int minCase, int maxCase, IReadOnlyDictionary<int, object> coveredCases)
    {
        Builder.SwitchOnSmiNoFeedback(_jumpTable!);
        _fallThrough.Bind(Builder);
        // Bind any uncovered cases.
        for (int j = minCase; ; ++j)
        {
            if (!coveredCases.ContainsKey(j)) BindCaseTargetForJumpTable(j, null);
            // Check for the exit condition here rather than the for in case
            // `max_case == INT_MAX` and we can't go above it.
            if (j >= maxCase) break;
        }
    }

    public void BindDefault(object? clause)
    {
        _default.Bind(Builder);
        BuildBlockCoverage(clause);
    }

    public void JumpToDefault() => EmitJump(_default);

    public void JumpToFallThroughIfFalse() =>
        EmitJumpIfFalse(BytecodeArrayBuilder.ToBooleanMode.AlreadyBoolean, _fallThrough);

    void BuildBlockCoverage(object? clause)
    {
        if (_blockCoverageBuilder is not null && clause is not null)
        {
            _blockCoverageBuilder.IncrementBlockCounter(clause, SourceRangeKind.Body);
        }
    }
}

/// <summary>Co-ordinates control flow in try-catch statements.</summary>
public sealed class TryCatchBuilder(BytecodeArrayBuilder builder, BlockCoverageBuilder? blockCoverageBuilder,
                                    object? statement, HandlerTable.CatchPrediction catchPrediction)
    : ControlFlowBuilder(builder)
{
    readonly int _handlerId = builder.NewHandlerEntry();
    readonly BytecodeLabel _exit = new();

    public override void Dispose()
    {
        if (blockCoverageBuilder is not null && statement is not null)
        {
            blockCoverageBuilder.IncrementBlockCounter(statement, SourceRangeKind.Continuation);
        }
        base.Dispose();
    }

    public void BeginTry(Register context) => Builder.MarkTryBegin(_handlerId, context);

    public void EndTry(bool emitCatch = true)
    {
        Builder.MarkTryEnd(_handlerId);
        if (emitCatch)
        {
            Builder.Jump(_exit);
            Builder.MarkHandler(_handlerId, catchPrediction);
            if (blockCoverageBuilder is not null && statement is not null)
            {
                blockCoverageBuilder.IncrementBlockCounter(statement, SourceRangeKind.Catch);
            }
        }
        else
        {
            Builder.DropHandlerEntry(_handlerId);
            if (blockCoverageBuilder is not null && statement is not null)
            {
                blockCoverageBuilder.AllocateBlockCoverageSlot(statement, SourceRangeKind.Catch);
            }
        }
    }

    public void EndCatch() => Builder.Bind(_exit);
}

/// <summary>Co-ordinates control flow in try-finally statements.</summary>
public sealed class TryFinallyBuilder(BytecodeArrayBuilder builder, BlockCoverageBuilder? blockCoverageBuilder,
                                      object? statement, HandlerTable.CatchPrediction catchPrediction)
    : ControlFlowBuilder(builder)
{
    readonly int _handlerId = builder.NewHandlerEntry();
    readonly BytecodeLabel _handler = new();

    // Unbound labels that identify jumps to the finally block in the code.
    readonly BytecodeLabels _finalizationSites = new();

    public override void Dispose()
    {
        if (blockCoverageBuilder is not null && statement is not null)
        {
            blockCoverageBuilder.IncrementBlockCounter(statement, SourceRangeKind.Continuation);
        }
        base.Dispose();
    }

    public void BeginTry(Register context) => Builder.MarkTryBegin(_handlerId, context);

    public void LeaveTry() => Builder.Jump(_finalizationSites.New());

    public void EndTry() => Builder.MarkTryEnd(_handlerId);

    public void BeginHandler()
    {
        Builder.Bind(_handler);
        Builder.MarkHandler(_handlerId, catchPrediction);
    }

    public void BeginFinally()
    {
        _finalizationSites.Bind(Builder);
        if (blockCoverageBuilder is not null && statement is not null)
        {
            blockCoverageBuilder.IncrementBlockCounter(statement, SourceRangeKind.Finally);
        }
    }

    public void EndFinally()
    {
        // Nothing to be done here.
    }
}

/// <summary>Control flow of a chain of conditionals (a ? b : c ? d : e).</summary>
public sealed class ConditionalChainControlFlowBuilder : ControlFlowBuilder
{
    readonly BytecodeLabels _endLabels = new();
    readonly int _thenCount;
    readonly BytecodeLabels[] _thenLabelsList;
    readonly BytecodeLabels[] _elseLabelsList;
    readonly int[] _blockCoverageThenSlots;
    readonly int[] _blockCoverageElseSlots;
    readonly BlockCoverageBuilder? _blockCoverageBuilder;

    public ConditionalChainControlFlowBuilder(BytecodeArrayBuilder builder, BlockCoverageBuilder? blockCoverageBuilder,
                                              object node, int thenCount)
        : base(builder)
    {
        _thenCount = thenCount;
        _blockCoverageBuilder = blockCoverageBuilder;
        _thenLabelsList = new BytecodeLabels[thenCount];
        _elseLabelsList = new BytecodeLabels[thenCount];
        for (int i = 0; i < thenCount; ++i)
        {
            _thenLabelsList[i] = new BytecodeLabels();
            _elseLabelsList[i] = new BytecodeLabels();
        }
        _blockCoverageThenSlots = new int[thenCount];
        _blockCoverageElseSlots = new int[thenCount];
        if (blockCoverageBuilder is not null)
        {
            for (int i = 0; i < thenCount; ++i)
            {
                _blockCoverageThenSlots[i] =
                    blockCoverageBuilder.AllocateConditionalChainBlockCoverageSlot(node, SourceRangeKind.Then, i);
                _blockCoverageElseSlots[i] =
                    blockCoverageBuilder.AllocateConditionalChainBlockCoverageSlot(node, SourceRangeKind.Else, i);
            }
        }
    }

    public override void Dispose()
    {
        _endLabels.Bind(Builder);
#if DEBUG
        Debug.Assert(_endLabels.Empty || _endLabels.IsBound);
        foreach (BytecodeLabels label in _thenLabelsList) Debug.Assert(label.Empty || label.IsBound);
        foreach (BytecodeLabels label in _elseLabelsList) Debug.Assert(label.Empty || label.IsBound);
#endif
        base.Dispose();
    }

    public BytecodeLabels ThenLabelsAt(int index)
    {
        Debug.Assert(index < _thenCount);
        return _thenLabelsList[index];
    }

    public BytecodeLabels ElseLabelsAt(int index)
    {
        Debug.Assert(index < _thenCount);
        return _elseLabelsList[index];
    }

    public int BlockCoverageThenSlotAt(int index) => _blockCoverageThenSlots[index];
    public int BlockCoverageElseSlotAt(int index) => _blockCoverageElseSlots[index];

    public void ThenAt(int index)
    {
        ThenLabelsAt(index).Bind(Builder);
        _blockCoverageBuilder?.IncrementBlockCounter(BlockCoverageThenSlotAt(index));
    }

    public void ElseAt(int index)
    {
        ElseLabelsAt(index).Bind(Builder);
        _blockCoverageBuilder?.IncrementBlockCounter(BlockCoverageElseSlotAt(index));
    }

    public void JumpToEnd() => Builder.Jump(_endLabels.New());
}

/// <summary>Control flow of an if statement or a conditional expression.</summary>
public sealed class ConditionalControlFlowBuilder : ControlFlowBuilder
{
    readonly BytecodeLabels _endLabels = new();
    readonly BytecodeLabels _thenLabels = new();
    readonly BytecodeLabels _elseLabels = new();

    readonly object _node;
    readonly bool _nodeIsIfStatement;
    readonly int _blockCoverageThenSlot;
    readonly int _blockCoverageElseSlot;
    readonly BlockCoverageBuilder? _blockCoverageBuilder;

    /// <param name="nodeIsIfStatement">node->IsIfStatement() (the node is an IfStatement or a Conditional).</param>
    public ConditionalControlFlowBuilder(BytecodeArrayBuilder builder, BlockCoverageBuilder? blockCoverageBuilder,
                                         object node, bool nodeIsIfStatement)
        : base(builder)
    {
        _node = node;
        _nodeIsIfStatement = nodeIsIfStatement;
        _blockCoverageBuilder = blockCoverageBuilder;
        if (blockCoverageBuilder is not null)
        {
            _blockCoverageThenSlot = blockCoverageBuilder.AllocateBlockCoverageSlot(node, SourceRangeKind.Then);
            _blockCoverageElseSlot = blockCoverageBuilder.AllocateBlockCoverageSlot(node, SourceRangeKind.Else);
        }
    }

    public override void Dispose()
    {
        if (!_elseLabels.IsBound) _elseLabels.Bind(Builder);
        _endLabels.Bind(Builder);

        Debug.Assert(_endLabels.Empty || _endLabels.IsBound);
        Debug.Assert(_thenLabels.Empty || _thenLabels.IsBound);
        Debug.Assert(_elseLabels.Empty || _elseLabels.IsBound);

        // IfStatement requires a continuation counter, Conditional does not (as it
        // can only contain expressions).
        if (_blockCoverageBuilder is not null && _nodeIsIfStatement)
        {
            _blockCoverageBuilder.IncrementBlockCounter(_node, SourceRangeKind.Continuation);
        }
        base.Dispose();
    }

    public BytecodeLabels ThenLabels => _thenLabels;
    public BytecodeLabels ElseLabels => _elseLabels;

    public void Then()
    {
        _thenLabels.Bind(Builder);
        _blockCoverageBuilder?.IncrementBlockCounter(_blockCoverageThenSlot);
    }

    public void Else()
    {
        _elseLabels.Bind(Builder);
        _blockCoverageBuilder?.IncrementBlockCounter(_blockCoverageElseSlot);
    }

    public void JumpToEnd()
    {
        Debug.Assert(_endLabels.Empty); // May only be called once.
        Builder.Jump(_endLabels.New());
    }
}
