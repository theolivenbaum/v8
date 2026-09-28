// Port of test/unittests/interpreter/interpreter-unittest.cc (continued):
// binary/unary feedback, globals, property ICs, calls and jumps.
using V8Sharp.Ast;
using V8Sharp.Interpreter;
using V8Sharp.Objects;
using V8Sharp.Parsing;
using V8Sharp.Runtime;
using ToBooleanMode = V8Sharp.Interpreter.BytecodeArrayBuilder.ToBooleanMode;

namespace V8Sharp.Tests.Interpreter;

public partial class InterpreterUnitTest
{
    [Fact]
    public void InterpreterBinaryOpSmiTypeFeedback()
    {
        bool additiveSafe = i_isolate.Flags.additive_safe_int_feedback;
        (Token op, LiteralForTest arg1, int arg2, JSValue result, BinaryOperationFeedback.Type feedback)[] kTestCases =
        [
            // ADD
            (Token.Add, L(2), 42, JSValue.FromInt(44), BinaryOperationFeedback.Type.SignedSmall),
            (Token.Add, L(2), Smi.kMaxValue, Num(Smi.kMaxValue + 2.0),
                additiveSafe ? BinaryOperationFeedback.Type.AdditiveSafeInteger : BinaryOperationFeedback.Type.Number),
            (Token.Add, L(3.1415), 2, Num(3.1415 + 2.0), BinaryOperationFeedback.Type.Number),
            (Token.Add, L("2"), 2, Str("22"), BinaryOperationFeedback.Type.Any),
            // SUB
            (Token.Sub, L(2), 42, JSValue.FromInt(-40), BinaryOperationFeedback.Type.SignedSmall),
            (Token.Sub, L(Smi.kMinValue), 1, Num(Smi.kMinValue - 1.0), BinaryOperationFeedback.Type.Number),
            (Token.Sub, L(3.1415), 2, Num(3.1415 - 2.0), BinaryOperationFeedback.Type.Number),
            (Token.Sub, L("2"), 2, JSValue.FromInt(0), BinaryOperationFeedback.Type.Any),
            // BIT_OR
            (Token.BitOr, L(4), 1, JSValue.FromInt(5), BinaryOperationFeedback.Type.SignedSmall),
            (Token.BitOr, L(3.1415), 8, JSValue.FromInt(11), BinaryOperationFeedback.Type.Number),
            (Token.BitOr, L("2"), 1, JSValue.FromInt(3), BinaryOperationFeedback.Type.Any),
            // BIT_AND
            (Token.BitAnd, L(3), 1, JSValue.FromInt(1), BinaryOperationFeedback.Type.SignedSmall),
            (Token.BitAnd, L(3.1415), 2, JSValue.FromInt(2), BinaryOperationFeedback.Type.Number),
            (Token.BitAnd, L("2"), 1, JSValue.FromInt(0), BinaryOperationFeedback.Type.Any),
            // SHL
            (Token.Shl, L(3), 1, JSValue.FromInt(6), BinaryOperationFeedback.Type.SignedSmall),
            (Token.Shl, L(3.1415), 2, JSValue.FromInt(12), BinaryOperationFeedback.Type.Number),
            (Token.Shl, L("2"), 1, JSValue.FromInt(4), BinaryOperationFeedback.Type.Any),
            // SAR
            (Token.Sar, L(3), 1, JSValue.FromInt(1), BinaryOperationFeedback.Type.SignedSmall),
            (Token.Sar, L(3.1415), 2, JSValue.FromInt(0), BinaryOperationFeedback.Type.Number),
            (Token.Sar, L("2"), 1, JSValue.FromInt(1), BinaryOperationFeedback.Type.Any),
        ];

        foreach (var testCase in kTestCases)
        {
            var builder = new BytecodeArrayBuilder(1, 1);
            var reg = new Register(0);
            LoadLiteralForTest(builder, testCase.arg1);
            builder.StoreAccumulatorInRegister(reg).LoadLiteral(Smi.FromInt(testCase.arg2));
            int bytecodeOffset = builder.CurrentBytecodeSize();
            builder.BinaryOperation(testCase.op, reg, kFeedbackIsEmbedded).Return();
            var tester = new InterpreterTester(i_isolate, ToBytecodeArray(builder));
            JSValue returnVal = tester.Call();
            var actual = tester.GetBinaryEmbeddedFeedback(bytecodeOffset, 2);
            Assert.True(testCase.feedback == actual, $"{testCase.op} {testCase.arg1} {testCase.arg2}: {actual}");
            Assert.True(ObjectOps.Equals(i_isolate, testCase.result, returnVal));
        }
    }

    static int[] FindOffsets(BytecodeArray bytecodeArray, Func<Bytecode, bool> predicate)
    {
        var offsets = new List<int>();
        for (var it = new BytecodeArrayIterator(bytecodeArray); !it.Done(); it.Advance())
        {
            if (predicate(it.CurrentBytecode())) offsets.Add(it.CurrentOffset());
        }
        return offsets.ToArray();
    }

    [Fact]
    public void InterpreterUnaryOpFeedback()
    {
        JSValue smiOne = JSValue.FromInt(1);
        JSValue smiMax = JSValue.FromInt(Smi.kMaxValue);
        JSValue smiMin = JSValue.FromInt(Smi.kMinValue);
        JSValue number = Num(2.1);
        JSValue bigint = BigInt.FromNumber(i_isolate, smiMax);
        JSValue str = Str("42");

        (Token op, JSValue smiFeedbackValue, JSValue smiToNumberFeedbackValue)[] kTestCases =
        [
            // Testing ADD and BIT_NOT would require generalizing the test setup.
            (Token.Sub, smiOne, smiMin),
            (Token.Inc, smiOne, smiMax),
            (Token.Dec, smiOne, smiMin),
        ];
        foreach (var testCase in kTestCases)
        {
            var builder = new BytecodeArrayBuilder(6, 0);
            builder.LoadAccumulatorWithRegister(builder.Parameter(0))
                .UnaryOperation(testCase.op, kFeedbackIsEmbedded)
                .LoadAccumulatorWithRegister(builder.Parameter(1))
                .UnaryOperation(testCase.op, kFeedbackIsEmbedded)
                .LoadAccumulatorWithRegister(builder.Parameter(2))
                .UnaryOperation(testCase.op, kFeedbackIsEmbedded)
                .LoadAccumulatorWithRegister(builder.Parameter(3))
                .UnaryOperation(testCase.op, kFeedbackIsEmbedded)
                .LoadAccumulatorWithRegister(builder.Parameter(4))
                .UnaryOperation(testCase.op, kFeedbackIsEmbedded)
                .Return();
            BytecodeArray bytecodeArray = ToBytecodeArray(builder);

            // Walk the built bytecode array to recover the exact unary-op offsets.
            int[] offsets = FindOffsets(bytecodeArray, Bytecodes.IsUnaryOpWithEmbeddedFeedback);
            Assert.Equal(5, offsets.Length);

            var tester = new InterpreterTester(i_isolate, bytecodeArray);
            tester.Call(testCase.smiFeedbackValue, testCase.smiToNumberFeedbackValue, number, bigint, str);
            Assert.Equal(BinaryOperationFeedback.Type.SignedSmall, tester.GetBinaryEmbeddedFeedback(offsets[0], 1));
            Assert.Equal(BinaryOperationFeedback.Type.Number, tester.GetBinaryEmbeddedFeedback(offsets[1], 1));
            Assert.Equal(BinaryOperationFeedback.Type.Number, tester.GetBinaryEmbeddedFeedback(offsets[2], 1));
            Assert.Equal(BinaryOperationFeedback.Type.BigInt, tester.GetBinaryEmbeddedFeedback(offsets[3], 1));
            Assert.Equal(BinaryOperationFeedback.Type.Any, tester.GetBinaryEmbeddedFeedback(offsets[4], 1));
        }
    }

    [Fact]
    public void InterpreterBitwiseTypeFeedback()
    {
        Token[] kBitwiseBinaryOperators = [Token.BitOr, Token.BitXor, Token.BitAnd, Token.Shl, Token.Shr, Token.Sar];
        foreach (Token op in kBitwiseBinaryOperators)
        {
            var builder = new BytecodeArrayBuilder(5, 0);
            builder.LoadAccumulatorWithRegister(builder.Parameter(0))
                .BinaryOperation(op, builder.Parameter(1), kFeedbackIsEmbedded)
                .BinaryOperation(op, builder.Parameter(2), kFeedbackIsEmbedded)
                .BinaryOperation(op, builder.Parameter(3), kFeedbackIsEmbedded)
                .Return();
            BytecodeArray bytecodeArray = ToBytecodeArray(builder);
            int[] offsets = FindOffsets(bytecodeArray, Bytecodes.IsBinaryOpWithEmbeddedFeedback);
            Assert.Equal(3, offsets.Length);

            var tester = new InterpreterTester(i_isolate, bytecodeArray);
            tester.Call(JSValue.FromInt(2), JSValue.FromInt(2), Num(2.2), Str("2"));
            Assert.Equal(BinaryOperationFeedback.Type.SignedSmall, tester.GetBinaryEmbeddedFeedback(offsets[0], 2));
            Assert.Equal(BinaryOperationFeedback.Type.Number, tester.GetBinaryEmbeddedFeedback(offsets[1], 2));
            Assert.Equal(BinaryOperationFeedback.Type.Any, tester.GetBinaryEmbeddedFeedback(offsets[2], 2));
        }
    }

    [Fact]
    public void InterpreterParameter1Assign()
    {
        var builder = new BytecodeArrayBuilder(1, 0);
        builder.LoadLiteral(Smi.FromInt(5))
            .StoreAccumulatorInRegister(builder.Receiver())
            .LoadAccumulatorWithRegister(builder.Receiver())
            .Return();
        var tester = new InterpreterTester(i_isolate, ToBytecodeArray(builder));
        Assert.Equal(5, tester.CallWithReceiver(JSValue.FromInt(3)).Number);
    }

    JSValue GlobalProperty(string name) =>
        ObjectOps.GetProperty(i_isolate, i_isolate.NativeContext.GlobalObject, factory.InternalizeString(name));

    [Fact]
    public void InterpreterLoadGlobal()
    {
        // Test loading a global.
        string source = "var global = 321;\nfunction " + InterpreterTester.function_name() + "() {\n  return global;\n}";
        var tester = new InterpreterTester(i_isolate, source);
        Assert.Equal(321, tester.Call().Number);
    }

    [Fact]
    public void InterpreterStoreGlobal()
    {
        // Test storing to a global.
        string source = "var global = 321;\nfunction " + InterpreterTester.function_name() + "() {\n  global = 999;\n}";
        var tester = new InterpreterTester(i_isolate, source);
        tester.Call();
        Assert.Equal(999, GlobalProperty("global").Number);
    }

    [Fact]
    public void InterpreterCallGlobal()
    {
        // Test calling a global function.
        string source = "function g_add(a, b) { return a + b; }\nfunction " + InterpreterTester.function_name() +
                        "() {\n  return g_add(5, 10);\n}";
        var tester = new InterpreterTester(i_isolate, source);
        Assert.Equal(15, tester.Call().Number);
    }

    [Fact]
    public void InterpreterLoadUnallocated()
    {
        // Test loading an unallocated global.
        string source = "unallocated = 123;\nfunction " + InterpreterTester.function_name() + "() {\n  return unallocated;\n}";
        var tester = new InterpreterTester(i_isolate, source);
        Assert.Equal(123, tester.Call().Number);
    }

    [Fact]
    public void InterpreterStoreUnallocated()
    {
        // Test storing to an unallocated global.
        string source = "unallocated = 321;\nfunction " + InterpreterTester.function_name() + "() {\n  unallocated = 999;\n}";
        var tester = new InterpreterTester(i_isolate, source);
        tester.Call();
        Assert.Equal(999, GlobalProperty("unallocated").Number);
    }

    [Fact]
    public void InterpreterLoadNamedProperty()
    {
        var feedbackSpec = new FeedbackVectorSpec();
        FeedbackSlot slot = feedbackSpec.AddLoadICSlot();
        FeedbackMetadata metadata = FeedbackMetadata.New(feedbackSpec);

        var builder = new BytecodeArrayBuilder(1, 0);
        builder.LoadNamedProperty(builder.Receiver(), "val", GetIndex(slot)).Return();
        var tester = new InterpreterTester(i_isolate, ToBytecodeArray(builder), metadata);

        JSValue obj = InterpreterTester.NewObject(i_isolate, "({ val : 123 })");
        // Test IC miss.
        Assert.Equal(123, tester.CallWithReceiver(obj).Number);
        // Test transition to monomorphic IC.
        Assert.Equal(123, tester.CallWithReceiver(obj).Number);
        // Test transition to polymorphic IC.
        JSValue object2 = InterpreterTester.NewObject(i_isolate, "({ val : 456, other : 123 })");
        Assert.Equal(456, tester.CallWithReceiver(object2).Number);
        // Test transition to megamorphic IC.
        tester.CallWithReceiver(InterpreterTester.NewObject(i_isolate, "({ val : 789, val2 : 123 })"));
        tester.CallWithReceiver(InterpreterTester.NewObject(i_isolate, "({ val : 789, val3 : 123 })"));
        JSValue object5 = InterpreterTester.NewObject(i_isolate, "({ val : 789, val4 : 123 })");
        Assert.Equal(789, tester.CallWithReceiver(object5).Number);
    }

    [Fact]
    public void InterpreterLoadKeyedProperty()
    {
        var feedbackSpec = new FeedbackVectorSpec();
        FeedbackSlot slot = feedbackSpec.AddKeyedLoadICSlot();
        FeedbackMetadata metadata = FeedbackMetadata.New(feedbackSpec);

        var builder = new BytecodeArrayBuilder(1, 1);
        builder.LoadLiteralRawString("key").LoadKeyedProperty(builder.Receiver(), GetIndex(slot)).Return();
        var tester = new InterpreterTester(i_isolate, ToBytecodeArray(builder), metadata);

        JSValue obj = InterpreterTester.NewObject(i_isolate, "({ key : 123 })");
        // Test IC miss.
        Assert.Equal(123, tester.CallWithReceiver(obj).Number);
        // Test transition to monomorphic IC.
        Assert.Equal(123, tester.CallWithReceiver(obj).Number);
        // Test transition to megamorphic IC.
        JSValue object3 = InterpreterTester.NewObject(i_isolate, "({ key : 789, val2 : 123 })");
        Assert.Equal(789, tester.CallWithReceiver(object3).Number);
    }

    JSValue GetObjectProperty(JSValue obj, string name) =>
        RuntimeObject.GetObjectProperty(i_isolate, obj, factory.InternalizeString(name), obj, out _);

    [Fact]
    public void InterpreterSetNamedProperty()
    {
        var feedbackSpec = new FeedbackVectorSpec();
        FeedbackSlot slot = feedbackSpec.AddStoreICSlot(LanguageMode.Strict);
        FeedbackMetadata metadata = FeedbackMetadata.New(feedbackSpec);

        var builder = new BytecodeArrayBuilder(1, 0);
        builder.LoadLiteral(Smi.FromInt(999))
            .SetNamedProperty(builder.Receiver(), "val", GetIndex(slot), LanguageMode.Strict)
            .Return();
        var tester = new InterpreterTester(i_isolate, ToBytecodeArray(builder), metadata);
        JSValue obj = InterpreterTester.NewObject(i_isolate, "({ val : 123 })");
        // Test IC miss.
        tester.CallWithReceiver(obj);
        Assert.Equal(999, GetObjectProperty(obj, "val").Number);
        // Test transition to monomorphic IC.
        tester.CallWithReceiver(obj);
        Assert.Equal(999, GetObjectProperty(obj, "val").Number);
        // Test transition to polymorphic IC.
        JSValue object2 = InterpreterTester.NewObject(i_isolate, "({ val : 456, other : 123 })");
        tester.CallWithReceiver(object2);
        Assert.Equal(999, GetObjectProperty(object2, "val").Number);
        // Test transition to megamorphic IC.
        tester.CallWithReceiver(InterpreterTester.NewObject(i_isolate, "({ val : 789, val2 : 123 })"));
        tester.CallWithReceiver(InterpreterTester.NewObject(i_isolate, "({ val : 789, val3 : 123 })"));
        JSValue object5 = InterpreterTester.NewObject(i_isolate, "({ val : 789, val4 : 123 })");
        tester.CallWithReceiver(object5);
        Assert.Equal(999, GetObjectProperty(object5, "val").Number);
    }

    [Fact]
    public void InterpreterSetKeyedProperty()
    {
        var feedbackSpec = new FeedbackVectorSpec();
        FeedbackSlot slot = feedbackSpec.AddKeyedStoreICSlot(LanguageMode.Sloppy);
        FeedbackMetadata metadata = FeedbackMetadata.New(feedbackSpec);

        var builder = new BytecodeArrayBuilder(1, 1);
        builder.LoadLiteralRawString("val")
            .StoreAccumulatorInRegister(new Register(0))
            .LoadLiteral(Smi.FromInt(999))
            .SetKeyedProperty(builder.Receiver(), new Register(0), GetIndex(slot), LanguageMode.Sloppy)
            .Return();
        var tester = new InterpreterTester(i_isolate, ToBytecodeArray(builder), metadata);
        JSValue obj = InterpreterTester.NewObject(i_isolate, "({ val : 123 })");
        // Test IC miss.
        tester.CallWithReceiver(obj);
        Assert.Equal(999, GetObjectProperty(obj, "val").Number);
        // Test transition to monomorphic IC.
        tester.CallWithReceiver(obj);
        Assert.Equal(999, GetObjectProperty(obj, "val").Number);
        // Test transition to megamorphic IC.
        JSValue object2 = InterpreterTester.NewObject(i_isolate, "({ val : 456, other : 123 })");
        tester.CallWithReceiver(object2);
        Assert.Equal(999, GetObjectProperty(object2, "val").Number);
    }

    [Fact]
    public void InterpreterCall()
    {
        var feedbackSpec = new FeedbackVectorSpec();
        FeedbackSlot slot = feedbackSpec.AddLoadICSlot();
        FeedbackSlot callSlot = feedbackSpec.AddCallICSlot();
        FeedbackMetadata metadata = FeedbackMetadata.New(feedbackSpec);
        int slotIndex = GetIndex(slot);
        int callSlotIndex = GetIndex(callSlot);

        // Check with no args.
        {
            var builder = new BytecodeArrayBuilder(1, 1);
            Register reg = builder.RegisterAllocator().NewRegister();
            RegisterList args = builder.RegisterAllocator().NewRegisterList(1);
            builder.LoadNamedProperty(builder.Receiver(), "func", slotIndex)
                .StoreAccumulatorInRegister(reg)
                .MoveRegister(builder.Receiver(), args[0]);
            builder.CallProperty(reg, args, callSlotIndex);
            builder.Return();
            var tester = new InterpreterTester(i_isolate, ToBytecodeArray(builder), metadata);
            JSValue obj = InterpreterTester.NewObject(i_isolate,
                "new (function Obj() { this.func = function() { return 0x265; }})()");
            Assert.Equal(0x265, tester.CallWithReceiver(obj).Number);
        }

        // Check that receiver is passed properly.
        {
            var builder = new BytecodeArrayBuilder(1, 1);
            Register reg = builder.RegisterAllocator().NewRegister();
            RegisterList args = builder.RegisterAllocator().NewRegisterList(1);
            builder.LoadNamedProperty(builder.Receiver(), "func", slotIndex)
                .StoreAccumulatorInRegister(reg)
                .MoveRegister(builder.Receiver(), args[0]);
            builder.CallProperty(reg, args, callSlotIndex);
            builder.Return();
            var tester = new InterpreterTester(i_isolate, ToBytecodeArray(builder), metadata);
            JSValue obj = InterpreterTester.NewObject(i_isolate,
                "new (function Obj() {  this.val = 1234;  this.func = function() { return this.val; };})()");
            Assert.Equal(1234, tester.CallWithReceiver(obj).Number);
        }

        // Check with two parameters (+ receiver).
        {
            var builder = new BytecodeArrayBuilder(1, 4);
            Register reg = builder.RegisterAllocator().NewRegister();
            RegisterList args = builder.RegisterAllocator().NewRegisterList(3);
            builder.LoadNamedProperty(builder.Receiver(), "func", slotIndex)
                .StoreAccumulatorInRegister(reg)
                .LoadAccumulatorWithRegister(builder.Receiver())
                .StoreAccumulatorInRegister(args[0])
                .LoadLiteral(Smi.FromInt(51))
                .StoreAccumulatorInRegister(args[1])
                .LoadLiteral(Smi.FromInt(11))
                .StoreAccumulatorInRegister(args[2]);
            builder.CallProperty(reg, args, callSlotIndex);
            builder.Return();
            var tester = new InterpreterTester(i_isolate, ToBytecodeArray(builder), metadata);
            JSValue obj = InterpreterTester.NewObject(i_isolate,
                "new (function Obj() {   this.func = function(a, b) { return a - b; }})()");
            Assert.True(SameValue(tester.CallWithReceiver(obj), JSValue.FromInt(40)));
        }

        // Check with 10 parameters (+ receiver).
        {
            var builder = new BytecodeArrayBuilder(1, 12);
            Register reg = builder.RegisterAllocator().NewRegister();
            RegisterList args = builder.RegisterAllocator().NewRegisterList(11);
            builder.LoadNamedProperty(builder.Receiver(), "func", slotIndex)
                .StoreAccumulatorInRegister(reg)
                .LoadAccumulatorWithRegister(builder.Receiver())
                .StoreAccumulatorInRegister(args[0]);
            string letters = "abcdefghij";
            for (int i = 0; i < letters.Length; i++)
            {
                builder.LoadLiteralRawString(letters[i].ToString()).StoreAccumulatorInRegister(args[i + 1]);
            }
            builder.CallProperty(reg, args, callSlotIndex);
            builder.Return();
            var tester = new InterpreterTester(i_isolate, ToBytecodeArray(builder), metadata);
            JSValue obj = InterpreterTester.NewObject(i_isolate,
                "new (function Obj() {   this.prefix = \"prefix_\";  this.func = function(a, b, c, d, e, f, g, h, i, j) {" +
                "      return this.prefix + a + b + c + d + e + f + g + h + i + j;  }})()");
            Assert.Equal("prefix_abcdefghij", tester.CallWithReceiver(obj).As<JSString>().ToString());
        }
    }

    static BytecodeArrayBuilder SetRegister(BytecodeArrayBuilder builder, Register reg, int value, Register scratch) =>
        builder.StoreAccumulatorInRegister(scratch)
            .LoadLiteral(Smi.FromInt(value))
            .StoreAccumulatorInRegister(reg)
            .LoadAccumulatorWithRegister(scratch);

    static BytecodeArrayBuilder IncrementRegister(BytecodeArrayBuilder builder, Register reg, int value, Register scratch) =>
        builder.StoreAccumulatorInRegister(scratch)
            .LoadLiteral(Smi.FromInt(value))
            .BinaryOperation(Token.Add, reg, kFeedbackIsEmbedded)
            .StoreAccumulatorInRegister(reg)
            .LoadAccumulatorWithRegister(scratch);

    [Fact]
    public void InterpreterJumps()
    {
        var feedbackSpec = new FeedbackVectorSpec();
        var builder = new BytecodeArrayBuilder(1, 2);
        FeedbackSlot slot2 = feedbackSpec.AddJumpLoopSlot();
        FeedbackMetadata metadata = FeedbackMetadata.New(feedbackSpec);

        Register reg = new(0), scratch = new(1);
        var loopHeader = new BytecodeLoopHeader();
        BytecodeLabel[] label = [new(), new()];

        builder.LoadLiteral(Smi.Zero).StoreAccumulatorInRegister(reg).Jump(label[0]);
        SetRegister(builder, reg, 1024, scratch).Bind(label[0]).Bind(loopHeader);
        IncrementRegister(builder, reg, 1, scratch).Jump(label[1]);
        SetRegister(builder, reg, 2048, scratch).JumpLoop(loopHeader, 0, 0, slot2.ToInt());
        SetRegister(builder, reg, 4096, scratch).Bind(label[1]);
        IncrementRegister(builder, reg, 2, scratch).LoadAccumulatorWithRegister(reg).Return();

        Assert.Equal(3, RunBytecode(ToBytecodeArray(builder), metadata).Number);
    }

    BytecodeArray ConditionalJumpsBytecode()
    {
        var builder = new BytecodeArrayBuilder(1, 2);
        Register reg = new(0), scratch = new(1);
        BytecodeLabel[] label = [new(), new()];
        BytecodeLabel done = new(), done1 = new();

        builder.LoadLiteral(Smi.Zero)
            .StoreAccumulatorInRegister(reg)
            .LoadFalse()
            .JumpIfFalse(ToBooleanMode.AlreadyBoolean, label[0]);
        IncrementRegister(builder, reg, 1024, scratch)
            .Bind(label[0])
            .LoadTrue()
            .JumpIfFalse(ToBooleanMode.AlreadyBoolean, done);
        IncrementRegister(builder, reg, 1, scratch)
            .LoadTrue()
            .JumpIfTrue(ToBooleanMode.AlreadyBoolean, label[1]);
        IncrementRegister(builder, reg, 2048, scratch).Bind(label[1]);
        IncrementRegister(builder, reg, 2, scratch)
            .LoadFalse()
            .JumpIfTrue(ToBooleanMode.AlreadyBoolean, done1);
        IncrementRegister(builder, reg, 4, scratch)
            .LoadAccumulatorWithRegister(reg)
            .Bind(done)
            .Bind(done1)
            .Return();
        return ToBytecodeArray(builder);
    }

    [Fact]
    public void InterpreterConditionalJumps() => Assert.Equal(7, RunBytecode(ConditionalJumpsBytecode()).Number);

    [Fact]
    public void InterpreterConditionalJumps2() => Assert.Equal(7, RunBytecode(ConditionalJumpsBytecode()).Number);

    [Fact]
    public void InterpreterJumpConstantWith16BitOperand()
    {
        var builder = new BytecodeArrayBuilder(1, 257);
        Register reg = new(0), scratch = new(256);
        BytecodeLabel done = new(), fake = new();

        builder.LoadLiteral(Smi.Zero);
        builder.StoreAccumulatorInRegister(reg);
        // Conditional jump to the fake label, to force both basic blocks to be live.
        builder.JumpIfTrue(ToBooleanMode.ConvertToBoolean, fake);
        // Consume all 8-bit operands
        for (int i = 1; i <= 256; i++)
        {
            builder.LoadLiteral(i + 0.5);
            builder.BinaryOperation(Token.Add, reg, kFeedbackIsEmbedded);
            builder.StoreAccumulatorInRegister(reg);
        }
        builder.Jump(done);

        // Emit more than 16-bit immediate operands worth of code to jump over.
        builder.Bind(fake);
        for (int i = 0; i < 6600; i++)
        {
            builder.LoadLiteral(Smi.Zero);
            builder.BinaryOperation(Token.Add, scratch, kFeedbackIsEmbedded);
            builder.StoreAccumulatorInRegister(scratch);
            builder.MoveRegister(scratch, reg);
        }
        builder.Bind(done);
        builder.LoadAccumulatorWithRegister(reg);
        builder.Return();

        BytecodeArray bytecodeArray = ToBytecodeArray(builder);
        bool found16BitConstantJump = false;
        for (var iterator = new BytecodeArrayIterator(bytecodeArray); !iterator.Done(); iterator.Advance())
        {
            if (iterator.CurrentBytecode() == Bytecode.JumpConstant && iterator.CurrentOperandScale() == OperandScale.Double)
            {
                found16BitConstantJump = true;
                break;
            }
        }
        Assert.True(found16BitConstantJump);
        Assert.Equal(256.0 / 2 * (1.5 + 256.5), RunBytecode(bytecodeArray).Number);
    }

    [Fact]
    public void InterpreterJumpWith32BitOperand()
    {
        var builder = new BytecodeArrayBuilder(1, 1);
        var reg = new Register(0);
        var done = new BytecodeLabel();

        builder.LoadLiteral(Smi.Zero);
        builder.StoreAccumulatorInRegister(reg);
        // Consume all 16-bit constant pool entries. Make sure to use doubles so that
        // the jump can't reuse an integer.
        for (int i = 1; i <= 65536; i++) builder.LoadLiteral(i + 0.5);
        builder.Jump(done);
        builder.LoadLiteral(Smi.Zero);
        builder.Bind(done);
        builder.Return();

        BytecodeArray bytecodeArray = ToBytecodeArray(builder);
        bool found32BitJump = false;
        for (var iterator = new BytecodeArrayIterator(bytecodeArray); !iterator.Done(); iterator.Advance())
        {
            if (iterator.CurrentBytecode() == Bytecode.Jump && iterator.CurrentOperandScale() == OperandScale.Quadruple)
            {
                found32BitJump = true;
                break;
            }
        }
        Assert.True(found32BitJump);
        Assert.Equal(65536.5, RunBytecode(bytecodeArray).Number);
    }
}
