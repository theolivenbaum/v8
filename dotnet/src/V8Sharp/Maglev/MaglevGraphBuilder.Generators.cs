// Port of the generator visitors of src/maglev/maglev-graph-builder.cc
// (VisitSwitchOnGeneratorState, VisitSuspendGenerator, VisitResumeGenerator):
// a resumed generator or async function enters its code at offset 0 with the
// generator object in the incoming new.target/generator register (the
// ResumeGeneratorTrampoline), switches on the generator's continuation to
// the resume points, and restores the live registers there from the
// generator's register file; a suspend stores them and returns.
//
// Loops that contain a resume point are "resumable loops": the resume edge
// enters the loop body without passing its header, so their headers make
// every live value a phi and start with nothing known
// (MergePointInterpreterFrameState::NewForLoop's is_resumable_loop).
using V8Sharp.Interpreter;

namespace V8Sharp.Maglev;

public sealed partial class MaglevGraphBuilder
{
    /// <summary>Whether the loop at <paramref name="loop"/> is resumable in this compilation (not inlined, not OSR).</summary>
    bool IsResumableLoop(LoopInfo loop) => loop.Resumable && !_unit.IsInline && !_info.IsOsr;

    /// <summary>VisitSwitchOnGeneratorState.</summary>
    void VisitSwitchOnGeneratorState()
    {
        // SwitchOnGeneratorState <generator> <table_start> <table_length>
        // It should be the first bytecode in the bytecode array.
        List<(int CaseValue, int Target)> offsets = BytecodeAnalysis.JumpTableTargets(_it, _constants);
        int next = _it.NextOffset();
        // If there are no jump offsets, then this generator is not resumable, which
        // means we can skip checking for it and switching on its state.
        if (offsets.Count == 0) return;
        if (_unit.IsInline)
        {
            // Inlining a generator function inlines only the initialization
            // part (creating the generator object): every resume point is dead
            // (MergeDeadIntoFrameState), and the switch falls through.
            return;
        }
        _graph.HasResumableGenerator = true;

        // An initial block checks whether the generator is undefined.
        ValueNode maybeGenerator = LoadRegister(0);
        BasicBlock checkBlock = _currentBlock!;
        MergeIntoFrameStateFrom(next, checkBlock);
        var branch = new ControlNode(Opcode.BranchIfRootConstant) { Inputs = [maybeGenerator], Int1 = (int)RootIndex.kUndefinedValue };
        branch.Int0 = next;
        FinishBlock(branch);

        // The generator prologue block.
        BasicBlock prologue = StartNewBlock(checkBlock);
        branch.FalseTarget = prologue;
        ValueNode generator = maybeGenerator;
        ValueNode state = AddNewNode(new ValueNode(Opcode.LoadGeneratorField, ValueRepresentation.kInt32)
        {
            Inputs = [generator],
            Int0 = (int)GeneratorField.kContinuation,
            Type = NodeType.kSmi,
            Properties = OpProperties.kCanRead,
        });
        AddNewNode(new Node(Opcode.StoreGeneratorContinuation)
        {
            Inputs = [generator, GetInt32Constant(JSGeneratorObject.kGeneratorExecuting)],
            Properties = OpProperties.kCanWrite | OpProperties.kNotIdempotent,
        });
        // Guarantee that we have something in the accumulator.
        SetAccumulator(generator);

        // Switch on generator state (every state is a resume point: no fallthrough).
        int caseValueBase = offsets[0].CaseValue;
        int tableLength = offsets[^1].CaseValue - caseValueBase + 1;
        var targets = new int[tableLength];
        Array.Fill(targets, -1);
        foreach ((int caseValue, int target) in offsets) targets[caseValue - caseValueBase] = target;
        // A hole in the table cannot be a state; it goes to the first resume point.
        for (int i = 0; i < targets.Length; i++) if (targets[i] < 0) targets[i] = offsets[0].Target;
        BasicBlock switchBlock = _currentBlock!;
        var merged = new HashSet<int>();
        foreach (int target in targets)
        {
            if (merged.Add(target)) MergeIntoFrameStateFrom(target, switchBlock);
        }
        FinishBlock(new ControlNode(Opcode.Switch)
        {
            Inputs = [state],
            Int0 = caseValueBase,
            Int1 = 1, // no fallthrough
            Targets = new BasicBlock?[tableLength],
            Obj1 = targets,
        });
    }

    /// <summary>VisitSuspendGenerator.</summary>
    void VisitSuspendGenerator()
    {
        // SuspendGenerator <generator> <first input register> <register count> <suspend_id>
        ValueNode generator = LoadRegister(0);
        ValueNode context = GetTaggedValue(_frame.Context);
        Register first = _it.GetRegisterOperand(1);
        int count = RegisterCount(2);
        int suspendId = Uint(3);
        int parameterCountWithoutReceiver = _unit.ParameterCount - 1;
        BytecodeLivenessState liveness = _analysis.GetOutLivenessFor(_it.CurrentOffset());
        var inputs = new ValueNode[2 + parameterCountWithoutReceiver + count];
        inputs[0] = GetTaggedValue(generator);
        inputs[1] = context;
        int k = 2;
        // The parameters (without the receiver), then the registers.
        for (int i = 1; i < _unit.ParameterCount; i++) inputs[k++] = GetTaggedValue(_frame.Get(Register.FromParameterIndex(i)));
        ValueNode optimizedOut = GetRootConstant(RootIndex.kUndefinedValue);
        for (int i = 0; i < count; i++)
        {
            var r = new Register(first.Index + i);
            inputs[k++] = liveness.RegisterIsLive(r.Index) && _frame.TryGet(r) is { } value ? GetTaggedValue(value) : optimizedOut;
        }
        AddNewNode(new Node(Opcode.GeneratorStore)
        {
            Inputs = inputs,
            Int0 = suspendId,
            // The bytecode offset goes to input_or_debug_pos (for the inspector).
            Int1 = _it.CurrentOffset(),
            Properties = OpProperties.kCanWrite | OpProperties.kNotIdempotent,
        });
        if (_unit.IsInline)
        {
            // An inlined generator suspends to its caller: the suspend is the
            // inlined function's return (V8 jumps to the inline exit).
            VisitReturn();
            return;
        }
        FinishBlock(new ControlNode(Opcode.Return) { Inputs = [GetTaggedValue(GetAccumulator())] });
    }

    /// <summary>VisitResumeGenerator.</summary>
    void VisitResumeGenerator()
    {
        // ResumeGenerator <generator> <first output register> <register count>
        ValueNode generator = GetTaggedValue(LoadRegister(0));
        ValueNode context = AddNewNode(new ValueNode(Opcode.LoadGeneratorField, ValueRepresentation.kTagged)
        {
            Inputs = [generator],
            Int0 = (int)GeneratorField.kContext,
            Type = NodeType.kContext,
            Properties = OpProperties.kCanRead,
        });
        SetCurrentContext(context);
        Register first = _it.GetRegisterOperand(1);
        int count = RegisterCount(2);
        int parameterCountWithoutReceiver = _unit.ParameterCount - 1;
        BytecodeLivenessState liveness = _analysis.GetOutLivenessFor(_it.CurrentOffset());
        for (int i = 0; i < count; i++)
        {
            var r = new Register(first.Index + i);
            if (!liveness.RegisterIsLive(r.Index)) continue;
            ValueNode value = AddNewNode(new ValueNode(Opcode.GeneratorRestoreRegister, ValueRepresentation.kTagged)
            {
                Inputs = [generator],
                Int0 = parameterCountWithoutReceiver + i,
                Properties = OpProperties.kNotIdempotent,
            });
            StoreRegister(r, value);
        }
        SetAccumulator(AddNewNode(new ValueNode(Opcode.LoadGeneratorField, ValueRepresentation.kTagged)
        {
            Inputs = [generator],
            Int0 = (int)GeneratorField.kInputOrDebugPos,
            Properties = OpProperties.kCanRead,
        }));
    }
}
