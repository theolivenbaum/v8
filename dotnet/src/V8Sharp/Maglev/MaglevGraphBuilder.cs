// Port of src/maglev/maglev-graph-builder.{h,cc}: builds the SSA graph of a
// function in one pass over its bytecode, using the feedback vector to
// speculate (MaglevGraphBuilder::Build, VisitSingleBytecode, the merge point
// processing, the checkpointed deopt frames, and the conversion helpers
// GetInt32 / GetFloat64 / GetTaggedValue / GetTruncatedInt32ForToNumber).
//
// This file holds the driver; the bytecode visitors are in
// MaglevGraphBuilder.Visitors.cs, property access in .PropertyAccess.cs and
// calls/inlining in .Calls.cs.
//
// Deviations (structural): bytecodes the builder does not specialise become
// CallBuiltin nodes that call the same BaselineBuiltins method the baseline
// compiler calls for that bytecode (V8 uses Generic* nodes that call the
// interpreter's builtins with feedback, which is the same thing); register
// lists those builtins read are stored into the frame first. Exception
// handlers, generators and a few bytecodes are not supported yet: the
// compilation bails out (MaglevBailoutException) and the function stays in
// the lower tiers.
using System.Reflection;
using V8Sharp.Baseline;
using V8Sharp.Deoptimizer;
using V8Sharp.Interpreter;

namespace V8Sharp.Maglev;

public sealed partial class MaglevGraphBuilder
{
    readonly MaglevCompilationInfo _info;
    readonly MaglevCompilationUnit _unit;
    readonly Graph _graph;
    readonly BytecodeArrayIterator _it;
    readonly BytecodeAnalysis _analysis;
    readonly JSValue[] _constants;
    InterpreterFrameState _frame;
    BasicBlock? _currentBlock;
    readonly MergePointInterpreterFrameState?[] _mergeStates;
    // Loop headers with several forward predecessors merge them in a
    // pre-header first (V8 requires loops to be entered through the header).
    readonly MergePointInterpreterFrameState?[] _preheaderStates;
    readonly int[] _predecessorCount;
    readonly int[] _forwardPredecessorCount;
    readonly bool[] _isJumpTarget;
    readonly bool[] _loopChangesContext;

    // The frame at the start of the current bytecode (for eager deopts) and
    // its cached deopt frame (V8: latest_checkpointed_frame_).
    readonly ValueNode?[] _frameAtBytecodeStart;
    DeoptFrame? _latestCheckpointedFrame;

    // Inlining (caller_details_).
    readonly MaglevGraphBuilder? _caller;
    readonly DeoptFrame? _callerDeoptFrame;
    readonly ValueNode[]? _inlinedArguments;
    readonly ValueNode? _inlinedReceiver;
    readonly ValueNode? _inlinedClosure;
    readonly ValueNode? _inlinedContext;
    readonly ValueNode? _inlinedNewTarget;
    // Return values of an inlined function, merged into its continuation.
    readonly List<(BasicBlock Block, ValueNode Value, KnownNodeAspects Known)> _inlinedReturns = [];

    // Lazy deopt result location (LazyDeoptResultLocationScope).
    Register _lazyResultLocation = Register.VirtualAccumulator();
    int _lazyResultSize = 1;

    public MaglevGraphBuilder(MaglevCompilationInfo info, MaglevCompilationUnit unit)
        : this(info, unit, null, null, null, null, null, null, null)
    {
    }

    MaglevGraphBuilder(MaglevCompilationInfo info, MaglevCompilationUnit unit, MaglevGraphBuilder? caller, DeoptFrame? callerDeoptFrame,
        ValueNode? receiver, ValueNode[]? arguments, ValueNode? closure, ValueNode? context, ValueNode? newTarget)
    {
        _info = info;
        _unit = unit;
        _graph = info.Graph;
        _caller = caller;
        _callerDeoptFrame = callerDeoptFrame;
        _inlinedReceiver = receiver;
        _inlinedArguments = arguments;
        _inlinedClosure = closure;
        _inlinedContext = context;
        _inlinedNewTarget = newTarget;
        _constants = unit.ConstantPool;
        _it = new BytecodeArrayIterator(unit.Bytecode);
        _analysis = unit.BytecodeAnalysis;
        _frame = new InterpreterFrameState(unit);
        int length = unit.Bytecode.Length + 1;
        _mergeStates = new MergePointInterpreterFrameState?[length];
        _preheaderStates = new MergePointInterpreterFrameState?[length];
        _predecessorCount = new int[length];
        _forwardPredecessorCount = new int[length];
        _isJumpTarget = new bool[length];
        _loopChangesContext = new bool[length];
        _frameAtBytecodeStart = new ValueNode?[InterpreterFrameState.SlotCount(unit)];
    }

    public Graph Graph => _graph;
    public MaglevCompilationUnit Unit => _unit;
    Isolate Isolate => _info.Isolate;
    FlagList Flags => _info.Isolate.Flags;

    // ---- Build ------------------------------------------------------------------------------

    /// <summary>MaglevGraphBuilder::Build for the top-level function.</summary>
    public void Build()
    {
        CheckSupported(_unit);
        CalculatePredecessorCounts();
        StartNewBlock(null);
        BuildRegisterFrameInitialization();
        if (_info.IsOsr)
        {
            BuildOsrEntry();
        }
        else
        {
            BuildBody(0);
        }
        if (_currentBlock is not null) throw new MaglevBailoutException("fell off the end of the bytecode");
        ResolveJumpTargets();
    }

    /// <summary>The bytecodes and features the builder supports; others bail out.</summary>
    internal static string? UnsupportedReason(SharedFunctionInfo shared, BytecodeArray bytecode)
    {
        if (bytecode.HandlerTable.Length != 0) return "exception handlers";
        if (Globals.IsResumableFunction(shared.Kind)) return "resumable function";
        var it = new BytecodeArrayIterator(bytecode);
        for (; !it.Done(); it.Advance())
        {
            Bytecode bc = it.CurrentBytecode();
            switch (bc)
            {
                case Bytecode.SwitchOnGeneratorState:
                case Bytecode.SuspendGenerator:
                case Bytecode.ResumeGenerator:
                case Bytecode.CreateCatchContext:
                case Bytecode.Illegal:
                    return "unsupported bytecode " + bc;
            }
            if (Bytecodes.IsDebugBreak(bc)) return "debug break";
        }
        return null;
    }

    static void CheckSupported(MaglevCompilationUnit unit)
    {
        string? reason = UnsupportedReason(unit.SharedFunctionInfo, unit.Bytecode);
        if (reason is not null) throw new MaglevBailoutException(reason);
    }

    /// <summary>CalculatePredecessorCounts.</summary>
    void CalculatePredecessorCounts()
    {
        BytecodeArray bytecode = _unit.Bytecode;
        // The function entry is a predecessor of offset 0 (not in an OSR compilation).
        if (!_info.IsOsr || _unit.IsInline)
        {
            _predecessorCount[0]++;
            _forwardPredecessorCount[0]++;
        }
        var it = new BytecodeArrayIterator(bytecode);
        for (; !it.Done(); it.Advance())
        {
            Bytecode bc = it.CurrentBytecode();
            int next = it.NextOffset();
            if (bc == Bytecode.JumpLoop)
            {
                int header = BytecodeAnalysis.JumpTargetOffset(it, _constants);
                _predecessorCount[header]++;
            }
            else if (Bytecodes.IsJump(bc))
            {
                int target = BytecodeAnalysis.JumpTargetOffset(it, _constants);
                _predecessorCount[target]++;
                _forwardPredecessorCount[target]++;
                _isJumpTarget[target] = true;
            }
            else if (Bytecodes.IsSwitch(bc))
            {
                foreach ((int _, int target) in BytecodeAnalysis.JumpTableTargets(it, _constants))
                {
                    _predecessorCount[target]++;
                    _forwardPredecessorCount[target]++;
                    _isJumpTarget[target] = true;
                }
            }
            if (!Bytecodes.IsUnconditionalJump(bc) && !Bytecodes.Returns(bc) && !Bytecodes.UnconditionallyThrows(bc) &&
                next < bytecode.Length)
            {
                _predecessorCount[next]++;
                _forwardPredecessorCount[next]++;
            }
            if (bc is Bytecode.PushContext or Bytecode.PopContext)
            {
                foreach (LoopInfo loop in _analysis.GetLoopInfos())
                {
                    if (loop.Contains(it.CurrentOffset())) _loopChangesContext[loop.LoopStart] = true;
                }
            }
        }
        // In an OSR compilation the OSR prologue is a predecessor of the OSR loop header.
        if (_info.IsOsr && !_unit.IsInline)
        {
            int header = _analysis.OsrEntryPoint;
            if (header < 0) throw new MaglevBailoutException("OSR offset is not a loop");
            _predecessorCount[header]++;
            _forwardPredecessorCount[header]++;
        }
    }

    /// <summary>
    /// BuildRegisterFrameInitialization: parameters (receiver first) and the
    /// context from the frame (or the caller's values when inlined), every
    /// register undefined, the new.target / generator register.
    /// </summary>
    void BuildRegisterFrameInitialization()
    {
        int parameterCount = _unit.ParameterCount;
        // The closure is read from the frame: the code is installed on the
        // feedback vector, which all closures of a CreateClosure site share.
        _unit.Closure = _unit.IsInline
            ? _inlinedClosure!
            : AddNewNode(new ValueNode(Opcode.InitialValue, ValueRepresentation.kTagged)
            {
                Int0 = InterpreterRuntime.kClosureOffset,
                Type = NodeType.kJSFunction,
            });
        if (_unit.IsInline)
        {
            _frame.Set(Register.FromParameterIndex(0), _inlinedReceiver!);
            for (int i = 1; i < parameterCount; i++)
            {
                ValueNode value = i - 1 < _inlinedArguments!.Length ? _inlinedArguments[i - 1] : GetRootConstant(RootIndex.kUndefinedValue);
                _frame.Set(Register.FromParameterIndex(i), value);
            }
            _frame.Context = _inlinedContext!;
        }
        else
        {
            for (int i = 0; i < parameterCount; i++)
            {
                Register r = Register.FromParameterIndex(i);
                ValueNode value = AddNewNode(new ValueNode(Opcode.InitialValue, ValueRepresentation.kTagged) { Int0 = r.Index });
                if (i == 0) value.Type = ReceiverType();
                _frame.Set(r, value);
            }
            ValueNode context = AddNewNode(new ValueNode(Opcode.InitialValue, ValueRepresentation.kTagged)
            {
                Int0 = InterpreterRuntime.kContextOffset,
                Type = NodeType.kContext,
            });
            _frame.Context = context;
        }
        ValueNode undefined = GetRootConstant(RootIndex.kUndefinedValue);
        for (int r = 0; r < _unit.RegisterCount; r++) _frame.Set(new Register(r), undefined);
        Register incoming = _unit.Bytecode.IncomingNewTargetOrGeneratorRegister;
        if (incoming.IsValid)
        {
            ValueNode newTarget = _unit.IsInline
                ? _inlinedNewTarget ?? undefined
                : AddNewNode(new ValueNode(Opcode.InitialValue, ValueRepresentation.kTagged) { Int0 = incoming.Index });
            _frame.Set(incoming, newTarget);
        }
        _frame.Accumulator = undefined;
    }

    NodeType ReceiverType()
    {
        SharedFunctionInfo shared = _unit.SharedFunctionInfo;
        // A sloppy function's receiver is converted to an object by the call.
        if (!shared.Native && shared.LanguageMode == Common.LanguageMode.Sloppy) return NodeType.kJSReceiver;
        return NodeType.kUnknown;
    }

    /// <summary>
    /// The OSR prologue: the live registers at the OSR loop header come from
    /// the interpreter frame (V8: the OSR'd frame's values as InitialValues).
    /// </summary>
    void BuildOsrEntry()
    {
        int header = _analysis.OsrEntryPoint;
        BytecodeLivenessState liveness = _analysis.GetInLivenessFor(header);
        for (int r = 0; r < _unit.RegisterCount; r++)
        {
            if (!liveness.RegisterIsLive(r)) continue;
            _frame.Set(new Register(r), AddNewNode(new ValueNode(Opcode.InitialValue, ValueRepresentation.kTagged) { Int0 = r }));
        }
        // The context may differ from the function's at the loop (a block context).
        _frame.Context = AddNewNode(new ValueNode(Opcode.InitialValue, ValueRepresentation.kTagged)
        {
            Int0 = InterpreterRuntime.kContextOffset,
            Type = NodeType.kContext,
        });
        // JumpLoop leaves the accumulator undefined.
        _frame.Accumulator = GetRootConstant(RootIndex.kUndefinedValue);
        BuildBody(0, osrHeader: header);
    }

    /// <summary>Whether the bytecode at <paramref name="offset"/> starts a block through a merge state.</summary>
    bool NeedsMergeState(int offset) => _isJumpTarget[offset] || _predecessorCount[offset] > 1 || _analysis.IsLoopHeader(offset);

    /// <summary>VisitSingleBytecode over the bytecode from <paramref name="start"/>, processing merge points.</summary>
    void BuildBody(int start, int osrHeader = -1)
    {
        _it.SetOffset(start);
        if (osrHeader >= 0)
        {
            // Jump from the OSR prologue to the loop header.
            BasicBlock pred = _currentBlock!;
            MergeIntoFrameStateFrom(osrHeader, pred);
            FinishBlock(new ControlNode(Opcode.Jump) { Int0 = osrHeader });
        }
        for (; !_it.Done(); _it.Advance())
        {
            int offset = _it.CurrentOffset();
            if (NeedsMergeState(offset))
            {
                ProcessMergePoint(offset);
            }
            else if (_currentBlock is null && _pendingFallthrough is not null && _pendingFallthroughOffset == offset)
            {
                StartFallthroughBlock(offset);
            }
            if (_currentBlock is null)
            {
                // Dead code: no predecessor reached this bytecode.
                continue;
            }
            VisitSingleBytecode();
        }
    }

    // A conditional branch's fallthrough when the next bytecode has no merge state.
    InterpreterFrameState? _pendingFallthrough;
    BasicBlock? _pendingFallthroughPredecessor;
    int _pendingFallthroughOffset = -1;
    readonly Dictionary<int, BasicBlock> _fallthroughBlocks = [];

    void StartFallthroughBlock(int offset)
    {
        BasicBlock pred = _pendingFallthroughPredecessor!;
        _frame = _pendingFallthrough!;
        _pendingFallthrough = null;
        _pendingFallthroughPredecessor = null;
        _pendingFallthroughOffset = -1;
        BasicBlock block = StartNewBlock(pred);
        block.Offset = offset;
        _fallthroughBlocks[offset] = block;
    }

    /// <summary>ProcessMergePoint: the bytecode at <paramref name="offset"/> starts a block with merged predecessors.</summary>
    void ProcessMergePoint(int offset)
    {
        // Straight-line code falls into the merge point.
        if (_currentBlock is not null)
        {
            MergeIntoFrameStateFrom(offset, _currentBlock);
            FinishBlock(new ControlNode(Opcode.Jump) { Int0 = offset });
        }
        _pendingFallthrough = null;

        if (_analysis.IsLoopHeader(offset))
        {
            MergePointInterpreterFrameState? pre = _preheaderStates[offset];
            if (pre is not null && pre.PredecessorsSoFar > 0)
            {
                BasicBlock preBlock = StartBlockFromMergeState(pre);
                preBlock.Offset = offset;
                InitializeLoopHeader(offset, preBlock);
                FinishBlock(new ControlNode(Opcode.Jump) { Target = _mergeStates[offset]!.Block });
            }
            MergePointInterpreterFrameState? loopState = _mergeStates[offset];
            if (loopState is null || loopState.PredecessorsSoFar == 0)
            {
                _currentBlock = null;
                return;
            }
            _currentBlock = loopState.Block!;
            _frame = new InterpreterFrameState(_unit);
            _frame.CopyFrom(loopState);
            return;
        }

        MergePointInterpreterFrameState? state = _mergeStates[offset];
        if (state is null || state.PredecessorsSoFar == 0)
        {
            _currentBlock = null;
            return;
        }
        StartBlockFromMergeState(state).Offset = offset;
    }

    BasicBlock StartBlockFromMergeState(MergePointInterpreterFrameState state)
    {
        BasicBlock block = _graph.NewBlock();
        block.State = state;
        state.Block = block;
        block.Predecessors.AddRange(state.Predecessors);
        foreach (Phi phi in state.Phis)
        {
            phi.Block = block;
            block.Phis.Add(phi);
        }
        _graph.Blocks.Add(block);
        _currentBlock = block;
        _frame = new InterpreterFrameState(_unit);
        _frame.CopyFrom(state);
        return block;
    }

    /// <summary>
    /// Creates the loop header block (with its loop phis) from the entry
    /// predecessor's frame; the header's nodes are built when the builder
    /// reaches the header bytecode.
    /// </summary>
    void InitializeLoopHeader(int offset, BasicBlock entryPredecessor)
    {
        LoopInfo loop = _analysis.GetLoopInfoFor(offset);
        var state = new MergePointInterpreterFrameState(_unit, offset, _predecessorCount[offset],
            _analysis.GetInLivenessFor(offset), loop);
        _mergeStates[offset] = state;
        state.InitializeLoop(this, _frame, entryPredecessor, _loopChangesContext[offset]);
        BasicBlock header = _graph.NewBlock();
        header.IsLoopHeader = true;
        header.Offset = offset;
        header.State = state;
        state.Block = header;
        header.Predecessors.Add(entryPredecessor);
        foreach (Phi phi in state.Phis)
        {
            phi.Block = header;
            header.Phis.Add(phi);
        }
        _graph.Blocks.Add(header);
    }

    /// <summary>
    /// MergeIntoFrameState: merges the current frame, coming from
    /// <paramref name="pred"/> along a forward edge, into the merge state at
    /// <paramref name="target"/> (a loop header's entry edge initialises the loop).
    /// </summary>
    void MergeIntoFrameStateFrom(int target, BasicBlock pred)
    {
        if (_analysis.IsLoopHeader(target))
        {
            if (_forwardPredecessorCount[target] > 1)
            {
                // Several forward edges into a loop: merge them in a pre-header.
                MergePointInterpreterFrameState? pre = _preheaderStates[target];
                if (pre is null)
                {
                    pre = new MergePointInterpreterFrameState(_unit, target, _forwardPredecessorCount[target],
                        _analysis.GetInLivenessFor(target), null);
                    _preheaderStates[target] = pre;
                }
                pre.Merge(this, _frame, pred);
                return;
            }
            InitializeLoopHeader(target, pred);
            return;
        }
        MergePointInterpreterFrameState? state = _mergeStates[target];
        if (state is null)
        {
            state = new MergePointInterpreterFrameState(_unit, target, _predecessorCount[target],
                _analysis.GetInLivenessFor(target), null);
            _mergeStates[target] = state;
        }
        state.Merge(this, _frame, pred);
    }

    /// <summary>
    /// Ends the current block with a conditional branch: the jump edge goes to
    /// <paramref name="jumpOffset"/>, the other to the next bytecode; the
    /// branch's true target is the jump target when <paramref name="jumpOnTrue"/>.
    /// </summary>
    void BuildBranch(ControlNode branch, int jumpOffset, bool jumpOnTrue)
    {
        BasicBlock block = _currentBlock!;
        int next = _it.NextOffset();
        if (jumpOffset == next)
        {
            // Both edges go to the next bytecode.
            var jump = new ControlNode(Opcode.Jump);
            MergeOrFallthrough(next, block);
            jump.Int0 = next;
            FinishBlock(jump);
            return;
        }
        MergeIntoFrameStateFrom(jumpOffset, block);
        MergeOrFallthrough(next, block);
        if (jumpOnTrue)
        {
            branch.Int0 = jumpOffset;
            branch.Int2 = next;
        }
        else
        {
            branch.Int0 = next;
            branch.Int2 = jumpOffset;
        }
        FinishBlock(branch);
    }

    void MergeOrFallthrough(int next, BasicBlock block)
    {
        if (NeedsMergeState(next))
        {
            MergeIntoFrameStateFrom(next, block);
        }
        else
        {
            // The fallthrough block starts with a copy of this frame.
            var copy = new InterpreterFrameState(_unit);
            Array.Copy(_frame.Values, copy.Values, copy.Values.Length);
            copy.Known = _frame.Known.Clone();
            _pendingFallthrough = copy;
            _pendingFallthroughPredecessor = block;
            _pendingFallthroughOffset = next;
        }
    }

    // ---- Blocks ---------------------------------------------------------------------------------

    BasicBlock StartNewBlock(BasicBlock? predecessor)
    {
        BasicBlock block = _graph.NewBlock();
        if (predecessor is not null) block.Predecessors.Add(predecessor);
        _graph.Blocks.Add(block);
        _currentBlock = block;
        return block;
    }

    /// <summary>Ends the current block with <paramref name="control"/>.</summary>
    void FinishBlock(ControlNode control)
    {
        BasicBlock block = _currentBlock!;
        control.Id = _graph.NewNodeId();
        control.Unit = _unit;
        if (control.BytecodeOffset < 0 && !_it.Done()) control.BytecodeOffset = Cursor;
        block.Control = control;
        _currentBlock = null;
    }

    /// <summary>
    /// Resolves the edges recorded by bytecode offset (Int0: the jump or true
    /// target, Int2: the false target) to blocks once every block of this
    /// unit exists (V8: BasicBlockRef patching).
    /// </summary>
    void ResolveJumpTargets()
    {
        foreach (BasicBlock block in _graph.Blocks)
        {
            ControlNode? c = block.Control;
            if (c is null || !ReferenceEquals(c.Unit, _unit)) continue;
            switch (c.Opcode)
            {
                case Opcode.Jump:
                    // (Int1 == -1: an inlined function's return, wired to the caller's continuation.)
                    if (c.Int1 != -1) c.Target ??= ResolveEdge(c.Int0);
                    break;
                case Opcode.BranchIfToBooleanTrue:
                case Opcode.BranchIfInt32Compare:
                case Opcode.BranchIfFloat64Compare:
                case Opcode.BranchIfReferenceEqual:
                case Opcode.BranchIfRootConstant:
                case Opcode.BranchIfUndefinedOrNull:
                case Opcode.BranchIfJSReceiver:
                case Opcode.BranchIfInt32ToBooleanTrue:
                case Opcode.BranchIfFloat64ToBooleanTrue:
                    c.Target ??= ResolveEdge(c.Int0);
                    c.FalseTarget ??= ResolveEdge(c.Int2);
                    break;
                case Opcode.Switch:
                    if (c.Obj1 is int[] offsets)
                    {
                        for (int i = 0; i < offsets.Length; i++) c.Targets![i] ??= ResolveEdge(offsets[i]);
                    }
                    break;
            }
        }
    }

    BasicBlock ResolveEdge(int offset)
    {
        if (_preheaderStates[offset]?.Block is { } pre) return pre;
        if (_mergeStates[offset]?.Block is { } merged) return merged;
        if (_fallthroughBlocks.TryGetValue(offset, out BasicBlock? fallthrough)) return fallthrough;
        throw new MaglevBailoutException("unresolved jump target " + offset);
    }

    // ---- Nodes -------------------------------------------------------------------------------------

    /// <summary>The bytecode offset after any prefix (what the frame record holds).</summary>
    int Cursor => _it.CurrentOffset() + _it.CurrentBytecodeSize() - _it.CurrentBytecodeSizeWithoutPrefix();

    /// <summary>
    /// AddNewNode: appends <paramref name="node"/> to the current block, with
    /// its deopt infos; a node with side effects invalidates unstable map facts.
    /// </summary>
    T AddNewNode<T>(T node, DeoptimizeReason reason = DeoptimizeReason.kUnknown) where T : Node
    {
        node.Id = _graph.NewNodeId();
        node.Unit = _unit;
        if (!_it.Done()) node.BytecodeOffset = Cursor;
        if ((node.Properties & OpProperties.kEagerDeopt) != 0)
        {
            node.EagerDeoptInfo = new EagerDeoptInfo(GetLatestCheckpointedFrame(), reason);
        }
        if ((node.Properties & OpProperties.kLazyDeopt) != 0)
        {
            node.LazyDeoptInfo = new LazyDeoptInfo(GetDeoptFrameForLazyDeopt(), _lazyResultLocation, _lazyResultSize);
        }
        _currentBlock!.Nodes.Add(node);
        if ((node.Properties & (OpProperties.kCanWrite | OpProperties.kCall)) != 0)
        {
            _frame.Known.ClearUnstableMaps();
        }
        return node;
    }

    ValueNode GetRootConstant(RootIndex index) => _graph.GetRootConstant(index);
    ValueNode GetSmiConstant(int value) => _graph.GetSmiConstant(value);
    ValueNode GetInt32Constant(int value) => _graph.GetInt32Constant(value);
    ValueNode GetFloat64Constant(double value) => _graph.GetFloat64Constant(value);
    ValueNode GetBooleanConstant(bool value) => _graph.GetBooleanConstant(value);
    ValueNode GetConstant(JSValue value) => _graph.GetConstant(value);

    // ---- Deopt frames ---------------------------------------------------------------------------------

    ValueNode ClosureNode => _unit.Closure!;

    /// <summary>Records the frame at the start of a bytecode (the eager deopt checkpoint).</summary>
    void Checkpoint()
    {
        Array.Copy(_frame.Values, _frameAtBytecodeStart, _frameAtBytecodeStart.Length);
        _latestCheckpointedFrame = null;
    }

    /// <summary>GetLatestCheckpointedFrame: the frame at the start of the current bytecode.</summary>
    DeoptFrame GetLatestCheckpointedFrame()
    {
        if (_latestCheckpointedFrame is not null) return _latestCheckpointedFrame;
        int offset = _it.CurrentOffset();
        BytecodeLivenessState liveness = _analysis.GetInLivenessFor(offset);
        var values = new List<(Register, ValueNode)>();
        for (int slot = 0; slot < _frameAtBytecodeStart.Length; slot++)
        {
            if (!InterpreterFrameState.IsLive(_unit, liveness, slot)) continue;
            ValueNode? v = _frameAtBytecodeStart[slot];
            if (v is null) continue;
            values.Add((InterpreterFrameState.RegisterOf(_unit, slot), v));
        }
        return _latestCheckpointedFrame = new InterpretedDeoptFrame(_unit, offset, _it.NextOffset(), values.ToArray(),
            ClosureNode, _callerDeoptFrame);
    }

    /// <summary>
    /// GetDeoptFrameForLazyDeopt: the frame after the current bytecode, minus
    /// its result location (filled from the node's result).
    /// </summary>
    DeoptFrame GetDeoptFrameForLazyDeopt()
    {
        int offset = _it.CurrentOffset();
        BytecodeLivenessState liveness = _analysis.GetOutLivenessFor(offset);
        var values = new List<(Register, ValueNode)>();
        int accumulatorSlot = InterpreterFrameState.AccumulatorSlot(_unit);
        bool resultInAccumulator = _lazyResultLocation == Register.VirtualAccumulator();
        int resultSlot = InterpreterFrameState.SlotOf(_unit, _lazyResultLocation);
        for (int slot = 0; slot < _frameAtBytecodeStart.Length; slot++)
        {
            if (!InterpreterFrameState.IsLive(_unit, liveness, slot)) continue;
            if (slot == accumulatorSlot && resultInAccumulator) continue;
            if (!resultInAccumulator && slot >= resultSlot && slot < resultSlot + _lazyResultSize) continue;
            // The registers after the bytecode are the ones before it, except
            // its outputs, which are the result location.
            ValueNode? v = slot == accumulatorSlot ? _frameAtBytecodeStart[slot] : _frameAtBytecodeStart[slot];
            if (v is null) continue;
            values.Add((InterpreterFrameState.RegisterOf(_unit, slot), v));
        }
        return new InterpretedDeoptFrame(_unit, offset, _it.NextOffset(), values.ToArray(), ClosureNode, _callerDeoptFrame);
    }

    /// <summary>
    /// The frame of this function at the current call bytecode, as the parent
    /// of an inlined callee (the callee's return value goes to the accumulator).
    /// </summary>
    DeoptFrame GetDeoptFrameForInlinedCall()
    {
        int offset = _it.CurrentOffset();
        BytecodeLivenessState liveness = _analysis.GetOutLivenessFor(offset);
        var values = new List<(Register, ValueNode)>();
        int accumulatorSlot = InterpreterFrameState.AccumulatorSlot(_unit);
        for (int slot = 0; slot < _frameAtBytecodeStart.Length; slot++)
        {
            if (slot == accumulatorSlot) continue;
            if (!InterpreterFrameState.IsLive(_unit, liveness, slot)) continue;
            ValueNode? v = _frameAtBytecodeStart[slot];
            if (v is null) continue;
            values.Add((InterpreterFrameState.RegisterOf(_unit, slot), v));
        }
        return new InterpretedDeoptFrame(_unit, offset, _it.NextOffset(), values.ToArray(), ClosureNode, _callerDeoptFrame);
    }

    /// <summary>EmitUnconditionalDeopt: the rest of the path is dead.</summary>
    void EmitUnconditionalDeopt(DeoptimizeReason reason)
    {
        var deopt = new ControlNode(Opcode.Deopt) { Reason = reason };
        deopt.EagerDeoptInfo = new EagerDeoptInfo(GetLatestCheckpointedFrame(), reason);
        FinishBlock(deopt);
    }

    /// <summary>
    /// An unconditional deopt in the middle of a bytecode's reduction: the
    /// rest of the bytecode is dead (V8 returns ReduceResult::DoneWithAbort).
    /// </summary>
    void EmitUnconditionalDeoptAndAbort(DeoptimizeReason reason)
    {
        EmitUnconditionalDeopt(reason);
        throw new AbortBytecodeException();
    }

    /// <summary>Unwinds the current bytecode's visitor after an unconditional deopt.</summary>
    sealed class AbortBytecodeException : Exception
    {
    }

    // ---- Frame access ---------------------------------------------------------------------------------

    ValueNode LoadRegister(int operandIndex) => _frame.Get(_it.GetRegisterOperand(operandIndex));
    ValueNode GetAccumulator() => _frame.Accumulator;
    void SetAccumulator(ValueNode value) => _frame.Accumulator = value;
    void StoreRegister(Register r, ValueNode value) => _frame.Set(r, value);

    NodeType GetType(ValueNode node) => _frame.Known.GetType(node);
    bool CheckType(ValueNode node, NodeType type) => NodeTypes.Is(GetType(node), type);
    void EnsureType(ValueNode node, NodeType type) => _frame.Known.EnsureType(node, type);

    // ---- Conversions (GetInt32 / GetFloat64 / GetTaggedValue ...) ------------------------------------

    ValueNode AddConversion(Opcode opcode, ValueRepresentation repr, ValueNode input, NodeType type = NodeType.kUnknown,
        OpProperties properties = OpProperties.kNone, DeoptimizeReason reason = DeoptimizeReason.kUnknown, int int0 = 0) =>
        AddNewNode(new ValueNode(opcode, repr) { Inputs = [input], Type = type, Properties = properties, Int0 = int0 }, reason);

    /// <summary>GetTaggedValue.</summary>
    internal ValueNode GetTaggedValue(ValueNode value)
    {
        switch (value.Representation)
        {
            case ValueRepresentation.kTagged:
                return value;
            case ValueRepresentation.kInt32:
            {
                if (value.IsConstant && value.TryGetInt32Constant(out int c)) return GetSmiConstant(c);
                NodeInfo info = _frame.Known.GetOrCreateInfoFor(value);
                return info.TaggedAlternative ??= AddConversion(Opcode.Int32ToNumber, ValueRepresentation.kTagged, value, NodeType.kNumber);
            }
            case ValueRepresentation.kUint32:
            {
                NodeInfo info = _frame.Known.GetOrCreateInfoFor(value);
                return info.TaggedAlternative ??= AddConversion(Opcode.Uint32ToNumber, ValueRepresentation.kTagged, value, NodeType.kNumber);
            }
            case ValueRepresentation.kFloat64:
            {
                if (value.Opcode == Opcode.Float64Constant) return GetConstant(JSValue.FromNumber(value.Double0));
                NodeInfo info = _frame.Known.GetOrCreateInfoFor(value);
                return info.TaggedAlternative ??= AddConversion(Opcode.Float64ToTagged, ValueRepresentation.kTagged, value, NodeType.kNumber);
            }
            case ValueRepresentation.kHoleyFloat64:
            {
                NodeInfo info = _frame.Known.GetOrCreateInfoFor(value);
                return info.TaggedAlternative ??= AddConversion(Opcode.HoleyFloat64ToTagged, ValueRepresentation.kTagged, value);
            }
            default:
                throw new MaglevBailoutException("cannot tag " + value.Representation);
        }
    }

    /// <summary>
    /// The tagged value of <paramref name="value"/> for a phi input from
    /// <paramref name="predecessor"/>: an untagged value is tagged at the end
    /// of the predecessor block (V8: the merge's EnsureTagged).
    /// </summary>
    internal ValueNode GetTaggedValueForPhi(ValueNode value, BasicBlock predecessor)
    {
        if (value.Representation == ValueRepresentation.kTagged) return value;
        if (value.IsConstant) return GetConstant(value.ConstantValue());
        Opcode op = value.Representation switch
        {
            ValueRepresentation.kInt32 => Opcode.Int32ToNumber,
            ValueRepresentation.kUint32 => Opcode.Uint32ToNumber,
            ValueRepresentation.kFloat64 => Opcode.Float64ToTagged,
            ValueRepresentation.kHoleyFloat64 => Opcode.HoleyFloat64ToTagged,
            _ => throw new MaglevBailoutException("cannot tag phi input"),
        };
        // Reuse a tagging of the same value in that block.
        foreach (Node n in predecessor.Nodes)
        {
            if (n.Opcode == op && n is ValueNode v && ReferenceEquals(v.Inputs[0], value)) return v;
        }
        var tagged = new ValueNode(op, ValueRepresentation.kTagged)
        {
            Inputs = [value],
            Id = _graph.NewNodeId(),
            Unit = _unit,
            Type = op == Opcode.HoleyFloat64ToTagged ? NodeType.kUnknown : NodeType.kNumber,
        };
        predecessor.Nodes.Add(tagged);
        return tagged;
    }

    /// <summary>GetInt32: an Int32 value, deoptimizing if the value is not an int32.</summary>
    ValueNode GetInt32(ValueNode value)
    {
        if (value.TryGetInt32Constant(out int constant) && value.IsConstant) return GetInt32Constant(constant);
        switch (value.Representation)
        {
            case ValueRepresentation.kInt32:
                return value;
            case ValueRepresentation.kUint32:
            {
                NodeInfo info = _frame.Known.GetOrCreateInfoFor(value);
                return info.Int32Alternative ??= AddConversion(Opcode.CheckedUint32ToInt32, ValueRepresentation.kInt32, value,
                    NodeType.kNumber, OpProperties.kEagerDeopt, DeoptimizeReason.kNotInt32);
            }
            case ValueRepresentation.kFloat64:
            case ValueRepresentation.kHoleyFloat64:
            {
                NodeInfo info = _frame.Known.GetOrCreateInfoFor(value);
                return info.Int32Alternative ??= AddConversion(Opcode.CheckedFloat64ToInt32, ValueRepresentation.kInt32, value,
                    NodeType.kNumber, OpProperties.kEagerDeopt, DeoptimizeReason.kNotInt32);
            }
            default:
            {
                if (value.IsConstant) EmitUnconditionalDeoptAndAbort(DeoptimizeReason.kNotASmi);
                if (value.Opcode is Opcode.Int32ToNumber) return value.Inputs[0];
                NodeInfo info = _frame.Known.GetOrCreateInfoFor(value);
                if (info.Int32Alternative is { } alt) return alt;
                ValueNode untagged = AddConversion(Opcode.CheckedSmiUntag, ValueRepresentation.kInt32, value, NodeType.kSmi,
                    OpProperties.kEagerDeopt, DeoptimizeReason.kNotASmi);
                info = _frame.Known.GetOrCreateInfoFor(value);
                info.Int32Alternative = untagged;
                info.Type &= NodeType.kNumber;
                return untagged;
            }
        }
    }

    /// <summary>GetFloat64ForToNumber: a Float64 value, deoptimizing unless the value has <paramref name="allowed"/> type.</summary>
    ValueNode GetFloat64(ValueNode value, NodeType allowed = NodeType.kNumber)
    {
        switch (value.Representation)
        {
            case ValueRepresentation.kFloat64:
                return value;
            case ValueRepresentation.kHoleyFloat64:
            {
                NodeInfo info = _frame.Known.GetOrCreateInfoFor(value);
                return info.Float64Alternative ??= AddConversion(Opcode.CheckedHoleyFloat64ToFloat64, ValueRepresentation.kFloat64,
                    value, NodeType.kNumber, OpProperties.kEagerDeopt, DeoptimizeReason.kHole);
            }
            case ValueRepresentation.kInt32:
            {
                if (value.IsConstant && value.TryGetInt32Constant(out int c)) return GetFloat64Constant(c);
                NodeInfo info = _frame.Known.GetOrCreateInfoFor(value);
                return info.Float64Alternative ??= AddConversion(Opcode.ChangeInt32ToFloat64, ValueRepresentation.kFloat64, value,
                    NodeType.kNumber);
            }
            case ValueRepresentation.kUint32:
            {
                NodeInfo info = _frame.Known.GetOrCreateInfoFor(value);
                return info.Float64Alternative ??= AddConversion(Opcode.ChangeUint32ToFloat64, ValueRepresentation.kFloat64, value,
                    NodeType.kNumber);
            }
            default:
            {
                if (value.IsConstant)
                {
                    if (value.TryGetFloat64Constant(out double d)) return GetFloat64Constant(d);
                    JSValue c = value.ConstantValue();
                    if (c.IsOddball && !c.IsTheHole && NodeTypes.CanBe(allowed, NodeType.kOddball))
                    {
                        return GetFloat64Constant(OddballToNumber(c));
                    }
                    if (c.IsUndefined && NodeTypes.CanBe(allowed, NodeType.kUndefined)) return GetFloat64Constant(double.NaN);
                    EmitUnconditionalDeoptAndAbort(DeoptimizeReason.kNotANumber);
                }
                if (value.Opcode is Opcode.Int32ToNumber) return GetFloat64(value.Inputs[0], allowed);
                if (value.Opcode is Opcode.Float64ToTagged) return value.Inputs[0];
                NodeInfo info = _frame.Known.GetOrCreateInfoFor(value);
                if (info.Float64Alternative is { } alt) return alt;
                if (info.Int32Alternative is { } i32)
                {
                    return info.Float64Alternative = AddConversion(Opcode.ChangeInt32ToFloat64, ValueRepresentation.kFloat64, i32,
                        NodeType.kNumber);
                }
                bool number = NodeTypes.Is(GetType(value), NodeType.kNumber);
                ValueNode untagged = number
                    ? AddConversion(Opcode.UnsafeNumberToFloat64, ValueRepresentation.kFloat64, value, NodeType.kNumber)
                    : AddConversion(Opcode.CheckedNumberOrOddballToFloat64, ValueRepresentation.kFloat64, value, NodeType.kNumber,
                        OpProperties.kEagerDeopt,
                        allowed == NodeType.kNumber ? DeoptimizeReason.kNotANumber : DeoptimizeReason.kNotANumberOrOddball,
                        (int)allowed);
                info = _frame.Known.GetOrCreateInfoFor(value);
                info.Float64Alternative = untagged;
                if (allowed == NodeType.kNumber) info.Type &= NodeType.kNumber;
                return untagged;
            }
        }
    }

    /// <summary>GetTruncatedInt32ForToNumber: ToInt32 of a number (or oddball), deoptimizing otherwise.</summary>
    ValueNode GetTruncatedInt32ForToNumber(ValueNode value, NodeType allowed)
    {
        switch (value.Representation)
        {
            case ValueRepresentation.kInt32:
                return value;
            case ValueRepresentation.kUint32:
            case ValueRepresentation.kFloat64:
            case ValueRepresentation.kHoleyFloat64:
            {
                if (value.IsConstant && value.TryGetFloat64Constant(out double c))
                {
                    return GetInt32Constant(Base.Numbers.Conversions.DoubleToInt32(c));
                }
                NodeInfo info = _frame.Known.GetOrCreateInfoFor(value);
                if (info.TruncatedInt32Alternative is { } alt) return alt;
                ValueNode f64 = value.Representation == ValueRepresentation.kHoleyFloat64 ? GetFloat64(value) :
                    value.Representation == ValueRepresentation.kUint32 ? GetFloat64(value) : value;
                return info.TruncatedInt32Alternative = AddConversion(Opcode.TruncateFloat64ToInt32, ValueRepresentation.kInt32, f64,
                    NodeType.kNumber);
            }
            default:
            {
                if (value.IsConstant)
                {
                    if (value.TryGetFloat64Constant(out double d)) return GetInt32Constant(Base.Numbers.Conversions.DoubleToInt32(d));
                    JSValue c = value.ConstantValue();
                    if ((c.IsOddball || c.IsUndefined) && !c.IsTheHole && NodeTypes.CanBe(allowed, NodeType.kOddball))
                    {
                        return GetInt32Constant(Base.Numbers.Conversions.DoubleToInt32(OddballToNumber(c)));
                    }
                    EmitUnconditionalDeoptAndAbort(DeoptimizeReason.kNotANumber);
                }
                if (value.Opcode is Opcode.Int32ToNumber) return value.Inputs[0];
                NodeInfo info = _frame.Known.GetOrCreateInfoFor(value);
                if (info.Int32Alternative is { } i32) return i32;
                if (info.TruncatedInt32Alternative is { } alt) return alt;
                if (info.Float64Alternative is { } f64)
                {
                    return info.TruncatedInt32Alternative = AddConversion(Opcode.TruncateFloat64ToInt32, ValueRepresentation.kInt32,
                        f64, NodeType.kNumber);
                }
                ValueNode truncated = AddConversion(Opcode.TruncateCheckedNumberOrOddballToInt32, ValueRepresentation.kInt32, value,
                    NodeType.kNumber, OpProperties.kEagerDeopt,
                    allowed == NodeType.kNumber ? DeoptimizeReason.kNotANumber : DeoptimizeReason.kNotANumberOrOddball, (int)allowed);
                _frame.Known.GetOrCreateInfoFor(value).TruncatedInt32Alternative = truncated;
                return truncated;
            }
        }
    }

    /// <summary>ToNumber of an oddball constant (undefined is not an Oddball in V8Sharp).</summary>
    static double OddballToNumber(JSValue c) => c.IsUndefined ? double.NaN : ((Oddball)c.Object).ToNumberValue;

    // ---- Checks ------------------------------------------------------------------------------------------

    Node AddCheck(Opcode opcode, ValueNode input, DeoptimizeReason reason, object? obj0 = null, int int0 = 0) =>
        AddNewNode(new Node(opcode) { Inputs = [input], Obj0 = obj0, Int0 = int0, Properties = OpProperties.kEagerDeopt }, reason);

    /// <summary>BuildCheckSmi.</summary>
    void BuildCheckSmi(ValueNode value)
    {
        if (CheckType(value, NodeType.kSmi)) return;
        if (value.Representation != ValueRepresentation.kTagged)
        {
            // An untagged int32 is a Smi only in the Smi range.
            AddCheck(Opcode.CheckInt32IsSmi, GetInt32(value), DeoptimizeReason.kNotASmi);
            return;
        }
        AddCheck(Opcode.CheckSmi, value, DeoptimizeReason.kNotASmi);
        EnsureType(value, NodeType.kSmi);
    }

    /// <summary>BuildCheckNumber.</summary>
    void BuildCheckNumber(ValueNode value)
    {
        if (value.Representation != ValueRepresentation.kTagged || CheckType(value, NodeType.kNumber)) return;
        AddCheck(Opcode.CheckNumber, value, DeoptimizeReason.kNotANumber);
        EnsureType(value, NodeType.kNumber);
    }

    /// <summary>BuildCheckHeapObject.</summary>
    void BuildCheckHeapObject(ValueNode value)
    {
        if (value.Representation != ValueRepresentation.kTagged) EmitUnconditionalDeoptAndAbort(DeoptimizeReason.kSmi);
        if (CheckType(value, NodeType.kAnyHeapObject)) return;
        AddCheck(Opcode.CheckHeapObject, value, DeoptimizeReason.kSmi);
        EnsureType(value, NodeType.kAnyHeapObject & ~NodeType.kUndefined);
    }

    /// <summary>BuildCheckString.</summary>
    void BuildCheckString(ValueNode value)
    {
        if (value.Representation != ValueRepresentation.kTagged) EmitUnconditionalDeoptAndAbort(DeoptimizeReason.kNotAString);
        if (CheckType(value, NodeType.kString)) return;
        AddCheck(Opcode.CheckString, value, DeoptimizeReason.kNotAString);
        EnsureType(value, NodeType.kString);
    }

    /// <summary>BuildCheckSymbol.</summary>
    void BuildCheckSymbol(ValueNode value)
    {
        if (value.Representation != ValueRepresentation.kTagged) EmitUnconditionalDeoptAndAbort(DeoptimizeReason.kNotASymbol);
        if (CheckType(value, NodeType.kSymbol)) return;
        AddCheck(Opcode.CheckSymbol, value, DeoptimizeReason.kNotASymbol);
        EnsureType(value, NodeType.kSymbol);
    }

    /// <summary>BuildCheckValue: the value is this exact object.</summary>
    void BuildCheckValue(ValueNode value, HeapObject expected, DeoptimizeReason reason)
    {
        if (value.Representation != ValueRepresentation.kTagged) EmitUnconditionalDeoptAndAbort(reason);
        if (value.Opcode == Opcode.Constant)
        {
            if (ReferenceEquals(value.Value0.HeapObjectOrNull, expected)) return;
            EmitUnconditionalDeoptAndAbort(reason);
        }
        AddCheck(Opcode.CheckValue, value, reason, expected);
        EnsureType(value, NodeTypes.ForConstant(expected));
    }

    /// <summary>
    /// BuildCheckMaps: the object has one of <paramref name="maps"/>. Elided
    /// when the known maps are a subset; the known maps become the checked ones.
    /// </summary>
    void BuildCheckMaps(ValueNode obj, Map[] maps)
    {
        if (obj.Representation != ValueRepresentation.kTagged) EmitUnconditionalDeoptAndAbort(DeoptimizeReason.kWrongMap);
        NodeInfo? info = _frame.Known.TryGetInfoFor(obj);
        if (info?.PossibleMaps is { } known)
        {
            bool subset = true;
            foreach (Map m in known)
            {
                if (Array.IndexOf(maps, m) < 0)
                {
                    subset = false;
                    break;
                }
            }
            if (subset) return;
            // Only the intersection can pass.
            var intersection = new List<Map>();
            foreach (Map m in maps) if (Array.IndexOf(known, m) >= 0) intersection.Add(m);
            if (intersection.Count == 0) EmitUnconditionalDeoptAndAbort(DeoptimizeReason.kWrongMap);
        }
        if (obj.Opcode == Opcode.Constant)
        {
            if (obj.Value0.HeapObjectOrNull is JSReceiver r && Array.IndexOf(maps, r.Map) >= 0 && r.Map.IsStable)
            {
                // A constant with a stable map keeps it (dependency below).
                RecordKnownMaps(obj, [r.Map]);
                return;
            }
        }
        AddCheck(Opcode.CheckMaps, obj, DeoptimizeReason.kWrongMap, maps);
        RecordKnownMaps(obj, maps);
    }

    void RecordKnownMaps(ValueNode obj, Map[] maps)
    {
        NodeInfo info = _frame.Known.GetOrCreateInfoFor(obj);
        info.PossibleMaps = maps;
        bool anyUnstable = false;
        NodeType type = NodeType.kNone;
        foreach (Map m in maps)
        {
            if (!m.IsStable) anyUnstable = true;
            type |= NodeTypes.ForMap(m);
        }
        info.AnyMapIsUnstable = anyUnstable;
        info.Type &= type == NodeType.kNone ? NodeType.kJSReceiver : type;
        // A stable map stays the object's map unless the code is deoptimized
        // (CompilationDependencies::DependOnStableMap).
        if (!anyUnstable)
        {
            foreach (Map m in maps) _info.AddDependency(m, Objects.DependentCode.DependencyGroups.PrototypeCheck);
        }
    }

    /// <summary>The maps a value is known to have on this path, or null.</summary>
    Map[]? KnownMaps(ValueNode obj) => _frame.Known.TryGetInfoFor(obj)?.PossibleMaps;

    // ---- Builtin calls (the generic nodes) --------------------------------------------------------------

    static readonly Dictionary<string, MethodInfo> s_baselineBuiltins = LoadBuiltins(typeof(BaselineBuiltins));
    static readonly Dictionary<string, MethodInfo> s_maglevBuiltins = LoadBuiltins(typeof(MaglevBuiltins));

    static Dictionary<string, MethodInfo> LoadBuiltins(Type type)
    {
        var result = new Dictionary<string, MethodInfo>(StringComparer.Ordinal);
        foreach (MethodInfo m in type.GetMethods(BindingFlags.Public | BindingFlags.Static)) result[m.Name] = m;
        return result;
    }

    /// <summary>
    /// A CallBuiltin node calling BaselineBuiltins.<paramref name="name"/>
    /// (the generic operation of a bytecode, as the baseline compiler calls it).
    /// </summary>
    ValueNode? CallBaseline(string name, ValueNode[] inputs, BuiltinArg[] args,
        (Register, ValueNode)[]? registerStores = null, OpProperties properties = OpProperties.kGenericCall)
    {
        MethodInfo method = s_baselineBuiltins.TryGetValue(name, out MethodInfo? m) ? m
            : throw new MaglevBailoutException("no baseline builtin " + name);
        return BuildCallBuiltin(method, name, inputs, args, registerStores, properties);
    }

    /// <summary>A CallBuiltin node calling MaglevBuiltins.<paramref name="name"/>.</summary>
    ValueNode? CallMaglev(string name, ValueNode[] inputs, BuiltinArg[] args, OpProperties properties,
        DeoptimizeReason reason = DeoptimizeReason.kUnknown, NodeType type = NodeType.kUnknown)
    {
        MethodInfo method = s_maglevBuiltins.TryGetValue(name, out MethodInfo? m) ? m
            : throw new InvalidOperationException("no maglev builtin " + name);
        ValueNode? result = BuildCallBuiltin(method, name, inputs, args, null, properties, reason);
        if (result is not null && type != NodeType.kUnknown) result.Type = type;
        return result;
    }

    ValueNode? BuildCallBuiltin(MethodInfo method, string name, ValueNode[] inputs, BuiltinArg[] args,
        (Register, ValueNode)[]? registerStores, OpProperties properties, DeoptimizeReason reason = DeoptimizeReason.kUnknown)
    {
        ParameterInfo[] parameters = method.GetParameters();
        for (int i = 0; i < inputs.Length; i++)
        {
            Type parameter = typeof(JSValue);
            for (int a = 0; a < args.Length; a++)
            {
                if (args[a].Kind == BuiltinArgKind.Input && args[a].Index == i) parameter = parameters[a].ParameterType;
            }
            if (parameter == typeof(int) || parameter == typeof(uint)) inputs[i] = GetInt32(inputs[i]);
            else if (parameter == typeof(double)) inputs[i] = GetFloat64(inputs[i]);
            else inputs[i] = GetTaggedValue(inputs[i]);
        }
        (Register, ValueNode)[] stores = registerStores ?? [];
        for (int i = 0; i < stores.Length; i++) stores[i].Item2 = GetTaggedValue(stores[i].Item2);
        var info = new CallBuiltinInfo(method, args, name) { RegisterStores = stores };
        var allInputs = new List<ValueNode>(inputs);
        foreach ((Register _, ValueNode v) in stores) allInputs.Add(v);
        bool deoptIfFalse = method.ReturnType == typeof(bool) && (properties & OpProperties.kEagerDeopt) != 0;
        if (method.ReturnType == typeof(void) || deoptIfFalse)
        {
            info.DeoptIfFalse = deoptIfFalse;
            AddNewNode(new Node(Opcode.CallBuiltin) { Inputs = allInputs.ToArray(), Obj0 = info, Properties = properties }, reason);
            return null;
        }
        ValueRepresentation repr = method.ReturnType == typeof(int) ? ValueRepresentation.kInt32
            : method.ReturnType == typeof(double) ? ValueRepresentation.kFloat64
            : ValueRepresentation.kTagged;
        if (method.ReturnType == typeof(bool)) throw new InvalidOperationException("bool builtin results are not values");
        var value = new ValueNode(Opcode.CallBuiltin, repr) { Inputs = allInputs.ToArray(), Obj0 = info, Properties = properties };
        AddNewNode(value, reason);
        return value;
    }
}
