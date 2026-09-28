// Port of test/unittests/interpreter/interpreter-unittest.cc (continued):
// lookup slots, eval, wide registers and parameters, with.
using V8Sharp.Objects;

namespace V8Sharp.Tests.Interpreter;

public partial class InterpreterUnitTest
{
    const string kLookupPrologue = "var f;var x = 1;function f1() {  eval(\"function t() {";
    const string kLookupEpilogue = "        }; f = t;\");}f1();";

    [Fact]
    public void InterpreterLookupSlot()
    {
        (string body, JSValue expected)[] lookupSlot =
        [
            ("return x;", JSValue.FromInt(1)),
            ("return typeof x;", Str("number")),
            ("return typeof dummy;", Str("undefined")),
            ("x = 10; return x;", JSValue.FromInt(10)),
            ("'use strict'; x = 20; return x;", JSValue.FromInt(20)),
        ];
        var cases = new (string, JSValue)[lookupSlot.Length];
        for (int i = 0; i < lookupSlot.Length; i++) cases[i] = (kLookupPrologue + lookupSlot[i].body + kLookupEpilogue, lookupSlot[i].expected);
        CheckSources(cases);
    }

    void CheckInnerOuter((string outer, string inner, JSValue expected)[] lookupSlot)
    {
        var cases = new (string, JSValue)[lookupSlot.Length];
        for (int i = 0; i < lookupSlot.Length; i++)
        {
            string body = lookupSlot[i].outer + "function inner() {" + lookupSlot[i].inner + "};" + "return inner();";
            cases[i] = (InterpreterTester.SourceForBody(body), lookupSlot[i].expected);
        }
        CheckSources(cases);
    }

    [Fact]
    public void InterpreterLookupContextSlot()
    {
        CheckInnerOuter(
        [
            // Eval in inner context.
            ("var x = 0;", "eval(''); return x;", JSValue.FromInt(0)),
            ("var x = 0;", "eval('var x = 1'); return x;", JSValue.FromInt(1)),
            ("var x = 0;", "'use strict'; eval('var x = 1'); return x;", JSValue.FromInt(0)),
            // Eval in outer context.
            ("var x = 0; eval('');", "return x;", JSValue.FromInt(0)),
            ("var x = 0; eval('var x = 1');", "return x;", JSValue.FromInt(1)),
            ("'use strict'; var x = 0; eval('var x = 1');", "return x;", JSValue.FromInt(0)),
        ]);
    }

    [Fact]
    public void InterpreterLookupGlobalSlot()
    {
        CheckInnerOuter(
        [
            // Eval in inner context.
            ("x = 0;", "eval(''); return x;", JSValue.FromInt(0)),
            ("x = 0;", "eval('var x = 1'); return x;", JSValue.FromInt(1)),
            ("x = 0;", "'use strict'; eval('var x = 1'); return x;", JSValue.FromInt(0)),
            // Eval in outer context.
            ("x = 0; eval('');", "return x;", JSValue.FromInt(0)),
            ("x = 0; eval('var x = 1');", "return x;", JSValue.FromInt(1)),
            ("'use strict'; x = 0; eval('var x = 1');", "return x;", JSValue.FromInt(0)),
        ]);
    }

    [Fact]
    public void InterpreterCallLookupSlot()
    {
        CheckBodies(
        [
            ("g = function(){ return 2 }; eval(''); return g();", JSValue.FromInt(2)),
            ("g = function(){ return 2 }; eval('g = function() {return 3}');\nreturn g();", JSValue.FromInt(3)),
            ("g = { x: function(){ return this.y }, y: 20 };\neval('g = { x: g.x, y: 30 }');\nreturn g.x();", JSValue.FromInt(30)),
        ]);
    }

    [Fact]
    public void InterpreterLookupSlotWide()
    {
        var str = new System.Text.StringBuilder("var y = 2.3;");
        for (int i = 1; i < 256; i++)
        {
            // C++'s ostream prints 2.3 + i with six significant digits.
            str.Append("y = ").Append(2 + i).Append(".3;");
        }
        string initFunctionBody = str.ToString();
        (string body, JSValue expected)[] lookupSlot =
        [
            (initFunctionBody + "return x;", JSValue.FromInt(1)),
            (initFunctionBody + "return typeof x;", Str("number")),
            (initFunctionBody + "return x = 10;", JSValue.FromInt(10)),
            ("'use strict';" + initFunctionBody + "x = 20; return x;", JSValue.FromInt(20)),
        ];
        var cases = new (string, JSValue)[lookupSlot.Length];
        for (int i = 0; i < lookupSlot.Length; i++) cases[i] = (kLookupPrologue + lookupSlot[i].body + kLookupEpilogue, lookupSlot[i].expected);
        CheckSources(cases);
    }

    [Fact]
    public void InterpreterDeleteLookupSlot()
    {
        const string prologue = "var f;var x = 1;y = 10;var obj = {val:10};var z = 30;function f1() {  var z = 20;  eval(\"function t() {";
        (string body, JSValue expected)[] deleteLookupSlot =
        [
            ("return delete x;", JSValue.False),
            ("return delete y;", JSValue.True),
            ("return delete z;", JSValue.False),
            ("return delete obj.val;", JSValue.True),
            ("'use strict'; return delete obj.val;", JSValue.True),
        ];
        var cases = new (string, JSValue)[deleteLookupSlot.Length];
        for (int i = 0; i < deleteLookupSlot.Length; i++) cases[i] = (prologue + deleteLookupSlot[i].body + kLookupEpilogue, deleteLookupSlot[i].expected);
        CheckSources(cases);
    }

    [Fact]
    public void JumpWithConstantsAndWideConstants()
    {
        const int kStep = 13;
        int[] results = [11, 12, 2];
        for (int constants = 11; constants < 256 + 3 * kStep; constants += kStep)
        {
            // Generate a string that consumes constant pool entries and
            // spread out branch distances in script below.
            var fillerOs = new System.Text.StringBuilder();
            for (int i = 0; i < constants; i++) fillerOs.Append("var x_ = 'x_").Append(i).Append("';\n");
            string filler = fillerOs.ToString();
            string script = "function " + InterpreterTester.function_name() + "(a) {\n" +
                            "  " + filler +
                            "  for (var i = a; i < 2; i++) {\n" +
                            "  " + filler +
                            "    if (i == 0) { " + filler + "i = 10; continue; }\n" +
                            "    else if (i == a) { " + filler + "i = 12; break; }\n" +
                            "    else { " + filler + " }\n" +
                            "  }\n" +
                            "  return i;\n" +
                            "}\n";
            for (int a = 0; a < 3; a++)
            {
                var tester = new InterpreterTester(i_isolate, script);
                JSValue returnVal = tester.Call(JSValue.FromInt(a));
                Assert.Equal(results[a], returnVal.Number);
            }
        }
    }

    [Fact]
    public void InterpreterEval()
    {
        CheckBodies(
        [
            ("return eval('1;');", JSValue.FromInt(1)),
            ("return eval('100 * 20;');", JSValue.FromInt(2000)),
            ("var x = 10; return eval('x + 20;');", JSValue.FromInt(30)),
            ("var x = 10; eval('x = 33;'); return x;", JSValue.FromInt(33)),
            ("'use strict'; var x = 20; var z = 0;\neval('var x = 33; z = x;'); return x + z;", JSValue.FromInt(53)),
            ("eval('var x = 33;'); eval('var y = x + 20'); return x + y;", JSValue.FromInt(86)),
            ("var x = 1; eval('for(i = 0; i < 10; i++) x = x + 1;'); return x", JSValue.FromInt(11)),
            ("var x = 10; eval('var x = 20;'); return x;", JSValue.FromInt(20)),
            ("var x = 1; eval('\"use strict\"; var x = 2;'); return x;", JSValue.FromInt(1)),
            ("'use strict'; var x = 1; eval('var x = 2;'); return x;", JSValue.FromInt(1)),
            ("var x = 10; eval('x + 20;'); return typeof x;", Str("number")),
            ("eval('var y = 10;'); return typeof unallocated;", Str("undefined")),
            ("'use strict'; eval('var y = 10;'); return typeof unallocated;", Str("undefined")),
            ("eval('var x = 10;'); return typeof x;", Str("number")),
            ("var x = {}; eval('var x = 10;'); return typeof x;", Str("number")),
            ("'use strict'; var x = {}; eval('var x = 10;'); return typeof x;", Str("object")),
        ]);
    }

    [Fact]
    public void InterpreterEvalParams()
    {
        (string body, int expected)[] evalParams =
        [
            ("var x = 10; return eval('x + p1;');", 30),
            ("var x = 10; eval('p1 = x;'); return p1;", 10),
            ("var a = 10;function inner() { return eval('a + p1;');}return inner();", 30),
        ];
        foreach (var (body, expected) in evalParams)
        {
            string source = "function " + InterpreterTester.function_name() + "(p1) {" + body + "}";
            var tester = new InterpreterTester(i_isolate, source);
            Assert.True(SameValue(tester.Call(JSValue.FromInt(20)), JSValue.FromInt(expected)), body);
        }
    }

    [Fact]
    public void InterpreterEvalGlobal()
    {
        CheckSources(
        [
            ("function add_global() { eval('function test() { z = 33; }; test()'); };function f() { add_global(); return z; }; f();",
                JSValue.FromInt(33)),
            ("function add_global() {\n eval('\"use strict\"; function test() { y = 33; };      try { test() } catch(e) {}');\n}\n" +
             "function f() { add_global(); return typeof y; } f();", Str("undefined")),
        ]);
    }

    [Fact]
    public void InterpreterEvalVariableDecl()
    {
        CheckSources(
        [
            ("function f() { eval('var x = 10; x++;'); return x; }", JSValue.FromInt(11)),
            ("function f() { var x = 20; eval('var x = 10; x++;'); return x; }", JSValue.FromInt(11)),
            ("function f() { var x = 20; eval('\"use strict\"; var x = 10; x++;'); return x; }", JSValue.FromInt(20)),
            ("function f() { var y = 30; eval('var x = {1:20}; x[2]=y;'); return x[2]; }", JSValue.FromInt(30)),
            ("function f() { eval('var x = {name:\"test\"};'); return x.name; }", Str("test")),
            ("function f() {  eval('var x = [{name:\"test\"}, {type:\"cc\"}];');  return x[1].type+x[0].name; }", Str("cctest")),
            ("function f() {\n var x = 3;\n var get_eval_x;\n eval('\"use strict\";       var x = 20;       " +
             "get_eval_x = function func() {return x;};');\n return get_eval_x() + x;\n}", JSValue.FromInt(23)),
        ]);
    }

    [Fact]
    public void InterpreterEvalFunctionDecl()
    {
        CheckSources(
        [
            ("function f() {\n var x = 3;\n eval('var x = 20;       function get_x() {return x;};');\n return get_x() + x;\n}",
                JSValue.FromInt(40)),
        ]);
    }

    [Fact]
    public void InterpreterWideRegisterArithmetic()
    {
        const int kMaxRegisterForTest = 150;
        var os = new System.Text.StringBuilder();
        os.Append("function ").Append(InterpreterTester.function_name()).Append("(arg) {\n");
        os.Append("  var retval = -77;\n");
        for (int i = 0; i < kMaxRegisterForTest; i++) os.Append("  var x").Append(i).Append(" = ").Append(i).Append(";\n");
        for (int pass = 0; pass < 2; pass++)
        {
            for (int i = 0; i < kMaxRegisterForTest / 2; i++)
            {
                int j = kMaxRegisterForTest - i - 1;
                os.Append("  var tmp = x").Append(j).Append(";\n");
                os.Append("  var x").Append(j).Append(" = x").Append(i).Append(";\n");
                os.Append("  var x").Append(i).Append(" = tmp;\n");
            }
        }
        for (int i = 0; i < kMaxRegisterForTest; i++)
        {
            os.Append("  if (arg == ").Append(i).Append(") {\n    retval = x").Append(i).Append(";\n  }\n");
        }
        os.Append("  return retval;\n}\n");

        var tester = new InterpreterTester(i_isolate, os.ToString());
        for (int i = 0; i < kMaxRegisterForTest; i++)
        {
            JSValue arg = JSValue.FromInt(i);
            Assert.True(SameValue(tester.Call(arg), arg));
        }
    }

    [Fact]
    public void InterpreterCallWideRegisters()
    {
        const int kPeriod = 25;
        const int kLength = 512;
        const int kStartChar = 65;
        for (int pass = 0; pass < 3; pass += 1)
        {
            var os = new System.Text.StringBuilder();
            for (int i = 0; i < pass * 97; i += 1) os.Append("var x").Append(i).Append(" = ").Append(i).Append('\n');
            os.Append("return String.fromCharCode(").Append(kStartChar);
            for (int i = 1; i < kLength; i += 1) os.Append(',').Append(kStartChar + (i % kPeriod));
            os.Append(");");
            var tester = new InterpreterTester(i_isolate, InterpreterTester.SourceForBody(os.ToString()));
            string returnString = tester.Call().As<JSString>().ToString();
            Assert.Equal(kLength, returnString.Length);
            for (int i = 0; i < kLength; i += 1) Assert.Equal(65 + (i % kPeriod), returnString[i]);
        }
    }

    [Fact]
    public void InterpreterWideParametersPickOne()
    {
        const int kParameterCount = 130;
        for (int parameter = 0; parameter < 10; parameter++)
        {
            var os = new System.Text.StringBuilder();
            os.Append("function ").Append(InterpreterTester.function_name()).Append("(arg) {\n");
            os.Append("  function selector(i");
            for (int i = 0; i < kParameterCount; i++) os.Append(",a").Append(i);
            os.Append(") {\n  return a").Append(parameter).Append(";\n  };\n");
            os.Append("  return selector(arg");
            for (int i = 0; i < kParameterCount; i++) os.Append(',').Append(i);
            os.Append(");}\n");
            var tester = new InterpreterTester(i_isolate, os.ToString());
            Assert.Equal(parameter, tester.Call(JSValue.FromInt(0xAA55)).Number);
        }
    }

    [Fact]
    public void InterpreterWideParametersSummation()
    {
        const int kParameterCount = 200;
        const int kBaseValue = 17000;
        var os = new System.Text.StringBuilder();
        os.Append("function ").Append(InterpreterTester.function_name()).Append("(arg) {\n");
        os.Append("  function summation(i");
        for (int i = 0; i < kParameterCount; i++) os.Append(",a").Append(i);
        os.Append(") {\n    var sum = ").Append(kBaseValue).Append(";\n    switch(i) {\n");
        for (int i = 0; i < kParameterCount; i++)
        {
            int j = kParameterCount - i - 1;
            os.Append("      case ").Append(j).Append(": sum += a").Append(j).Append(";\n");
        }
        os.Append("  }\n    return sum;\n  };\n  return summation(arg");
        for (int i = 0; i < kParameterCount; i++) os.Append(',').Append(i);
        os.Append(");}\n");
        var tester = new InterpreterTester(i_isolate, os.ToString());
        for (int i = 0; i < kParameterCount; i++)
        {
            int expected = kBaseValue + i * (i + 1) / 2;
            Assert.Equal(expected, tester.Call(JSValue.FromInt(i)).Number);
        }
    }

    [Fact]
    public void InterpreterWithStatement()
    {
        CheckBodies(
        [
            ("with({x:42}) return x;", JSValue.FromInt(42)),
            ("with({}) { var y = 10; return y;}", JSValue.FromInt(10)),
            ("var y = {x:42}; function inner() {   var x = 20;   with(y) return x;}return inner();", JSValue.FromInt(42)),
            ("var y = {x:42}; function inner(o) {   var x = 20;   with(o) return x;}return inner(y);", JSValue.FromInt(42)),
        ]);
    }
}
