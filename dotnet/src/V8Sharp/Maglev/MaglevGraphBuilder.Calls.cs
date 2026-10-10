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
using System.Reflection;
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
        else if (KnownCallTarget(callee, feedback) is { } target)
        {
            BuildCheckValue(callee, target, DeoptimizeReason.kWrongCallTarget);
            // TryReduceBuiltin's Function.prototype.apply (feedback naming
            // apply itself once the applied functions differ).
            if (ReferenceEquals(target, Isolate.NativeContext.FunctionPrototypeApply) &&
                TryReduceFunctionPrototypeApply(receiver, args) is { } applied2)
            {
                SetAccumulator(applied2);
                return;
            }
            // TryReduceBuiltin's Function.prototype.call (ReduceFunctionPrototypeCall).
            if (target.Shared.BuiltinId == Builtins.Builtin.FunctionPrototypeCall && mode != ConvertReceiverMode.NullOrUndefined &&
                TryReduceFunctionPrototypeCall(receiver, args, nexus) is { } called)
            {
                SetAccumulator(called);
                return;
            }
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
            if (TryBuildDirectCall(target, receiver, args, argsFirst, mode) is { } direct)
            {
                SetAccumulator(direct);
                return;
            }
            if (argsFirst.IsValid || args.Length < s_callWithValues.Length)
            {
                SetAccumulator(BuildCallKnownJSFunction(target, receiver, args, argsFirst, mode));
                return;
            }
        }
        else if (!s_noFeedbackCellCalls && feedback.HeapObjectOrNull is FeedbackCell cell && !FeedbackVector.IsCleared(feedback) &&
                 !ReferenceEquals(cell, FeedbackCell.ManyClosuresCell) && cell.Value is FeedbackVector cellVector &&
                 callee.Representation == ValueRepresentation.kTagged && !cellVector.SharedFunctionInfo.IsClassConstructor &&
                 TryBuildCallForFeedbackCell(callee, cell, cellVector, receiver, args, argsFirst, mode, nexus) is { } closureCall)
        {
            // BuildCallWithFeedback's FeedbackCell case: the closures of one
            // CreateClosure site (CheckJSFunction, the feedback cell checked,
            // TryBuildCallKnownJSFunction).
            SetAccumulator(closureCall);
            return;
        }
        else if (speculate && nexus.IcState() == InlineCacheState.UNINITIALIZED && nexus.GetCallCount() == 0)
        {
            EmitUnconditionalDeopt(DeoptimizeReason.kInsufficientTypeFeedbackForCall);
            return;
        }
        else if (callee.Opcode == Opcode.Constant && ReferenceEquals(callee.Value0.HeapObjectOrNull, Isolate.NativeContext.FunctionPrototypeApply) &&
                 TryReduceFunctionPrototypeApply(receiver, args) is { } applyCall)
        {
            // ReduceCallForConstant: the callee is Function.prototype.apply
            // (a constant from the prototype chain), whatever function the
            // megamorphic feedback saw it applied to.
            SetAccumulator(applyCall);
            return;
        }
        if (argsFirst.IsValid || args.Length < s_callWithValues.Length)
        {
            // V8's generic Call node: the Call builtin, without feedback collection.
            SetAccumulator(BuildCall(callee, receiver, args, argsFirst, mode));
            return;
        }
        SetAccumulator(BuildGenericCall(bytecode, callee, receiver, args, slot));
    }

    /// <summary>
    /// The target of a call: a constant callee (ReduceCallForConstant, e.g. the
    /// method an arm of a polymorphic load continuation loaded), whatever the
    /// feedback, or the feedback's function (checked by the caller).
    /// </summary>
    static JSFunction? KnownCallTarget(ValueNode callee, JSValue feedback)
    {
        if (callee.Opcode == Opcode.Constant && callee.Value0.HeapObjectOrNull is JSFunction constant) return constant;
        return feedback.HeapObjectOrNull is JSFunction target && !FeedbackVector.IsCleared(feedback) ? target : null;
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
        if (args.Length == 2 && IsInlinedArgumentsObject(args[1]))
        {
            // The applied function gets the inlined call's arguments.
            ValueNode? inlined = TryBuildInlinedCall(target, targetNode, args[0], _inlinedArguments!, ConvertReceiverMode.Any, nexus,
                isConstruct: false, null);
            if (inlined is not null) return inlined;
            if (_inlinedArguments!.Length < s_callWithValues.Length)
            {
                return BuildCall(targetNode, args[0], _inlinedArguments, Register.InvalidValue(), ConvertReceiverMode.Any);
            }
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
    /// ReduceFunctionPrototypeCall: f.call(thisArg, ...args) is a call of f
    /// (the receiver of call) with thisArg and the rest: a constant f is
    /// inlined or called directly (ReduceCallForConstant), any other f takes
    /// the generic call with the arguments as values.
    /// </summary>
    ValueNode? TryReduceFunctionPrototypeCall(ValueNode function, ValueNode[] args, FeedbackNexus nexus)
    {
        ValueNode receiver = args.Length > 0 ? GetTaggedValue(args[0]) : GetRootConstant(RootIndex.kUndefinedValue);
        ConvertReceiverMode mode = args.Length > 0 ? ConvertReceiverMode.Any : ConvertReceiverMode.NullOrUndefined;
        ValueNode[] rest = args.Length > 1 ? args[1..] : [];
        if (rest.Length >= s_callWithValues.Length) return null;
        if (function.Opcode == Opcode.Constant && function.Value0.HeapObjectOrNull is JSFunction f && !f.Shared.IsClassConstructor)
        {
            if (TryBuildInlinedCall(f, function, receiver, rest, mode, nexus, isConstruct: false, null) is { } inlined) return inlined;
            if (TryBuildDirectCall(f, receiver, rest, Register.InvalidValue(), mode) is { } direct) return direct;
            return BuildCallKnownJSFunction(f, receiver, rest, Register.InvalidValue(), mode);
        }
        // (Another receiver of call only when it is known to be a function: the
        // call builtin's TypeError for a receiver that is not callable is its own.)
        if (function.Representation != ValueRepresentation.kTagged || !NodeTypes.Is(function.Type, NodeType.kJSFunction)) return null;
        return BuildCall(function, receiver, rest, Register.InvalidValue(), mode);
    }

    /// <summary>
    /// TryReduceFunctionPrototypeApply for an unknown applied function
    /// <paramref name="function"/>: f.apply(), f.apply(thisArg),
    /// f.apply(thisArg, null/undefined) and f.apply(thisArg, arguments).
    /// </summary>
    ValueNode? TryReduceFunctionPrototypeApply(ValueNode function, ValueNode[] args)
    {
        if (function.Representation != ValueRepresentation.kTagged) return null;
        // A constant applied function (e.g. a method of a virtual object's
        // prototype): the known-target reduction, which can inline it.
        if (function.Opcode == Opcode.Constant && function.Value0.HeapObjectOrNull is JSFunction constant)
        {
            return TryReduceFunctionPrototypeApplyCallWithReceiver(constant, function, args, default);
        }
        if (args.Length == 0)
        {
            ValueNode undefined = GetRootConstant(RootIndex.kUndefinedValue);
            return BuildCall(function, undefined, [], Register.InvalidValue(), ConvertReceiverMode.NullOrUndefined);
        }
        if (args.Length == 1 || args[1].Opcode == Opcode.RootConstant && args[1].ConstantValue().IsNullOrUndefined)
        {
            return BuildCall(function, GetTaggedValue(args[0]), [], Register.InvalidValue(), ConvertReceiverMode.Any);
        }
        if (args.Length == 2 && IsInlinedArgumentsObject(args[1]) && _inlinedArguments!.Length < s_callWithValues.Length)
        {
            return BuildCall(function, GetTaggedValue(args[0]), _inlinedArguments, Register.InvalidValue(), ConvertReceiverMode.Any);
        }
        if (args.Length == 2 && !_unit.IsInline && args[1].Opcode == Opcode.CallBuiltin &&
            args[1].Obj0 is CallBuiltinInfo { ArgumentsKind: not ArgumentsObjectKind.None } && ReferenceEquals(args[1].Unit, _unit))
        {
            ValueNode result = CallMaglev2("CallForwardArguments", [function, GetTaggedValue(args[0]), args[1]],
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
            // The baseline tier's call paths (BaselineCalls) take the arguments as values.
            case Bytecode.CallProperty0:
                return CallBaselineCalls("CallProperty0", [callee, receiver],
                    [BuiltinArg.Isolate, Fv, BuiltinArg.I(slot), BuiltinArg.In(0), BuiltinArg.In(1)]);
            case Bytecode.CallProperty1:
                return CallBaselineCalls("CallProperty1", [callee, receiver, args[0]],
                    [BuiltinArg.Isolate, Fv, BuiltinArg.I(slot), BuiltinArg.In(0), BuiltinArg.In(1), BuiltinArg.In(2)]);
            case Bytecode.CallProperty2:
                return CallBaselineCalls("CallProperty2", [callee, receiver, args[0], args[1]],
                    [BuiltinArg.Isolate, Fv, BuiltinArg.I(slot), BuiltinArg.In(0), BuiltinArg.In(1), BuiltinArg.In(2), BuiltinArg.In(3)]);
            case Bytecode.CallUndefinedReceiver0:
                return CallBaselineCalls("CallUndefinedReceiver0", [callee],
                    [BuiltinArg.Isolate, Fv, BuiltinArg.I(slot), BuiltinArg.In(0)]);
            case Bytecode.CallUndefinedReceiver1:
                return CallBaselineCalls("CallUndefinedReceiver1", [callee, args[0]],
                    [BuiltinArg.Isolate, Fv, BuiltinArg.I(slot), BuiltinArg.In(0), BuiltinArg.In(1)]);
            default:
                return CallBaselineCalls("CallUndefinedReceiver2", [callee, args[0], args[1]],
                    [BuiltinArg.Isolate, Fv, BuiltinArg.I(slot), BuiltinArg.In(0), BuiltinArg.In(1), BuiltinArg.In(2)]);
        }
    }

    /// <summary>
    /// CallKnownJSFunction: a call of a target the feedback named (checked by
    /// CheckValue); no call feedback is collected.
    /// </summary>
    ValueNode BuildCallKnownJSFunction(JSFunction target, ValueNode receiver, ValueNode[] args, Register argsFirst,
        ConvertReceiverMode mode) =>
        BuildCall(GetConstant(target), receiver, args, argsFirst, mode);

    /// <summary>
    /// CallKnownJSFunction (V8's node of that name): a call of the constant
    /// <paramref name="target"/> that enters its Maglev code directly
    /// (MaglevCalls, "Direct calls"), or null when the target cannot be
    /// called that way.
    /// </summary>
    ValueNode? TryBuildDirectCall(JSFunction target, ValueNode receiver, ValueNode[] args, Register argsFirst, ConvertReceiverMode mode)
    {
        if (target.RawFeedbackCell.Value is not FeedbackVector vector) return null;
        return TryBuildDirectCall(target, null, target.Shared, vector, receiver, args, argsFirst, mode);
    }

    /// <summary>
    /// The call of a closure of the feedback cell <paramref name="cell"/>
    /// (any closure of one CreateClosure site): the callee is checked to be a
    /// JSFunction with that cell, then called as a known function whose code
    /// is the cell's vector's (V8: TryBuildCallKnownJSFunction with the
    /// closure's context and the cell's dispatch handle).
    /// </summary>
    /// <summary>V8SHARP_MAGLEV_NO_FEEDBACK_CELL_CALLS=1: closures of feedback cells are called generically (for A/B measurements).</summary>
    static readonly bool s_noFeedbackCellCalls = Environment.GetEnvironmentVariable("V8SHARP_MAGLEV_NO_FEEDBACK_CELL_CALLS") == "1";

    /// <summary>V8SHARP_MAGLEV_NO_FEEDBACK_CELL_INLINING=1: closures of feedback cells are called, not inlined.</summary>
    static readonly bool s_noFeedbackCellInlining = Environment.GetEnvironmentVariable("V8SHARP_MAGLEV_NO_FEEDBACK_CELL_INLINING") == "1";

    ValueNode? TryBuildCallForFeedbackCell(ValueNode callee, FeedbackCell cell, FeedbackVector vector, ValueNode receiver,
        ValueNode[] args, Register argsFirst, ConvertReceiverMode mode, FeedbackNexus? nexus)
    {
        SharedFunctionInfo shared = vector.SharedFunctionInfo;
        if (shared.FunctionData is not BytecodeArray || shared.HasBuiltinId || shared.IsClassConstructor) return null;
        if (!argsFirst.IsValid && args.Length > 3) return null;
        AddCheck(Opcode.CheckJSFunctionFeedbackCell, callee, DeoptimizeReason.kWrongFeedbackCell, cell);
        EnsureType(callee, NodeType.kJSFunction);
        if (!s_noFeedbackCellInlining && nexus is { } n &&
            TryBuildInlinedCall(null, shared, vector, callee, receiver, args, mode, n, isConstruct: false, null) is { } inlined)
        {
            return inlined;
        }
        return TryBuildDirectCall(null, callee, shared, vector, receiver, args, argsFirst, mode);
    }

    ValueNode? TryBuildDirectCall(JSFunction? target, ValueNode? targetNode, SharedFunctionInfo shared, FeedbackVector vector,
        ValueNode receiver, ValueNode[] args, Register argsFirst, ConvertReceiverMode mode)
    {
        if (shared.FunctionData is not BytecodeArray bytecode || shared.HasBuiltinId || shared.IsClassConstructor) return null;
        int formal = bytecode.ParameterCount - 1;
        if (formal > MaglevFastCalls.kMaxArity) return null;
        // The direct entry's arity (MaglevCodeGenerator.DefineFastCallEntry).
        if (MaglevCodeGenerator.ReadsActualArguments(bytecode)) formal = MaglevFastCalls.kMaxArity;
        if (args.Length > formal) return null;
        // The slow path takes the arguments from consecutive registers or as values.
        if (!argsFirst.IsValid && args.Length > 3) return null;
        var info = new KnownCallInfo
        {
            Target = target,
            Vector = vector,
            FormalCount = formal,
            Argc = args.Length,
            Mode = mode,
            ArgsFirst = args.Length == 0 ? Register.InvalidValue() : argsFirst,
        };
        var inputs = new List<ValueNode>(args.Length + 2) { GetTaggedValue(receiver) };
        foreach (ValueNode arg in args) inputs.Add(GetTaggedValue(arg));
        if (!shared.Native && shared.LanguageMode == Common.LanguageMode.Sloppy)
        {
            // CallFunction's receiver conversion, statically where it can be.
            if (mode == ConvertReceiverMode.NullOrUndefined ||
                receiver.Opcode == Opcode.RootConstant && receiver.ConstantValue().IsNullOrUndefined)
            {
                // The global proxy of the callee's native context (a closure's: loaded from it).
                inputs.Add(target is not null
                    ? GetConstant(target.Context.NativeContext.GlobalProxyObject)
                    : CallMaglev("GlobalProxyOfFunction", [targetNode!], [BuiltinArg.In(0)], OpProperties.kNone)!);
                info.HasConvertedReceiver = true;
                info.ConvertedReceiverInput = inputs.Count - 1;
            }
            else if (!CheckType(receiver, NodeType.kJSReceiver))
            {
                info.CheckReceiver = true;
            }
        }
        if (target is null)
        {
            inputs.Add(targetNode!);
            info.TargetInput = inputs.Count - 1;
        }
        return AddNewNode(new ValueNode(Opcode.CallKnownJSFunction, ValueRepresentation.kTagged)
        {
            Inputs = inputs.ToArray(),
            Obj0 = info,
            Properties = OpProperties.kGenericCall,
        });
    }

    static readonly MethodInfo[] s_callWithValues =
    [
        typeof(MaglevCalls).GetMethod(nameof(MaglevCalls.CallWithValues0))!,
        typeof(MaglevCalls).GetMethod(nameof(MaglevCalls.CallWithValues1))!,
        typeof(MaglevCalls).GetMethod(nameof(MaglevCalls.CallWithValues2))!,
        typeof(MaglevCalls).GetMethod(nameof(MaglevCalls.CallWithValues3))!,
        typeof(MaglevCalls).GetMethod(nameof(MaglevCalls.CallWithValues4))!,
        typeof(MaglevCalls).GetMethod(nameof(MaglevCalls.CallWithValues5))!,
        typeof(MaglevCalls).GetMethod(nameof(MaglevCalls.CallWithValues6))!,
    ];

    /// <summary>
    /// A call of <paramref name="callee"/> (V8's generic Call node): up to
    /// three arguments as values (MaglevCalls.CallWithValuesN, which enters
    /// Maglev code through its direct entry), more from consecutive registers
    /// (MaglevCalls.Call).
    /// </summary>
    ValueNode BuildCall(ValueNode callee, ValueNode receiver, ValueNode[] args, Register argsFirst, ConvertReceiverMode mode)
    {
        if (args.Length < s_callWithValues.Length)
        {
            var inputs = new ValueNode[2 + args.Length];
            var builtinArgs = new BuiltinArg[3 + args.Length + 1];
            inputs[0] = callee;
            inputs[1] = receiver;
            builtinArgs[0] = BuiltinArg.Isolate;
            builtinArgs[1] = BuiltinArg.In(0);
            builtinArgs[2] = BuiltinArg.In(1);
            for (int i = 0; i < args.Length; i++)
            {
                inputs[2 + i] = args[i];
                builtinArgs[3 + i] = BuiltinArg.In(2 + i);
            }
            builtinArgs[^1] = BuiltinArg.I((int)mode);
            MethodInfo method = s_callWithValues[args.Length];
            return BuildCallBuiltin(method, method.Name, inputs, builtinArgs, [], OpProperties.kGenericCall)!;
        }
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
                case Builtin.ArrayPrototypePush:
                    return TryReduceArrayPrototypePush(receiver, args);
                case Builtin.ArrayPrototypePop:
                    return TryReduceArrayPrototypePop(receiver);
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
    /// <summary>The call being considered passes an allocation that has not escaped (TryBuildInlinedCall).</summary>
    bool _callReceivesFreshAllocation;
    /// <summary>Diagnostics (MaglevGenericCallCounts.BySite): the last call not inlined and why.</summary>
    string? _lastNotInlined;

    static void LabelSite(ValueNode? node, string? site)
    {
        if (MaglevGenericCallCounts.BySite && site is not null && node?.Obj0 is CallBuiltinInfo info) info.Site = site;
    }
    static readonly bool s_inlineForEscapeAnalysis = Environment.GetEnvironmentVariable("V8SHARP_MAGLEV_NO_INLINE_FOR_EA") != "1";

    string? ShouldInlineCall(JSFunction target, FeedbackNexus nexus, bool isConstruct) =>
        ShouldInlineCall(target.Shared, target.RawFeedbackCell.Value as FeedbackVector, nexus, isConstruct);

    string? ShouldInlineCall(SharedFunctionInfo shared, FeedbackVector? vector, FeedbackNexus nexus, bool isConstruct)
    {
        if (!Flags.maglev_inlining) return "inlining disabled";
        if (shared.FunctionData is not BytecodeArray bytecode) return "no bytecode";
        if (shared.HasBuiltinId || shared.Native) return "builtin";
        // SharedFunctionInfo::GetInlineability: kHasOptimizationDisabled.
        if (MaglevCompiler.OptimizationDisabled(shared)) return "optimization disabled";
        if (vector is null) return "no feedback vector";
        // A worker thread does not materialize a constant pool (MaglevCompilationUnit).
        if (_info.IsConcurrent && bytecode.ConstantPoolValues is null) return "constant pool not materialized";
        if (!isConstruct && shared.IsClassConstructor) return "class constructor";
        if (isConstruct && Globals.IsDerivedConstructor(shared.Kind)) return "derived constructor";
        if (UnsupportedReason(shared, bytecode) is { } reason) return reason;
        if (!InlineableBytecodes(bytecode)) return "frame-reading bytecode";
        // V8Sharp: the IL backend's catch blocks are in the outermost function
        // (an exception leaves inlined frames through the interpreter's frames).
        if (bytecode.HandlerTable.Length != 0) return "exception handlers";
        int length = bytecode.Length;
        bool small = length <= Flags.max_maglev_inlined_bytecode_size_small;
        if (_onlyInlineSmall && !small) return "polymorphic continuation (small functions only)";
        int depth = _unit.InliningDepth + 1;
        if (depth > Flags.max_maglev_hard_inline_depth) return "too deep";
        // V8Sharp: a call that receives an allocation which has not escaped
        // is inlined deeper (up to the hard depth limit), so the object can
        // stay virtual (V8's Turbofan inlines such calls; Maglev's depth limit
        // would make the call escape it).
        if (!small && depth > MaxInlineDepth && !(_callReceivesFreshAllocation && s_inlineForEscapeAnalysis)) return "inline depth";
        if (length > MaxInlinedBytecodeSize) return "too big";
        if (!small && _info.InlinedBytecodeSize + length > MaxInlinedBytecodeSizeCumulative) return "budget";
        if (!small)
        {
            float frequency = nexus.IsNull ? 1f : nexus.ComputeCallFrequency();
            if (frequency < MinInliningFrequency) return "infrequent";
        }
        // Direct recursion is not inlined (MaglevReducer::CanInlineCall checks
        // the current unit only: a function further up the inlining stack, as
        // the shared constructor of Class.create classes constructing each
        // other, is inlined again within the depth limits).
        if (ReferenceEquals(_unit.SharedFunctionInfo, shared)) return "recursive";
        return null;
    }

    // V8Sharp has no Turbofan: Maglev is the top tier, so it inlines as V8
    // with --maglev-as-top-tier, whose weak implications raise
    // max_maglev_inlined_bytecode_size to 460 and lower
    // min_maglev_inlining_frequency to 0.10 (flag-definitions.h). Flags set
    // explicitly keep their values. The variables V8SHARP_MAGLEV_INLINE_DEPTH,
    // _FREQUENCY, _SIZE and _BUDGET (or the runtimeconfig properties
    // V8Sharp.MaglevInline{Depth,Frequency,Size,Budget}) override the
    // defaults for experiments.
    const int kTopTierInlinedBytecodeSize = 460;
    const double kTopTierInliningFrequency = 0.10;

    static readonly int s_inlineDepth = int.TryParse(Setting("V8SHARP_MAGLEV_INLINE_DEPTH", "V8Sharp.MaglevInlineDepth"), out int d) ? d : -1;
    static readonly double s_inlineFrequency = double.TryParse(Setting("V8SHARP_MAGLEV_INLINE_FREQUENCY", "V8Sharp.MaglevInlineFrequency"),
        System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double f) ? f : -1;
    static readonly int s_inlineSize = int.TryParse(Setting("V8SHARP_MAGLEV_INLINE_SIZE", "V8Sharp.MaglevInlineSize"), out int z) ? z : -1;
    static readonly int s_inlineBudget = int.TryParse(Setting("V8SHARP_MAGLEV_INLINE_BUDGET", "V8Sharp.MaglevInlineBudget"), out int c) ? c : -1;

    static string? Setting(string variable, string property) =>
        Environment.GetEnvironmentVariable(variable) ?? AppContext.GetData(property)?.ToString();

    int MaxInlinedBytecodeSize => Flags.IsExplicitlySet("max_maglev_inlined_bytecode_size")
        ? Flags.max_maglev_inlined_bytecode_size
        : s_inlineSize >= 0 ? s_inlineSize : kTopTierInlinedBytecodeSize;

    int MaxInlinedBytecodeSizeCumulative => s_inlineBudget >= 0 && !Flags.IsExplicitlySet("max_maglev_inlined_bytecode_size_cumulative")
        ? s_inlineBudget
        : Flags.max_maglev_inlined_bytecode_size_cumulative;

    int MaxInlineDepth => s_inlineDepth >= 0 && !Flags.IsExplicitlySet("max_maglev_inline_depth") ? s_inlineDepth : Flags.max_maglev_inline_depth;

    double MinInliningFrequency => Flags.IsExplicitlySet("min_maglev_inlining_frequency")
        ? Flags.min_maglev_inlining_frequency
        : s_inlineFrequency >= 0 ? s_inlineFrequency : kTopTierInliningFrequency;

    /// <summary>Bytecodes that read the frame through the interpreter state cannot run in an inlined frame.</summary>
    static bool InlineableBytecodes(BytecodeArray bytecode)
    {
        var it = new BytecodeArrayIterator(bytecode);
        for (; !it.Done(); it.Advance())
        {
            switch (it.CurrentBytecode())
            {
                // (An inlined function's arguments object is built from its
                // pushed frame: BuildInlinedArgumentsObject.)
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
        FeedbackNexus nexus, bool isConstruct, ValueNode? newTarget) =>
        TryBuildInlinedCall(target, target.Shared, target.RawFeedbackCell.Value as FeedbackVector, closure, receiver, args, mode, nexus,
            isConstruct, newTarget);

    /// <param name="target">The callee, or null for a closure of a feedback cell (<paramref name="closure"/> at run time).</param>
    ValueNode? TryBuildInlinedCall(JSFunction? target, SharedFunctionInfo shared, FeedbackVector? feedbackVector, ValueNode closure,
        ValueNode receiver, ValueNode[] args, ConvertReceiverMode mode, FeedbackNexus nexus, bool isConstruct, ValueNode? newTarget)
    {
        _callReceivesFreshAllocation = receiver is InlinedAllocation { EscapedDuringBuild: false } ||
            Array.Exists(args, static a => a is InlinedAllocation { EscapedDuringBuild: false });
        string? reason = ShouldInlineCall(shared, feedbackVector, nexus, isConstruct);
        _callReceivesFreshAllocation = false;
        if (reason is not null)
        {
            if (_info.IsTracing) Console.WriteLine($"[maglev] not inlining {MaglevCompiler.DebugName(shared)}: {reason}");
            if (MaglevGenericCallCounts.BySite) _lastNotInlined = $"{MaglevCompiler.DebugName(_unit.SharedFunctionInfo)}@{_it.CurrentOffset()} -> {MaglevCompiler.DebugName(shared)}: {reason}";
            return null;
        }
        // The receiver a sloppy callee sees (ConvertReceiver).
        if (!isConstruct && !shared.Native && shared.LanguageMode == Common.LanguageMode.Sloppy)
        {
            if (mode == ConvertReceiverMode.NullOrUndefined ||
                receiver.Opcode == Opcode.RootConstant && receiver.ConstantValue().IsNullOrUndefined)
            {
                receiver = target is not null
                    ? GetConstant(target.Context.NativeContext.GlobalProxyObject)
                    : CallMaglev("GlobalProxyOfFunction", [closure], [BuiltinArg.In(0)], OpProperties.kNone)!;
            }
            else if (!CheckType(receiver, NodeType.kJSReceiver))
            {
                return null;
            }
        }
        FeedbackVector vector = feedbackVector!;
        // The inlined code's calls can observe this frame's parameters.
        FlushDirtyParameters();
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
        // A closure of a feedback cell: its context is loaded from it (V8: BuildLoadJSFunctionContext).
        ValueNode context = target is not null
            ? GetConstant(target.Context)
            : CallMaglev("ContextOfFunction", [closure], [BuiltinArg.In(0)], OpProperties.kNone)!;
        ValueNode closureValue = target is not null ? GetConstant(target) : closure;
        DeoptFrame parentFrame = GetDeoptFrameForInlinedCall();

        // EnterInlinedFrame: the callee's interpreter frame (record and register window).
        var enterInputs = new List<ValueNode> { taggedReceiver };
        enterInputs.AddRange(taggedArgs);
        enterInputs.Add(context);
        enterInputs.Add(closureValue);
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
        // V8Sharp: the arguments beyond the formal parameters are in the deopt
        // frames (AppendExtraArguments), so the frame is pushed lazily too
        // (EagerFrame stays false; it pushed such frames on entry before).
        unit.EagerFrame = false;
        // A frame pushed on entry holds the receiver and the arguments.
        if (unit.EagerFrame) foreach (ValueNode input in unit.EntryNode.Inputs) EscapeDuringBuild(input);

        BasicBlock callBlock = _currentBlock!;
        var inner = new MaglevGraphBuilder(_info, unit, this, parentFrame, taggedReceiver, taggedArgs, closureValue, context,
            isConstruct ? newTarget : null);
        inner._frame.Known = _frame.Known.Clone();
        inner.BuildInlined(callBlock);
        _latestCheckpointedFrame = null;
        if (inner._mayHaveChangedMaps)
        {
            _mayHaveChangedMaps = true;
            _forInState.ReceiverNeedsMapCheck = true;
        }

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
        // The result phi's type: the union of the returned values' types on
        // their paths (before the merges below change returns[0]'s aspects).
        NodeType resultType = NodeType.kNone;
        foreach ((BasicBlock _, ValueNode value, KnownNodeAspects k) in returns) resultType |= k.GetType(value);
        foreach ((BasicBlock block, ValueNode _, KnownNodeAspects k) in returns)
        {
            block.Control!.Target = continuation;
            continuation.Predecessors.Add(block);
            if (!ReferenceEquals(k, known)) known.Merge(k);
        }
        ValueNode result = returns[0].Value;
        if (!returns.TrueForAll(r => ReferenceEquals(r.Value, result)))
        {
            var phi = new Phi(Register.VirtualAccumulator(), -1) { Id = _graph.NewNodeId(), Block = continuation, Unit = _unit, Type = resultType };
            foreach ((BasicBlock _, ValueNode value, KnownNodeAspects _) in returns)
            {
                EscapeDuringBuild(value);
                phi.InputList.Add(value);
            }
            continuation.Phis.Add(phi);
            result = phi;
        }
        _currentBlock = continuation;
        // (What the callee's nodes changed, its returns' known node aspects
        // already forget: MarkPossibleSideEffect.)
        _frame.Known = known;
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
            // BuildInlinedAllocation(CreateJSConstructor(target)), or the
            // FastNewObject call while the map's slack tracking runs.
            ValueNode? receiver = TryBuildInlinedAllocation(initialMap);
            if (receiver is null)
            {
                receiver = AddNewNode(new ValueNode(Opcode.FastNewObject, ValueRepresentation.kTagged)
                {
                    Obj0 = initialMap,
                    Obj1 = target,
                    Type = NodeType.kOtherJSReceiver,
                    Properties = OpProperties.kCanAllocate | OpProperties.kNotIdempotent,
                });
                RecordKnownMaps(receiver, [initialMap]);
            }
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
            if (args.Length <= MaglevFastCalls.kMaxArity)
            {
                // The arguments as values (MaglevCalls.ConstructWithReceiverValues).
                var valueInputs = new ValueNode[3 + args.Length];
                var valueArgs = new BuiltinArg[4 + args.Length];
                valueInputs[0] = GetConstant(target);
                valueInputs[1] = receiver;
                valueInputs[2] = newTarget;
                valueArgs[0] = BuiltinArg.Isolate;
                for (int i = 0; i < 3 + args.Length; i++)
                {
                    if (i >= 3) valueInputs[i] = args[i - 3];
                    valueArgs[1 + i] = BuiltinArg.In(i);
                }
                ValueNode constructedValues = CallMaglev2("ConstructKnownJSFunction" + args.Length, valueInputs, valueArgs, []);
                LabelSite(constructedValues, _lastNotInlined);
                constructedValues.Type = NodeType.kJSReceiver;
                SetAccumulator(constructedValues);
                return;
            }
            var stores = new (Register, ValueNode)[args.Length];
            for (int i = 0; i < args.Length; i++) stores[i] = (new Register(first.Index + i), args[i]);
            ValueNode constructed = CallMaglev2("ConstructKnownJSFunction", [GetConstant(target), receiver, newTarget],
                [BuiltinArg.Isolate, BuiltinArg.In(0), BuiltinArg.In(1), BuiltinArg.In(2),
                 args.Length == 0 ? BuiltinArg.I(0) : BuiltinArg.RegIndex(first), BuiltinArg.I(args.Length)], stores);
            constructed.Type = NodeType.kJSReceiver;
            SetAccumulator(constructed);
            return;
        }
        else if (speculate && count == 0 && feedback.HeapObjectOrNull is AllocationSite site && TryReduceConstructArrayConstructor(
                     constructor, newTarget, site))
        {
            return;
        }
        else if (speculate && nexus.IcState() == InlineCacheState.UNINITIALIZED && nexus.GetCallCount() == 0)
        {
            EmitUnconditionalDeopt(DeoptimizeReason.kInsufficientTypeFeedbackForConstruct);
            return;
        }
        if (count <= MaglevFastCalls.kMaxArity)
        {
            // The arguments as values (MaglevBuiltins.ConstructValuesN).
            var inputs = new ValueNode[2 + count];
            var builtinArgs = new BuiltinArg[5 + count];
            inputs[0] = constructor;
            inputs[1] = newTarget;
            builtinArgs[0] = BuiltinArg.Isolate;
            builtinArgs[1] = Fv;
            builtinArgs[2] = BuiltinArg.I(slot);
            builtinArgs[3] = BuiltinArg.In(0);
            builtinArgs[4] = BuiltinArg.In(1);
            for (int i = 0; i < count; i++)
            {
                inputs[2 + i] = _frame.Get(new Register(first.Index + i));
                builtinArgs[5 + i] = BuiltinArg.In(2 + i);
            }
            SetAccumulator(CallMaglev2("ConstructValues" + count, inputs, builtinArgs, []));
            return;
        }
        SetAccumulator(CallBaseline("Construct", [constructor, newTarget],
            [BuiltinArg.Isolate, Fv, BuiltinArg.I(slot), BuiltinArg.In(0), BuiltinArg.RegIndex(first), BuiltinArg.I(count),
             BuiltinArg.In(1)], RegisterListStores(first, count))!);
    }

    /// <summary>
    /// TryReduceConstructArrayConstructor for `new Array()` with an
    /// AllocationSite as the construct feedback: the checks that the target
    /// and new.target are the Array function, and the array of the site's
    /// elements kind allocated by a frame-free helper (MaglevBuiltins.NewArrayFromSite).
    /// </summary>
    /// <remarks>
    /// V8 builds the allocation inline with the site's elements kind and
    /// depends on the site (DependOnElementsKind); V8Sharp reads the site's
    /// kind when the array is created and gives the array the site's memento,
    /// as Runtime_NewArray does, so no dependency is needed.
    /// </remarks>
    bool TryReduceConstructArrayConstructor(ValueNode constructor, ValueNode newTarget, AllocationSite site)
    {
        if (site.SpeculationDisabled) return false;
        NativeContext native = (_unit.Function ?? _info.Function).Context.NativeContext;
        if (native.ArrayFunction is not JSFunction arrayFunction) return false;
        BuildCheckValue(constructor, arrayFunction, DeoptimizeReason.kWrongConstructor);
        BuildCheckValue(newTarget, arrayFunction, DeoptimizeReason.kWrongConstructor);
        ValueNode array = BuildCallBuiltin(s_maglevBuiltins["NewArrayFromSite"], "NewArrayFromSite", [],
            [BuiltinArg.Isolate, BuiltinArg.C(native), BuiltinArg.C(arrayFunction), BuiltinArg.C(site)], null,
            OpProperties.kCanAllocate | OpProperties.kNotIdempotent)!;
        ((CallBuiltinInfo)array.Obj0!).NoFrame = true;
        array.Type = NodeType.kJSArray;
        SetAccumulator(array);
        return true;
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
