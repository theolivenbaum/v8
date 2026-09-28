// Port of test/unittests/interpreter/interpreter-unittest.cc (continued):
// classes, const declarations, generators and stack traces.
//
// Not ported, because V8Sharp has no machine code: InterpreterWithNativeStack,
// InterpreterGetBytecodeHandler and InterpreterLookupNameOfBytecodeHandler
// (bytecode handler Code objects). Not ported because source positions are
// collected eagerly (deviations.md, Codegen): InterpreterCollectSourcePositions,
// _StackOverflow, _ThrowFrom1stFrame and _ThrowFrom2ndFrame, which check that
// the table is absent until collected; the stack-trace half of
// _GenerateStackTrace is ported.
using V8Sharp.Objects;

namespace V8Sharp.Tests.Interpreter;

public partial class InterpreterUnitTest
{
    [Fact]
    public void InterpreterClassLiterals()
    {
        CheckBodies(
        [
            ("class C {\n  constructor(x) { this.x_ = x; }\n  method() { return this.x_; }\n}\nreturn new C(99).method();",
                JSValue.FromInt(99)),
            ("class C {\n  constructor(x) { this.x_ = x; }\n  static static_method(x) { return x; }\n}\nreturn C.static_method(101);",
                JSValue.FromInt(101)),
            ("class C {\n  get x() { return 102; }\n}\nreturn new C().x", JSValue.FromInt(102)),
            ("class C {\n  static get x() { return 103; }\n}\nreturn C.x", JSValue.FromInt(103)),
            ("class C {\n  constructor() { this.x_ = 0; }  set x(value) { this.x_ = value; }\n  get x() { return this.x_; }\n}\n" +
             "var c = new C();c.x = 104;return c.x;", JSValue.FromInt(104)),
            ("var x = 0;class C {\n  static set x(value) { x = value; }\n  static get x() { return x; }\n}\nC.x = 105;return C.x;",
                JSValue.FromInt(105)),
            ("var method = 'f';class C {\n  [method]() { return 106; }\n}\nreturn new C().f();", JSValue.FromInt(106)),
        ]);
    }

    [Fact]
    public void InterpreterClassAndSuperClass()
    {
        CheckBodies(
        [
            ("class A {\n  constructor(x) { this.x_ = x; }\n  method() { return this.x_; }\n}\nclass B extends A {\n" +
             "   constructor(x, y) { super(x); this.y_ = y; }\n   method() { return super.method() + 1; }\n}\n" +
             "return new B(998, 0).method();\n", JSValue.FromInt(999)),
            ("class A {\n  constructor() { this.x_ = 2; this.y_ = 3; }\n}\nclass B extends A {\n  constructor() { super(); }" +
             "  method() { this.x_++; this.y_++; return this.x_ + this.y_; }\n}\nreturn new B().method();\n", JSValue.FromInt(7)),
            ("var calls = 0;\nclass B {}\nB.prototype.x = 42;\nclass C extends B {\n  constructor() {\n    super();\n    calls++;\n" +
             "  }\n}\nnew C;\nreturn calls;\n", JSValue.FromInt(1)),
            ("class A {\n  method() { return 1; }\n  get x() { return 2; }\n}\nclass B extends A {\n" +
             "  method() { return super.x === 2 ? super.method() : -1; }\n}\nreturn new B().method();\n", JSValue.FromInt(1)),
            ("var object = { setY(v) { super.y = v; }};\nobject.setY(10);\nreturn object.y;\n", JSValue.FromInt(10)),
        ]);
    }

    static (string, JSValue)[] WithStrict((string body, JSValue expected)[] cases, bool strict)
    {
        var result = new (string, JSValue)[cases.Length];
        for (int i = 0; i < cases.Length; i++) result[i] = ((strict ? "'use strict'; " : "") + cases[i].body, cases[i].expected);
        return result;
    }

    [Fact]
    public void InterpreterConstDeclaration()
    {
        (string body, JSValue expected)[] constDecl =
        [
            ("const x = 3; return x;", JSValue.FromInt(3)),
            ("let x = 10; x = x + 20; return x;", JSValue.FromInt(30)),
            ("let x = 10; x = 20; return x;", JSValue.FromInt(20)),
            ("let x; x = 20; return x;", JSValue.FromInt(20)),
            ("let x; return x;", JSValue.Undefined),
            ("var x = 10; { let x = 30; } return x;", JSValue.FromInt(10)),
            ("let x = 10; { let x = 20; } return x;", JSValue.FromInt(10)),
            ("var x = 10; eval('let x = 20;'); return x;", JSValue.FromInt(10)),
            ("var x = 10; eval('const x = 20;'); return x;", JSValue.FromInt(10)),
            ("var x = 10; { const x = 20; } return x;", JSValue.FromInt(10)),
            ("var x = 10; { const x = 20; return x;} return -1;", JSValue.FromInt(20)),
            ("var a = 10;\nfor (var i = 0; i < 10; ++i) {\n const x = i;\n a = a + x;\n}\nreturn a;\n", JSValue.FromInt(55)),
        ];
        // Tests for sloppy mode.
        CheckBodies(WithStrict(constDecl, false));
        // Tests for strict mode.
        CheckBodies(WithStrict(constDecl, true));
    }

    [Fact]
    public void InterpreterConstDeclarationLookupSlots()
    {
        (string body, JSValue expected)[] constDecl =
        [
            ("const x = 3; function f1() {return x;}; return x;", JSValue.FromInt(3)),
            ("let x = 10; x = x + 20; function f1() {return x;}; return x;", JSValue.FromInt(30)),
            ("let x; x = 20; function f1() {return x;}; return x;", JSValue.FromInt(20)),
            ("let x; function f1() {return x;}; return x;", JSValue.Undefined),
        ];
        CheckBodies(WithStrict(constDecl, false));
        CheckBodies(WithStrict(constDecl, true));
    }

    [Fact]
    public void InterpreterConstInLookupContextChain()
    {
        const string prologue = "function OuterMost() {\n  const outerConst = 10;\n  let outerLet = 20;\n  function Outer() {\n" +
                                "    function Inner() {\n      this.innerFunc = function() { ";
        const string epilogue = "      }\n    }\n    this.getInnerFunc =         function() {return new Inner().innerFunc;}\n  }\n" +
                                "  this.getOuterFunc =     function() {return new Outer().getInnerFunc();}}\n" +
                                "var f = new OuterMost().getOuterFunc();\nf();\n";
        (string body, int expected)[] constDecl =
        [
            ("return outerConst;", 10),
            ("return outerLet;", 20),
            ("outerLet = 30; return outerLet;", 30),
            ("var outerLet = 40; return outerLet;", 40),
            ("var outerConst = 50; return outerConst;", 50),
            ("try { outerConst = 30 } catch(e) { return -1; }", -1),
        ];
        var cases = new (string, JSValue)[constDecl.Length];
        for (int i = 0; i < constDecl.Length; i++) cases[i] = (prologue + constDecl[i].body + epilogue, JSValue.FromInt(constDecl[i].expected));
        CheckSources(cases);
    }

    [Fact]
    public void InterpreterIllegalConstDeclaration()
    {
        (string body, string message)[] constDecl =
        [
            ("const x = x = 10 + 3; return x;", "Uncaught ReferenceError: Cannot access 'x' before initialization"),
            ("const x = 10; x = 20; return x;", "Uncaught TypeError: Assignment to constant variable."),
            ("const x = 10; { x = 20; } return x;", "Uncaught TypeError: Assignment to constant variable."),
            ("const x = 10; eval('x = 20;'); return x;", "Uncaught TypeError: Assignment to constant variable."),
            ("let x = x + 10; return x;", "Uncaught ReferenceError: Cannot access 'x' before initialization"),
            ("'use strict'; (function f1() { f1 = 123; })() ", "Uncaught TypeError: Assignment to constant variable."),
        ];
        foreach (bool strict in new[] { false, true })
        {
            foreach (var (body, expected) in constDecl)
            {
                string source = InterpreterTester.SourceForBody((strict ? "'use strict'; " : "") + body);
                var tester = new InterpreterTester(i_isolate, source);
                JSMessageObject message = tester.CheckThrowsReturnMessage();
                Assert.Equal(expected, MessageHandler.GetMessage(i_isolate, message).ToString());
            }
        }
    }

    [Fact]
    public void InterpreterGenerators()
    {
        CheckBodies(
        [
            ("function* f() { }; return f().next().value", JSValue.Undefined),
            ("function* f() { yield 42 }; return f().next().value", JSValue.FromInt(42)),
            ("function* f() { for (let x of [42]) yield x}; return f().next().value", JSValue.FromInt(42)),
        ]);
    }

    [Fact]
    public void InterpreterCollectSourcePositions_GenerateStackTrace()
    {
        const string source = "\n      (function () {\n        try {\n          throw new Error();\n        } catch (e) {\n" +
                              "          return e.stack;\n        }\n      });\n      ";
        JSValue function = CompileRun(source);
        JSValue result = Execution.Call(i_isolate, function, JSValue.Undefined, []);
        Assert.Equal("Error\n    at <anonymous>:4:17", result.As<JSString>().ToString());
    }
}
