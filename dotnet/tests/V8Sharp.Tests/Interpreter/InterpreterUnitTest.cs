// Port of test/unittests/interpreter/interpreter-unittest.cc.
//
// AST strings are C# strings (the materializer internalizes them, as
// AstValueFactory::Internalize does); BigInt literals are AstBigInt.
using V8Sharp.Ast;
using V8Sharp.Base.Numbers;
using V8Sharp.Codegen;
using V8Sharp.Common;
using V8Sharp.Interpreter;
using V8Sharp.Objects;
using V8Sharp.Parsing;
using V8Sharp.Runtime;
using ToBooleanMode = V8Sharp.Interpreter.BytecodeArrayBuilder.ToBooleanMode;

namespace V8Sharp.Tests.Interpreter;

public partial class InterpreterUnitTest : TestWithContext
{
    const int kFeedbackIsEmbedded = InterpreterConstants.kFeedbackIsEmbedded;
    const int kMaxInt8 = sbyte.MaxValue;

    BytecodeArray ToBytecodeArray(BytecodeArrayBuilder builder) =>
        builder.ToBytecodeArray(new CompilerHeap(i_isolate, Factory.EmptyScript));

    JSValue RunBytecode(BytecodeArray bytecodeArray, FeedbackMetadata? feedbackMetadata = null)
    {
        var tester = new InterpreterTester(i_isolate, bytecodeArray, feedbackMetadata);
        return tester.Call();
    }

    static int GetIndex(FeedbackSlot slot) => FeedbackVector.GetIndex(slot);

    JSValue Num(double d) => JSValue.FromNumber(d);

    JSValue Str(string s) => factory.NewStringFromUtf16(s);

    JSValue CompileRun(string source) => InterpreterTester.CompileRun(i_isolate, source);

    bool SameValue(JSValue a, JSValue b) => ObjectOps.SameValue(a, b);

    [Fact]
    public void InterpreterReturn()
    {
        var builder = new BytecodeArrayBuilder(1, 0);
        builder.Return();
        BytecodeArray bytecodeArray = ToBytecodeArray(builder);
        Assert.True(RunBytecode(bytecodeArray).IsUndefined);
    }

    [Fact]
    public void InterpreterLoadUndefined()
    {
        var builder = new BytecodeArrayBuilder(1, 0);
        builder.LoadUndefined().Return();
        Assert.True(RunBytecode(ToBytecodeArray(builder)).IsUndefined);
    }

    [Fact]
    public void InterpreterLoadNull()
    {
        var builder = new BytecodeArrayBuilder(1, 0);
        builder.LoadNull().Return();
        Assert.True(RunBytecode(ToBytecodeArray(builder)).IsNull);
    }

    [Fact]
    public void InterpreterLoadTheHole()
    {
        var builder = new BytecodeArrayBuilder(1, 0);
        builder.LoadTheHole().Return();
        Assert.True(RunBytecode(ToBytecodeArray(builder)).IsTheHole);
    }

    [Fact]
    public void InterpreterLoadTrue()
    {
        var builder = new BytecodeArrayBuilder(1, 0);
        builder.LoadTrue().Return();
        Assert.True(RunBytecode(ToBytecodeArray(builder)).IsTrue);
    }

    [Fact]
    public void InterpreterLoadFalse()
    {
        var builder = new BytecodeArrayBuilder(1, 0);
        builder.LoadFalse().Return();
        Assert.True(RunBytecode(ToBytecodeArray(builder)).IsFalse);
    }

    [Fact]
    public void InterpreterLoadLiteral()
    {
        // Small Smis.
        for (int i = -128; i < 128; i++)
        {
            var builder = new BytecodeArrayBuilder(1, 0);
            builder.LoadLiteral(Smi.FromInt(i)).Return();
            JSValue returnVal = RunBytecode(ToBytecodeArray(builder));
            Assert.True(returnVal.IsSmi);
            Assert.Equal(i, (int)returnVal.Number);
        }

        // Large Smis.
        {
            var builder = new BytecodeArrayBuilder(1, 0);
            builder.LoadLiteral(Smi.FromInt(0x12345678)).Return();
            JSValue returnVal = RunBytecode(ToBytecodeArray(builder));
            Assert.Equal(0x12345678, returnVal.Number);
        }

        // Heap numbers.
        {
            var builder = new BytecodeArrayBuilder(1, 0);
            builder.LoadLiteral(-2.1e19).Return();
            JSValue returnVal = RunBytecode(ToBytecodeArray(builder));
            Assert.Equal(-2.1e19, returnVal.Number);
        }

        // Strings.
        {
            var builder = new BytecodeArrayBuilder(1, 0);
            builder.LoadLiteralRawString("String").Return();
            JSValue returnVal = RunBytecode(ToBytecodeArray(builder));
            Assert.Equal("String", returnVal.As<JSString>().ToString());
        }
    }

    [Fact]
    public void InterpreterLoadStoreRegisters()
    {
        for (int i = 0; i <= kMaxInt8; i++)
        {
            var builder = new BytecodeArrayBuilder(1, i + 1);
            var reg = new Register(i);
            builder.LoadTrue()
                .StoreAccumulatorInRegister(reg)
                .LoadFalse()
                .LoadAccumulatorWithRegister(reg)
                .Return();
            Assert.True(RunBytecode(ToBytecodeArray(builder)).IsTrue);
        }
    }

    static readonly Token[] kShiftOperators = [Token.Shl, Token.Sar, Token.Shr];

    static readonly Token[] kArithmeticOperators =
    [
        Token.BitOr, Token.BitXor, Token.BitAnd, Token.Shl,
        Token.Sar, Token.Shr, Token.Add, Token.Sub,
        Token.Mul, Token.Div, Token.Mod,
    ];

    static double BinaryOpC(Token op, double lhs, double rhs)
    {
        switch (op)
        {
            case Token.Add: return lhs + rhs;
            case Token.Sub: return lhs - rhs;
            case Token.Mul: return lhs * rhs;
            case Token.Div: return lhs / rhs;
            case Token.Mod: return lhs % rhs;
            case Token.BitOr: return Conversions.DoubleToInt32(lhs) | Conversions.DoubleToInt32(rhs);
            case Token.BitXor: return Conversions.DoubleToInt32(lhs) ^ Conversions.DoubleToInt32(rhs);
            case Token.BitAnd: return Conversions.DoubleToInt32(lhs) & Conversions.DoubleToInt32(rhs);
            case Token.Shl: return unchecked(Conversions.DoubleToInt32(lhs) << (int)(Conversions.DoubleToUint32(rhs) & 0x1F));
            case Token.Sar:
            {
                int val = Conversions.DoubleToInt32(lhs);
                int count = (int)(Conversions.DoubleToUint32(rhs) & 0x1F);
                return val >> count;
            }
            case Token.Shr:
            {
                uint val = Conversions.DoubleToUint32(lhs);
                int count = (int)(Conversions.DoubleToUint32(rhs) & 0x1F);
                return val >> count;
            }
            default:
                throw new InvalidOperationException();
        }
    }

    [Fact]
    public void InterpreterShiftOpsSmi()
    {
        int[] lhsInputs = [0, -17, -182, 1073741823, -1];
        int[] rhsInputs = [5, 2, 1, -1, -2, 0, 31, 32, -32, 64, 37];
        foreach (int lhs in lhsInputs)
        {
            foreach (int rhs in rhsInputs)
            {
                foreach (Token op in kShiftOperators)
                {
                    var builder = new BytecodeArrayBuilder(1, 1);
                    var reg = new Register(0);
                    builder.LoadLiteral(Smi.FromInt(lhs))
                        .StoreAccumulatorInRegister(reg)
                        .LoadLiteral(Smi.FromInt(rhs))
                        .BinaryOperation(op, reg, kFeedbackIsEmbedded)
                        .Return();
                    var tester = new InterpreterTester(i_isolate, ToBytecodeArray(builder));
                    JSValue returnValue = tester.Call();
                    Assert.True(SameValue(returnValue, Num(BinaryOpC(op, lhs, rhs))));
                }
            }
        }
    }

    [Fact]
    public void InterpreterBinaryOpsSmi()
    {
        int[] lhsInputs = [3266, 1024, 0, -17, -18000];
        int[] rhsInputs = [3266, 5, 4, 3, 2, 1, -1, -2];
        foreach (int lhs in lhsInputs)
        {
            foreach (int rhs in rhsInputs)
            {
                foreach (Token op in kArithmeticOperators)
                {
                    var builder = new BytecodeArrayBuilder(1, 1);
                    var reg = new Register(0);
                    builder.LoadLiteral(Smi.FromInt(lhs))
                        .StoreAccumulatorInRegister(reg)
                        .LoadLiteral(Smi.FromInt(rhs))
                        .BinaryOperation(op, reg, kFeedbackIsEmbedded)
                        .Return();
                    var tester = new InterpreterTester(i_isolate, ToBytecodeArray(builder));
                    Assert.True(SameValue(tester.Call(), Num(BinaryOpC(op, lhs, rhs))));
                }
            }
        }
    }

    [Fact]
    public void InterpreterBinaryOpsHeapNumber()
    {
        double[] lhsInputs = [3266.101, 1024.12, 0.01, -17.99, -18000.833, 9.1e17];
        double[] rhsInputs = [3266.101, 5.999, 4.778, 3.331, 2.643, 1.1, -1.8, -2.9, 8.3e-27];
        foreach (double lhs in lhsInputs)
        {
            foreach (double rhs in rhsInputs)
            {
                foreach (Token op in kArithmeticOperators)
                {
                    var builder = new BytecodeArrayBuilder(1, 1);
                    var reg = new Register(0);
                    builder.LoadLiteral(lhs)
                        .StoreAccumulatorInRegister(reg)
                        .LoadLiteral(rhs)
                        .BinaryOperation(op, reg, kFeedbackIsEmbedded)
                        .Return();
                    var tester = new InterpreterTester(i_isolate, ToBytecodeArray(builder));
                    Assert.True(SameValue(tester.Call(), Num(BinaryOpC(op, lhs, rhs))));
                }
            }
        }
    }

    [Fact]
    public void InterpreterBinaryOpsBigInt()
    {
        // This test only checks that the recorded type feedback is kBigInt.
        string[] inputs = ["1", "-42", "0xFFFF"];
        foreach (string lhs in inputs)
        {
            foreach (string rhs in inputs)
            {
                foreach (Token op in kArithmeticOperators)
                {
                    // Skip over unsigned right shift.
                    if (op == Token.Shr) continue;
                    var builder = new BytecodeArrayBuilder(1, 1);
                    var reg = new Register(0);
                    builder.LoadLiteral(new AstBigInt(lhs)).StoreAccumulatorInRegister(reg).LoadLiteral(new AstBigInt(rhs));
                    int bytecodeOffset = builder.CurrentBytecodeSize();
                    builder.BinaryOperation(op, reg, kFeedbackIsEmbedded).Return();
                    BytecodeArray bytecodeArray = ToBytecodeArray(builder);
                    var tester = new InterpreterTester(i_isolate, bytecodeArray);
                    JSValue returnValue = tester.Call();
                    Assert.True(returnValue.IsBigInt);
                    var embeddedFeedback = tester.GetBinaryEmbeddedFeedback(bytecodeOffset, 2);
                    Assert.True(embeddedFeedback is BinaryOperationFeedback.Type.BigInt64 or BinaryOperationFeedback.Type.BigInt);
                }
            }
        }
    }

    // LiteralForTest: a string, heap number, Smi or oddball literal.
    abstract record LiteralForTest
    {
        public sealed record String(string Value) : LiteralForTest;
        public sealed record HeapNumber(double Value) : LiteralForTest;
        public sealed record SmiLiteral(int Value) : LiteralForTest;
        public sealed record Oddball(string Kind) : LiteralForTest;

        public static readonly LiteralForTest True = new Oddball("true");
        public static readonly LiteralForTest False = new Oddball("false");
        public static readonly LiteralForTest Undefined = new Oddball("undefined");
        public static readonly LiteralForTest Null = new Oddball("null");
    }

    static LiteralForTest L(string s) => new LiteralForTest.String(s);
    static LiteralForTest L(double d) => new LiteralForTest.HeapNumber(d);
    static LiteralForTest L(int smi) => new LiteralForTest.SmiLiteral(smi);

    static void LoadLiteralForTest(BytecodeArrayBuilder builder, LiteralForTest value)
    {
        switch (value)
        {
            case LiteralForTest.String s: builder.LoadLiteralRawString(s.Value); return;
            case LiteralForTest.HeapNumber n: builder.LoadLiteral(n.Value); return;
            case LiteralForTest.SmiLiteral i: builder.LoadLiteral(Smi.FromInt(i.Value)); return;
            case LiteralForTest.Oddball { Kind: "true" }: builder.LoadTrue(); return;
            case LiteralForTest.Oddball { Kind: "false" }: builder.LoadFalse(); return;
            case LiteralForTest.Oddball { Kind: "undefined" }: builder.LoadUndefined(); return;
            case LiteralForTest.Oddball { Kind: "null" }: builder.LoadNull(); return;
        }
        throw new InvalidOperationException();
    }

    [Fact]
    public void InterpreterStringAdd()
    {
        (string lhs, LiteralForTest rhs, string expected, BinaryOperationFeedback.Type feedback)[] testCases =
        [
            ("a", L("b"), "ab", BinaryOperationFeedback.Type.String),
            ("aaaaaa", L("b"), "aaaaaab", BinaryOperationFeedback.Type.String),
            ("aaa", L("bbbbb"), "aaabbbbb", BinaryOperationFeedback.Type.String),
            ("", L("b"), "b", BinaryOperationFeedback.Type.String),
            ("a", L(""), "a", BinaryOperationFeedback.Type.String),
            ("1.11", L(2.5), "1.112.5", BinaryOperationFeedback.Type.Any),
            ("-1.11", L(2.56), "-1.112.56", BinaryOperationFeedback.Type.Any),
            ("", L(2.5), "2.5", BinaryOperationFeedback.Type.Any),
        ];

        foreach (var testCase in testCases)
        {
            var builder = new BytecodeArrayBuilder(1, 1);
            var reg = new Register(0);
            builder.LoadLiteralRawString(testCase.lhs).StoreAccumulatorInRegister(reg);
            LoadLiteralForTest(builder, testCase.rhs);
            int bytecodeOffset = builder.CurrentBytecodeSize();
            builder.BinaryOperation(Token.Add, reg, kFeedbackIsEmbedded).Return();
            var tester = new InterpreterTester(i_isolate, ToBytecodeArray(builder));
            JSValue returnValue = tester.Call();
            Assert.Equal(testCase.expected, returnValue.As<JSString>().ToString());
            Assert.Equal(testCase.feedback, tester.GetBinaryEmbeddedFeedback(bytecodeOffset, 2));
        }
    }

    [Fact]
    public void InterpreterReceiverParameter()
    {
        var builder = new BytecodeArrayBuilder(1, 0);
        builder.LoadAccumulatorWithRegister(builder.Receiver()).Return();
        BytecodeArray bytecodeArray = ToBytecodeArray(builder);
        JSValue obj = InterpreterTester.NewObject(i_isolate, "({ val : 123 })");
        var tester = new InterpreterTester(i_isolate, bytecodeArray);
        JSValue returnVal = tester.CallWithReceiver(obj);
        Assert.True(returnVal.IsIdenticalTo(obj));
    }

    [Fact]
    public void InterpreterParameter0()
    {
        var builder = new BytecodeArrayBuilder(2, 0);
        builder.LoadAccumulatorWithRegister(builder.Parameter(0)).Return();
        var tester = new InterpreterTester(i_isolate, ToBytecodeArray(builder));

        // Check for heap objects.
        Assert.True(tester.Call(JSValue.True).IsTrue);

        // Check for Smis.
        Assert.Equal(3, tester.Call(JSValue.FromInt(3)).Number);
    }

    [Fact]
    public void InterpreterParameter8()
    {
        var builder = new BytecodeArrayBuilder(8, 0);
        builder.LoadAccumulatorWithRegister(builder.Receiver())
            .BinaryOperation(Token.Add, builder.Parameter(0), kFeedbackIsEmbedded)
            .BinaryOperation(Token.Add, builder.Parameter(1), kFeedbackIsEmbedded)
            .BinaryOperation(Token.Add, builder.Parameter(2), kFeedbackIsEmbedded)
            .BinaryOperation(Token.Add, builder.Parameter(3), kFeedbackIsEmbedded)
            .BinaryOperation(Token.Add, builder.Parameter(4), kFeedbackIsEmbedded)
            .BinaryOperation(Token.Add, builder.Parameter(5), kFeedbackIsEmbedded)
            .BinaryOperation(Token.Add, builder.Parameter(6), kFeedbackIsEmbedded)
            .Return();
        var tester = new InterpreterTester(i_isolate, ToBytecodeArray(builder));
        JSValue returnVal = tester.CallWithReceiver(JSValue.FromInt(1), JSValue.FromInt(2), JSValue.FromInt(3),
            JSValue.FromInt(4), JSValue.FromInt(5), JSValue.FromInt(6), JSValue.FromInt(7), JSValue.FromInt(8));
        Assert.Equal(36, returnVal.Number);
    }

    [Fact]
    public void InterpreterBinaryOpTypeFeedback()
    {
        bool additiveSafe = i_isolate.Flags.additive_safe_int_feedback;
        (Token op, LiteralForTest arg1, LiteralForTest arg2, JSValue result, BinaryOperationFeedback.Type feedback)[] kTestCases =
        [
            // ADD
            (Token.Add, L(2), L(3), JSValue.FromInt(5), BinaryOperationFeedback.Type.SignedSmall),
            (Token.Add, L(Smi.kMaxValue), L(1), Num(Smi.kMaxValue + 1.0),
                additiveSafe ? BinaryOperationFeedback.Type.AdditiveSafeInteger : BinaryOperationFeedback.Type.Number),
            (Token.Add, L(3.1415), L(3), Num(3.1415 + 3), BinaryOperationFeedback.Type.Number),
            (Token.Add, L(3.1415), L(1.4142), Num(3.1415 + 1.4142), BinaryOperationFeedback.Type.Number),
            (Token.Add, L("foo"), L("bar"), Str("foobar"), BinaryOperationFeedback.Type.String),
            (Token.Add, L(2), L("2"), Str("22"), BinaryOperationFeedback.Type.Any),
            // SUB
            (Token.Sub, L(2), L(3), JSValue.FromInt(-1), BinaryOperationFeedback.Type.SignedSmall),
            (Token.Sub, L(Smi.kMinValue), L(1), Num(Smi.kMinValue - 1.0), BinaryOperationFeedback.Type.Number),
            (Token.Sub, L(3.1415), L(3), Num(3.1415 - 3), BinaryOperationFeedback.Type.Number),
            (Token.Sub, L(3.1415), L(1.4142), Num(3.1415 - 1.4142), BinaryOperationFeedback.Type.Number),
            (Token.Sub, L(2), L("1"), JSValue.FromInt(1), BinaryOperationFeedback.Type.Any),
            // MUL
            (Token.Mul, L(2), L(3), JSValue.FromInt(6), BinaryOperationFeedback.Type.SignedSmall),
            (Token.Mul, L(Smi.kMinValue), L(2), Num(Smi.kMinValue * 2.0), BinaryOperationFeedback.Type.Number),
            (Token.Mul, L(3.1415), L(3), Num(3 * 3.1415), BinaryOperationFeedback.Type.Number),
            (Token.Mul, L(3.1415), L(1.4142), Num(3.1415 * 1.4142), BinaryOperationFeedback.Type.Number),
            (Token.Mul, L(2), L("1"), JSValue.FromInt(2), BinaryOperationFeedback.Type.Any),
            // DIV
            (Token.Div, L(6), L(3), JSValue.FromInt(2), BinaryOperationFeedback.Type.SignedSmall),
            (Token.Div, L(3), L(2), Num(3.0 / 2.0), BinaryOperationFeedback.Type.SignedSmallInputs),
            (Token.Div, L(3.1415), L(3), Num(3.1415 / 3), BinaryOperationFeedback.Type.Number),
            (Token.Div, L(3.1415), L(double.NegativeInfinity), Num(-0.0), BinaryOperationFeedback.Type.Number),
            (Token.Div, L(2), L("1"), JSValue.FromInt(2), BinaryOperationFeedback.Type.Any),
            // MOD
            (Token.Mod, L(5), L(3), JSValue.FromInt(2), BinaryOperationFeedback.Type.SignedSmall),
            (Token.Mod, L(-4), L(2), Num(-0.0), BinaryOperationFeedback.Type.Number),
            (Token.Mod, L(3.1415), L(3), Num(3.1415 % 3.0), BinaryOperationFeedback.Type.Number),
            (Token.Mod, L(-3.1415), L(-1.4142), Num(-3.1415 % -1.4142), BinaryOperationFeedback.Type.Number),
            (Token.Mod, L(3), L("-2"), JSValue.FromInt(1), BinaryOperationFeedback.Type.Any),
        ];

        foreach (var testCase in kTestCases)
        {
            var builder = new BytecodeArrayBuilder(1, 1);
            var reg = new Register(0);
            LoadLiteralForTest(builder, testCase.arg1);
            builder.StoreAccumulatorInRegister(reg);
            LoadLiteralForTest(builder, testCase.arg2);
            int bytecodeOffset = builder.CurrentBytecodeSize();
            builder.BinaryOperation(testCase.op, reg, kFeedbackIsEmbedded).Return();
            var tester = new InterpreterTester(i_isolate, ToBytecodeArray(builder));
            JSValue returnVal = tester.Call();
            var actual = tester.GetBinaryEmbeddedFeedback(bytecodeOffset, 2);
            Assert.True(testCase.feedback == actual, $"{testCase.op} {testCase.arg1} {testCase.arg2}: {actual}");
            Assert.True(ObjectOps.Equals(i_isolate, testCase.result, returnVal));
        }
    }
}
