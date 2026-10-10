// Port of test/unittests/interpreter/bytecode-array-builder-unittest.cc.
//
// AST objects are stand-ins: AstRawStrings are C# strings (literals are
// interned, as the AstValueFactory interns), Scopes are plain objects, and a
// Variable is the ContextSlotVariable the builder reads from it. The script
// scope and the first function scope have context cells (the V8 test sets
// --script-context-cells and --function-context-cells).
using V8Sharp.Ast;
using V8Sharp.Common;
using V8Sharp.Parsing;
using V8Sharp.Runtime;
using V8Sharp.Codegen;
using V8Sharp.Interpreter;
using ToBooleanMode = V8Sharp.Interpreter.BytecodeArrayBuilder.ToBooleanMode;


namespace V8Sharp.Tests.Interpreter;

public class BytecodeArrayBuilderUnitTest
{
    const int kFeedbackIsEmbedded = InterpreterConstants.kFeedbackIsEmbedded;
    const int kSystemPointerSize = 8;

    sealed class FeedbackSpec
    {
        int _slots;
        public int AddSlot() => _slots++;
    }

    [Fact]
    public void BytecodeArrayBuilderTest_AllBytecodesGenerated()
    {
        var feedback_spec = new FeedbackSpec();
        var builder = new BytecodeArrayBuilder(1, 131);
        object scope = new(); // A script scope with context cells.

        Assert.Equal(131, builder.LocalsCount());
        Assert.Equal(131, builder.FixedRegisterCount());

        var reg = new Register(0);
        var other = new Register(reg.Index + 1);
        var wide = new Register(128);
        RegisterList empty = RegisterList.Empty;
        RegisterList single = BytecodeUtils.NewRegisterList(0, 1);
        RegisterList pair = BytecodeUtils.NewRegisterList(0, 2);
        RegisterList triple = BytecodeUtils.NewRegisterList(0, 3);
        RegisterList reg_list = BytecodeUtils.NewRegisterList(0, 10);

        // Emit argument creation operations.
        builder.CreateArguments(CreateArgumentsType.kMappedArguments)
            .CreateArguments(CreateArgumentsType.kUnmappedArguments)
            .CreateArguments(CreateArgumentsType.kRestParameter);

        // Emit constant loads.
        builder.LoadLiteral(Smi.Zero)
            .StoreAccumulatorInRegister(reg)
            .LoadLiteral(Smi.FromInt(8))
            .CompareOperation(Token.Eq, reg, kFeedbackIsEmbedded) // Prevent peephole optimization
                                                                         // LdaSmi, Star -> LdrSmi.
            .StoreAccumulatorInRegister(reg)
            .LoadLiteral(Smi.FromInt(10000000))
            .StoreAccumulatorInRegister(reg)
            .LoadLiteralRawString("A constant")
            .StoreAccumulatorInRegister(reg)
            .LoadUndefined()
            .StoreAccumulatorInRegister(reg)
            .LoadNull()
            .StoreAccumulatorInRegister(reg)
            .LoadTheHole()
            .StoreAccumulatorInRegister(reg)
            .LoadTdzHole()
            .StoreAccumulatorInRegister(reg)
            .LoadTrue()
            .StoreAccumulatorInRegister(reg)
            .LoadFalse()
            .StoreAccumulatorInRegister(wide);

        // Emit Ldar and Star taking care to foil the register optimizer.
        builder.LoadAccumulatorWithRegister(other)
            .BinaryOperation(Token.Add, reg, kFeedbackIsEmbedded)
            .StoreAccumulatorInRegister(reg)
            .LoadNull();

        // The above had a lot of Star0, but we must also emit the rest of
        // the short-star codes.
        for (int i = 1; i < 16; ++i) builder.StoreAccumulatorInRegister(new Register(i));

        // Emit register-register transfer.
        builder.MoveRegister(reg, other);
        builder.MoveRegister(reg, wide);

        int load_global_slot = feedback_spec.AddSlot();
        int load_global_typeof_slot = feedback_spec.AddSlot();
        int sloppy_store_global_slot = feedback_spec.AddSlot();
        int load_slot = feedback_spec.AddSlot();
        int call_slot = feedback_spec.AddSlot();
        int keyed_load_slot = feedback_spec.AddSlot();
        int sloppy_store_slot = feedback_spec.AddSlot();
        int strict_store_slot = feedback_spec.AddSlot();
        int sloppy_keyed_store_slot = feedback_spec.AddSlot();
        int strict_keyed_store_slot = feedback_spec.AddSlot();
        int define_named_own_slot = feedback_spec.AddSlot();
        int store_array_element_slot = feedback_spec.AddSlot();

        // Emit global load / store operations.
        const string name = "var_name";
        builder.LoadGlobal(name, load_global_slot, TypeofMode.NotInside)
            .LoadGlobal(name, load_global_typeof_slot, TypeofMode.Inside)
            .StoreGlobal(name, sloppy_store_global_slot);

        // Emit context operations.
        var var1 = new ContextSlotVariable(1, MaybeAssignedFlag.kMaybeAssigned, ScopeHasContextCells: true);
        var var2 = new ContextSlotVariable(1, MaybeAssignedFlag.kNotAssigned, ScopeHasContextCells: true);
        var var3 = new ContextSlotVariable(3, MaybeAssignedFlag.kNotAssigned, ScopeHasContextCells: true);

        // Emit context operations which operate on the script context.
        builder.PushContext(reg)
            .PopContext(reg)
            .LoadContextSlot(reg, var1, 0)
            .StoreContextSlot(reg, var1, 0)
            .LoadContextSlot(reg, var2, 0)
            .StoreContextSlot(reg, var3, 0);

        // Emit context operations which operate on the local context.
        builder.LoadContextSlot(Register.CurrentContext(), var1, 0)
            .StoreContextSlot(Register.CurrentContext(), var1, 0)
            .LoadContextSlot(Register.CurrentContext(), var2, 0)
            .StoreContextSlot(Register.CurrentContext(), var3, 0)
            .LoadContextSlot(Register.CurrentContext(), var1, 0);

        // Emit context operations.
        object fun_scope = new();
        var fun_var1 = new ContextSlotVariable(1, MaybeAssignedFlag.kMaybeAssigned, ScopeHasContextCells: true);
        var fun_var2 = new ContextSlotVariable(1, MaybeAssignedFlag.kNotAssigned, ScopeHasContextCells: true);
        var fun_var3 = new ContextSlotVariable(3, MaybeAssignedFlag.kNotAssigned, ScopeHasContextCells: true);
        builder.CreateFunctionContext(fun_scope, 3, scopeHasContextCells: true)
            .StoreAccumulatorInRegister(reg)
            .LoadContextSlot(reg, fun_var1, 0)
            .StoreContextSlot(reg, fun_var1, 0)
            .LoadContextSlot(reg, fun_var2, 0)
            .StoreContextSlot(reg, fun_var3, 0)
            .PushContext(reg)
            .LoadContextSlot(Register.CurrentContext(), fun_var1, 0)
            .StoreContextSlot(Register.CurrentContext(), fun_var1, 0)
            .LoadContextSlot(Register.CurrentContext(), fun_var2, 0)
            .StoreContextSlot(Register.CurrentContext(), fun_var3, 0)
            .PopContext(reg);

        object fun_scope2 = new(); // No context cells.
        var fun2_var1 = new ContextSlotVariable(1, MaybeAssignedFlag.kMaybeAssigned, ScopeHasContextCells: false);
        builder.CreateFunctionContext(fun_scope2, 1, scopeHasContextCells: false)
            .StoreAccumulatorInRegister(reg)
            .LoadContextSlot(reg, fun2_var1, 0)
            .PushContext(reg)
            .LoadContextSlot(Register.CurrentContext(), fun2_var1, 0)
            .PopContext(reg);

        // (V8 checks DeclarationScope::is_hoisted_in_context here; that is scope
        // analysis, not the builder, and is tested with the parser port.)

        // Emit load / store property operations.
        builder.LoadNamedProperty(reg, name, load_slot)
            .LoadNamedPropertyFromSuper(reg, name, load_slot)
            .LoadKeyedProperty(reg, keyed_load_slot)
            .LoadEnumeratedKeyedProperty(reg, reg, reg, keyed_load_slot)
            .SetNamedProperty(reg, name, sloppy_store_slot, LanguageMode.Sloppy)
            .SetKeyedProperty(reg, reg, sloppy_keyed_store_slot, LanguageMode.Sloppy)
            .SetNamedProperty(reg, name, strict_store_slot, LanguageMode.Strict)
            .SetKeyedProperty(reg, reg, strict_keyed_store_slot, LanguageMode.Strict)
            .DefineNamedOwnProperty(reg, name, define_named_own_slot)
            .DefineKeyedOwnProperty(reg, reg, DefineKeyedOwnPropertyFlags.NoFlags, define_named_own_slot)
            .StoreInArrayLiteral(reg, reg, store_array_element_slot)
            .GetPrivateField(reg, 0, 0, reg, 0)
            .SetPrivateField(reg, 0, 0, reg, 0);

        // Emit Iterator-protocol operations
        builder.GetIterator(reg, load_slot, call_slot);
        builder.ForOfNext(reg, reg, 1);
        builder.ArrayDestructure(pair, 2);

        // Emit load / store lookup slots.
        builder.LoadLookupSlot(name, TypeofMode.NotInside)
            .LoadLookupSlot(name, TypeofMode.Inside)
            .StoreLookupSlot(name, LanguageMode.Sloppy, LookupHoistingMode.kNormal)
            .StoreLookupSlot(name, LanguageMode.Sloppy, LookupHoistingMode.kLegacySloppy)
            .StoreLookupSlot(name, LanguageMode.Strict, LookupHoistingMode.kNormal);

        // Emit load / store lookup slots with context fast paths.
        builder.LoadLookupContextSlot(name, TypeofMode.NotInside, ContextMode.NoContextCells, 1, 0)
            .LoadLookupContextSlot(name, TypeofMode.Inside, ContextMode.NoContextCells, 1, 0)
            .LoadLookupContextSlot(name, TypeofMode.NotInside, ContextMode.HasContextCells, 1, 0)
            .LoadLookupContextSlot(name, TypeofMode.Inside, ContextMode.HasContextCells, 1, 0);

        // Emit load / store lookup slots with global fast paths.
        builder.LoadLookupGlobalSlot(name, TypeofMode.NotInside, 1, 0)
            .LoadLookupGlobalSlot(name, TypeofMode.Inside, 1, 0);

        // Emit closure operations (AllocationType::kYoung == 0).
        builder.CreateClosure(0, 1, 0);

        // Emit create context operation.
        builder.CreateBlockContext(scope);
        builder.CreateCatchContext(reg, scope);
        builder.CreateFunctionContext(scope, 1, scopeHasContextCells: true);
        builder.CreateEvalContext(scope, 1);
        builder.CreateWithContext(reg, scope);

        // Emit literal creation operations.
        builder.CreateRegExpLiteral("a", 0, 0);
        builder.CreateArrayLiteral(0, 0, 0);
        builder.CreateObjectLiteral(0, 0, 0);

        // Emit tagged template operations.
        builder.GetTemplateObject(0, 0);

        // Call operations.
        builder.CallAnyReceiver(reg, reg_list, 1)
            .CallProperty(reg, reg_list, 1)
            .CallProperty(reg, single, 1)
            .CallProperty(reg, pair, 1)
            .CallProperty(reg, triple, 1)
            .CallUndefinedReceiver(reg, reg_list, 1)
            .CallUndefinedReceiver(reg, empty, 1)
            .CallUndefinedReceiver(reg, single, 1)
            .CallUndefinedReceiver(reg, pair, 1)
            .CallRuntime(FunctionId.IsArray, reg)
            .CallRuntimeForPair(FunctionId.LoadLookupSlotForCall, reg_list, pair)
            .CallJSRuntime(NativeContextFields.PROMISE_THEN_INDEX, reg_list)
            .CallWithSpread(reg, reg_list, 1);

        // Emit binary operator invocations.
        builder.BinaryOperation(Token.Add, reg, kFeedbackIsEmbedded)
            .BinaryOperation(Token.Sub, reg, kFeedbackIsEmbedded)
            .BinaryOperation(Token.Mul, reg, kFeedbackIsEmbedded)
            .BinaryOperation(Token.Div, reg, kFeedbackIsEmbedded)
            .BinaryOperation(Token.Mod, reg, kFeedbackIsEmbedded)
            .BinaryOperation(Token.Exp, reg, kFeedbackIsEmbedded);

        builder.Add_StringConstant_Internalize(Token.Add, reg, 1,
                                               AddStringConstantAndInternalizeVariant.LhsIsStringConstant);

        // Emit bitwise operator invocations
        builder.BinaryOperation(Token.BitOr, reg, kFeedbackIsEmbedded)
            .BinaryOperation(Token.BitXor, reg, kFeedbackIsEmbedded)
            .BinaryOperation(Token.BitAnd, reg, kFeedbackIsEmbedded);

        // Emit shift operator invocations
        builder.BinaryOperation(Token.Shl, reg, kFeedbackIsEmbedded)
            .BinaryOperation(Token.Sar, reg, kFeedbackIsEmbedded)
            .BinaryOperation(Token.Shr, reg, kFeedbackIsEmbedded);

        // Emit Smi binary operations.
        foreach (Token op in new[]
                 {
                     Token.Add, Token.Sub, Token.Mul, Token.Div, Token.Mod,
                     Token.Exp, Token.BitOr, Token.BitXor, Token.BitAnd, Token.Shl,
                     Token.Sar, Token.Shr,
                 })
        {
            builder.BinaryOperationSmiLiteral(op, Smi.FromInt(42), kFeedbackIsEmbedded);
        }

        // Emit unary and count operator invocations.
        builder.UnaryOperation(Token.Inc, kFeedbackIsEmbedded)
            .UnaryOperation(Token.Dec, kFeedbackIsEmbedded)
            .UnaryOperation(Token.Add, 1)
            .UnaryOperation(Token.Sub, kFeedbackIsEmbedded)
            .UnaryOperation(Token.BitNot, kFeedbackIsEmbedded);

        // Emit unary operator invocations.
        builder.LogicalNot(ToBooleanMode.ConvertToBoolean)
            .LogicalNot(ToBooleanMode.AlreadyBoolean)
            .TypeOf(1);

        // Emit delete
        builder.Delete(reg, LanguageMode.Sloppy).Delete(reg, LanguageMode.Strict);

        // Emit construct.
        builder.Construct(reg, reg_list, 1)
            .ConstructWithSpread(reg, reg_list, 1)
            .ConstructForwardAllArgs(reg, 1);

        // Emit test operator invocations.
        builder.CompareOperation(Token.Eq, reg, kFeedbackIsEmbedded)
            .CompareOperation(Token.EqStrict, reg, kFeedbackIsEmbedded)
            .CompareOperation(Token.LessThan, reg, kFeedbackIsEmbedded)
            .CompareOperation(Token.GreaterThan, reg, kFeedbackIsEmbedded)
            .CompareOperation(Token.LessThanEq, reg, kFeedbackIsEmbedded)
            .CompareOperation(Token.GreaterThanEq, reg, kFeedbackIsEmbedded)
            .CompareTypeOf(TestTypeOfFlags.LiteralFlag.Number)
            .CompareOperation(Token.InstanceOf, reg, 2)
            .CompareOperation(Token.In, reg, 3)
            .CompareReference(reg)
            .CompareUndetectable()
            .CompareUndefined()
            .CompareNull();

        // Emit conversion operator invocations.
        builder.ToNumber(1).ToNumeric(1).ToObject(reg).ToName().ToString().ToBoolean(ToBooleanMode.ConvertToBoolean);

        // Emit GetSuperConstructor.
        builder.GetSuperConstructor(reg);

        // Constructor check for GetSuperConstructor.
        builder.ThrowIfNotSuperConstructor(reg);

        // Hole checks.
        builder.ThrowReferenceErrorIfTdzHole(name)
            .ThrowSuperAlreadyCalledIfNotTdzHole()
            .ThrowSuperNotCalledIfTdzHole();

        // Short jumps with Imm8 operands
        {
            var loop_header = new BytecodeLoopHeader();
            var after_jump = new BytecodeLabel[12];
            for (int i = 0; i < after_jump.Length; i++) after_jump[i] = new BytecodeLabel();
            var after_loop = new BytecodeLabel();
            builder.JumpIfNull(after_loop)
                .Bind(loop_header)
                .Jump(after_jump[0])
                .Bind(after_jump[0])
                .JumpIfNull(after_jump[1])
                .Bind(after_jump[1])
                .JumpIfNotNull(after_jump[2])
                .Bind(after_jump[2])
                .JumpIfUndefined(after_jump[3])
                .Bind(after_jump[3])
                .JumpIfNotUndefined(after_jump[4])
                .Bind(after_jump[4])
                .JumpIfUndefinedOrNull(after_jump[5])
                .Bind(after_jump[5])
                .JumpIfJSReceiver(after_jump[6])
                .Bind(after_jump[6])
                .JumpIfForInDone(after_jump[7], reg, reg)
                .Bind(after_jump[7])
                .JumpIfTrue(ToBooleanMode.ConvertToBoolean, after_jump[8])
                .Bind(after_jump[8])
                .JumpIfTrue(ToBooleanMode.AlreadyBoolean, after_jump[9])
                .Bind(after_jump[9])
                .JumpIfFalse(ToBooleanMode.ConvertToBoolean, after_jump[10])
                .Bind(after_jump[10])
                .JumpIfFalse(ToBooleanMode.AlreadyBoolean, after_jump[11])
                .Bind(after_jump[11])
                .JumpLoop(loop_header, 0, 0, 0)
                .Bind(after_loop);
        }

        var end = new BytecodeLabel[12];
        for (int i = 0; i < end.Length; i++) end[i] = new BytecodeLabel();
        {
            // Longer jumps with constant operands
            var after_jump = new BytecodeLabel();
            builder.JumpIfNull(after_jump)
                .Jump(end[0])
                .Bind(after_jump)
                .JumpIfTrue(ToBooleanMode.ConvertToBoolean, end[1])
                .JumpIfTrue(ToBooleanMode.AlreadyBoolean, end[2])
                .JumpIfFalse(ToBooleanMode.ConvertToBoolean, end[3])
                .JumpIfFalse(ToBooleanMode.AlreadyBoolean, end[4])
                .JumpIfNull(end[5])
                .JumpIfNotNull(end[6])
                .JumpIfUndefined(end[7])
                .JumpIfNotUndefined(end[8])
                .JumpIfUndefinedOrNull(end[9])
                .LoadLiteralRawString("prototype")
                .JumpIfJSReceiver(end[10])
                .JumpIfForInDone(end[11], reg, reg);
        }

        // Emit Smi table switch bytecode.
        BytecodeJumpTable jump_table = builder.AllocateJumpTable(1, 0);
        builder.SwitchOnSmiNoFeedback(jump_table).Bind(jump_table, 0);

        // Emit set pending message bytecode.
        builder.SetPendingMessage();

        // Emit throw and re-throw in it's own basic block so that the rest of the
        // code isn't omitted due to being dead.
        var after_throw = new BytecodeLabel();
        var after_rethrow = new BytecodeLabel();
        builder.JumpIfNull(after_throw).Throw().Bind(after_throw);
        builder.JumpIfNull(after_rethrow).ReThrow().Bind(after_rethrow);

        builder.ForInEnumerate(reg)
            .ForInPrepare(triple, 1)
            .ForInNext(reg, reg, pair, 1)
            .ForInStep(reg);

        // Wide constant pool loads
        for (int i = 0; i < 1024; i++)
        {
            // Emit junk in constant pool to force wide constant pool index.
            builder.LoadLiteral(2.5321 + i);
        }
        builder.LoadLiteral(Smi.FromInt(20000000));
        const string wide_name = "var_wide_name";

        builder.DefineKeyedOwnPropertyInLiteral(reg, reg, DefineKeyedOwnPropertyInLiteralFlags.NoFlags, 0);

        // Emit wide context operations.
        var var = new ContextSlotVariable(1024, MaybeAssignedFlag.kMaybeAssigned, ScopeHasContextCells: true);
        builder.LoadContextSlot(reg, var, 0).StoreContextSlot(reg, var, 0);

        // Emit wide load / store lookup slots.
        builder.LoadLookupSlot(wide_name, TypeofMode.NotInside)
            .LoadLookupSlot(wide_name, TypeofMode.Inside)
            .StoreLookupSlot(wide_name, LanguageMode.Sloppy, LookupHoistingMode.kNormal)
            .StoreLookupSlot(wide_name, LanguageMode.Sloppy, LookupHoistingMode.kLegacySloppy)
            .StoreLookupSlot(wide_name, LanguageMode.Strict, LookupHoistingMode.kNormal);

        // CreateClosureWide
        builder.CreateClosure(1000, 321, 0);

        // Emit wide variant of literal creation operations.
        builder.CreateRegExpLiteral("wide_literal", 0, 0)
            .CreateArrayLiteral(0, 0, 0)
            .CreateEmptyArrayLiteral(0)
            .CreateArrayFromIterable()
            .CreateObjectLiteral(0, 0, 0)
            .CreateEmptyObjectLiteral()
            .CloneObject(reg, 0, 0);

        // Emit load and store operations for module variables.
        builder.LoadModuleVariable(-1, 42)
            .LoadModuleVariable(0, 42)
            .LoadModuleVariable(1, 42)
            .StoreModuleVariable(-1, 42)
            .StoreModuleVariable(0, 42)
            .StoreModuleVariable(1, 42);

        // Emit generator operations.
        {
            // We have to skip over suspend because it returns and marks the remaining
            // bytecode dead.
            var after_suspend = new BytecodeLabel();
            builder.JumpIfTrue(ToBooleanMode.AlreadyBoolean, after_suspend)
                .SuspendGenerator(reg, reg_list, 0)
                .Bind(after_suspend)
                .ResumeGenerator(reg, reg_list);
        }
        BytecodeJumpTable gen_jump_table = builder.AllocateJumpTable(1, 0);
        builder.SwitchOnGeneratorState(reg, gen_jump_table).Bind(gen_jump_table, 0);

        // Intrinsics handled by the interpreter.
        builder.CallRuntime(FunctionId.InlineAsyncFunctionReject, reg_list);

        // Emit debugger bytecode.
        builder.Debugger();

        // Emit SetPrototypeProperties bytecode.
        builder.SetPrototypeProperties(0, 0);

        // Emit abort bytecode.
        var after_abort = new BytecodeLabel();
        builder.JumpIfNull(after_abort).Abort(AbortReason.OperandIsASmi).Bind(after_abort);

        // Insert dummy ops to force longer jumps.
        for (int i = 0; i < 256; i++) builder.Debugger();

        // Emit block counter increments.
        builder.IncBlockCounter(0);

        // Bind labels for long jumps at the very end.
        foreach (BytecodeLabel label in end) builder.Bind(label);

        // Return must be the last instruction.
        builder.Return();

        // Generate BytecodeArray.
        BytecodeArray the_array = builder.ToBytecodeArray();
        Assert.Equal(builder.TotalRegisterCount() * kSystemPointerSize, the_array.FrameSize);

        // Build scorecard of bytecodes encountered in the BytecodeArray.
        var scorecard = new int[Bytecodes.ToByte(Bytecodes.kLast) + 1];

        Bytecode final_bytecode = Bytecode.LdaZero;
        int pos = 0;
        while (pos < the_array.Length)
        {
            byte code = the_array.Get(pos);
            scorecard[code] += 1;
            final_bytecode = Bytecodes.FromByte(code);
            OperandScale operand_scale = OperandScale.Single;
            int prefix_offset = 0;
            if (Bytecodes.IsPrefixScalingBytecode(final_bytecode))
            {
                operand_scale = Bytecodes.PrefixBytecodeToOperandScale(final_bytecode);
                prefix_offset = 1;
                code = the_array.Get(pos + 1);
                scorecard[code] += 1;
                final_bytecode = Bytecodes.FromByte(code);
            }
            pos += prefix_offset + Bytecodes.Size(final_bytecode, operand_scale);
        }

        // Insert entry for illegal bytecode as this is never willingly emitted.
        scorecard[Bytecodes.ToByte(Bytecode.Illegal)] = 1;

        // This bytecode is too inconvenient to test manually.
        scorecard[Bytecodes.ToByte(Bytecode.FindNonDefaultConstructorOrConstruct)] = 1;

        // Check return occurs at the end and only once in the BytecodeArray.
        Assert.Equal(Bytecode.Return, final_bytecode);
        Assert.Equal(1, scorecard[Bytecodes.ToByte(final_bytecode)]);

        for (int b = 0; b < Bytecodes.kBytecodeCount; b++)
        {
            // Check Bytecode is marked in scorecard, unless it's a debug break
            if (!Bytecodes.IsDebugBreak((Bytecode)b))
            {
                Assert.True(scorecard[b] >= 1, "missing bytecode " + Bytecodes.ToString((Bytecode)b));
            }
        }
    }

    [Fact]
    public void BytecodeArrayBuilderTest_FrameSizesLookGood()
    {
        for (int locals = 0; locals < 5; locals++)
        {
            for (int temps = 0; temps < 3; temps++)
            {
                var builder = new BytecodeArrayBuilder(1, locals);
                BytecodeRegisterAllocator allocator = builder.RegisterAllocator();
                for (int i = 0; i < locals; i++)
                {
                    builder.LoadLiteral(Smi.Zero);
                    builder.StoreAccumulatorInRegister(new Register(i));
                }
                for (int i = 0; i < temps; i++)
                {
                    Register temp = allocator.NewRegister();
                    builder.LoadLiteral(Smi.Zero);
                    builder.StoreAccumulatorInRegister(temp);
                    // Ensure temporaries are used so not optimized away by the
                    // register optimizer.
                    builder.ToName().StoreAccumulatorInRegister(temp);
                }
                builder.Return();

                BytecodeArray the_array = builder.ToBytecodeArray();
                int total_registers = locals + temps;
                Assert.Equal(total_registers * kSystemPointerSize, the_array.FrameSize);
            }
        }
    }

    [Fact]
    public void BytecodeArrayBuilderTest_RegisterValues()
    {
        int index = 1;

        var the_register = new Register(index);
        Assert.Equal(index, the_register.Index);

        int actual_operand = the_register.ToOperand();
        int actual_index = Register.FromOperand(actual_operand).Index;
        Assert.Equal(index, actual_index);
    }

    [Fact]
    public void BytecodeArrayBuilderTest_Parameters()
    {
        var builder = new BytecodeArrayBuilder(10, 0);

        Register receiver = builder.Receiver();
        Register param8 = builder.Parameter(8);
        Assert.Equal(9, receiver.Index - param8.Index);
    }

    [Fact]
    public void BytecodeArrayBuilderTest_Constants()
    {
        var builder = new BytecodeArrayBuilder(1, 0);

        double heap_num_1 = 3.14;
        double heap_num_2 = 5.2;
        double nan = double.NaN;
        // The AstValueFactory returns the same AstRawString for equal contents.
        string @string = "foo";
        string string_copy = string.Intern(new string("foo".AsSpan()));

        builder.LoadLiteral(heap_num_1)
            .LoadLiteral(heap_num_2)
            .LoadLiteralRawString(@string)
            .LoadLiteral(heap_num_1)
            .LoadLiteral(heap_num_1)
            .LoadLiteral(nan)
            .LoadLiteralRawString(string_copy)
            .LoadLiteral(heap_num_2)
            .LoadLiteral(nan)
            .Return();

        BytecodeArray array = builder.ToBytecodeArray();
        // Should only have one entry for each identical constant.
        Assert.Equal(4, array.ConstantPool.Length);
    }

    [Fact]
    public void BytecodeArrayBuilderTest_ForwardJumps()
    {
        const int kFarJumpDistance = 256 + 20;

        var builder = new BytecodeArrayBuilder(1, 1);

        var reg = new Register(0);
        BytecodeLabel far0 = new(), far1 = new(), far2 = new(), far3 = new(), far4 = new();
        BytecodeLabel near0 = new(), near1 = new(), near2 = new(), near3 = new(), near4 = new();
        BytecodeLabel after_jump_near0 = new(), after_jump_far0 = new();

        builder.JumpIfNull(after_jump_near0)
            .Jump(near0)
            .Bind(after_jump_near0)
            .CompareOperation(Token.Eq, reg, kFeedbackIsEmbedded)
            .JumpIfTrue(ToBooleanMode.AlreadyBoolean, near1)
            .CompareOperation(Token.Eq, reg, kFeedbackIsEmbedded)
            .JumpIfFalse(ToBooleanMode.AlreadyBoolean, near2)
            .BinaryOperation(Token.Add, reg, kFeedbackIsEmbedded)
            .JumpIfTrue(ToBooleanMode.ConvertToBoolean, near3)
            .BinaryOperation(Token.Add, reg, kFeedbackIsEmbedded)
            .JumpIfFalse(ToBooleanMode.ConvertToBoolean, near4)
            .Bind(near0)
            .Bind(near1)
            .Bind(near2)
            .Bind(near3)
            .Bind(near4)
            .JumpIfNull(after_jump_far0)
            .Jump(far0)
            .Bind(after_jump_far0)
            .CompareOperation(Token.Eq, reg, kFeedbackIsEmbedded)
            .JumpIfTrue(ToBooleanMode.AlreadyBoolean, far1)
            .CompareOperation(Token.Eq, reg, kFeedbackIsEmbedded)
            .JumpIfFalse(ToBooleanMode.AlreadyBoolean, far2)
            .BinaryOperation(Token.Add, reg, kFeedbackIsEmbedded)
            .JumpIfTrue(ToBooleanMode.ConvertToBoolean, far3)
            .BinaryOperation(Token.Add, reg, kFeedbackIsEmbedded)
            .JumpIfFalse(ToBooleanMode.ConvertToBoolean, far4);
        for (int i = 0; i < kFarJumpDistance - 22; i++) builder.Debugger();
        builder.Bind(far0).Bind(far1).Bind(far2).Bind(far3).Bind(far4);
        builder.Return();

        BytecodeArray array = builder.ToBytecodeArray();
        Assert.Equal(48 + kFarJumpDistance - 22 + 1, array.Length);

        var iterator = new BytecodeArrayIterator(array);

        // Ignore JumpIfNull operation.
        iterator.Advance();

        Assert.Equal(Bytecode.Jump, iterator.CurrentBytecode());
        Assert.Equal(22u, iterator.GetUnsignedImmediateOperand(0));
        iterator.Advance();

        // Ignore compare operation.
        iterator.Advance();

        Assert.Equal(Bytecode.JumpIfTrue, iterator.CurrentBytecode());
        Assert.Equal(17u, iterator.GetUnsignedImmediateOperand(0));
        iterator.Advance();

        // Ignore compare operation.
        iterator.Advance();

        Assert.Equal(Bytecode.JumpIfFalse, iterator.CurrentBytecode());
        Assert.Equal(12u, iterator.GetUnsignedImmediateOperand(0));
        iterator.Advance();

        // Ignore add operation.
        iterator.Advance();

        Assert.Equal(Bytecode.JumpIfToBooleanTrue, iterator.CurrentBytecode());
        Assert.Equal(7u, iterator.GetUnsignedImmediateOperand(0));
        iterator.Advance();

        // Ignore add operation.
        iterator.Advance();

        Assert.Equal(Bytecode.JumpIfToBooleanFalse, iterator.CurrentBytecode());
        Assert.Equal(2u, iterator.GetUnsignedImmediateOperand(0));
        iterator.Advance();

        // Ignore JumpIfNull operation.
        iterator.Advance();

        Assert.Equal(Bytecode.JumpConstant, iterator.CurrentBytecode());
        Assert.Equal(Smi.FromInt(kFarJumpDistance), iterator.GetConstantForOperand(0));
        iterator.Advance();

        // Ignore compare operation.
        iterator.Advance();

        Assert.Equal(Bytecode.JumpIfTrueConstant, iterator.CurrentBytecode());
        Assert.Equal(Smi.FromInt(kFarJumpDistance - 5), iterator.GetConstantForOperand(0));
        iterator.Advance();

        // Ignore compare operation.
        iterator.Advance();

        Assert.Equal(Bytecode.JumpIfFalseConstant, iterator.CurrentBytecode());
        Assert.Equal(Smi.FromInt(kFarJumpDistance - 10), iterator.GetConstantForOperand(0));
        iterator.Advance();

        // Ignore add operation.
        iterator.Advance();

        Assert.Equal(Bytecode.JumpIfToBooleanTrueConstant, iterator.CurrentBytecode());
        Assert.Equal(Smi.FromInt(kFarJumpDistance - 15), iterator.GetConstantForOperand(0));
        iterator.Advance();

        // Ignore add operation.
        iterator.Advance();

        Assert.Equal(Bytecode.JumpIfToBooleanFalseConstant, iterator.CurrentBytecode());
        Assert.Equal(Smi.FromInt(kFarJumpDistance - 20), iterator.GetConstantForOperand(0));
        iterator.Advance();
    }

    [Fact]
    public void BytecodeArrayBuilderTest_BackwardJumps()
    {
        var builder = new BytecodeArrayBuilder(1, 1);

        var end = new BytecodeLabel();
        builder.JumpIfNull(end);

        var after_loop = new BytecodeLabel();
        // Conditional jump to force the code after the JumpLoop to be live.
        // Technically this jump is illegal because it's jumping into the middle of
        // the subsequent loops, but that's ok for this unit test.
        var loop_header = new BytecodeLoopHeader();
        builder.JumpIfNull(after_loop)
            .Bind(loop_header)
            .JumpLoop(loop_header, 0, 0, 0)
            .Bind(after_loop);
        for (int i = 0; i < 42; i++)
        {
            var also_after_loop = new BytecodeLabel();
            // Conditional jump to force the code after the JumpLoop to be live.
            builder.JumpIfNull(also_after_loop)
                .JumpLoop(loop_header, 0, 0, 0)
                .Bind(also_after_loop);
        }

        // Add padding to force wide backwards jumps.
        for (int i = 0; i < 256; i++) builder.Debugger();

        builder.JumpLoop(loop_header, 0, 0, 0);
        builder.Bind(end);
        builder.Return();

        BytecodeArray array = builder.ToBytecodeArray();
        var iterator = new BytecodeArrayIterator(array);
        // Ignore the JumpIfNull to the end
        iterator.Advance();
        // Ignore the JumpIfNull to after the first JumpLoop
        iterator.Advance();
        Assert.Equal(Bytecode.JumpLoop, iterator.CurrentBytecode());
        Assert.Equal(0u, iterator.GetUnsignedImmediateOperand(0));
        iterator.Advance();
        for (uint i = 0; i < 42; i++)
        {
            // Ignore the JumpIfNull to after the JumpLoop
            iterator.Advance();

            Assert.Equal(Bytecode.JumpLoop, iterator.CurrentBytecode());
            Assert.Equal(OperandScale.Single, iterator.CurrentOperandScale());
            // offset of 6 (because kJumpLoop takes three immediate operands and
            // JumpIfNull takes 1)
            Assert.Equal(3, Bytecodes.NumberOfOperands(Bytecode.JumpLoop));
            Assert.Equal(i * 6 + 6, iterator.GetUnsignedImmediateOperand(0));
            iterator.Advance();
        }
        // Check padding to force wide backwards jumps.
        for (int i = 0; i < 256; i++)
        {
            Assert.Equal(Bytecode.Debugger, iterator.CurrentBytecode());
            iterator.Advance();
        }
        Assert.Equal(Bytecode.JumpLoop, iterator.CurrentBytecode());
        Assert.Equal(OperandScale.Double, iterator.CurrentOperandScale());
        Assert.Equal((uint)(42 * 6 + 1 + 256 + 4), iterator.GetUnsignedImmediateOperand(0));
        iterator.Advance();
        Assert.Equal(Bytecode.Return, iterator.CurrentBytecode());
        iterator.Advance();
        Assert.True(iterator.Done());
    }

    [Fact]
    public void BytecodeArrayBuilderTest_SmallSwitch()
    {
        var builder = new BytecodeArrayBuilder(1, 1);

        // Small jump table that fits into the single-size constant pool
        int small_jump_table_size = 5;
        int small_jump_table_base = -2;
        BytecodeJumpTable small_jump_table = builder.AllocateJumpTable(small_jump_table_size, small_jump_table_base);

        builder.LoadLiteral(Smi.FromInt(7)).SwitchOnSmiNoFeedback(small_jump_table);
        for (int i = 0; i < small_jump_table_size; i++)
        {
            builder.Bind(small_jump_table, small_jump_table_base + i).Debugger();
        }
        builder.Return();

        BytecodeArray array = builder.ToBytecodeArray();
        var iterator = new BytecodeArrayIterator(array);

        Assert.Equal(Bytecode.LdaSmi, iterator.CurrentBytecode());
        iterator.Advance();

        Assert.Equal(Bytecode.SwitchOnSmiNoFeedback, iterator.CurrentBytecode());
        Assert.Equal(OperandScale.Single, iterator.CurrentOperandScale());
        {
            int i = 0;
            int switch_end = iterator.CurrentOffset() + iterator.CurrentBytecodeSize();

            foreach (JumpTableTargetOffset entry in iterator.GetJumpTableTargetOffsets())
            {
                Assert.Equal(small_jump_table_base + i, entry.CaseValue);
                Assert.Equal(switch_end + i, entry.TargetOffset);
                i++;
            }
            Assert.Equal(small_jump_table_size, i);
        }
        iterator.Advance();

        for (int i = 0; i < small_jump_table_size; i++)
        {
            Assert.Equal(Bytecode.Debugger, iterator.CurrentBytecode());
            iterator.Advance();
        }

        Assert.Equal(Bytecode.Return, iterator.CurrentBytecode());
        iterator.Advance();
        Assert.True(iterator.Done());
    }

    [Fact]
    public void BytecodeArrayBuilderTest_WideSwitch()
    {
        var builder = new BytecodeArrayBuilder(1, 1);

        // Large jump table that requires a wide Switch bytecode.
        int large_jump_table_size = 256;
        int large_jump_table_base = -10;
        BytecodeJumpTable large_jump_table = builder.AllocateJumpTable(large_jump_table_size, large_jump_table_base);

        builder.LoadLiteral(Smi.FromInt(7)).SwitchOnSmiNoFeedback(large_jump_table);
        for (int i = 0; i < large_jump_table_size; i++)
        {
            builder.Bind(large_jump_table, large_jump_table_base + i).Debugger();
        }
        builder.Return();

        BytecodeArray array = builder.ToBytecodeArray();
        var iterator = new BytecodeArrayIterator(array);

        Assert.Equal(Bytecode.LdaSmi, iterator.CurrentBytecode());
        iterator.Advance();

        Assert.Equal(Bytecode.SwitchOnSmiNoFeedback, iterator.CurrentBytecode());
        Assert.Equal(OperandScale.Double, iterator.CurrentOperandScale());
        {
            int i = 0;
            int switch_end = iterator.CurrentOffset() + iterator.CurrentBytecodeSize();

            foreach (JumpTableTargetOffset entry in iterator.GetJumpTableTargetOffsets())
            {
                Assert.Equal(large_jump_table_base + i, entry.CaseValue);
                Assert.Equal(switch_end + i, entry.TargetOffset);
                i++;
            }
            Assert.Equal(large_jump_table_size, i);
        }
        iterator.Advance();

        for (int i = 0; i < large_jump_table_size; i++)
        {
            Assert.Equal(Bytecode.Debugger, iterator.CurrentBytecode());
            iterator.Advance();
        }

        Assert.Equal(Bytecode.Return, iterator.CurrentBytecode());
        iterator.Advance();
        Assert.True(iterator.Done());
    }
}
