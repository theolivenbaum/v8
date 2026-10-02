// Port of the bytecode visitors of src/maglev/maglev-graph-builder.cc
// (VisitLdar ... VisitAbort): loads, stores, arithmetic and comparisons with
// feedback-driven representations, control flow, and the generic paths.
using V8Sharp.Deoptimizer;
using V8Sharp.Interpreter;
using BOF = V8Sharp.Interpreter.BinaryOperationFeedback;
using COF = V8Sharp.Interpreter.CompareOperationFeedback;

namespace V8Sharp.Maglev;

public sealed partial class MaglevGraphBuilder
{
    // ---- Operand helpers -------------------------------------------------------------------------

    int ConstantPoolIndex(int i) => (int)_it.GetConstantPoolIndexOperand(i);
    int FeedbackSlot(int i) => _it.GetSlotOperand(i);
    int Uint(int i) => (int)_it.GetUnsignedImmediateOperand(i);
    int Imm(int i) => _it.GetImmediateOperand(i);
    int Flag8(int i) => (int)_it.GetFlag8Operand(i);
    int Flag16(int i) => (int)_it.GetFlag16Operand(i);
    int ContextSlot(int i) => (int)_it.GetContextSlotOperand(i);
    int RegisterCount(int i) => (int)_it.GetRegisterCountOperand(i);
    JSValue Constant(int poolIndex) => _constants[poolIndex];
    int EmbeddedFeedbackOffset(int i) => Cursor + _it.CurrentOperandOffset(i);
    int RawByteOperand(int i) => _unit.Bytecode.Bytecodes[Cursor + _it.CurrentOperandOffset(i)];
    int JumpTargetOffset() => BytecodeAnalysis.JumpTargetOffset(_it, _constants);

    BuiltinArg Fv => BuiltinArg.C(_unit.Feedback);

    BinaryOperationHint BinaryHint(int operandIndex) =>
        FeedbackTypeHints.BinaryOperationHintFromFeedback(
            (int)BOF.DecodeTypeIndex((BOF.TypeIndex)_it.GetEmbeddedFeedback(operandIndex)));

    CompareOperationHint CompareHint(int operandIndex) =>
        FeedbackTypeHints.CompareOperationHintFromFeedback(
            (int)COF.DecodeTypeIndex((COF.TypeIndex)_it.GetEmbeddedFeedback(operandIndex)));

    // ---- VisitSingleBytecode ---------------------------------------------------------------------

    void VisitSingleBytecode()
    {
        Checkpoint();
        try
        {
            Visit(_it.CurrentBytecode());
        }
        catch (AbortBytecodeException)
        {
            // The bytecode ended in an unconditional deopt.
        }
    }

    void Visit(Bytecode bytecode)
    {
        switch (bytecode)
        {
            // ---- Accumulator loads ---------------------------------------------------------------
            case Bytecode.Ldar:
                SetAccumulator(LoadRegister(0));
                break;
            case Bytecode.LdaZero:
                SetAccumulator(GetSmiConstant(0));
                break;
            case Bytecode.LdaSmi:
                SetAccumulator(GetSmiConstant(Imm(0)));
                break;
            case Bytecode.LdaUndefined:
                SetAccumulator(GetRootConstant(RootIndex.kUndefinedValue));
                break;
            case Bytecode.LdaNull:
                SetAccumulator(GetRootConstant(RootIndex.kNullValue));
                break;
            case Bytecode.LdaTheHole:
            case Bytecode.LdaTdzHole:
                SetAccumulator(GetRootConstant(RootIndex.kTheHoleValue));
                break;
            case Bytecode.LdaTrue:
                SetAccumulator(GetRootConstant(RootIndex.kTrueValue));
                break;
            case Bytecode.LdaFalse:
                SetAccumulator(GetRootConstant(RootIndex.kFalseValue));
                break;
            case Bytecode.LdaConstant:
                SetAccumulator(GetConstant(Constant(ConstantPoolIndex(0))));
                break;

            // ---- Context slots ---------------------------------------------------------------------
            case Bytecode.LdaContextSlotNoCell:
            case Bytecode.LdaContextSlot:
            case Bytecode.LdaImmutableContextSlot:
                SetAccumulator(BuildLoadContextSlot(LoadRegister(0), Uint(2), ContextSlot(1)));
                break;
            case Bytecode.LdaCurrentContextSlotNoCell:
            case Bytecode.LdaCurrentContextSlot:
            case Bytecode.LdaImmutableCurrentContextSlot:
                SetAccumulator(BuildLoadContextSlot(_frame.Context, 0, ContextSlot(0)));
                break;
            case Bytecode.StaContextSlotNoCell:
            case Bytecode.StaContextSlot:
                BuildStoreContextSlot(LoadRegister(0), Uint(2), ContextSlot(1), GetAccumulator());
                break;
            case Bytecode.StaCurrentContextSlotNoCell:
            case Bytecode.StaCurrentContextSlot:
                BuildStoreContextSlot(_frame.Context, 0, ContextSlot(0), GetAccumulator());
                break;

            // ---- Register transfers ----------------------------------------------------------------
            case Bytecode.Star:
                StoreRegister(_it.GetRegisterOperand(0), GetAccumulator());
                break;
            case Bytecode.Star0:
            case Bytecode.Star1:
            case Bytecode.Star2:
            case Bytecode.Star3:
            case Bytecode.Star4:
            case Bytecode.Star5:
            case Bytecode.Star6:
            case Bytecode.Star7:
            case Bytecode.Star8:
            case Bytecode.Star9:
            case Bytecode.Star10:
            case Bytecode.Star11:
            case Bytecode.Star12:
            case Bytecode.Star13:
            case Bytecode.Star14:
            case Bytecode.Star15:
                StoreRegister(_it.GetStarTargetRegister(), GetAccumulator());
                break;
            case Bytecode.Mov:
                StoreRegister(_it.GetRegisterOperand(1), LoadRegister(0));
                break;
            case Bytecode.PushContext:
            {
                StoreRegister(_it.GetRegisterOperand(0), _frame.Context);
                ValueNode context = GetTaggedValue(GetAccumulator());
                SetCurrentContext(context);
                break;
            }
            case Bytecode.PopContext:
                SetCurrentContext(LoadRegister(0));
                break;

            // ---- Tests ---------------------------------------------------------------------------------
            case Bytecode.TestReferenceEqual:
                SetAccumulator(BuildTaggedEqual(LoadRegister(0), GetAccumulator()));
                break;
            case Bytecode.TestUndetectable:
                SetAccumulator(AddNewNode(new ValueNode(Opcode.TestUndetectable, ValueRepresentation.kTagged)
                {
                    Inputs = [GetTaggedValue(GetAccumulator())],
                    Type = NodeType.kBoolean,
                }));
                break;
            case Bytecode.TestNull:
                SetAccumulator(BuildTaggedEqual(GetAccumulator(), GetRootConstant(RootIndex.kNullValue)));
                break;
            case Bytecode.TestUndefined:
                SetAccumulator(BuildTaggedEqual(GetAccumulator(), GetRootConstant(RootIndex.kUndefinedValue)));
                break;
            case Bytecode.TestTypeOf:
                SetAccumulator(AddNewNode(new ValueNode(Opcode.TestTypeOf, ValueRepresentation.kTagged)
                {
                    Inputs = [GetTaggedValue(GetAccumulator())],
                    Int0 = Flag8(0),
                    Type = NodeType.kBoolean,
                }));
                break;

            // ---- Globals -------------------------------------------------------------------------------
            case Bytecode.LdaGlobal:
            case Bytecode.LdaGlobalInsideTypeof:
                VisitLdaGlobal(bytecode == Bytecode.LdaGlobalInsideTypeof);
                break;
            case Bytecode.StaGlobal:
                VisitStaGlobal();
                break;

            // ---- Lookup slots (generic) --------------------------------------------------------------
            case Bytecode.LdaLookupSlot:
            case Bytecode.LdaLookupSlotInsideTypeof:
                SetAccumulator(CallBaseline("LdaLookupSlot", [_frame.Context],
                    [BuiltinArg.Isolate, BuiltinArg.In(0), BuiltinArg.C(Constant(ConstantPoolIndex(0))),
                     BuiltinArg.B(bytecode == Bytecode.LdaLookupSlotInsideTypeof)])!);
                break;
            case Bytecode.LdaLookupContextSlotNoCell:
            case Bytecode.LdaLookupContextSlot:
            case Bytecode.LdaLookupContextSlotNoCellInsideTypeof:
            case Bytecode.LdaLookupContextSlotInsideTypeof:
                SetAccumulator(CallBaseline("LdaLookupContextSlot", [_frame.Context],
                    [BuiltinArg.Isolate, BuiltinArg.In(0), BuiltinArg.C(Constant(ConstantPoolIndex(0))), BuiltinArg.I(ContextSlot(1)),
                     BuiltinArg.I(Uint(2)),
                     BuiltinArg.B(bytecode is Bytecode.LdaLookupContextSlotNoCellInsideTypeof or Bytecode.LdaLookupContextSlotInsideTypeof)])!);
                break;
            case Bytecode.LdaLookupGlobalSlot:
            case Bytecode.LdaLookupGlobalSlotInsideTypeof:
                SetAccumulator(CallBaseline("LdaLookupGlobalSlot", [_frame.Context],
                    [BuiltinArg.Isolate, Fv, BuiltinArg.In(0), BuiltinArg.C(Constant(ConstantPoolIndex(0))), BuiltinArg.I(FeedbackSlot(1)),
                     BuiltinArg.I(Uint(2)), BuiltinArg.B(bytecode == Bytecode.LdaLookupGlobalSlotInsideTypeof)])!);
                break;
            case Bytecode.StaLookupSlot:
                SetAccumulator(CallBaseline("StaLookupSlot", [_frame.Context, GetAccumulator()],
                    [BuiltinArg.Isolate, BuiltinArg.In(0), BuiltinArg.C(Constant(ConstantPoolIndex(0))), BuiltinArg.I(Flag8(1)),
                     BuiltinArg.In(1)])!);
                break;

            // ---- Property access -------------------------------------------------------------------------
            case Bytecode.GetNamedProperty:
                VisitGetNamedProperty();
                break;
            case Bytecode.GetNamedPropertyFromSuper:
                SetAccumulator(CallBaseline("GetNamedPropertyFromSuper", [LoadRegister(0), GetAccumulator()],
                    [BuiltinArg.Isolate, Fv, BuiltinArg.I(FeedbackSlot(2)), BuiltinArg.In(0), BuiltinArg.In(1),
                     BuiltinArg.C(Constant(ConstantPoolIndex(1)))])!);
                break;
            case Bytecode.GetKeyedProperty:
                VisitGetKeyedProperty();
                break;
            case Bytecode.GetEnumeratedKeyedProperty:
                SetAccumulator(CallBaseline("GetEnumeratedKeyedProperty",
                    [LoadRegister(0), GetAccumulator(), LoadRegister(1), LoadRegister(2)],
                    [BuiltinArg.Isolate, Fv, BuiltinArg.I(FeedbackSlot(3)), BuiltinArg.In(0), BuiltinArg.In(1), BuiltinArg.In(2),
                     BuiltinArg.In(3)])!);
                break;
            case Bytecode.GetPrivateField:
                SetAccumulator(CallBaseline("GetPrivateField", [LoadRegister(0), LoadRegister(3)],
                    [BuiltinArg.Isolate, Fv, BuiltinArg.I(FeedbackSlot(4)), BuiltinArg.In(0), BuiltinArg.I(ContextSlot(1)),
                     BuiltinArg.I(Uint(2)), BuiltinArg.In(1)])!);
                break;
            case Bytecode.LdaModuleVariable:
                SetAccumulator(CallBaseline("LdaModuleVariable", [_frame.Context],
                    [BuiltinArg.Isolate, BuiltinArg.In(0), BuiltinArg.I(Imm(0)), BuiltinArg.I(Uint(1))])!);
                break;
            case Bytecode.StaModuleVariable:
                CallBaseline("StaModuleVariable", [_frame.Context, GetAccumulator()],
                    [BuiltinArg.Isolate, BuiltinArg.In(0), BuiltinArg.I(Imm(0)), BuiltinArg.I(Uint(1)), BuiltinArg.In(1)]);
                break;
            case Bytecode.SetNamedProperty:
            case Bytecode.DefineNamedOwnProperty:
                VisitSetNamedProperty(bytecode == Bytecode.DefineNamedOwnProperty);
                break;
            case Bytecode.SetKeyedProperty:
                VisitSetKeyedProperty();
                break;
            case Bytecode.StaInArrayLiteral:
                CallBaseline("StaInArrayLiteral", [LoadRegister(0), LoadRegister(1), GetAccumulator()],
                    [BuiltinArg.Isolate, Fv, BuiltinArg.I(FeedbackSlot(2)), BuiltinArg.In(0), BuiltinArg.In(1), BuiltinArg.In(2)]);
                break;
            case Bytecode.DefineKeyedOwnProperty:
            case Bytecode.DefineKeyedOwnPropertyInLiteral:
                CallBaseline(bytecode == Bytecode.DefineKeyedOwnProperty ? "DefineKeyedOwnProperty" : "DefineKeyedOwnPropertyInLiteral",
                    [LoadRegister(0), LoadRegister(1), GetAccumulator()],
                    [BuiltinArg.Isolate, Fv, BuiltinArg.I(FeedbackSlot(3)), BuiltinArg.In(0), BuiltinArg.In(1), BuiltinArg.I(Flag8(2)),
                     BuiltinArg.In(2)]);
                break;
            case Bytecode.SetPrototypeProperties:
                SetAccumulator(CallBaseline("SetPrototypeProperties", [_frame.Context, ClosureNode, GetAccumulator()],
                    [BuiltinArg.Isolate, BuiltinArg.In(0), BuiltinArg.In(1), BuiltinArg.C(Constant(ConstantPoolIndex(0))),
                     BuiltinArg.I(FeedbackSlot(1)), BuiltinArg.In(2)])!);
                break;
            case Bytecode.SetPrivateField:
                CallBaseline("SetPrivateField", [LoadRegister(0), LoadRegister(3), GetAccumulator()],
                    [BuiltinArg.Isolate, Fv, BuiltinArg.I(FeedbackSlot(4)), BuiltinArg.In(0), BuiltinArg.I(ContextSlot(1)),
                     BuiltinArg.I(Uint(2)), BuiltinArg.In(1), BuiltinArg.In(2)]);
                break;

            // ---- Binary operators ------------------------------------------------------------------------
            case Bytecode.Add: VisitBinaryOperation(Operation.Add, "Add"); break;
            case Bytecode.Sub: VisitBinaryOperation(Operation.Subtract, "Subtract"); break;
            case Bytecode.Mul: VisitBinaryOperation(Operation.Multiply, "Multiply"); break;
            case Bytecode.Div: VisitBinaryOperation(Operation.Divide, "Divide"); break;
            case Bytecode.Mod: VisitBinaryOperation(Operation.Modulus, "Modulus"); break;
            case Bytecode.Exp: VisitBinaryOperation(Operation.Exponentiate, "Exponentiate"); break;
            case Bytecode.BitwiseOr: VisitBinaryOperation(Operation.BitwiseOr, "BitwiseOr"); break;
            case Bytecode.BitwiseXor: VisitBinaryOperation(Operation.BitwiseXor, "BitwiseXor"); break;
            case Bytecode.BitwiseAnd: VisitBinaryOperation(Operation.BitwiseAnd, "BitwiseAnd"); break;
            case Bytecode.ShiftLeft: VisitBinaryOperation(Operation.ShiftLeft, "ShiftLeft"); break;
            case Bytecode.ShiftRight: VisitBinaryOperation(Operation.ShiftRight, "ShiftRight"); break;
            case Bytecode.ShiftRightLogical: VisitBinaryOperation(Operation.ShiftRightLogical, "ShiftRightLogical"); break;
            case Bytecode.AddSmi: VisitBinarySmiOperation(Operation.Add, "Add"); break;
            case Bytecode.SubSmi: VisitBinarySmiOperation(Operation.Subtract, "Subtract"); break;
            case Bytecode.MulSmi: VisitBinarySmiOperation(Operation.Multiply, "Multiply"); break;
            case Bytecode.DivSmi: VisitBinarySmiOperation(Operation.Divide, "Divide"); break;
            case Bytecode.ModSmi: VisitBinarySmiOperation(Operation.Modulus, "Modulus"); break;
            case Bytecode.ExpSmi: VisitBinarySmiOperation(Operation.Exponentiate, "Exponentiate"); break;
            case Bytecode.BitwiseOrSmi: VisitBinarySmiOperation(Operation.BitwiseOr, "BitwiseOr"); break;
            case Bytecode.BitwiseXorSmi: VisitBinarySmiOperation(Operation.BitwiseXor, "BitwiseXor"); break;
            case Bytecode.BitwiseAndSmi: VisitBinarySmiOperation(Operation.BitwiseAnd, "BitwiseAnd"); break;
            case Bytecode.ShiftLeftSmi: VisitBinarySmiOperation(Operation.ShiftLeft, "ShiftLeft"); break;
            case Bytecode.ShiftRightSmi: VisitBinarySmiOperation(Operation.ShiftRight, "ShiftRight"); break;
            case Bytecode.ShiftRightLogicalSmi: VisitBinarySmiOperation(Operation.ShiftRightLogical, "ShiftRightLogical"); break;
            case Bytecode.Add_StringConstant_Internalize:
                SetAccumulator(CallBaseline("AddStringConstantInternalize", [LoadRegister(0), GetAccumulator()],
                    [BuiltinArg.Isolate, Fv, BuiltinArg.I(FeedbackSlot(1)), BuiltinArg.I(Flag8(2)), BuiltinArg.In(0),
                     BuiltinArg.In(1)])!);
                break;

            // ---- Unary operators -------------------------------------------------------------------------
            case Bytecode.Inc: VisitUnaryOperation(Operation.Increment, "Increment"); break;
            case Bytecode.Dec: VisitUnaryOperation(Operation.Decrement, "Decrement"); break;
            case Bytecode.Negate: VisitUnaryOperation(Operation.Negate, "Negate"); break;
            case Bytecode.BitwiseNot: VisitUnaryOperation(Operation.BitwiseNot, "BitwiseNot"); break;
            case Bytecode.ToBooleanLogicalNot:
                SetAccumulator(BuildToBoolean(GetAccumulator(), negate: true));
                break;
            case Bytecode.LogicalNot:
            {
                ValueNode value = GetAccumulator();
                if (value.Opcode == Opcode.RootConstant)
                {
                    SetAccumulator(GetBooleanConstant(!value.ConstantValue().IsTrue));
                    break;
                }
                SetAccumulator(AddNewNode(new ValueNode(Opcode.LogicalNot, ValueRepresentation.kTagged)
                {
                    Inputs = [GetTaggedValue(value)],
                    Type = NodeType.kBoolean,
                }));
                break;
            }
            case Bytecode.TypeOf:
                SetAccumulator(CallBaseline("TypeOf", [GetAccumulator()],
                    [BuiltinArg.Isolate, Fv, BuiltinArg.I(FeedbackSlot(0)), BuiltinArg.In(0)], properties: OpProperties.kNone)!);
                break;
            case Bytecode.DeletePropertyStrict:
            case Bytecode.DeletePropertySloppy:
                SetAccumulator(CallBaseline(bytecode == Bytecode.DeletePropertyStrict ? "DeletePropertyStrict" : "DeletePropertySloppy",
                    [LoadRegister(0), GetAccumulator()], [BuiltinArg.Isolate, BuiltinArg.In(0), BuiltinArg.In(1)])!);
                break;
            case Bytecode.GetSuperConstructor:
            {
                Register output = _it.GetRegisterOperand(0);
                ValueNode result = WithLazyResult(output, 1, () =>
                    CallBaseline("GetSuperConstructor", [GetAccumulator()], [BuiltinArg.Isolate, BuiltinArg.In(0)])!);
                StoreRegister(output, result);
                break;
            }
            case Bytecode.FindNonDefaultConstructorOrConstruct:
            {
                Register output = _it.GetRegisterOperand(2);
                var second = new Register(output.Index + 1);
                WithLazyResult<ValueNode?>(output, 2, () =>
                {
                    CallBaseline("FindNonDefaultConstructorOrConstruct", [LoadRegister(0), LoadRegister(1)],
                        [BuiltinArg.Isolate, BuiltinArg.In(0), BuiltinArg.In(1), BuiltinArg.RegRef(output), BuiltinArg.RegRef(second)]);
                    return null!;
                });
                LoadRegisterOutputs(output, 2);
                break;
            }

            // ---- Calls -------------------------------------------------------------------------------------
            case Bytecode.CallAnyReceiver:
            case Bytecode.CallProperty:
            case Bytecode.CallProperty0:
            case Bytecode.CallProperty1:
            case Bytecode.CallProperty2:
            case Bytecode.CallUndefinedReceiver:
            case Bytecode.CallUndefinedReceiver0:
            case Bytecode.CallUndefinedReceiver1:
            case Bytecode.CallUndefinedReceiver2:
                VisitCall(bytecode);
                break;
            case Bytecode.CallWithSpread:
            {
                Register first = _it.GetRegisterOperand(1);
                int count = RegisterCount(2);
                SetAccumulator(CallBaseline("CallWithSpread", [LoadRegister(0)],
                    [BuiltinArg.Isolate, Fv, BuiltinArg.I(FeedbackSlot(3)), BuiltinArg.In(0), BuiltinArg.RegIndex(first),
                     BuiltinArg.I(count)], RegisterListStores(first, count))!);
                break;
            }
            case Bytecode.CallRuntime:
                VisitCallRuntime();
                break;
            case Bytecode.CallRuntimeForPair:
            {
                Register first = _it.GetRegisterOperand(1);
                int count = RegisterCount(2);
                Register output = _it.GetRegisterOperand(3);
                var second = new Register(output.Index + 1);
                int id = (int)_it.GetRuntimeIdOperand(0);
                WithLazyResult(output, 2, () =>
                    CallBaseline("CallRuntimeForPair", [],
                        [BuiltinArg.Isolate, BuiltinArg.I(id), BuiltinArg.RegIndex(first), BuiltinArg.I(count),
                         BuiltinArg.RegRef(output), BuiltinArg.RegRef(second)], RegisterListStores(first, count))!);
                LoadRegisterOutputs(output, 2);
                break;
            }
            case Bytecode.CallJSRuntime:
            {
                Register first = _it.GetRegisterOperand(1);
                int count = RegisterCount(2);
                SetAccumulator(CallBaseline("CallJSRuntime", [_frame.Context],
                    [BuiltinArg.Isolate, BuiltinArg.In(0), BuiltinArg.I((int)_it.GetNativeContextIndexOperand(0)),
                     BuiltinArg.RegIndex(first), BuiltinArg.I(count)], RegisterListStores(first, count))!);
                break;
            }
            case Bytecode.InvokeIntrinsic:
                VisitInvokeIntrinsic();
                break;
            case Bytecode.Construct:
                VisitConstruct();
                break;
            case Bytecode.ConstructWithSpread:
            {
                Register first = _it.GetRegisterOperand(1);
                int count = RegisterCount(2);
                SetAccumulator(CallBaseline("ConstructWithSpread", [LoadRegister(0), GetAccumulator()],
                    [BuiltinArg.Isolate, Fv, BuiltinArg.I(FeedbackSlot(3)), BuiltinArg.In(0), BuiltinArg.RegIndex(first),
                     BuiltinArg.I(count), BuiltinArg.In(1)], RegisterListStores(first, count))!);
                break;
            }
            case Bytecode.ConstructForwardAllArgs:
                RequireOutermostFrame();
                SetAccumulator(CallBaseline("ConstructForwardAllArgs", [LoadRegister(0), GetAccumulator()],
                    [BuiltinArg.Isolate, BuiltinArg.State, Fv, BuiltinArg.I(FeedbackSlot(1)), BuiltinArg.In(0), BuiltinArg.In(1)],
                    ParameterStores())!);
                break;

            // ---- Compare operations --------------------------------------------------------------------------
            case Bytecode.TestEqual: VisitCompareOperation(CompareOperation.kEqual, "TestEqual"); break;
            case Bytecode.TestEqualStrict: VisitCompareOperation(CompareOperation.kStrictEqual, "TestEqualStrict"); break;
            case Bytecode.TestLessThan: VisitCompareOperation(CompareOperation.kLessThan, "TestLessThan"); break;
            case Bytecode.TestGreaterThan: VisitCompareOperation(CompareOperation.kGreaterThan, "TestGreaterThan"); break;
            case Bytecode.TestLessThanOrEqual: VisitCompareOperation(CompareOperation.kLessThanOrEqual, "TestLessThanOrEqual"); break;
            case Bytecode.TestGreaterThanOrEqual:
                VisitCompareOperation(CompareOperation.kGreaterThanOrEqual, "TestGreaterThanOrEqual");
                break;
            case Bytecode.TestInstanceOf:
                SetAccumulator(CallBaseline("TestInstanceOf", [LoadRegister(0), GetAccumulator()],
                    [BuiltinArg.Isolate, Fv, BuiltinArg.I(FeedbackSlot(1)), BuiltinArg.In(0), BuiltinArg.In(1)])!);
                GetAccumulator().Type = NodeType.kBoolean;
                break;
            case Bytecode.TestIn:
                SetAccumulator(CallBaseline("TestIn", [LoadRegister(0), GetAccumulator()],
                    [BuiltinArg.Isolate, Fv, BuiltinArg.I(FeedbackSlot(1)), BuiltinArg.In(0), BuiltinArg.In(1)])!);
                GetAccumulator().Type = NodeType.kBoolean;
                break;

            // ---- Casts ---------------------------------------------------------------------------------------
            case Bytecode.ToName:
            {
                ValueNode value = GetAccumulator();
                if (CheckType(value, NodeType.kName)) break;
                SetAccumulator(CallBaseline("ToName", [value], [BuiltinArg.Isolate, BuiltinArg.In(0)])!);
                break;
            }
            case Bytecode.ToNumber:
            case Bytecode.ToNumeric:
                VisitToNumberOrNumeric(bytecode == Bytecode.ToNumber);
                break;
            case Bytecode.ToObject:
            {
                ValueNode value = LoadRegisterAccumulatorForToObject();
                Register output = _it.GetRegisterOperand(0);
                if (CheckType(value, NodeType.kJSReceiver))
                {
                    StoreRegister(output, value);
                    break;
                }
                ValueNode result = WithLazyResult(output, 1, () =>
                    CallBaseline("ToObject", [value], [BuiltinArg.Isolate, BuiltinArg.In(0)])!);
                result.Type = NodeType.kJSReceiver;
                StoreRegister(output, result);
                break;
            }
            case Bytecode.ToString:
            {
                ValueNode value = GetAccumulator();
                if (CheckType(value, NodeType.kString)) break;
                ValueNode result = CallBaseline("ToString", [value], [BuiltinArg.Isolate, BuiltinArg.In(0)])!;
                result.Type = NodeType.kString;
                SetAccumulator(result);
                break;
            }
            case Bytecode.ToBoolean:
                SetAccumulator(BuildToBoolean(GetAccumulator(), negate: false));
                break;

            // ---- Literals -----------------------------------------------------------------------------------
            case Bytecode.CreateRegExpLiteral:
                SetAccumulator(CallBaseline("CreateRegExpLiteral", [],
                    [BuiltinArg.Isolate, Fv, BuiltinArg.I(FeedbackSlot(1)), BuiltinArg.C(Constant(ConstantPoolIndex(0))),
                     BuiltinArg.I(Flag16(2))])!);
                break;
            case Bytecode.CreateArrayLiteral:
            case Bytecode.CreateObjectLiteral:
            {
                // JSHeapBroker::ReadFeedbackForArrayOrObjectLiteral: no AllocationSite yet.
                if (_unit.Feedback.Slots[FeedbackSlot(1)].HeapObjectOrNull is not AllocationSite)
                {
                    EmitUnconditionalDeopt(bytecode == Bytecode.CreateArrayLiteral
                        ? DeoptimizeReason.kInsufficientTypeFeedbackForArrayLiteral
                        : DeoptimizeReason.kInsufficientTypeFeedbackForObjectLiteral);
                    break;
                }
                ValueNode result = CallBaseline(bytecode == Bytecode.CreateArrayLiteral ? "CreateArrayLiteral" : "CreateObjectLiteral", [],
                    [BuiltinArg.Isolate, Fv, BuiltinArg.I(FeedbackSlot(1)), BuiltinArg.C(Constant(ConstantPoolIndex(0))),
                     BuiltinArg.I(Flag8(2))])!;
                result.Type = bytecode == Bytecode.CreateArrayLiteral ? NodeType.kJSArray : NodeType.kOtherJSReceiver;
                SetAccumulator(result);
                break;
            }
            case Bytecode.CreateArrayFromIterable:
                SetAccumulator(CallBaseline("CreateArrayFromIterable", [GetAccumulator()], [BuiltinArg.Isolate, BuiltinArg.In(0)])!);
                break;
            case Bytecode.CreateEmptyArrayLiteral:
            {
                ValueNode result = CallBaseline("CreateEmptyArrayLiteral", [],
                    [BuiltinArg.Isolate, Fv, BuiltinArg.I(FeedbackSlot(0))], properties: OpProperties.kCanAllocate | OpProperties.kNotIdempotent)!;
                result.Type = NodeType.kJSArray;
                SetAccumulator(result);
                break;
            }
            case Bytecode.CreateEmptyObjectLiteral:
            {
                ValueNode result = CallBaseline("CreateEmptyObjectLiteral", [_frame.Context], [BuiltinArg.Isolate, BuiltinArg.In(0)],
                    properties: OpProperties.kCanAllocate | OpProperties.kNotIdempotent)!;
                result.Type = NodeType.kOtherJSReceiver;
                SetAccumulator(result);
                break;
            }
            case Bytecode.CloneObject:
                SetAccumulator(CallBaseline("CloneObject", [LoadRegister(0)],
                    [BuiltinArg.Isolate, Fv, BuiltinArg.I(FeedbackSlot(2)), BuiltinArg.In(0), BuiltinArg.I(Flag8(1))])!);
                break;
            case Bytecode.GetTemplateObject:
                SetAccumulator(CallBaseline("GetTemplateObject", [ClosureNode],
                    [BuiltinArg.Isolate, Fv, BuiltinArg.I(FeedbackSlot(1)), BuiltinArg.In(0), BuiltinArg.C(Constant(ConstantPoolIndex(0)))])!);
                break;
            case Bytecode.CreateClosure:
            {
                ValueNode result = CallBaseline("CreateClosure", [_frame.Context, ClosureNode],
                    [BuiltinArg.Isolate, BuiltinArg.In(0), BuiltinArg.In(1), BuiltinArg.C(Constant(ConstantPoolIndex(0))),
                     BuiltinArg.I(FeedbackSlot(1))], properties: OpProperties.kCanAllocate | OpProperties.kNotIdempotent)!;
                result.Type = NodeType.kJSFunction;
                SetAccumulator(result);
                break;
            }

            // ---- Contexts ----------------------------------------------------------------------------------
            case Bytecode.CreateBlockContext:
                SetAccumulator(WithType(CallBaseline("CreateBlockContext", [_frame.Context],
                    [BuiltinArg.Isolate, BuiltinArg.In(0), BuiltinArg.C(Constant(ConstantPoolIndex(0)))],
                    properties: OpProperties.kCanAllocate | OpProperties.kNotIdempotent)!, NodeType.kContext));
                break;
            case Bytecode.CreateFunctionContext:
            case Bytecode.CreateFunctionContextWithCells:
            case Bytecode.CreateEvalContext:
                SetAccumulator(WithType(CallBaseline("CreateFunctionContext", [_frame.Context],
                    [BuiltinArg.Isolate, BuiltinArg.In(0), BuiltinArg.C(Constant(ConstantPoolIndex(0))),
                     BuiltinArg.B(bytecode == Bytecode.CreateEvalContext)],
                    properties: OpProperties.kCanAllocate | OpProperties.kNotIdempotent)!, NodeType.kContext));
                break;
            case Bytecode.CreateWithContext:
                SetAccumulator(WithType(CallBaseline("CreateWithContext", [_frame.Context, LoadRegister(0)],
                    [BuiltinArg.Isolate, BuiltinArg.In(0), BuiltinArg.In(1), BuiltinArg.C(Constant(ConstantPoolIndex(1)))])!, NodeType.kContext));
                break;

            // ---- Arguments ----------------------------------------------------------------------------------
            case Bytecode.CreateMappedArguments:
                RequireOutermostFrame();
                SetAccumulator(CallBaseline("CreateMappedArguments", [_frame.Context],
                    [BuiltinArg.Isolate, BuiltinArg.State, BuiltinArg.In(0)], ParameterStores())!);
                break;
            case Bytecode.CreateUnmappedArguments:
                RequireOutermostFrame();
                SetAccumulator(CallBaseline("CreateUnmappedArguments", [], [BuiltinArg.Isolate, BuiltinArg.State], ParameterStores())!);
                break;
            case Bytecode.CreateRestParameter:
                RequireOutermostFrame();
                SetAccumulator(CallBaseline("CreateRestParameter", [], [BuiltinArg.Isolate, BuiltinArg.State], ParameterStores())!);
                break;

            // ---- Control flow -------------------------------------------------------------------------------
            case Bytecode.JumpLoop:
                VisitJumpLoop();
                break;
            case Bytecode.Jump:
            case Bytecode.JumpConstant:
            {
                int target = JumpTargetOffset();
                MergeIntoFrameStateFrom(target, _currentBlock!);
                FinishBlock(new ControlNode(Opcode.Jump) { Int0 = target });
                break;
            }
            case Bytecode.JumpIfTrue:
            case Bytecode.JumpIfTrueConstant:
                BuildBranchIfTrue(GetAccumulator(), JumpTargetOffset(), jumpOnTrue: true);
                break;
            case Bytecode.JumpIfFalse:
            case Bytecode.JumpIfFalseConstant:
                BuildBranchIfTrue(GetAccumulator(), JumpTargetOffset(), jumpOnTrue: false);
                break;
            case Bytecode.JumpIfToBooleanTrue:
            case Bytecode.JumpIfToBooleanTrueConstant:
                BuildBranchIfToBooleanTrue(GetAccumulator(), JumpTargetOffset(), jumpOnTrue: true);
                break;
            case Bytecode.JumpIfToBooleanFalse:
            case Bytecode.JumpIfToBooleanFalseConstant:
                BuildBranchIfToBooleanTrue(GetAccumulator(), JumpTargetOffset(), jumpOnTrue: false);
                break;
            case Bytecode.JumpIfNull:
            case Bytecode.JumpIfNullConstant:
                BuildBranchIfReferenceEqual(GetAccumulator(), RootIndex.kNullValue, JumpTargetOffset(), jumpOnTrue: true);
                break;
            case Bytecode.JumpIfNotNull:
            case Bytecode.JumpIfNotNullConstant:
                BuildBranchIfReferenceEqual(GetAccumulator(), RootIndex.kNullValue, JumpTargetOffset(), jumpOnTrue: false);
                break;
            case Bytecode.JumpIfUndefined:
            case Bytecode.JumpIfUndefinedConstant:
                BuildBranchIfReferenceEqual(GetAccumulator(), RootIndex.kUndefinedValue, JumpTargetOffset(), jumpOnTrue: true);
                break;
            case Bytecode.JumpIfNotUndefined:
            case Bytecode.JumpIfNotUndefinedConstant:
                BuildBranchIfReferenceEqual(GetAccumulator(), RootIndex.kUndefinedValue, JumpTargetOffset(), jumpOnTrue: false);
                break;
            case Bytecode.JumpIfUndefinedOrNull:
            case Bytecode.JumpIfUndefinedOrNullConstant:
                BuildBranchOnValue(Opcode.BranchIfUndefinedOrNull, GetAccumulator(), JumpTargetOffset(), jumpOnTrue: true);
                break;
            case Bytecode.JumpIfJSReceiver:
            case Bytecode.JumpIfJSReceiverConstant:
                BuildBranchOnValue(Opcode.BranchIfJSReceiver, GetAccumulator(), JumpTargetOffset(), jumpOnTrue: true);
                break;
            case Bytecode.JumpIfForInDone:
            case Bytecode.JumpIfForInDoneConstant:
            {
                // index == cache length (both Smis).
                ValueNode index = LoadRegister(1);
                ValueNode length = LoadRegister(2);
                var branch = new ControlNode(Opcode.BranchIfInt32Compare)
                {
                    Inputs = [GetInt32(index), GetInt32(length)],
                    Operation = CompareOperation.kEqual,
                };
                BuildBranch(branch, JumpTargetOffset(), jumpOnTrue: true);
                break;
            }
            case Bytecode.SwitchOnSmiNoFeedback:
                VisitSwitchOnSmiNoFeedback();
                break;

            // ---- for-in / for-of ---------------------------------------------------------------------------
            case Bytecode.ForInEnumerate:
                SetAccumulator(CallBaseline("ForInEnumerate", [LoadRegister(0)], [BuiltinArg.Isolate, BuiltinArg.In(0)])!);
                break;
            case Bytecode.ForInPrepare:
            {
                Register output = _it.GetRegisterOperand(0);
                ValueNode enumerator = GetAccumulator();
                WithLazyResult<ValueNode?>(output, 3, () =>
                {
                    CallBaseline("ForInPrepare", [enumerator],
                        [BuiltinArg.Isolate, Fv, BuiltinArg.I(FeedbackSlot(1)), BuiltinArg.In(0), BuiltinArg.RegRef(output),
                         BuiltinArg.RegRef(new Register(output.Index + 1)), BuiltinArg.RegRef(new Register(output.Index + 2))]);
                    return null!;
                });
                LoadRegisterOutputs(output, 3);
                // The cache length is a Smi.
                EnsureType(_frame.Get(new Register(output.Index + 2)), NodeType.kSmi);
                SetAccumulator(GetSmiConstant(0));
                break;
            }
            case Bytecode.ForInNext:
            {
                Register pair = _it.GetRegisterOperand(2);
                SetAccumulator(CallBaseline("ForInNext", [LoadRegister(0), LoadRegister(1), _frame.Get(pair),
                        _frame.Get(new Register(pair.Index + 1))],
                    [BuiltinArg.Isolate, Fv, BuiltinArg.I(FeedbackSlot(3)), BuiltinArg.In(0), BuiltinArg.In(1), BuiltinArg.In(2),
                     BuiltinArg.In(3)])!);
                break;
            }
            case Bytecode.ForInStep:
            {
                Register index = _it.GetRegisterOperand(0);
                ValueNode next = AddNewNode(new ValueNode(Opcode.Int32IncrementWithOverflow, ValueRepresentation.kInt32)
                {
                    Inputs = [GetInt32(_frame.Get(index))],
                    Properties = OpProperties.kEagerDeopt,
                }, DeoptimizeReason.kOverflow);
                StoreRegister(index, next);
                break;
            }
            case Bytecode.ForOfNext:
                SetAccumulator(CallBaseline("ForOfNext", [LoadRegister(0), LoadRegister(1)],
                    [BuiltinArg.Isolate, Fv, BuiltinArg.I(FeedbackSlot(2)), BuiltinArg.In(0), BuiltinArg.In(1)])!);
                break;
            case Bytecode.GetIterator:
                SetAccumulator(CallBaseline("GetIterator", [LoadRegister(0)],
                    [BuiltinArg.Isolate, Fv, BuiltinArg.I(FeedbackSlot(1)), BuiltinArg.I(FeedbackSlot(2)), BuiltinArg.In(0)])!);
                break;
            case Bytecode.ArrayDestructure:
            {
                Register first = _it.GetRegisterOperand(0);
                int count = RegisterCount(1);
                CallBaseline("ArrayDestructure", [GetAccumulator()],
                    [BuiltinArg.Isolate, BuiltinArg.In(0), BuiltinArg.RegIndex(first), BuiltinArg.I(count)]);
                LoadRegisterOutputs(first, count);
                SetAccumulator(GetRootConstant(RootIndex.kUndefinedValue));
                break;
            }

            // ---- Non-local control flow ---------------------------------------------------------------------
            case Bytecode.SetPendingMessage:
                SetAccumulator(CallBaseline("SetPendingMessage", [GetAccumulator()], [BuiltinArg.Isolate, BuiltinArg.In(0)],
                    properties: OpProperties.kCanWrite | OpProperties.kNotIdempotent)!);
                break;
            case Bytecode.Throw:
            case Bytecode.ReThrow:
                CallMaglev(bytecode == Bytecode.Throw ? "Throw" : "ReThrow", [GetAccumulator()],
                    [BuiltinArg.Isolate, BuiltinArg.In(0)], OpProperties.kCall | OpProperties.kCanThrow | OpProperties.kNotIdempotent);
                FinishBlock(new ControlNode(Opcode.Deopt) { Int1 = 1, Reason = DeoptimizeReason.kUnknown });
                break;
            case Bytecode.Return:
                VisitReturn();
                break;
            case Bytecode.ThrowReferenceErrorIfTdzHole:
            {
                ValueNode value = GetAccumulator();
                if (value.Opcode == Opcode.RootConstant && !value.ConstantValue().IsTheHole) break;
                if (value.Representation != ValueRepresentation.kTagged || CheckType(value, NodeType.kUnknown & ~NodeType.kOtherHeapObject)) break;
                CallBaseline("ThrowReferenceErrorIfHole", [value],
                    [BuiltinArg.Isolate, BuiltinArg.In(0), BuiltinArg.C(Constant(ConstantPoolIndex(0)))],
                    properties: OpProperties.kCanThrow | OpProperties.kNotIdempotent);
                break;
            }
            case Bytecode.ThrowSuperNotCalledIfTdzHole:
                CallBaseline("ThrowSuperNotCalledIfHole", [GetAccumulator()], [BuiltinArg.Isolate, BuiltinArg.In(0)],
                    properties: OpProperties.kCanThrow | OpProperties.kNotIdempotent);
                break;
            case Bytecode.ThrowSuperAlreadyCalledIfNotTdzHole:
                CallBaseline("ThrowSuperAlreadyCalledIfNotHole", [GetAccumulator()], [BuiltinArg.Isolate, BuiltinArg.In(0)],
                    properties: OpProperties.kCanThrow | OpProperties.kNotIdempotent);
                break;
            case Bytecode.ThrowIfNotSuperConstructor:
                CallBaseline("ThrowIfNotSuperConstructor", [ClosureNode, LoadRegister(0)],
                    [BuiltinArg.Isolate, BuiltinArg.In(0), BuiltinArg.In(1)], properties: OpProperties.kCanThrow | OpProperties.kNotIdempotent);
                break;

            // ---- Misc --------------------------------------------------------------------------------------
            case Bytecode.Debugger:
                // Runtime_HandleDebuggerStatement: no debugger is attached.
                SetAccumulator(GetRootConstant(RootIndex.kUndefinedValue));
                break;
            case Bytecode.IncBlockCounter:
                CallBaseline("IncBlockCounter", [ClosureNode],
                    [BuiltinArg.Isolate, BuiltinArg.In(0), BuiltinArg.I((int)_it.GetCoverageSlotOperand(0))],
                    properties: OpProperties.kCanWrite | OpProperties.kNotIdempotent);
                break;
            case Bytecode.Abort:
                CallBaseline("Abort", [], [BuiltinArg.Isolate, BuiltinArg.I(RawByteOperand(0))]);
                break;

            default:
                throw new MaglevBailoutException("unsupported bytecode " + bytecode);
        }
    }

    static ValueNode WithType(ValueNode node, NodeType type)
    {
        node.Type = type;
        return node;
    }

    /// <summary>The accumulator for ToObject (the bytecode reads the accumulator).</summary>
    ValueNode LoadRegisterAccumulatorForToObject() => GetAccumulator();

    /// <summary>Builtins that read the frame (arguments objects) run only in the outermost frame.</summary>
    void RequireOutermostFrame()
    {
        if (_unit.IsInline) throw new MaglevBailoutException("frame-reading bytecode in an inlined function");
    }

    /// <summary>Stores of the parameters into the frame (for builtins that read them from the frame).</summary>
    (Register, ValueNode)[] ParameterStores()
    {
        var stores = new (Register, ValueNode)[_unit.ParameterCount];
        for (int i = 0; i < stores.Length; i++)
        {
            Register r = Register.FromParameterIndex(i);
            stores[i] = (r, _frame.Get(r));
        }
        return stores;
    }

    /// <summary>The stores of a register list into the frame (the builtin reads it there).</summary>
    (Register, ValueNode)[] RegisterListStores(Register first, int count)
    {
        var stores = new (Register, ValueNode)[count];
        for (int i = 0; i < count; i++)
        {
            var r = new Register(first.Index + i);
            stores[i] = (r, _frame.Get(r));
        }
        return stores;
    }

    /// <summary>After a builtin wrote registers through references: their new values are read from the frame.</summary>
    void LoadRegisterOutputs(Register first, int count)
    {
        for (int i = 0; i < count; i++)
        {
            var r = new Register(first.Index + i);
            StoreRegister(r, AddNewNode(new ValueNode(Opcode.LoadRegister, ValueRepresentation.kTagged) { Int0 = r.Index }));
        }
    }

    /// <summary>LazyDeoptResultLocationScope: nodes built by <paramref name="build"/> lazily deopt with the result in <paramref name="location"/>.</summary>
    T WithLazyResult<T>(Register location, int size, Func<T> build)
    {
        Register savedLocation = _lazyResultLocation;
        int savedSize = _lazyResultSize;
        _lazyResultLocation = location;
        _lazyResultSize = size;
        try
        {
            return build();
        }
        finally
        {
            _lazyResultLocation = savedLocation;
            _lazyResultSize = savedSize;
        }
    }

    // ---- Contexts --------------------------------------------------------------------------------

    /// <summary>The context <paramref name="depth"/> levels up from <paramref name="context"/>, then slot <paramref name="index"/>.</summary>
    ValueNode BuildLoadContextSlot(ValueNode context, int depth, int index) =>
        AddNewNode(new ValueNode(Opcode.LoadContextSlot, ValueRepresentation.kTagged)
        {
            Inputs = [GetTaggedValue(context)],
            Int0 = index,
            Int1 = depth,
            Properties = OpProperties.kCanRead,
        });

    void BuildStoreContextSlot(ValueNode context, int depth, int index, ValueNode value) =>
        AddNewNode(new Node(Opcode.StoreContextSlot)
        {
            Inputs = [GetTaggedValue(context), GetTaggedValue(value)],
            Int0 = index,
            Int1 = depth,
            Properties = OpProperties.kCanWrite | OpProperties.kNotIdempotent,
        });

    void SetCurrentContext(ValueNode context)
    {
        context = GetTaggedValue(context);
        _frame.Context = context;
        AddNewNode(new Node(Opcode.SetCurrentContext) { Inputs = [context], Properties = OpProperties.kNotIdempotent });
    }

    // ---- Globals ------------------------------------------------------------------------------------

    void VisitLdaGlobal(bool insideTypeof)
    {
        int slot = FeedbackSlot(1);
        // BuildLoadGlobal / BuildStoreGlobal: JSHeapBroker::ReadFeedbackForGlobalAccess.
        if (new FeedbackNexus(Isolate, _unit.Feedback, slot).IcState() == InlineCacheState.UNINITIALIZED)
        {
            EmitUnconditionalDeopt(DeoptimizeReason.kInsufficientTypeFeedbackForGenericGlobalAccess);
            return;
        }
        JSValue feedback = _unit.Feedback.Slots[slot];
        if (feedback.HeapObjectOrNull is PropertyCell cell && cell.PropertyDetails.Kind == PropertyKind.Data)
        {
            JSValue value = cell.Value;
            if (!value.IsTheHole && !ReferenceEquals(value.HeapObjectOrNull, Oddball.PropertyCellHole))
            {
                PropertyCellType type = cell.PropertyDetails.CellType;
                _info.AddDependency(cell, Objects.DependentCode.DependencyGroups.PropertyCellChanged);
                if (type is PropertyCellType.Constant or PropertyCellType.Undefined && cell.PropertyDetails.IsReadOnly ||
                    type == PropertyCellType.Constant)
                {
                    SetAccumulator(GetConstant(value));
                    return;
                }
                ValueNode load = AddNewNode(new ValueNode(Opcode.LoadPropertyCellValue, ValueRepresentation.kTagged)
                {
                    Obj0 = cell,
                    Properties = OpProperties.kCanRead,
                });
                if (type == PropertyCellType.ConstantType)
                {
                    if (value.IsSmi) load.Type = NodeType.kSmi;
                    else if (value.HeapObjectOrNull is JSReceiver r && r.Map.IsStable) RecordKnownMaps(load, [r.Map]);
                }
                SetAccumulator(load);
                return;
            }
        }
        if (FeedbackVector.IsCleared(feedback) || feedback.IsNumber)
        {
            // Lexical variables (script context slots) and cleared feedback: generic.
        }
        SetAccumulator(CallBaseline(insideTypeof ? "LdaGlobalInsideTypeof" : "LdaGlobal", [_frame.Context],
            [BuiltinArg.Isolate, Fv, BuiltinArg.I(slot), BuiltinArg.In(0), BuiltinArg.C(Constant(ConstantPoolIndex(0)))])!);
    }

    void VisitStaGlobal()
    {
        int slot = FeedbackSlot(1);
        // BuildLoadGlobal / BuildStoreGlobal: JSHeapBroker::ReadFeedbackForGlobalAccess.
        if (new FeedbackNexus(Isolate, _unit.Feedback, slot).IcState() == InlineCacheState.UNINITIALIZED)
        {
            EmitUnconditionalDeopt(DeoptimizeReason.kInsufficientTypeFeedbackForGenericGlobalAccess);
            return;
        }
        JSValue feedback = _unit.Feedback.Slots[slot];
        ValueNode value = GetAccumulator();
        if (feedback.HeapObjectOrNull is PropertyCell cell && cell.PropertyDetails.Kind == PropertyKind.Data &&
            !cell.PropertyDetails.IsReadOnly)
        {
            PropertyCellType type = cell.PropertyDetails.CellType;
            if (type == PropertyCellType.Mutable || type == PropertyCellType.ConstantType && cell.Value.IsSmi)
            {
                _info.AddDependency(cell, Objects.DependentCode.DependencyGroups.PropertyCellChanged);
                if (type == PropertyCellType.ConstantType) BuildCheckSmi(value);
                AddNewNode(new Node(Opcode.StorePropertyCellValue)
                {
                    Inputs = [GetTaggedValue(value)],
                    Obj0 = cell,
                    Properties = OpProperties.kCanWrite | OpProperties.kNotIdempotent,
                });
                return;
            }
        }
        CallBaseline("StaGlobal", [_frame.Context, value],
            [BuiltinArg.Isolate, Fv, BuiltinArg.I(slot), BuiltinArg.In(0), BuiltinArg.C(Constant(ConstantPoolIndex(0))), BuiltinArg.In(1)]);
    }

    // ---- Arithmetic ------------------------------------------------------------------------------

    /// <summary>The binary operations (V8's Operation for the arithmetic bytecodes).</summary>
    enum Operation
    {
        Add,
        Subtract,
        Multiply,
        Divide,
        Modulus,
        Exponentiate,
        BitwiseOr,
        BitwiseXor,
        BitwiseAnd,
        ShiftLeft,
        ShiftRight,
        ShiftRightLogical,
        Increment,
        Decrement,
        Negate,
        BitwiseNot,
    }

    static bool IsBitwise(Operation op) => op is Operation.BitwiseOr or Operation.BitwiseXor or Operation.BitwiseAnd or
        Operation.ShiftLeft or Operation.ShiftRight or Operation.ShiftRightLogical;

    static NodeType BinopHintToAssumedInputType(BinaryOperationHint hint) => hint switch
    {
        BinaryOperationHint.kSignedSmall => NodeType.kSmi,
        BinaryOperationHint.kSignedSmallInputs or BinaryOperationHint.kAdditiveSafeInteger or BinaryOperationHint.kNumber => NodeType.kNumber,
        _ => NodeType.kNumberOrOddball,
    };

    /// <summary>VisitBinaryOperation.</summary>
    void VisitBinaryOperation(Operation op, string generic)
    {
        BinaryOperationHint hint = BinaryHint(1);
        switch (hint)
        {
            case BinaryOperationHint.kNone:
                EmitUnconditionalDeopt(DeoptimizeReason.kInsufficientTypeFeedbackForBinaryOperation);
                return;
            case BinaryOperationHint.kSignedSmall:
            case BinaryOperationHint.kSignedSmallInputs:
            case BinaryOperationHint.kAdditiveSafeInteger:
            case BinaryOperationHint.kNumber:
            case BinaryOperationHint.kNumberOrOddball:
            {
                NodeType assumed = BinopHintToAssumedInputType(hint);
                ValueNode left = LoadRegister(0);
                ValueNode right = GetAccumulator();
                if (IsBitwise(op))
                {
                    SetAccumulator(BuildTruncatingInt32BinaryOperation(op, GetTruncatedInt32ForToNumber(left, assumed),
                        GetTruncatedInt32ForToNumber(right, assumed)));
                    return;
                }
                if (hint == BinaryOperationHint.kSignedSmall && op != Operation.Exponentiate)
                {
                    SetAccumulator(BuildInt32BinaryOperation(op, GetInt32(left), GetInt32(right)));
                    return;
                }
                SetAccumulator(BuildFloat64BinaryOperation(op, GetFloat64(left, assumed), GetFloat64(right, assumed)));
                return;
            }
            case BinaryOperationHint.kString:
                if (op == Operation.Add)
                {
                    ValueNode left = LoadRegister(0);
                    ValueNode right = GetAccumulator();
                    BuildCheckString(left);
                    BuildCheckString(right);
                    ValueNode result = CallMaglev("StringAdd", [left, right], [BuiltinArg.Isolate, BuiltinArg.In(0), BuiltinArg.In(1)],
                        OpProperties.kCanAllocate | OpProperties.kCanThrow | OpProperties.kNotIdempotent, type: NodeType.kString)!;
                    SetAccumulator(result);
                    return;
                }
                break;
        }
        SetAccumulator(CallBaseline(generic, [LoadRegister(0), GetAccumulator()],
            [BuiltinArg.Isolate, BuiltinArg.In(0), BuiltinArg.In(1), BuiltinArg.FeedbackRef(EmbeddedFeedbackOffset(1))])!);
    }

    /// <summary>VisitBinarySmiOperation: the right operand is the Smi immediate.</summary>
    void VisitBinarySmiOperation(Operation op, string generic)
    {
        BinaryOperationHint hint = BinaryHint(1);
        int constant = Imm(0);
        switch (hint)
        {
            case BinaryOperationHint.kNone:
                EmitUnconditionalDeopt(DeoptimizeReason.kInsufficientTypeFeedbackForBinaryOperation);
                return;
            case BinaryOperationHint.kSignedSmall:
            case BinaryOperationHint.kSignedSmallInputs:
            case BinaryOperationHint.kAdditiveSafeInteger:
            case BinaryOperationHint.kNumber:
            case BinaryOperationHint.kNumberOrOddball:
            {
                NodeType assumed = BinopHintToAssumedInputType(hint);
                ValueNode left = GetAccumulator();
                if (IsBitwise(op))
                {
                    SetAccumulator(BuildTruncatingInt32BinaryOperation(op, GetTruncatedInt32ForToNumber(left, assumed),
                        GetInt32Constant(constant)));
                    return;
                }
                if (hint == BinaryOperationHint.kSignedSmall && op != Operation.Exponentiate)
                {
                    SetAccumulator(BuildInt32BinaryOperation(op, GetInt32(left), GetInt32Constant(constant)));
                    return;
                }
                SetAccumulator(BuildFloat64BinaryOperation(op, GetFloat64(left, assumed), GetFloat64Constant(constant)));
                return;
            }
        }
        SetAccumulator(CallBaseline(generic, [GetAccumulator(), GetSmiConstant(constant)],
            [BuiltinArg.Isolate, BuiltinArg.In(0), BuiltinArg.In(1), BuiltinArg.FeedbackRef(EmbeddedFeedbackOffset(1))])!);
    }

    /// <summary>VisitUnaryOperation.</summary>
    void VisitUnaryOperation(Operation op, string generic)
    {
        BinaryOperationHint hint = BinaryHint(0);
        ValueNode value = GetAccumulator();
        switch (hint)
        {
            case BinaryOperationHint.kNone:
                EmitUnconditionalDeopt(DeoptimizeReason.kInsufficientTypeFeedbackForUnaryOperation);
                return;
            case BinaryOperationHint.kSignedSmall:
            case BinaryOperationHint.kSignedSmallInputs:
            case BinaryOperationHint.kAdditiveSafeInteger:
            case BinaryOperationHint.kNumber:
            case BinaryOperationHint.kNumberOrOddball:
            {
                NodeType assumed = BinopHintToAssumedInputType(hint);
                if (op == Operation.BitwiseNot)
                {
                    SetAccumulator(AddNewNode(new ValueNode(Opcode.Int32BitwiseNot, ValueRepresentation.kInt32)
                    {
                        Inputs = [GetTruncatedInt32ForToNumber(value, assumed)],
                        Type = NodeType.kNumber,
                    }));
                    return;
                }
                if (hint == BinaryOperationHint.kSignedSmall)
                {
                    Opcode opcode = op switch
                    {
                        Operation.Increment => Opcode.Int32IncrementWithOverflow,
                        Operation.Decrement => Opcode.Int32DecrementWithOverflow,
                        _ => Opcode.Int32NegateWithOverflow,
                    };
                    ValueNode input = GetInt32(value);
                    if (input.IsConstant && input.TryGetInt32Constant(out int c))
                    {
                        long folded = op switch { Operation.Increment => (long)c + 1, Operation.Decrement => (long)c - 1, _ => -(long)c };
                        if (folded == (int)folded && !(op == Operation.Negate && c == 0))
                        {
                            SetAccumulator(GetInt32Constant((int)folded));
                            return;
                        }
                    }
                    SetAccumulator(AddNewNode(new ValueNode(opcode, ValueRepresentation.kInt32)
                    {
                        Inputs = [input],
                        Properties = OpProperties.kEagerDeopt,
                        Type = NodeType.kNumber,
                    }, op == Operation.Negate ? DeoptimizeReason.kMinusZero : DeoptimizeReason.kOverflow));
                    return;
                }
                ValueNode f = GetFloat64(value, assumed);
                ValueNode result = op switch
                {
                    Operation.Increment => Float64Binary(Opcode.Float64Add, f, GetFloat64Constant(1)),
                    Operation.Decrement => Float64Binary(Opcode.Float64Subtract, f, GetFloat64Constant(1)),
                    _ => AddNewNode(new ValueNode(Opcode.Float64Negate, ValueRepresentation.kFloat64) { Inputs = [f], Type = NodeType.kNumber }),
                };
                SetAccumulator(result);
                return;
            }
        }
        SetAccumulator(CallBaseline(generic, [value],
            [BuiltinArg.Isolate, BuiltinArg.In(0), BuiltinArg.FeedbackRef(EmbeddedFeedbackOffset(0))])!);
    }

    ValueNode Float64Binary(Opcode opcode, ValueNode left, ValueNode right)
    {
        if (left.Opcode == Opcode.Float64Constant && right.Opcode == Opcode.Float64Constant)
        {
            double l = left.Double0, r = right.Double0;
            double? folded = opcode switch
            {
                Opcode.Float64Add => l + r,
                Opcode.Float64Subtract => l - r,
                Opcode.Float64Multiply => l * r,
                Opcode.Float64Divide => l / r,
                _ => null,
            };
            if (folded is { } value) return GetFloat64Constant(value);
        }
        return AddNewNode(new ValueNode(opcode, ValueRepresentation.kFloat64) { Inputs = [left, right], Type = NodeType.kNumber });
    }

    /// <summary>BuildInt32BinaryOperationNode: Int32 arithmetic that deoptimizes on overflow, -0 or a fraction.</summary>
    ValueNode BuildInt32BinaryOperation(Operation op, ValueNode left, ValueNode right)
    {
        if (left.IsConstant && right.IsConstant && left.TryGetInt32Constant(out int l) && right.TryGetInt32Constant(out int r))
        {
            long? folded = op switch
            {
                Operation.Add => (long)l + r,
                Operation.Subtract => (long)l - r,
                Operation.Multiply when (long)l * r != 0 || (l >= 0 && r >= 0) => (long)l * r,
                _ => null,
            };
            if (folded is { } f && f == (int)f) return GetInt32Constant((int)f);
        }
        Opcode opcode = op switch
        {
            Operation.Add => Opcode.Int32AddWithOverflow,
            Operation.Subtract => Opcode.Int32SubtractWithOverflow,
            Operation.Multiply => Opcode.Int32MultiplyWithOverflow,
            Operation.Divide => Opcode.Int32DivideWithOverflow,
            Operation.Modulus => Opcode.Int32ModulusWithOverflow,
            _ => throw new InvalidOperationException(),
        };
        return AddNewNode(new ValueNode(opcode, ValueRepresentation.kInt32)
        {
            Inputs = [left, right],
            Properties = OpProperties.kEagerDeopt,
            Type = NodeType.kNumber,
        }, op is Operation.Divide or Operation.Modulus ? DeoptimizeReason.kNotInt32 : DeoptimizeReason.kOverflow);
    }

    /// <summary>BuildTruncatingInt32BinaryOperationNodeForToNumber: the bitwise operations on ToInt32 values.</summary>
    ValueNode BuildTruncatingInt32BinaryOperation(Operation op, ValueNode left, ValueNode right)
    {
        if (left.IsConstant && right.IsConstant && left.TryGetInt32Constant(out int l) && right.TryGetInt32Constant(out int r))
        {
            switch (op)
            {
                case Operation.BitwiseOr: return GetInt32Constant(l | r);
                case Operation.BitwiseXor: return GetInt32Constant(l ^ r);
                case Operation.BitwiseAnd: return GetInt32Constant(l & r);
                case Operation.ShiftLeft: return GetInt32Constant(l << (r & 31));
                case Operation.ShiftRight: return GetInt32Constant(l >> (r & 31));
            }
        }
        Opcode opcode = op switch
        {
            Operation.BitwiseOr => Opcode.Int32BitwiseOr,
            Operation.BitwiseXor => Opcode.Int32BitwiseXor,
            Operation.BitwiseAnd => Opcode.Int32BitwiseAnd,
            Operation.ShiftLeft => Opcode.Int32ShiftLeft,
            Operation.ShiftRight => Opcode.Int32ShiftRight,
            _ => Opcode.Int32ShiftRightLogical,
        };
        return AddNewNode(new ValueNode(opcode, opcode == Opcode.Int32ShiftRightLogical ? ValueRepresentation.kUint32 : ValueRepresentation.kInt32)
        {
            Inputs = [left, right],
            Type = NodeType.kNumber,
        });
    }

    /// <summary>BuildFloat64BinaryOperationNodeForToNumber.</summary>
    ValueNode BuildFloat64BinaryOperation(Operation op, ValueNode left, ValueNode right)
    {
        Opcode opcode = op switch
        {
            Operation.Add => Opcode.Float64Add,
            Operation.Subtract => Opcode.Float64Subtract,
            Operation.Multiply => Opcode.Float64Multiply,
            Operation.Divide => Opcode.Float64Divide,
            Operation.Modulus => Opcode.Float64Modulus,
            _ => Opcode.Float64Exponentiate,
        };
        return Float64Binary(opcode, left, right);
    }

    // ---- Comparisons -------------------------------------------------------------------------------

    /// <summary>VisitCompareOperation.</summary>
    void VisitCompareOperation(CompareOperation op, string generic)
    {
        ValueNode left = LoadRegister(0);
        ValueNode right = GetAccumulator();
        CompareOperationHint hint = CompareHint(1);
        bool isEquality = op is CompareOperation.kEqual or CompareOperation.kStrictEqual;
        if (_info.IsTracing) Console.WriteLine($"[maglev] compare {op} hint {hint} left {left} right {right} types {GetType(left)} {GetType(right)}");

        // TryReduceCompareEqualAgainstConstant: a strict comparison with an oddball/undefined constant is a reference compare.
        if (op == CompareOperation.kStrictEqual && (IsReferenceCompareConstant(left) || IsReferenceCompareConstant(right)))
        {
            SetAccumulator(BuildTaggedEqual(left, right));
            return;
        }
        if (isEquality && ReferenceEquals(left, right) && left.Representation == ValueRepresentation.kTagged &&
            !NodeTypes.CanBe(GetType(left), NodeType.kNumber))
        {
            SetAccumulator(GetBooleanConstant(true));
            return;
        }

        switch (hint)
        {
            case CompareOperationHint.kNone:
                EmitUnconditionalDeopt(DeoptimizeReason.kInsufficientTypeFeedbackForCompareOperation);
                return;
            case CompareOperationHint.kSignedSmall:
            {
                ValueNode l = GetInt32(left);
                ValueNode r = GetInt32(right);
                if (l.IsConstant && r.IsConstant && l.TryGetInt32Constant(out int lc) && r.TryGetInt32Constant(out int rc))
                {
                    SetAccumulator(GetBooleanConstant(EvaluateCompare(op, lc, rc)));
                    return;
                }
                SetAccumulator(AddNewNode(new ValueNode(Opcode.Int32Compare, ValueRepresentation.kTagged)
                {
                    Inputs = [l, r],
                    Int0 = (int)op,
                    Type = NodeType.kBoolean,
                }));
                return;
            }
            case CompareOperationHint.kNumberOrOddball when isEquality && (MaybeOddball(left) || MaybeOddball(right)):
                break;
            case CompareOperationHint.kNumberOrBoolean when op == CompareOperation.kStrictEqual && (MaybeOddball(left) || MaybeOddball(right)):
                break;
            case CompareOperationHint.kNumber:
            case CompareOperationHint.kNumberOrBoolean:
            case CompareOperationHint.kNumberOrOddball:
            {
                NodeType assumed = hint switch
                {
                    CompareOperationHint.kNumber => NodeType.kNumber,
                    CompareOperationHint.kNumberOrBoolean => NodeType.kNumberOrBoolean,
                    _ => NodeType.kNumberOrOddball,
                };
                if (left.IsInt32 && right.IsInt32)
                {
                    SetAccumulator(AddNewNode(new ValueNode(Opcode.Int32Compare, ValueRepresentation.kTagged)
                    {
                        Inputs = [left, right],
                        Int0 = (int)op,
                        Type = NodeType.kBoolean,
                    }));
                    return;
                }
                SetAccumulator(AddNewNode(new ValueNode(Opcode.Float64Compare, ValueRepresentation.kTagged)
                {
                    Inputs = [GetFloat64(left, assumed), GetFloat64(right, assumed)],
                    Int0 = (int)op,
                    Type = NodeType.kBoolean,
                }));
                return;
            }
            case CompareOperationHint.kInternalizedString when isEquality:
            {
                // Internalized strings are equal iff identical.
                BuildCheckInternalizedString(left);
                BuildCheckInternalizedString(right);
                SetAccumulator(BuildTaggedEqual(left, right));
                return;
            }
            case CompareOperationHint.kSymbol when isEquality:
                BuildCheckSymbol(left);
                BuildCheckSymbol(right);
                SetAccumulator(BuildTaggedEqual(left, right));
                return;
            case CompareOperationHint.kReceiver when op == CompareOperation.kStrictEqual || op == CompareOperation.kEqual:
                BuildCheckJSReceiver(left);
                BuildCheckJSReceiver(right);
                SetAccumulator(BuildTaggedEqual(left, right));
                return;
            case CompareOperationHint.kString:
            {
                BuildCheckString(left);
                BuildCheckString(right);
                ValueNode result = CallMaglev("StringCompare", [left, right],
                    [BuiltinArg.In(0), BuiltinArg.In(1), BuiltinArg.I((int)op)], OpProperties.kNone, type: NodeType.kBoolean)!;
                SetAccumulator(result);
                return;
            }
        }
        // The generic compare.
        if (op == CompareOperation.kStrictEqual)
        {
            SetAccumulator(WithType(CallBaseline("TestEqualStrict", [left, right],
                [BuiltinArg.In(0), BuiltinArg.In(1), BuiltinArg.FeedbackRef(EmbeddedFeedbackOffset(1))],
                properties: OpProperties.kNotIdempotent)!, NodeType.kBoolean));
            return;
        }
        SetAccumulator(WithType(CallBaseline(generic, [left, right],
            [BuiltinArg.Isolate, BuiltinArg.In(0), BuiltinArg.In(1), BuiltinArg.FeedbackRef(EmbeddedFeedbackOffset(1))])!, NodeType.kBoolean));
    }

    bool MaybeOddball(ValueNode value) => value.Representation == ValueRepresentation.kTagged && !CheckType(value, NodeType.kNumber);

    /// <summary>A constant that strict equality compares by identity (oddballs, receivers, symbols).</summary>
    static bool IsReferenceCompareConstant(ValueNode value) =>
        value.Opcode == Opcode.RootConstant ||
        value.Opcode == Opcode.Constant && value.Value0.HeapObjectOrNull is JSReceiver or Symbol;

    static bool EvaluateCompare(CompareOperation op, int l, int r) => op switch
    {
        CompareOperation.kEqual or CompareOperation.kStrictEqual => l == r,
        CompareOperation.kLessThan => l < r,
        CompareOperation.kLessThanOrEqual => l <= r,
        CompareOperation.kGreaterThan => l > r,
        _ => l >= r,
    };

    void BuildCheckInternalizedString(ValueNode value)
    {
        if (value.Representation != ValueRepresentation.kTagged) EmitUnconditionalDeoptAndAbort(DeoptimizeReason.kNotAString);
        if (CheckType(value, NodeType.kInternalizedString)) return;
        AddCheck(Opcode.CheckInstanceType, value, DeoptimizeReason.kWrongInstanceType, int0: 1);
        EnsureType(value, NodeType.kInternalizedString);
    }

    void BuildCheckJSReceiver(ValueNode value)
    {
        if (value.Representation != ValueRepresentation.kTagged) EmitUnconditionalDeoptAndAbort(DeoptimizeReason.kNotAJavaScriptObject);
        if (CheckType(value, NodeType.kJSReceiver)) return;
        AddCheck(Opcode.CheckInstanceType, value, DeoptimizeReason.kNotAJavaScriptObject, int0: 2);
        EnsureType(value, NodeType.kJSReceiver);
    }

    /// <summary>A reference comparison of two values (TaggedEqual), folded for constants.</summary>
    ValueNode BuildTaggedEqual(ValueNode left, ValueNode right)
    {
        if (left.IsConstant && right.IsConstant)
        {
            return GetBooleanConstant(left.ConstantValue().IsIdenticalTo(right.ConstantValue()));
        }
        if (ReferenceEquals(left, right) && left.Representation == ValueRepresentation.kTagged &&
            !NodeTypes.CanBe(GetType(left), NodeType.kNumber))
        {
            return GetBooleanConstant(true);
        }
        // A root constant cannot equal a value of a disjoint type.
        if (right.Opcode == Opcode.RootConstant && !NodeTypes.CanBe(GetType(left), right.Type) && left.Representation == ValueRepresentation.kTagged)
        {
            return GetBooleanConstant(false);
        }
        if (left.Representation != ValueRepresentation.kTagged && right.Opcode == Opcode.RootConstant) return GetBooleanConstant(false);
        if (right.Representation != ValueRepresentation.kTagged && left.Opcode == Opcode.RootConstant) return GetBooleanConstant(false);
        return AddNewNode(new ValueNode(Opcode.TaggedEqual, ValueRepresentation.kTagged)
        {
            Inputs = [GetTaggedValue(left), GetTaggedValue(right)],
            Type = NodeType.kBoolean,
        });
    }

    /// <summary>ToBoolean (BuildToBoolean), folded on known types.</summary>
    ValueNode BuildToBoolean(ValueNode value, bool negate)
    {
        if (CheckType(value, NodeType.kBoolean) && value.Representation == ValueRepresentation.kTagged)
        {
            if (!negate) return value;
            if (value.Opcode == Opcode.RootConstant) return GetBooleanConstant(!value.ConstantValue().IsTrue);
            return AddNewNode(new ValueNode(Opcode.LogicalNot, ValueRepresentation.kTagged) { Inputs = [value], Type = NodeType.kBoolean });
        }
        if (value.IsConstant)
        {
            return GetBooleanConstant(InterpreterOps.ToBoolean(value.ConstantValue()) != negate);
        }
        switch (value.Representation)
        {
            case ValueRepresentation.kInt32:
            case ValueRepresentation.kUint32:
                return AddNewNode(new ValueNode(Opcode.Int32ToBoolean, ValueRepresentation.kTagged)
                {
                    Inputs = [value],
                    Int0 = negate ? 1 : 0,
                    Type = NodeType.kBoolean,
                });
            case ValueRepresentation.kFloat64:
            case ValueRepresentation.kHoleyFloat64:
                return AddNewNode(new ValueNode(Opcode.Float64ToBoolean, ValueRepresentation.kTagged)
                {
                    Inputs = [value],
                    Int0 = negate ? 1 : 0,
                    Type = NodeType.kBoolean,
                });
        }
        return AddNewNode(new ValueNode(negate ? Opcode.ToBooleanLogicalNot : Opcode.ToBoolean, ValueRepresentation.kTagged)
        {
            Inputs = [value],
            Type = NodeType.kBoolean,
        });
    }

    void VisitToNumberOrNumeric(bool toNumber)
    {
        ValueNode value = GetAccumulator();
        if (value.Representation != ValueRepresentation.kTagged || CheckType(value, NodeType.kNumber)) return;
        BinaryOperationHint hint = BinaryHint(0);
        switch (hint)
        {
            case BinaryOperationHint.kNone:
                EmitUnconditionalDeopt(DeoptimizeReason.kInsufficientTypeFeedbackForUnaryOperation);
                return;
            case BinaryOperationHint.kSignedSmall:
            case BinaryOperationHint.kSignedSmallInputs:
            case BinaryOperationHint.kAdditiveSafeInteger:
            case BinaryOperationHint.kNumber:
                BuildCheckNumber(value);
                return;
        }
        SetAccumulator(CallBaseline(toNumber ? "ToNumber" : "ToNumeric", [value],
            [BuiltinArg.Isolate, Fv, BuiltinArg.I(FeedbackSlot(0)), BuiltinArg.In(0)])!);
    }

    // ---- Branches -----------------------------------------------------------------------------------

    /// <summary>JumpIfTrue / JumpIfFalse: the accumulator is a boolean.</summary>
    void BuildBranchIfTrue(ValueNode value, int jumpOffset, bool jumpOnTrue)
    {
        if (value.Opcode == Opcode.RootConstant)
        {
            bool taken = value.ConstantValue().IsTrue == jumpOnTrue;
            BuildUnconditionalBranch(taken ? jumpOffset : _it.NextOffset());
            return;
        }
        if (TryBuildFusedCompareBranch(value, jumpOffset, jumpOnTrue)) return;
        BuildBranchIfReferenceEqual(value, RootIndex.kTrueValue, jumpOffset, jumpOnTrue);
    }

    /// <summary>A compare node feeding a branch becomes the branch's condition (V8: BuildBranchIfInt32Compare ...).</summary>
    bool TryBuildFusedCompareBranch(ValueNode value, int jumpOffset, bool jumpOnTrue)
    {
        switch (value.Opcode)
        {
            case Opcode.Int32Compare:
                BuildBranch(new ControlNode(Opcode.BranchIfInt32Compare)
                {
                    Inputs = [value.Inputs[0], value.Inputs[1]],
                    Operation = (CompareOperation)value.Int0,
                }, jumpOffset, jumpOnTrue);
                return true;
            case Opcode.Float64Compare:
                BuildBranch(new ControlNode(Opcode.BranchIfFloat64Compare)
                {
                    Inputs = [value.Inputs[0], value.Inputs[1]],
                    Operation = (CompareOperation)value.Int0,
                }, jumpOffset, jumpOnTrue);
                return true;
            case Opcode.TaggedEqual:
                BuildBranch(new ControlNode(Opcode.BranchIfReferenceEqual) { Inputs = [value.Inputs[0], value.Inputs[1]] },
                    jumpOffset, jumpOnTrue);
                return true;
            case Opcode.LogicalNot:
            case Opcode.ToBooleanLogicalNot when CheckType(value.Inputs[0], NodeType.kBoolean):
                BuildBranchIfTrue(value.Inputs[0], jumpOffset, !jumpOnTrue);
                return true;
        }
        return false;
    }

    void BuildUnconditionalBranch(int target)
    {
        if (target == _it.NextOffset())
        {
            // Fall through: nothing to do (the next bytecode continues the block).
            return;
        }
        MergeIntoFrameStateFrom(target, _currentBlock!);
        FinishBlock(new ControlNode(Opcode.Jump) { Int0 = target });
    }

    void BuildBranchIfToBooleanTrue(ValueNode value, int jumpOffset, bool jumpOnTrue)
    {
        if (value.IsConstant)
        {
            bool taken = InterpreterOps.ToBoolean(value.ConstantValue()) == jumpOnTrue;
            BuildUnconditionalBranch(taken ? jumpOffset : _it.NextOffset());
            return;
        }
        if (CheckType(value, NodeType.kBoolean) && value.Representation == ValueRepresentation.kTagged)
        {
            BuildBranchIfTrue(value, jumpOffset, jumpOnTrue);
            return;
        }
        switch (value.Representation)
        {
            case ValueRepresentation.kInt32:
            case ValueRepresentation.kUint32:
                BuildBranch(new ControlNode(Opcode.BranchIfInt32ToBooleanTrue) { Inputs = [value] }, jumpOffset, jumpOnTrue);
                return;
            case ValueRepresentation.kFloat64:
            case ValueRepresentation.kHoleyFloat64:
                BuildBranch(new ControlNode(Opcode.BranchIfFloat64ToBooleanTrue) { Inputs = [value] }, jumpOffset, jumpOnTrue);
                return;
        }
        BuildBranch(new ControlNode(Opcode.BranchIfToBooleanTrue) { Inputs = [value] }, jumpOffset, jumpOnTrue);
    }

    void BuildBranchIfReferenceEqual(ValueNode value, RootIndex root, int jumpOffset, bool jumpOnTrue)
    {
        if (value.IsConstant)
        {
            bool equal = value.ConstantValue().IsIdenticalTo(ValueNode.RootValue(root));
            BuildUnconditionalBranch(equal == jumpOnTrue ? jumpOffset : _it.NextOffset());
            return;
        }
        if (value.Representation != ValueRepresentation.kTagged || !NodeTypes.CanBe(GetType(value), NodeTypes.ForConstant(ValueNode.RootValue(root))))
        {
            // The value cannot be the root.
            BuildUnconditionalBranch(!jumpOnTrue ? jumpOffset : _it.NextOffset());
            return;
        }
        BuildBranch(new ControlNode(Opcode.BranchIfRootConstant) { Inputs = [value], Int1 = (int)root }, jumpOffset, jumpOnTrue);
    }

    void BuildBranchOnValue(Opcode opcode, ValueNode value, int jumpOffset, bool jumpOnTrue)
    {
        value = GetTaggedValue(value);
        if (opcode == Opcode.BranchIfJSReceiver && CheckType(value, NodeType.kJSReceiver))
        {
            BuildUnconditionalBranch(jumpOnTrue ? jumpOffset : _it.NextOffset());
            return;
        }
        if (opcode == Opcode.BranchIfUndefinedOrNull && !NodeTypes.CanBe(GetType(value), NodeType.kNullOrUndefined))
        {
            BuildUnconditionalBranch(!jumpOnTrue ? jumpOffset : _it.NextOffset());
            return;
        }
        BuildBranch(new ControlNode(opcode) { Inputs = [value] }, jumpOffset, jumpOnTrue);
    }

    /// <summary>VisitSwitchOnSmiNoFeedback.</summary>
    void VisitSwitchOnSmiNoFeedback()
    {
        int caseValueBase = Imm(2);
        int tableLength = Uint(1);
        List<(int CaseValue, int Target)> targets = BytecodeAnalysis.JumpTableTargets(_it, _constants);
        int next = _it.NextOffset();
        var offsets = new int[tableLength + 1];
        Array.Fill(offsets, next);
        foreach ((int caseValue, int target) in targets) offsets[caseValue - caseValueBase] = target;
        ValueNode index = GetInt32(GetAccumulator());
        BasicBlock block = _currentBlock!;
        var merged = new HashSet<int>();
        foreach (int target in offsets)
        {
            if (merged.Add(target)) MergeIntoFrameStateFrom(target, block);
        }
        var control = new ControlNode(Opcode.Switch)
        {
            Inputs = [index],
            Int0 = caseValueBase,
            Targets = new BasicBlock?[tableLength + 1],
            Obj1 = offsets,
        };
        FinishBlock(control);
    }

    /// <summary>VisitJumpLoop: the interrupt check and the back edge into the loop header.</summary>
    void VisitJumpLoop()
    {
        int header = JumpTargetOffset();
        if (_info.IsOsr && !_unit.IsInline && header < _analysis.OsrEntryPoint)
        {
            // The back edge of a loop around the OSR'd one (VisitSingleBytecode):
            // loops must be entered through their header, which this code does
            // not have, so it exits to the interpreter.
            EmitUnconditionalDeopt(DeoptimizeReason.kOSREarlyExit);
            return;
        }
        MergePointInterpreterFrameState? state = _mergeStates[header];
        if (state is null || !state.IsLoop) throw new MaglevBailoutException($"loop without header (JumpLoop at {_it.CurrentOffset()} to {header}, state {(state is null ? "none" : "not a loop")})");
        // HandleNoHeapWritesInterrupt (V8's loop interrupt check; there is no Turbofan to count budget for).
        AddNewNode(new Node(Opcode.HandleNoHeapWritesInterrupt) { Properties = OpProperties.kCanThrow | OpProperties.kNotIdempotent });
        // JumpLoop clobbers the accumulator.
        SetAccumulator(GetRootConstant(RootIndex.kUndefinedValue));
        BasicBlock block = _currentBlock!;
        state.MergeLoop(this, _frame, block);
        state.Block!.Predecessors.Add(block);
        FinishBlock(new ControlNode(Opcode.JumpLoop) { Target = state.Block });
    }

    /// <summary>VisitReturn.</summary>
    void VisitReturn()
    {
        ValueNode value = GetTaggedValue(GetAccumulator());
        if (_unit.IsInline)
        {
            // An inlined function's return goes to the continuation in the caller.
            BasicBlock block = _currentBlock!;
            _inlinedReturns.Add((block, value, _frame.Known.Clone()));
            FinishBlock(new ControlNode(Opcode.Jump) { Int1 = -1 });
            return;
        }
        FinishBlock(new ControlNode(Opcode.Return) { Inputs = [value] });
    }
}
