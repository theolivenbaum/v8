// Port of the call parts of src/maglev/maglev-graph-builder.cc and
// maglev-inlining.cc: ReduceCall / BuildCallFromRegisters (call feedback ->
// CheckValue on the target -> builtin reduction, inlining, or a call of the
// known JSFunction), TryReduceBuiltin for the Math functions and
// String.prototype.charCodeAt, BuildInlineFunction (the callee's graph built
// in place with its own frame state, returns joined in a continuation), and
// the construct path (FastNewObject + the inlined constructor).
//
// Deviation: an inlined frame is a real interpreter frame (frame record and
// register window, pushed by EnterInlinedFrame and popped by
// LeaveInlinedFrame) whose registers are only written on deoptimization.
// V8's inlined frames exist only in the deopt translation; V8Sharp's stack
// walker (Error.stack, %GetOptimizationStatus, function.arguments) reads frame
// records, so each inlined call keeps one (a few stores).
using V8Sharp.Builtins;
using V8Sharp.Deoptimizer;
using V8Sharp.Interpreter;

namespace V8Sharp.Maglev;

public sealed partial class MaglevGraphBuilder
{
    // ---- Calls -------------------------------------------------------------------------------------------

    void VisitCall(Bytecode bytecode)
    {
        ValueNode callee = LoadRegister(0);
        ValueNode receiver;
        ValueNode[] args;
        Register argsFirst;
        int slot;
        ConvertReceiverMode mode;
        switch (bytecode)
        {
            case Bytecode.CallAnyReceiver:
            case Bytecode.CallProperty:
            {
                Register first = _it.GetRegisterOperand(1);
                int count = RegisterCount(2);
                receiver = _frame.Get(first);
                args = RegisterValues(new Register(first.Index + 1), count - 1);
                argsFirst = new Register(first.Index + 1);
                slot = FeedbackSlot(3);
                mode = bytecode == Bytecode.CallProperty ? ConvertReceiverMode.NotNullOrUndefined : ConvertReceiverMode.Any;
                break;
            }
            case Bytecode.CallProperty0:
                receiver = LoadRegister(1);
                args = [];
                argsFirst = _it.GetRegisterOperand(1);
                slot = FeedbackSlot(2);
                mode = ConvertReceiverMode.NotNullOrUndefined;
                break;
            case Bytecode.CallProperty1:
                receiver = LoadRegister(1);
                args = [LoadRegister(2)];
                argsFirst = _it.GetRegisterOperand(2);
                slot = FeedbackSlot(3);
                mode = ConvertReceiverMode.NotNullOrUndefined;
                break;
            case Bytecode.CallProperty2:
                receiver = LoadRegister(1);
                args = [LoadRegister(2), LoadRegister(3)];
                argsFirst = _it.GetRegisterOperand(2);
                if (_it.GetRegisterOperand(3).Index != argsFirst.Index + 1) argsFirst = Register.InvalidValue();
                slot = FeedbackSlot(4);
                mode = ConvertReceiverMode.NotNullOrUndefined;
                break;
            case Bytecode.CallUndefinedReceiver:
            {
                Register first = _it.GetRegisterOperand(1);
                int count = RegisterCount(2);
                receiver = GetRootConstant(RootIndex.kUndefinedValue);
                args = RegisterValues(first, count);
                argsFirst = first;
                slot = FeedbackSlot(3);
                mode = ConvertReceiverMode.NullOrUndefined;
                break;
            }
            case Bytecode.CallUndefinedReceiver0:
                receiver = GetRootConstant(RootIndex.kUndefinedValue);
                args = [];
                argsFirst = _it.GetRegisterOperand(0);
                slot = FeedbackSlot(1);
                mode = ConvertReceiverMode.NullOrUndefined;
                break;
            case Bytecode.CallUndefinedReceiver1:
                receiver = GetRootConstant(RootIndex.kUndefinedValue);
                args = [LoadRegister(1)];
                argsFirst = _it.GetRegisterOperand(1);
                slot = FeedbackSlot(2);
                mode = ConvertReceiverMode.NullOrUndefined;
                break;
            default:
                receiver = GetRootConstant(RootIndex.kUndefinedValue);
                args = [LoadRegister(1), LoadRegister(2)];
                argsFirst = _it.GetRegisterOperand(1);
                if (_it.GetRegisterOperand(2).Index != argsFirst.Index + 1) argsFirst = Register.InvalidValue();
                slot = FeedbackSlot(3);
                mode = ConvertReceiverMode.NullOrUndefined;
                break;
        }

        var nexus = new FeedbackNexus(Isolate, _unit.Feedback, slot);
        JSValue feedback = nexus.GetFeedback();
        // The call target is checked whatever the speculation mode; the mode
        // gates the builtin reductions (SaveCallSpeculationScope).
        SpeculationMode speculationMode = nexus.GetSpeculationMode();
        bool speculate = speculationMode != SpeculationMode.kDisallowSpeculation;
        if (speculate && feedback.HeapObjectOrNull is JSFunction applied && !FeedbackVector.IsCleared(feedback) &&
            nexus.GetCallFeedbackContent() == CallFeedbackContent.kReceiver)
        {
            // BuildCallWithFeedback: the feedback names the receiver of
            // Function.prototype.apply (the function applied).
            BuildCheckValue(callee, Isolate.NativeContext.FunctionPrototypeApply, DeoptimizeReason.kWrongCallTarget);
            if (TryReduceFunctionPrototypeApplyCallWithReceiver(applied, receiver, args, nexus) is { } applyResult)
            {
                SetAccumulator(applyResult);
                return;
            }
        }
        else if (feedback.HeapObjectOrNull is JSFunction target && !FeedbackVector.IsCleared(feedback))
        {
            BuildCheckValue(callee, target, DeoptimizeReason.kWrongCallTarget);
            // SaveCallSpeculationScope: the reduction's checks disallow speculation here when they fail.
            ValueNode? reduced = null;
            if (speculate)
            {
                _speculationVector = _unit.Feedback;
                _speculationSlot = slot;
                _speculationMode = speculationMode;
                try
                {
                    reduced = TryReduceBuiltin(target, receiver, args);
                }
                finally
                {
                    _speculationVector = null;
                    _speculationSlot = -1;
                    _speculationMode = SpeculationMode.kAllowSpeculation;
                }
            }
            if (reduced is not null)
            {
                SetAccumulator(reduced);
                return;
            }
            if (TryBuildInlinedCall(target, callee, receiver, args, mode, nexus, isConstruct: false, null) is { } inlined)
            {
                SetAccumulator(inlined);
                return;
            }
            if (argsFirst.IsValid || args.Length == 0)
            {
                SetAccumulator(BuildCallKnownJSFunction(target, receiver, args, argsFirst, mode));
                return;
            }
        }
        else if (speculate && nexus.IcState() == InlineCacheState.UNINITIALIZED && nexus.GetCallCount() == 0)
        {
            EmitUnconditionalDeopt(DeoptimizeReason.kInsufficientTypeFeedbackForCall);
            return;
        }
        if (argsFirst.IsValid || args.Length == 0)
        {
            // V8's generic Call node: the Call builtin, without feedback collection.
            SetAccumulator(BuildCall(callee, receiver, args, argsFirst, mode));
            return;
        }
        SetAccumulator(BuildGenericCall(bytecode, callee, receiver, args, slot));
    }

    /// <summary>
    /// TryReduceFunctionPrototypeApplyCallWithReceiver: f.apply(thisArg) and
    /// f.apply(thisArg, null/undefined) are calls of f; f.apply(thisArg,
    /// arguments) forwards the frame's arguments (CallForwardVarargs).
    /// </summary>
    ValueNode? TryReduceFunctionPrototypeApplyCallWithReceiver(JSFunction target, ValueNode function, ValueNode[] args,
        FeedbackNexus nexus)
    {
        BuildCheckValue(function, target, DeoptimizeReason.kWrongCallTarget);
        ValueNode targetNode = GetConstant(target);
        if (args.Length == 0)
        {
            ValueNode undefined = GetRootConstant(RootIndex.kUndefinedValue);
            return TryBuildInlinedCall(target, targetNode, undefined, [], ConvertReceiverMode.NullOrUndefined, nexus, isConstruct: false, null) ??
                   BuildCall(targetNode, undefined, [], Register.InvalidValue(), ConvertReceiverMode.NullOrUndefined);
        }
        if (args.Length == 1 || args[1].Opcode == Opcode.RootConstant && args[1].ConstantValue().IsNullOrUndefined)
        {
            return TryBuildInlinedCall(target, targetNode, args[0], [], ConvertReceiverMode.Any, nexus, isConstruct: false, null) ??
                   BuildCall(targetNode, args[0], [], Register.InvalidValue(), ConvertReceiverMode.Any);
        }
        if (args.Length == 2 && !_unit.IsInline && args[1].Opcode == Opcode.CallBuiltin &&
            args[1].Obj0 is CallBuiltinInfo { ArgumentsKind: not ArgumentsObjectKind.None } && ReferenceEquals(args[1].Unit, _unit))
        {
            ValueNode result = CallMaglev2("CallForwardArguments", [targetNode, args[0], args[1]],
                [BuiltinArg.Isolate, BuiltinArg.State, BuiltinArg.In(0), BuiltinArg.In(1), BuiltinArg.In(2)], []);
            ((CallBuiltinInfo)result.Obj0!).ForwardsArguments = true;
            return result;
        }
        return null;
    }

    /// <summary>
    /// Whether the function assigns a parameter: its elided arguments object
    /// would be materialized from the frame's parameters, which the code then
    /// has overwritten (StoreRegister).
    /// </summary>
    static bool WritesParameters(BytecodeArray bytecode)
    {
        for (var it = new BytecodeArrayIterator(bytecode); !it.Done(); it.Advance())
        {
            Bytecode bc = it.CurrentBytecode();
            int count = Bytecodes.NumberOfOperands(bc);
            for (int i = 0; i < count; i++)
            {
                if (Bytecodes.IsRegisterOutputOperandType(Bytecodes.GetOperandType(bc, i)) && it.GetRegisterOperand(i).IsParameter) return true;
            }
        }
        return false;
    }

    static bool HasContextAllocatedParameters(ScopeInfo scopeInfo)
    {
        for (int i = 0; i < scopeInfo.ContextLocalCount; i++)
        {
            if (scopeInfo.ContextLocalIsParameter(i)) return true;
        }
        return false;
    }

    ValueNode[] RegisterValues(Register first, int count)
    {
        var values = new ValueNode[count];
        for (int i = 0; i < count; i++) values[i] = _frame.Get(new Register(first.Index + i));
        return values;
    }

    /// <summary>The generic call: the baseline builtin of the call bytecode (with feedback collection).</summary>
    ValueNode BuildGenericCall(Bytecode bytecode, ValueNode callee, ValueNode receiver, ValueNode[] args, int slot)
    {
        switch (bytecode)
        {
            case Bytecode.CallAnyReceiver:
            case Bytecode.CallProperty:
            case Bytecode.CallUndefinedReceiver:
            {
                Register first = _it.GetRegisterOperand(1);
                int count = RegisterCount(2);
                string name = bytecode switch
                {
                    Bytecode.CallAnyReceiver => "CallAnyReceiver",
                    Bytecode.CallProperty => "CallProperty",
                    _ => "CallUndefinedReceiver",
                };
                return CallBaseline(name, [callee],
                    [BuiltinArg.Isolate, Fv, BuiltinArg.I(slot), BuiltinArg.In(0), BuiltinArg.RegIndex(first), BuiltinArg.I(count)],
                    RegisterListStores(first, count))!;
            }
            case Bytecode.CallProperty0:
                return CallBaseline("CallProperty0", [callee, receiver],
                    [BuiltinArg.Isolate, Fv, BuiltinArg.I(slot), BuiltinArg.In(0), BuiltinArg.In(1)])!;
            case Bytecode.CallProperty1:
            {
                Register a0 = _it.GetRegisterOperand(2);
                return CallBaseline("CallProperty1", [callee, receiver],
                    [BuiltinArg.Isolate, Fv, BuiltinArg.I(slot), BuiltinArg.In(0), BuiltinArg.In(1), BuiltinArg.RegIndex(a0)],
                    [(a0, args[0])])!;
            }
            case Bytecode.CallProperty2:
            {
                Register a0 = _it.GetRegisterOperand(2);
                Register a1 = _it.GetRegisterOperand(3);
                return CallBaseline("CallProperty2", [callee, receiver],
                    [BuiltinArg.Isolate, Fv, BuiltinArg.I(slot), BuiltinArg.In(0), BuiltinArg.In(1), BuiltinArg.RegIndex(a0),
                     BuiltinArg.RegIndex(a1)], [(a0, args[0]), (a1, args[1])])!;
            }
            case Bytecode.CallUndefinedReceiver0:
                return CallBaseline("CallUndefinedReceiver0", [callee],
                    [BuiltinArg.Isolate, Fv, BuiltinArg.I(slot), BuiltinArg.In(0)])!;
            case Bytecode.CallUndefinedReceiver1:
            {
                Register a0 = _it.GetRegisterOperand(1);
                return CallBaseline("CallUndefinedReceiver1", [callee],
                    [BuiltinArg.Isolate, Fv, BuiltinArg.I(slot), BuiltinArg.In(0), BuiltinArg.RegIndex(a0)], [(a0, args[0])])!;
            }
            default:
            {
                Register a0 = _it.GetRegisterOperand(1);
                Register a1 = _it.GetRegisterOperand(2);
                return CallBaseline("CallUndefinedReceiver2", [callee],
                    [BuiltinArg.Isolate, Fv, BuiltinArg.I(slot), BuiltinArg.In(0), BuiltinArg.RegIndex(a0), BuiltinArg.RegIndex(a1)],
                    [(a0, args[0]), (a1, args[1])])!;
            }
        }
    }

    /// <summary>
    /// CallKnownJSFunction: a call of a target the feedback named (checked by
    /// CheckValue); no call feedback is collected.
    /// </summary>
    ValueNode BuildCallKnownJSFunction(JSFunction target, ValueNode receiver, ValueNode[] args, Register argsFirst,
        ConvertReceiverMode mode) =>
        BuildCall(GetConstant(target), receiver, args, argsFirst, mode);

    /// <summary>A call of <paramref name="callee"/> with the arguments in consecutive registers (MaglevCalls.Call).</summary>
    ValueNode BuildCall(ValueNode callee, ValueNode receiver, ValueNode[] args, Register argsFirst, ConvertReceiverMode mode)
    {
        var stores = new (Register, ValueNode)[args.Length];
        for (int i = 0; i < args.Length; i++) stores[i] = (new Register(argsFirst.Index + i), args[i]);
        return CallMaglev2("CallKnownJSFunction", [callee, receiver],
            [BuiltinArg.Isolate, BuiltinArg.In(0), BuiltinArg.In(1), args.Length == 0 ? BuiltinArg.I(0) : BuiltinArg.RegIndex(argsFirst),
             BuiltinArg.I(args.Length), BuiltinArg.I((int)mode)], stores);
    }

    ValueNode CallMaglev2(string name, ValueNode[] inputs, BuiltinArg[] args, (Register, ValueNode)[] stores) =>
        BuildCallBuiltin(s_maglevBuiltins[name], name, inputs, args, stores, OpProperties.kGenericCall)!;

    // ---- Builtin reductions (TryReduceBuiltin) ------------------------------------------------------------

    ValueNode? TryReduceBuiltin(JSFunction target, ValueNode receiver, ValueNode[] args)
    {
        if (Flags.maglev_disable_builtin_reducers) return null;
        Builtin id = target.Shared.BuiltinId;
        if (id == Builtin.NoBuiltinId) return null;
        // CanSpeculateCall({kDisallowBoundsCheckSpeculation}): after an out of
        // bounds deopt only the reductions with a bounds-checked path remain.
        if (_speculationMode == SpeculationMode.kDisallowBoundsCheckSpeculation && id != Builtin.StringPrototypeCharCodeAt) return null;
        try
        {
            switch (id)
            {
                case Builtin.MathAbs:
                case Builtin.MathFloor:
                case Builtin.MathCeil:
                case Builtin.MathRound:
                case Builtin.MathTrunc:
                case Builtin.MathSqrt:
                case Builtin.MathSin:
                case Builtin.MathCos:
                case Builtin.MathTan:
                case Builtin.MathExp:
                case Builtin.MathLog:
                case Builtin.MathAtan:
                case Builtin.MathAsin:
                case Builtin.MathAcos:
                case Builtin.MathLog2:
                case Builtin.MathLog10:
                case Builtin.MathCbrt:
                    return ReduceMathUnary(id, args);
                case Builtin.MathMax:
                case Builtin.MathMin:
                    return ReduceMathMinMax(id == Builtin.MathMax, args);
                case Builtin.MathPow:
                    if (args.Length < 2) return null;
                    return Float64Binary(Opcode.Float64Exponentiate, GetFloat64(args[0]), GetFloat64(args[1]));
                case Builtin.MathAtan2:
                    if (args.Length < 2) return null;
                    return AddNewNode(new ValueNode(Opcode.Float64Ieee754Binary, ValueRepresentation.kFloat64)
                    {
                        Inputs = [GetFloat64(args[0]), GetFloat64(args[1])],
                        Int0 = 0,
                        Type = NodeType.kNumber,
                    });
                case Builtin.StringPrototypeCharCodeAt:
                {
                    if (args.Length < 1 || receiver.Representation != ValueRepresentation.kTagged) return null;
                    ValueNode index = GetInt32ElementIndex(args[0]);
                    BuildCheckString(receiver);
                    if (_speculationMode == SpeculationMode.kDisallowBoundsCheckSpeculation)
                    {
                        // The Select of an index check: NaN out of bounds (one node).
                        return AddNewNode(new ValueNode(Opcode.BuiltinStringPrototypeCharCodeAtOrNaN, ValueRepresentation.kFloat64)
                        {
                            Inputs = [receiver, index],
                            Type = NodeType.kNumber,
                        });
                    }
                    ValueNode length = AddNewNode(new ValueNode(Opcode.StringLength, ValueRepresentation.kInt32)
                    {
                        Inputs = [receiver],
                        Type = NodeType.kSmi,
                    });
                    AddNewNode(new Node(Opcode.CheckInt32Condition)
                    {
                        Inputs = [index, length],
                        Int0 = (int)CompareOperation.kLessThan,
                        Int1 = 1,
                        Properties = OpProperties.kEagerDeopt,
                    }, DeoptimizeReason.kOutOfBounds);
                    return AddNewNode(new ValueNode(Opcode.BuiltinStringPrototypeCharCodeAt, ValueRepresentation.kInt32)
                    {
                        Inputs = [receiver, index],
                        Type = NodeType.kSmi,
                    });
                }
            }
        }
        catch (MaglevBailoutException)
        {
            return null;
        }
        return null;
    }

    ValueNode? ReduceMathUnary(Builtin id, ValueNode[] args)
    {
        if (args.Length == 0) return GetFloat64Constant(double.NaN);
        ValueNode x = args[0];
        if (id == Builtin.MathAbs && x.IsInt32)
        {
            return AddNewNode(new ValueNode(Opcode.Int32AbsWithOverflow, ValueRepresentation.kInt32)
            {
                Inputs = [x],
                Properties = OpProperties.kEagerDeopt,
                Type = NodeType.kNumber,
            }, DeoptimizeReason.kOverflow);
        }
        if (id is Builtin.MathFloor or Builtin.MathCeil or Builtin.MathRound or Builtin.MathTrunc && x.IsInt32) return x;
        ValueNode f = GetFloat64(x, NodeType.kNumberOrOddball);
        Opcode opcode = id switch
        {
            Builtin.MathAbs => Opcode.Float64Abs,
            Builtin.MathSqrt => Opcode.Float64Sqrt,
            Builtin.MathFloor or Builtin.MathCeil or Builtin.MathRound or Builtin.MathTrunc => Opcode.Float64Round,
            _ => Opcode.Float64Ieee754Unary,
        };
        int kind = id switch
        {
            Builtin.MathFloor => 0,
            Builtin.MathCeil => 1,
            Builtin.MathRound => 2,
            Builtin.MathTrunc => 3,
            _ => (int)id,
        };
        return AddNewNode(new ValueNode(opcode, ValueRepresentation.kFloat64) { Inputs = [f], Int0 = kind, Type = NodeType.kNumber });
    }

    ValueNode? ReduceMathMinMax(bool max, ValueNode[] args)
    {
        if (args.Length == 0) return GetFloat64Constant(max ? double.NegativeInfinity : double.PositiveInfinity);
        ValueNode result = GetFloat64(args[0], NodeType.kNumberOrOddball);
        for (int i = 1; i < args.Length; i++)
        {
            result = AddNewNode(new ValueNode(max ? Opcode.Float64Max : Opcode.Float64Min, ValueRepresentation.kFloat64)
            {
                Inputs = [result, GetFloat64(args[i], NodeType.kNumberOrOddball)],
                Type = NodeType.kNumber,
            });
        }
        return result;
    }

    // ---- Inlining (BuildInlineFunction) ---------------------------------------------------------------------

    /// <summary>Whether <paramref name="target"/> can be inlined here (MaglevGraphBuilder::ShouldInlineCall).</summary>
    string? ShouldInlineCall(JSFunction target, FeedbackNexus nexus, bool isConstruct)
    {
        if (!Flags.maglev_inlining) return "inlining disabled";
        SharedFunctionInfo shared = target.Shared;
        if (shared.FunctionData is not BytecodeArray bytecode) return "no bytecode";
        if (shared.HasBuiltinId || shared.Native) return "builtin";
        if (target.RawFeedbackCell.Value is not FeedbackVector) return "no feedback vector";
        if (!isConstruct && shared.IsClassConstructor) return "class constructor";
        if (isConstruct && Globals.IsDerivedConstructor(shared.Kind)) return "derived constructor";
        if (Globals.IsResumableFunction(shared.Kind)) return "resumable";
        if (UnsupportedReason(shared, bytecode) is { } reason) return reason;
        if (!InlineableBytecodes(bytecode)) return "frame-reading bytecode";
        // V8Sharp: the IL backend's catch blocks are in the outermost function
        // (an exception leaves inlined frames through the interpreter's frames).
        if (bytecode.HandlerTable.Length != 0) return "exception handlers";
        if (IsInsideTryBlock) return "inside a try block";
        int length = bytecode.Length;
        bool small = length <= Flags.max_maglev_inlined_bytecode_size_small;
        int depth = _unit.InliningDepth + 1;
        if (depth > Flags.max_maglev_hard_inline_depth) return "too deep";
        if (!small && depth > Flags.max_maglev_inline_depth) return "inline depth";
        if (length > Flags.max_maglev_inlined_bytecode_size) return "too big";
        if (!small && _info.InlinedBytecodeSize + length > Flags.max_maglev_inlined_bytecode_size_cumulative) return "budget";
        if (!small)
        {
            float frequency = nexus.IsNull ? 1f : nexus.ComputeCallFrequency();
            if (frequency < Flags.min_maglev_inlining_frequency) return "infrequent";
        }
        // Direct recursion is not inlined.
        for (MaglevCompilationUnit? u = _unit; u is not null; u = u.Caller)
        {
            if (ReferenceEquals(u.SharedFunctionInfo, shared)) return "recursive";
        }
        return null;
    }

    /// <summary>Bytecodes that read the frame through the interpreter state cannot run in an inlined frame.</summary>
    static bool InlineableBytecodes(BytecodeArray bytecode)
    {
        var it = new BytecodeArrayIterator(bytecode);
        for (; !it.Done(); it.Advance())
        {
            switch (it.CurrentBytecode())
            {
                case Bytecode.CreateMappedArguments:
                case Bytecode.CreateUnmappedArguments:
                case Bytecode.CreateRestParameter:
                case Bytecode.ConstructForwardAllArgs:
                    return false;
            }
        }
        return true;
    }

    /// <summary>
    /// BuildInlineFunction: builds the callee's graph in place. Returns the
    /// call's result, or null when the call is not inlined.
    /// </summary>
    ValueNode? TryBuildInlinedCall(JSFunction target, ValueNode closure, ValueNode receiver, ValueNode[] args, ConvertReceiverMode mode,
        FeedbackNexus nexus, bool isConstruct, ValueNode? newTarget)
    {
        string? reason = ShouldInlineCall(target, nexus, isConstruct);
        if (reason is not null)
        {
            if (_info.IsTracing) Console.WriteLine($"[maglev] not inlining {MaglevCompiler.DebugName(target.Shared)}: {reason}");
            return null;
        }
        SharedFunctionInfo shared = target.Shared;
        // The receiver a sloppy callee sees (ConvertReceiver).
        if (!isConstruct && !shared.Native && shared.LanguageMode == Common.LanguageMode.Sloppy)
        {
            if (mode == ConvertReceiverMode.NullOrUndefined ||
                receiver.Opcode == Opcode.RootConstant && receiver.ConstantValue().IsNullOrUndefined)
            {
                receiver = GetConstant(target.Context.NativeContext.GlobalProxyObject);
            }
            else if (!CheckType(receiver, NodeType.kJSReceiver))
            {
                return null;
            }
        }
        var vector = (FeedbackVector)target.RawFeedbackCell.Value!;
        var unit = new MaglevCompilationUnit(_info, target, shared, vector, _unit, _unit.InliningDepth + 1);
        try
        {
            _ = unit.BytecodeAnalysis;
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            return null;
        }
        _info.InlinedBytecodeSize += unit.Bytecode.Length;
        _info.MaxInliningDepth = Math.Max(_info.MaxInliningDepth, unit.InliningDepth);

        ValueNode taggedReceiver = GetTaggedValue(receiver);
        var taggedArgs = new ValueNode[args.Length];
        for (int i = 0; i < args.Length; i++) taggedArgs[i] = GetTaggedValue(args[i]);
        ValueNode context = GetConstant(target.Context);
        DeoptFrame parentFrame = GetDeoptFrameForInlinedCall();

        // EnterInlinedFrame: the callee's interpreter frame (record and register window).
        var enterInputs = new List<ValueNode> { taggedReceiver };
        enterInputs.AddRange(taggedArgs);
        enterInputs.Add(context);
        enterInputs.Add(GetConstant(target));
        if (newTarget is not null) enterInputs.Add(GetTaggedValue(newTarget));
        unit.EntryNode = AddNewNode(new Node(Opcode.EnterInlinedFrame)
        {
            Inputs = enterInputs.ToArray(),
            Obj0 = unit,
            Int0 = args.Length,
            Int1 = isConstruct ? 1 : 0,
            Int2 = newTarget is not null ? 1 : 0,
            Properties = OpProperties.kCanThrow | OpProperties.kCanWrite | OpProperties.kNotIdempotent,
        });
        unit.Argc = args.Length;
        unit.IsConstruct = isConstruct;
        // Arguments beyond the formal parameters are in no deopt frame (they are
        // only in the frame), so such a frame is pushed on entry.
        unit.EagerFrame = args.Length > unit.Bytecode.ParameterCount - 1;

        BasicBlock callBlock = _currentBlock!;
        var inner = new MaglevGraphBuilder(_info, unit, this, parentFrame, taggedReceiver, taggedArgs, GetConstant(target), context,
            isConstruct ? newTarget : null);
        inner._frame.Known = _frame.Known.Clone();
        inner.BuildInlined(callBlock);
        _latestCheckpointedFrame = null;

        // The continuation: the returns of the callee join here.
        List<(BasicBlock Block, ValueNode Value, KnownNodeAspects Known)> returns = inner._inlinedReturns;
        if (returns.Count == 0)
        {
            // The callee never returns normally (it always deopts or throws).
            _currentBlock = null;
            throw new AbortBytecodeException();
        }
        BasicBlock continuation = _graph.NewBlock();
        _graph.Blocks.Add(continuation);
        KnownNodeAspects known = returns[0].Known;
        foreach ((BasicBlock block, ValueNode _, KnownNodeAspects k) in returns)
        {
            block.Control!.Target = continuation;
            continuation.Predecessors.Add(block);
            if (!ReferenceEquals(k, known)) known.Merge(k);
        }
        ValueNode result = returns[0].Value;
        if (!returns.TrueForAll(r => ReferenceEquals(r.Value, result)))
        {
            var phi = new Phi(Register.VirtualAccumulator(), -1) { Id = _graph.NewNodeId(), Block = continuation, Unit = _unit };
            foreach ((BasicBlock _, ValueNode value, KnownNodeAspects _) in returns) phi.InputList.Add(value);
            continuation.Phis.Add(phi);
            result = phi;
        }
        _currentBlock = continuation;
        _frame.Known = known;
        // The callee may have changed anything (its stores, its calls).
        _frame.Known.ClearUnstableMaps();
        AddNewNode(new Node(Opcode.LeaveInlinedFrame)
        {
            Inputs = [_frame.Context],
            Obj0 = unit,
            Properties = OpProperties.kNotIdempotent,
        });
        if (_info.IsTracing) Console.WriteLine($"[maglev] inlined {MaglevCompiler.DebugName(shared)} into {_unit}");
        return result;
    }

    /// <summary>BuildInlineFunction's body: the callee graph starting from <paramref name="callBlock"/>.</summary>
    void BuildInlined(BasicBlock callBlock)
    {
        CalculatePredecessorCounts();
        BasicBlock entry = _graph.NewBlock();
        _graph.Blocks.Add(entry);
        entry.Predecessors.Add(callBlock);
        // The caller's block jumps to the callee's entry.
        var jump = new ControlNode(Opcode.Jump) { Target = entry, Id = _graph.NewNodeId(), Unit = _caller!._unit };
        callBlock.Control = jump;
        _caller._currentBlock = null;
        _currentBlock = entry;
        BuildRegisterFrameInitialization();
        BuildBody(0);
        if (_currentBlock is not null) throw new MaglevBailoutException("inlined function fell off the end");
        ResolveJumpTargets();
    }

    // ---- Construct ---------------------------------------------------------------------------------------------

    void VisitConstruct()
    {
        ValueNode constructor = LoadRegister(0);
        Register first = _it.GetRegisterOperand(1);
        int count = RegisterCount(2);
        int slot = FeedbackSlot(3);
        ValueNode newTarget = GetAccumulator();
        var nexus = new FeedbackNexus(Isolate, _unit.Feedback, slot);
        JSValue feedback = nexus.GetFeedback();
        bool speculate = nexus.GetSpeculationMode() == SpeculationMode.kAllowSpeculation;
        if (speculate && feedback.HeapObjectOrNull is JSFunction target && target.Map.IsConstructor &&
            target.PrototypeOrInitialMap is Map initialMap && !initialMap.IsDictionaryMap &&
            initialMap.InstanceType == InstanceType.JSObjectType && !Globals.IsDerivedConstructor(target.Shared.Kind) &&
            target.Shared.FunctionData is BytecodeArray && !target.Shared.HasBuiltinId)
        {
            BuildCheckValue(constructor, target, DeoptimizeReason.kWrongConstructor);
            BuildCheckValue(newTarget, target, DeoptimizeReason.kWrongConstructor);
            // FastNewObject from the initial map (depends on it staying the initial map).
            _info.AddDependency(initialMap, Objects.DependentCode.DependencyGroups.InitialMapChanged);
            ValueNode[] args = RegisterValues(first, count);
            ValueNode receiver = AddNewNode(new ValueNode(Opcode.FastNewObject, ValueRepresentation.kTagged)
            {
                Obj0 = initialMap,
                Obj1 = target,
                Type = NodeType.kOtherJSReceiver,
                Properties = OpProperties.kCanAllocate | OpProperties.kNotIdempotent,
            });
            RecordKnownMaps(receiver, [initialMap]);
            ValueNode? result = TryBuildInlinedCall(target, constructor, receiver, args, ConvertReceiverMode.Any, nexus,
                isConstruct: true, newTarget);
            if (result is not null)
            {
                // CheckConstructResult: an object result replaces the receiver.
                if (!CheckType(result, NodeType.kJSReceiver) || result.Representation != ValueRepresentation.kTagged)
                {
                    result = result.IsConstant || result.Representation != ValueRepresentation.kTagged
                        ? receiver
                        : CallMaglev("ConstructResult", [result, receiver], [BuiltinArg.In(0), BuiltinArg.In(1)], OpProperties.kNone,
                            type: NodeType.kJSReceiver)!;
                }
                SetAccumulator(result);
                return;
            }
            // Not inlined: the construct stub and the call with the allocated receiver.
            var stores = new (Register, ValueNode)[args.Length];
            for (int i = 0; i < args.Length; i++) stores[i] = (new Register(first.Index + i), args[i]);
            ValueNode constructed = CallMaglev2("ConstructKnownJSFunction", [GetConstant(target), receiver, newTarget],
                [BuiltinArg.Isolate, BuiltinArg.In(0), BuiltinArg.In(1), BuiltinArg.In(2),
                 args.Length == 0 ? BuiltinArg.I(0) : BuiltinArg.RegIndex(first), BuiltinArg.I(args.Length)], stores);
            constructed.Type = NodeType.kJSReceiver;
            SetAccumulator(constructed);
            return;
        }
        else if (speculate && nexus.IcState() == InlineCacheState.UNINITIALIZED && nexus.GetCallCount() == 0)
        {
            EmitUnconditionalDeopt(DeoptimizeReason.kInsufficientTypeFeedbackForConstruct);
            return;
        }
        SetAccumulator(CallBaseline("Construct", [constructor, newTarget],
            [BuiltinArg.Isolate, Fv, BuiltinArg.I(slot), BuiltinArg.In(0), BuiltinArg.RegIndex(first), BuiltinArg.I(count),
             BuiltinArg.In(1)], RegisterListStores(first, count))!);
    }

    // ---- Runtime calls -----------------------------------------------------------------------------------------

    void VisitCallRuntime()
    {
        Register first = _it.GetRegisterOperand(1);
        int count = RegisterCount(2);
        Runtime.FunctionId id = _it.GetRuntimeIdOperand(0);
        SetAccumulator(CallBaseline("CallRuntime", [],
            [BuiltinArg.Isolate, BuiltinArg.I((int)id), BuiltinArg.RegIndex(first), BuiltinArg.I(count)],
            RegisterListStores(first, count))!);
    }

    void VisitInvokeIntrinsic()
    {
        Register first = _it.GetRegisterOperand(1);
        int count = RegisterCount(2);
        int id = RawByteOperand(0);
        SetAccumulator(CallBaseline("InvokeIntrinsic", [],
            [BuiltinArg.Isolate, BuiltinArg.I(id), BuiltinArg.RegIndex(first), BuiltinArg.I(count)],
            RegisterListStores(first, count))!);
    }
}
