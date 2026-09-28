// Port of test/unittests/interpreter/interpreter-unittest.cc (continued):
// comparisons, typeof, instanceof/in, unary not, runtime calls, literals.
using V8Sharp.Ast;
using V8Sharp.Base.Numbers;
using V8Sharp.Interpreter;
using V8Sharp.Objects;
using V8Sharp.Parsing;
using V8Sharp.Runtime;
using ToBooleanMode = V8Sharp.Interpreter.BytecodeArrayBuilder.ToBooleanMode;
using LiteralFlag = V8Sharp.Interpreter.TestTypeOfFlags.LiteralFlag;

namespace V8Sharp.Tests.Interpreter;

public partial class InterpreterUnitTest
{
    static readonly Token[] kComparisonTypes =
    [
        Token.Eq, Token.EqStrict, Token.LessThan,
        Token.LessThanEq, Token.GreaterThan, Token.GreaterThanEq,
    ];

    static bool CompareC(Token op, double lhs, double rhs, bool typesDiffered = false) => op switch
    {
        Token.Eq => lhs == rhs,
        Token.NotEq => lhs != rhs,
        Token.EqStrict => lhs == rhs && !typesDiffered,
        Token.NotEqStrict => lhs != rhs || typesDiffered,
        Token.LessThan => lhs < rhs,
        Token.LessThanEq => lhs <= rhs,
        Token.GreaterThan => lhs > rhs,
        Token.GreaterThanEq => lhs >= rhs,
        _ => throw new InvalidOperationException(),
    };

    static bool CompareC(Token op, string lhs, string rhs)
    {
        int c = string.CompareOrdinal(lhs, rhs);
        return op switch
        {
            Token.Eq or Token.EqStrict => c == 0,
            Token.NotEq or Token.NotEqStrict => c != 0,
            Token.LessThan => c < 0,
            Token.LessThanEq => c <= 0,
            Token.GreaterThan => c > 0,
            Token.GreaterThanEq => c >= 0,
            _ => throw new InvalidOperationException(),
        };
    }

    bool BooleanValue(JSValue value)
    {
        Assert.True(value.IsBoolean);
        return value.IsTrue;
    }

    [Fact]
    public void InterpreterSmiComparisons()
    {
        // NB Constants cover 31-bit space.
        int[] inputs =
        [
            int.MinValue / 2, int.MinValue / 4, -108733832, -999, -42, -2, -1, 0, +1, +2, 42, 12345678,
            int.MaxValue / 4, int.MaxValue / 2,
        ];
        foreach (Token comparison in kComparisonTypes)
        {
            foreach (int lhs in inputs)
            {
                foreach (int rhs in inputs)
                {
                    var builder = new BytecodeArrayBuilder(1, 1);
                    var r0 = new Register(0);
                    builder.LoadLiteral(Smi.FromInt(lhs)).StoreAccumulatorInRegister(r0).LoadLiteral(Smi.FromInt(rhs));
                    int comparisonBytecodeOffset = builder.CurrentBytecodeSize();
                    builder.CompareOperation(comparison, r0, kFeedbackIsEmbedded).Return();
                    var tester = new InterpreterTester(i_isolate, ToBytecodeArray(builder));
                    Assert.Equal(CompareC(comparison, lhs, rhs), BooleanValue(tester.Call()));
                    Assert.Equal(CompareOperationFeedback.Type.SignedSmall,
                        tester.GetCompareEmbeddedFeedback(comparisonBytecodeOffset, 2));
                }
            }
        }
    }

    [Fact]
    public void InterpreterHeapNumberComparisons()
    {
        double[] inputs = [2.2250738585072014E-308, double.MaxValue, -0.001, 0.01, 0.1000001, 1e99, -1e-99];
        foreach (Token comparison in kComparisonTypes)
        {
            foreach (double lhs in inputs)
            {
                foreach (double rhs in inputs)
                {
                    var builder = new BytecodeArrayBuilder(1, 1);
                    var r0 = new Register(0);
                    builder.LoadLiteral(lhs).StoreAccumulatorInRegister(r0).LoadLiteral(rhs);
                    int comparisonBytecodeOffset = builder.CurrentBytecodeSize();
                    builder.CompareOperation(comparison, r0, kFeedbackIsEmbedded).Return();
                    var tester = new InterpreterTester(i_isolate, ToBytecodeArray(builder));
                    Assert.Equal(CompareC(comparison, lhs, rhs), BooleanValue(tester.Call()));
                    Assert.Equal(CompareOperationFeedback.Type.Number,
                        tester.GetCompareEmbeddedFeedback(comparisonBytecodeOffset, 2));
                }
            }
        }
    }

    [Fact]
    public void InterpreterBigIntComparisons()
    {
        // This test only checks that the recorded type feedback is kBigInt.
        string[] inputs = ["0", "-42", "0xFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFF"];
        foreach (Token comparison in kComparisonTypes)
        {
            foreach (string lhs in inputs)
            {
                foreach (string rhs in inputs)
                {
                    var builder = new BytecodeArrayBuilder(1, 1);
                    var r0 = new Register(0);
                    builder.LoadLiteral(new AstBigInt(lhs)).StoreAccumulatorInRegister(r0).LoadLiteral(new AstBigInt(rhs));
                    int comparisonBytecodeOffset = builder.CurrentBytecodeSize();
                    builder.CompareOperation(comparison, r0, kFeedbackIsEmbedded).Return();
                    var tester = new InterpreterTester(i_isolate, ToBytecodeArray(builder));
                    Assert.True(tester.Call().IsBoolean);
                    var feedback = tester.GetCompareEmbeddedFeedback(comparisonBytecodeOffset, 2);
                    Assert.True(feedback is CompareOperationFeedback.Type.BigInt or CompareOperationFeedback.Type.BigInt64);
                }
            }
        }
    }

    static bool IsOrderedRelationalCompareOp(Token op) =>
        op is Token.LessThan or Token.LessThanEq or Token.GreaterThan or Token.GreaterThanEq;

    [Fact]
    public void InterpreterStringComparisons()
    {
        string[] inputs = ["A", "abc", "z", "", "Foo!", "Foo"];
        foreach (Token comparison in kComparisonTypes)
        {
            foreach (string lhs in inputs)
            {
                foreach (string rhs in inputs)
                {
                    var builder = new BytecodeArrayBuilder(1, 1);
                    var r0 = new Register(0);
                    builder.LoadLiteralRawString(lhs).StoreAccumulatorInRegister(r0).LoadLiteralRawString(rhs);
                    int comparisonBytecodeOffset = builder.CurrentBytecodeSize();
                    builder.CompareOperation(comparison, r0, kFeedbackIsEmbedded).Return();
                    var tester = new InterpreterTester(i_isolate, ToBytecodeArray(builder));
                    Assert.Equal(CompareC(comparison, lhs, rhs), BooleanValue(tester.Call()));
                    var expectedFeedback = IsOrderedRelationalCompareOp(comparison)
                        ? CompareOperationFeedback.Type.String
                        : CompareOperationFeedback.Type.InternalizedString;
                    Assert.Equal(expectedFeedback, tester.GetCompareEmbeddedFeedback(comparisonBytecodeOffset, 2));
                }
            }
        }
    }

    static void LoadStringAndAddSpace(BytecodeArrayBuilder builder, string str)
    {
        Register stringReg = builder.RegisterAllocator().NewRegister();
        builder.LoadLiteralRawString(str)
            .StoreAccumulatorInRegister(stringReg)
            .LoadLiteralRawString(" ")
            .BinaryOperation(Token.Add, stringReg, kFeedbackIsEmbedded);
    }

    [Fact]
    public void InterpreterMixedComparisons()
    {
        // This test compares a HeapNumber with a String. The latter is
        // convertible to a HeapNumber so comparison will be between numeric
        // values except for the strict comparisons where no conversion is
        // performed.
        string[] inputs = ["-1.77", "-40.333", "0.01", "55.77e50", "2.01"];
        foreach (Token comparison in kComparisonTypes)
        {
            foreach (string lhsStr in inputs)
            {
                foreach (string rhsStr in inputs)
                {
                    // We test the case where either the lhs or the rhs is a string...
                    foreach (bool rhsIsString in new[] { false, true })
                    {
                        // ... and the case when the string is internalized or computed.
                        foreach (bool internalized in new[] { true, false })
                        {
                            double lhs = double.Parse(lhsStr, System.Globalization.CultureInfo.InvariantCulture);
                            double rhs = double.Parse(rhsStr, System.Globalization.CultureInfo.InvariantCulture);
                            var builder = new BytecodeArrayBuilder(1, 0);
                            // lhs is in a register, rhs is in the accumulator.
                            Register lhsReg = builder.RegisterAllocator().NewRegister();
                            if (rhsIsString)
                            {
                                builder.LoadLiteral(lhs).StoreAccumulatorInRegister(lhsReg);
                                if (internalized) builder.LoadLiteralRawString(rhsStr);
                                else LoadStringAndAddSpace(builder, rhsStr);
                            }
                            else
                            {
                                if (internalized) builder.LoadLiteralRawString(lhsStr);
                                else LoadStringAndAddSpace(builder, lhsStr);
                                builder.StoreAccumulatorInRegister(lhsReg);
                                builder.LoadLiteral(rhs);
                            }
                            int comparisonBytecodeOffset = builder.CurrentBytecodeSize();
                            builder.CompareOperation(comparison, lhsReg, kFeedbackIsEmbedded).Return();
                            var tester = new InterpreterTester(i_isolate, ToBytecodeArray(builder));
                            Assert.Equal(CompareC(comparison, lhs, rhs, true), BooleanValue(tester.Call()));
                            // Comparison with a number and string collects kAny feedback.
                            Assert.Equal(CompareOperationFeedback.Type.Any,
                                tester.GetCompareEmbeddedFeedback(comparisonBytecodeOffset, 2));
                        }
                    }
                }
            }
        }
    }

    [Fact]
    public void InterpreterStrictNotEqual()
    {
        const string codeSnippet = "function f(lhs, rhs) {\n  return lhs !== rhs;\n}\nf(0, 0);\n";
        var tester = new InterpreterTester(i_isolate, codeSnippet);

        // Test passing different types.
        string[] inputs = ["-1.77", "-40.333", "0.01", "55.77e5", "2.01"];
        foreach (string l in inputs)
        {
            foreach (string r in inputs)
            {
                double lhs = double.Parse(l, System.Globalization.CultureInfo.InvariantCulture);
                double rhs = double.Parse(r, System.Globalization.CultureInfo.InvariantCulture);
                Assert.Equal(CompareC(Token.NotEqStrict, lhs, rhs, true), BooleanValue(tester.Call(Num(lhs), Str(r))));
            }
        }

        // Test passing string types.
        string[] inputsStr = ["A", "abc", "z", "", "Foo!", "Foo"];
        foreach (string l in inputsStr)
        {
            foreach (string r in inputsStr)
            {
                Assert.Equal(CompareC(Token.NotEqStrict, l, r), BooleanValue(tester.Call(Str(l), Str(r))));
            }
        }

        // Test passing doubles.
        double[] inputsNumber = [2.2250738585072014E-308, double.MaxValue, -0.001, 0.01, 0.1000001, 1e99, -1e-99];
        foreach (double l in inputsNumber)
        {
            foreach (double r in inputsNumber)
            {
                Assert.Equal(CompareC(Token.NotEqStrict, l, r), BooleanValue(tester.Call(Num(l), Num(r))));
            }
        }
    }

    [Fact]
    public void InterpreterCompareTypeOf()
    {
        (JSValue value, LiteralFlag flag)[] inputs =
        [
            (JSValue.FromInt(24), LiteralFlag.Number),
            (Num(2.5), LiteralFlag.Number),
            (Str("foo"), LiteralFlag.String),
            (factory.NewConsString(factory.NewStringFromUtf16("foo"), factory.NewStringFromUtf16("bar")), LiteralFlag.String),
            (Roots.ReadOnlyRoots.prototype_string, LiteralFlag.String),
            (factory.NewSymbol(), LiteralFlag.Symbol),
            (JSValue.True, LiteralFlag.Boolean),
            (JSValue.False, LiteralFlag.Boolean),
            (JSValue.Undefined, LiteralFlag.Undefined),
            (InterpreterTester.NewObject(i_isolate, "(function() { return function() {}; })();"), LiteralFlag.Function),
            (InterpreterTester.NewObject(i_isolate, "new Object();"), LiteralFlag.Object),
            (JSValue.Null, LiteralFlag.Object),
        ];
        foreach (LiteralFlag literalFlag in Enum.GetValues<LiteralFlag>())
        {
            if (literalFlag == LiteralFlag.Other) continue;
            var builder = new BytecodeArrayBuilder(2, 0);
            builder.LoadAccumulatorWithRegister(builder.Parameter(0)).CompareTypeOf(literalFlag).Return();
            var tester = new InterpreterTester(i_isolate, ToBytecodeArray(builder));
            foreach (var (value, flag) in inputs)
            {
                Assert.Equal(flag == literalFlag, BooleanValue(tester.Call(value)));
            }
        }
    }

    [Fact]
    public void InterpreterInstanceOf()
    {
        JSString name = factory.NewStringFromUtf16("cons");
        JSFunction func = factory.NewFunctionForTesting(name);
        JSObject instance = factory.NewJSObject(func);
        JSValue other = Num(3.3333);
        JSValue[] cases = [instance, other];
        for (int i = 0; i < cases.Length; i++)
        {
            bool expectedValue = i == 0;
            var feedbackSpec = new FeedbackVectorSpec();
            var builder = new BytecodeArrayBuilder(1, 1);
            var r0 = new Register(0);
            int caseEntry = builder.AllocateDeferredConstantPoolEntry();
            builder.SetDeferredConstantPoolEntry(caseEntry, cases[i]);
            builder.LoadConstantPoolEntry(caseEntry).StoreAccumulatorInRegister(r0);

            FeedbackSlot slot = feedbackSpec.AddInstanceOfSlot();
            FeedbackMetadata metadata = FeedbackMetadata.New(feedbackSpec);

            int funcEntry = builder.AllocateDeferredConstantPoolEntry();
            builder.SetDeferredConstantPoolEntry(funcEntry, func);
            builder.LoadConstantPoolEntry(funcEntry).CompareOperation(Token.InstanceOf, r0, GetIndex(slot)).Return();

            Assert.Equal(expectedValue, BooleanValue(RunBytecode(ToBytecodeArray(builder), metadata)));
        }
    }

    [Fact]
    public void InterpreterTestIn()
    {
        // Allocate an array
        JSArray array = factory.NewJSArray(V8Sharp.Objects.ElementsKind.PACKED_SMI_ELEMENTS);
        // Check for these properties on the array object
        string[] properties = ["length", "fuzzle", "x", "0"];
        for (int i = 0; i < properties.Length; i++)
        {
            bool expectedValue = i == 0;
            var feedbackSpec = new FeedbackVectorSpec();
            var builder = new BytecodeArrayBuilder(1, 1);
            var r0 = new Register(0);
            builder.LoadLiteralRawString(properties[i]).StoreAccumulatorInRegister(r0);

            FeedbackSlot slot = feedbackSpec.AddKeyedHasICSlot();
            FeedbackMetadata metadata = FeedbackMetadata.New(feedbackSpec);

            int arrayEntry = builder.AllocateDeferredConstantPoolEntry();
            builder.SetDeferredConstantPoolEntry(arrayEntry, array);
            builder.LoadConstantPoolEntry(arrayEntry).CompareOperation(Token.In, r0, GetIndex(slot)).Return();

            Assert.Equal(expectedValue, BooleanValue(RunBytecode(ToBytecodeArray(builder), metadata)));
        }
    }

    [Fact]
    public void InterpreterUnaryNot()
    {
        for (int i = 1; i < 10; i++)
        {
            bool expectedValue = (i & 1) == 1;
            var builder = new BytecodeArrayBuilder(1, 0);
            builder.LoadFalse();
            for (int j = 0; j < i; j++) builder.LogicalNot(ToBooleanMode.AlreadyBoolean);
            builder.Return();
            Assert.Equal(expectedValue, BooleanValue(RunBytecode(ToBytecodeArray(builder))));
        }
    }

    [Fact]
    public void InterpreterUnaryNotNonBoolean()
    {
        (LiteralForTest literal, bool expected)[] objectTypeTuples =
        [
            (LiteralForTest.Undefined, true),
            (LiteralForTest.Null, true),
            (LiteralForTest.False, true),
            (LiteralForTest.True, false),
            (L(9.1), false),
            (L(0), true),
            (L("hello"), false),
            (L(""), true),
        ];
        foreach (var (literal, expected) in objectTypeTuples)
        {
            var builder = new BytecodeArrayBuilder(1, 0);
            LoadLiteralForTest(builder, literal);
            builder.LogicalNot(ToBooleanMode.ConvertToBoolean).Return();
            Assert.Equal(expected, BooleanValue(RunBytecode(ToBytecodeArray(builder))));
        }
    }

    [Fact]
    public void InterpreterTypeof()
    {
        (string body, string expected)[] typeofVals =
        [
            ("return typeof undefined;", "undefined"),
            ("return typeof null;", "object"),
            ("return typeof true;", "boolean"),
            ("return typeof false;", "boolean"),
            ("return typeof 9.1;", "number"),
            ("return typeof 7771;", "number"),
            ("return typeof 'hello';", "string"),
            ("return typeof global_unallocated;", "undefined"),
        ];
        foreach (var (body, expected) in typeofVals)
        {
            var tester = new InterpreterTester(i_isolate, InterpreterTester.SourceForBody(body));
            Assert.Equal(expected, tester.Call().As<JSString>().ToString());
        }
    }

    [Fact]
    public void InterpreterCallRuntime()
    {
        var builder = new BytecodeArrayBuilder(1, 2);
        RegisterList args = builder.RegisterAllocator().NewRegisterList(2);
        builder.LoadLiteral(Smi.FromInt(15))
            .StoreAccumulatorInRegister(args[0])
            .LoadLiteral(Smi.FromInt(40))
            .StoreAccumulatorInRegister(args[1])
            .CallRuntime(FunctionId.Add, args)
            .Return();
        Assert.Equal(55, RunBytecode(ToBytecodeArray(builder)).Number);
    }

    [Fact]
    public void InterpreterFunctionLiteral()
    {
        // Test calling a function literal.
        string source = "function " + InterpreterTester.function_name() + "(a) {\n  return (function(x){ return x + 2; })(a);\n}";
        var tester = new InterpreterTester(i_isolate, source);
        Assert.Equal(5, tester.Call(JSValue.FromInt(3)).Number);
    }

    void CheckBodies((string body, JSValue expected)[] literals)
    {
        foreach (var (body, expected) in literals)
        {
            var tester = new InterpreterTester(i_isolate, InterpreterTester.SourceForBody(body));
            JSValue returnValue = tester.Call();
            Assert.True(ObjectOps.SameValue(returnValue, expected) ||
                        (returnValue.IsString && expected.IsString &&
                         returnValue.As<JSString>().ToString() == expected.As<JSString>().ToString()),
                body);
        }
    }

    [Fact]
    public void InterpreterRegExpLiterals()
    {
        CheckBodies(
        [
            ("return /abd/.exec('cccabbdd');\n", JSValue.Null),
            ("return /ab+d/.exec('cccabbdd')[0];\n", Str("abbd")),
            ("return /AbC/i.exec('ssaBC')[0];\n", Str("aBC")),
            ("return 'ssaBC'.match(/AbC/i)[0];\n", Str("aBC")),
            ("return 'ssaBCtAbC'.match(/(AbC)/gi)[1];\n", Str("AbC")),
        ]);
    }

    [Fact]
    public void InterpreterArrayLiterals()
    {
        CheckBodies(
        [
            ("return [][0];\n", JSValue.Undefined),
            ("return [1, 3, 2][1];\n", JSValue.FromInt(3)),
            ("return ['a', 'b', 'c'][2];\n", Str("c")),
            ("var a = 100; return [a, a + 1, a + 2, a + 3][2];\n", JSValue.FromInt(102)),
            ("return [[1, 2, 3], ['a', 'b', 'c']][1][0];\n", Str("a")),
            ("var t = 't'; return [[t, t + 'est'], [1 + t]][0][1];\n", Str("test")),
        ]);
    }

    [Fact]
    public void InterpreterObjectLiterals()
    {
        CheckBodies(
        [
            ("return { }.name;", JSValue.Undefined),
            ("return { name: 'string', val: 9.2 }.name;", Str("string")),
            ("var a = 15; return { name: 'string', val: a }.val;", JSValue.FromInt(15)),
            ("var a = 5; return { val: a, val: a + 1 }.val;", JSValue.FromInt(6)),
            ("return { func: function() { return 'test' } }.func();", Str("test")),
            ("return { func(a) { return a + 'st'; } }.func('te');", Str("test")),
            ("return { get a() { return 22; } }.a;", JSValue.FromInt(22)),
            ("var a = { get b() { return this.x + 't'; },\n          set b(val) { this.x = val + 's' } };\na.b = 'te';\nreturn a.b;",
                Str("test")),
            ("var a = 123; return { 1: a }[1];", JSValue.FromInt(123)),
            ("return Object.getPrototypeOf({ __proto__: null });", JSValue.Null),
            ("var a = 'test'; return { [a]: 1 }.test;", JSValue.FromInt(1)),
            ("var a = 'test'; return { b: a, [a]: a + 'ing' }['test']", Str("testing")),
            ("var a = 'proto_str';\nvar b = { [a]: 1, __proto__: { var : a } };\nreturn Object.getPrototypeOf(b).var",
                Str("proto_str")),
            ("var n = 'name';\nreturn { [n]: 'val', get a() { return 987 } }['a'];", JSValue.FromInt(987)),
        ]);
    }

    [Fact]
    public void InterpreterConstruct()
    {
        string source = "function counter() { this.count = 0; }\nfunction " + InterpreterTester.function_name() +
                        "() {\n  var c = new counter();\n  return c.count;\n}";
        Assert.Equal(0, new InterpreterTester(i_isolate, source).Call().Number);
    }

    [Fact]
    public void InterpreterConstructWithArgument()
    {
        string source = "function counter(arg0) { this.count = 17; this.x = arg0; }\nfunction " +
                        InterpreterTester.function_name() + "() {\n  var c = new counter(3);\n  return c.x;\n}";
        Assert.Equal(3, new InterpreterTester(i_isolate, source).Call().Number);
    }

    [Fact]
    public void InterpreterConstructWithArguments()
    {
        string source = "function counter(arg0, arg1) {\n  this.count = 7; this.x = arg0; this.y = arg1;\n}\nfunction " +
                        InterpreterTester.function_name() + "() {\n  var c = new counter(3, 5);\n  return c.count + c.x + c.y;\n}";
        Assert.Equal(15, new InterpreterTester(i_isolate, source).Call().Number);
    }
}
