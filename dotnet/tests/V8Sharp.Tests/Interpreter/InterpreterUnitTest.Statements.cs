// Port of test/unittests/interpreter/interpreter-unittest.cc (continued):
// contexts, logical operators, try/catch/finally, count and compound
// operators, arguments, conditionals and delete.
using V8Sharp.Objects;

namespace V8Sharp.Tests.Interpreter;

public partial class InterpreterUnitTest
{
    /// <summary>Runs each full source (which defines f) and checks f()'s result.</summary>
    void CheckSources((string source, JSValue expected)[] cases)
    {
        foreach (var (source, expected) in cases)
        {
            var tester = new InterpreterTester(i_isolate, source);
            JSValue returnValue = tester.Call();
            Assert.True(ObjectOps.SameValue(returnValue, expected), source);
        }
    }

    [Fact]
    public void InterpreterContextVariables()
    {
        var uniqueVars = new System.Text.StringBuilder();
        for (int i = 0; i < 250; i++) uniqueVars.Append("var a").Append(i).Append(" = 0;");
        CheckBodies(
        [
            ("var a; (function() { a = 1; })(); return a;", JSValue.FromInt(1)),
            ("var a = 10; (function() { a; })(); return a;", JSValue.FromInt(10)),
            ("var a = 20; var b = 30;\nreturn (function() { return a + b; })();", JSValue.FromInt(50)),
            ("'use strict'; let a = 1;\n{ let b = 2; return (function() { return a + b; })(); }", JSValue.FromInt(3)),
            ("'use strict'; let a = 10;\n{ let b = 20; var c = function() { [a, b] };\n  return a + b; }", JSValue.FromInt(30)),
            ("'use strict';" + uniqueVars + "eval(); var b = 100; return b;", JSValue.FromInt(100)),
        ]);
    }

    [Fact]
    public void InterpreterContextParameters()
    {
        (string body, int expected)[] contextParams =
        [
            ("return (function() { return arg1; })();", 1),
            ("(function() { arg1 = 4; })(); return arg1;", 4),
            ("(function() { arg3 = arg2 - arg1; })(); return arg3;", 1),
        ];
        foreach (var (body, expected) in contextParams)
        {
            string source = "function " + InterpreterTester.function_name() + "(arg1, arg2, arg3) {" + body + "}";
            var tester = new InterpreterTester(i_isolate, source);
            JSValue returnValue = tester.Call(JSValue.FromInt(1), JSValue.FromInt(2), JSValue.FromInt(3));
            Assert.True(SameValue(returnValue, JSValue.FromInt(expected)));
        }
    }

    [Fact]
    public void InterpreterOuterContextVariables()
    {
        (string body, int expected)[] contextVars =
        [
            ("return outerVar * innerArg;", 200),
            ("outerVar = innerArg; return outerVar", 20),
        ];
        const string header = "function Outer() {  var outerVar = 10;  function Inner(innerArg) {    this.innerFunc = function() { ";
        const string footer = "  }}  this.getInnerFunc = function() { return new Inner(20).innerFunc; }}var f = new Outer().getInnerFunc();";
        foreach (var (body, expected) in contextVars)
        {
            var tester = new InterpreterTester(i_isolate, header + body + footer);
            Assert.True(SameValue(tester.Call(), JSValue.FromInt(expected)));
        }
    }

    [Fact]
    public void InterpreterComma()
    {
        CheckBodies(
        [
            ("var a; return 0, a;\n", JSValue.Undefined),
            ("return 'a', 2.2, 3;\n", JSValue.FromInt(3)),
            ("return 'a', 'b', 'c';\n", Str("c")),
            ("return 3.2, 2.3, 4.5;\n", Num(4.5)),
            ("var a = 10; return b = a, b = b+1;\n", JSValue.FromInt(11)),
            ("var a = 10; return b = a, b = b+1, b + 10;\n", JSValue.FromInt(21)),
        ]);
    }

    [Fact]
    public void InterpreterLogicalOr()
    {
        CheckBodies(
        [
            ("var a, b; return a || b;\n", JSValue.Undefined),
            ("var a, b = 10; return a || b;\n", JSValue.FromInt(10)),
            ("var a = '0', b = 10; return a || b;\n", Str("0")),
            ("return 0 || 3.2;\n", Num(3.2)),
            ("return 'a' || 0;\n", Str("a")),
            ("var a = '0', b = 10; return (a == 0) || b;\n", JSValue.True),
        ]);
    }

    [Fact]
    public void InterpreterLogicalAnd()
    {
        CheckBodies(
        [
            ("var a, b = 10; return a && b;\n", JSValue.Undefined),
            ("var a = 0, b = 10; return a && b / a;\n", JSValue.FromInt(0)),
            ("var a = '0', b = 10; return a && b;\n", JSValue.FromInt(10)),
            ("return 0.0 && 3.2;\n", JSValue.FromInt(0)),
            ("return 'a' && 'b';\n", Str("b")),
            ("return 'a' && 0 || 'b', 'c';\n", Str("c")),
            ("var x = 1, y = 3; return x && 0 + 1 || y;\n", JSValue.FromInt(1)),
            ("var x = 1, y = 3; return (x == 1) && (3 == 3) || y;\n", JSValue.True),
        ]);
    }

    [Fact]
    public void InterpreterTryCatch()
    {
        CheckBodies(
        [
            ("var a = 1; try { a = 2 } catch(e) { a = 3 }; return a;", JSValue.FromInt(2)),
            ("var a; try { undef.x } catch(e) { a = 2 }; return a;", JSValue.FromInt(2)),
            ("var a; try { throw 1 } catch(e) { a = e + 2 }; return a;", JSValue.FromInt(3)),
            ("var a; try { throw 1 } catch(e) { a = e + 2 };       try { throw a } catch(e) { a = e + 3 }; return a;",
                JSValue.FromInt(6)),
        ]);
    }

    [Fact]
    public void InterpreterTryFinally()
    {
        (string body, string expected)[] finallies =
        [
            ("var a = 1; try { a = a + 1; } finally { a = a + 2; }; return a;", "R4"),
            ("var a = 1; try { a = 2; return 23; } finally { a = 3 }; return a;", "R23"),
            ("var a = 1; try { a = 2; throw 23; } finally { a = 3 }; return a;", "E23"),
            ("var a = 1; try { a = 2; throw 23; } finally { return a; };", "R2"),
            ("var a = 1; try { a = 2; throw 23; } finally { throw 42; };", "E42"),
            ("var a = 1; for (var i = 10; i < 20; i += 5) {  try { a = 2; break; } finally { a = 3; }} return a + i;", "R13"),
            ("var a = 1; for (var i = 10; i < 20; i += 5) {  try { a = 2; continue; } finally { a = 3; }} return a + i;", "R23"),
            ("var a = 1; try { a = 2;  try { a = 3; throw 23; } finally { a = 4; }} catch(e) { a = a + e; } return a;", "R27"),
            ("var func_name;function tcf2(a) {  try { throw new Error('boom');}   catch(e) {return 153; }   " +
             "finally {func_name = tcf2.name;}}tcf2();return func_name;", "Rtcf2"),
        ];
        const string tryWrapper = "(function() { try { return 'R' + f() } catch(e) { return 'E' + e }})()";
        foreach (var (body, expected) in finallies)
        {
            var tester = new InterpreterTester(i_isolate, InterpreterTester.SourceForBody(body));
            tester.GetBytecodeFunction();
            JSValue wrapped = CompileRun(tryWrapper);
            Assert.Equal(expected, wrapped.As<JSString>().ToString());
        }
    }

    [Fact]
    public void InterpreterThrow()
    {
        (string body, JSValue expected)[] throws =
        [
            ("throw undefined;\n", JSValue.Undefined),
            ("throw 1;\n", JSValue.FromInt(1)),
            ("throw 'Error';\n", Str("Error")),
            ("var a = true; if (a) { throw 'Error'; }\n", Str("Error")),
            ("var a = false; if (a) { throw 'Error'; }\n", JSValue.Undefined),
            ("throw 'Error1'; throw 'Error2'\n", Str("Error1")),
        ];
        const string tryWrapper = "(function() { try { f(); } catch(e) { return e; }})()";
        foreach (var (body, expected) in throws)
        {
            var tester = new InterpreterTester(i_isolate, InterpreterTester.SourceForBody(body));
            tester.GetBytecodeFunction();
            Assert.True(SameValue(CompileRun(tryWrapper), expected), body);
        }
    }

    [Fact]
    public void InterpreterCountOperators()
    {
        CheckBodies(
        [
            ("var a = 1; return ++a;", JSValue.FromInt(2)),
            ("var a = 1; return a++;", JSValue.FromInt(1)),
            ("var a = 5; return --a;", JSValue.FromInt(4)),
            ("var a = 5; return a--;", JSValue.FromInt(5)),
            ("var a = 5.2; return --a;", Num(4.2)),
            ("var a = 'string'; return ++a;", Num(double.NaN)),
            ("var a = 'string'; return a--;", Num(double.NaN)),
            ("var a = true; return ++a;", JSValue.FromInt(2)),
            ("var a = false; return a--;", JSValue.FromInt(0)),
            ("var a = { val: 11 }; return ++a.val;", JSValue.FromInt(12)),
            ("var a = { val: 11 }; return a.val--;", JSValue.FromInt(11)),
            ("var a = { val: 11 }; return ++a.val;", JSValue.FromInt(12)),
            ("var name = 'val'; var a = { val: 22 }; return --a[name];", JSValue.FromInt(21)),
            ("var name = 'val'; var a = { val: 22 }; return a[name]++;", JSValue.FromInt(22)),
            ("var a = 1; (function() { a = 2 })(); return ++a;", JSValue.FromInt(3)),
            ("var a = 1; (function() { a = 2 })(); return a--;", JSValue.FromInt(2)),
            ("var i = 5; while(i--) {}; return i;", JSValue.FromInt(-1)),
            ("var i = 1; if(i--) { return 1; } else { return 2; };", JSValue.FromInt(1)),
            ("var i = -2; do {} while(i++) {}; return i;", JSValue.FromInt(1)),
            ("var i = -1; for(; i++; ) {}; return i", JSValue.FromInt(1)),
            ("var i = 20; switch(i++) {\n  case 20: return 1;\n  default: return 2;\n}", JSValue.FromInt(1)),
        ]);
    }

    [Fact]
    public void InterpreterGlobalCountOperators()
    {
        CheckSources(
        [
            ("var global = 100;function f(){ return ++global; }", JSValue.FromInt(101)),
            ("var global = 100; function f(){ return --global; }", JSValue.FromInt(99)),
            ("var global = 100; function f(){ return global++; }", JSValue.FromInt(100)),
            ("unallocated = 200; function f(){ return ++unallocated; }", JSValue.FromInt(201)),
            ("unallocated = 200; function f(){ return --unallocated; }", JSValue.FromInt(199)),
            ("unallocated = 200; function f(){ return unallocated++; }", JSValue.FromInt(200)),
        ]);
    }

    [Fact]
    public void InterpreterCompoundExpressions()
    {
        CheckBodies(
        [
            ("var a = 1; a += 2; return a;", JSValue.FromInt(3)),
            ("var a = 10; a /= 2; return a;", JSValue.FromInt(5)),
            ("var a = 'test'; a += 'ing'; return a;", Str("testing")),
            ("var a = { val: 2 }; a.val *= 2; return a.val;", JSValue.FromInt(4)),
            ("var a = 1; (function f() { a = 2; })(); a += 24;return a;", JSValue.FromInt(26)),
        ]);
    }

    [Fact]
    public void InterpreterGlobalCompoundExpressions()
    {
        CheckSources(
        [
            ("var global = 100;function f() { global += 20; return global; }", JSValue.FromInt(120)),
            ("unallocated = 100;function f() { unallocated -= 20; return unallocated; }", JSValue.FromInt(80)),
        ]);
    }

    [Fact]
    public void InterpreterCreateArguments()
    {
        (string source, int index)[] createArgs =
        [
            ("function f() { return arguments[0]; }", 0),
            ("function f(a) { return arguments[0]; }", 0),
            ("function f() { return arguments[2]; }", 2),
            ("function f(a) { return arguments[2]; }", 2),
            ("function f(a, b, c, d) { return arguments[2]; }", 2),
            ("function f(a) {'use strict'; return arguments[0]; }", 0),
            ("function f(a, b, c, d) {'use strict'; return arguments[2]; }", 2),
            // Check arguments are mapped in sloppy mode and unmapped in strict.
            ("function f(a, b, c, d) {  c = b; return arguments[2]; }", 1),
            ("function f(a, b, c, d) {  'use strict'; c = b; return arguments[2]; }", 2),
            // Check arguments for duplicate parameters in sloppy mode.
            ("function f(a, a, b) { return arguments[1]; }", 1),
            // check rest parameters
            ("function f(...restArray) { return restArray[0]; }", 0),
            ("function f(a, ...restArray) { return restArray[0]; }", 1),
            ("function f(a, ...restArray) { return arguments[0]; }", 0),
            ("function f(a, ...restArray) { return arguments[1]; }", 1),
            ("function f(a, ...restArray) { return restArray[1]; }", 2),
            ("function f(a, ...arguments) { return arguments[0]; }", 1),
            ("function f(a, b, ...restArray) { return restArray[0]; }", 2),
        ];

        // Test passing no arguments.
        foreach (var (source, _) in createArgs)
        {
            Assert.True(new InterpreterTester(i_isolate, source).Call().IsUndefined, source);
        }

        // Test passing one argument.
        foreach (var (source, index) in createArgs)
        {
            JSValue returnVal = new InterpreterTester(i_isolate, source).Call(JSValue.FromInt(40));
            if (index == 0) Assert.Equal(40, returnVal.Number);
            else Assert.True(returnVal.IsUndefined, source);
        }

        // Test passing three argument.
        JSValue[] args = [JSValue.FromInt(40), JSValue.FromInt(60), JSValue.FromInt(80)];
        foreach (var (source, index) in createArgs)
        {
            JSValue returnVal = new InterpreterTester(i_isolate, source).Call(args[0], args[1], args[2]);
            Assert.True(SameValue(returnVal, args[index]), source);
        }
    }

    [Fact]
    public void InterpreterConditional()
    {
        CheckBodies(
        [
            ("return true ? 2 : 3;", JSValue.FromInt(2)),
            ("return false ? 2 : 3;", JSValue.FromInt(3)),
            ("var a = 1; return a ? 20 : 30;", JSValue.FromInt(20)),
            ("var a = 1; return a ? 20 : 30;", JSValue.FromInt(20)),
            ("var a = 'string'; return a ? 20 : 30;", JSValue.FromInt(20)),
            ("var a = undefined; return a ? 20 : 30;", JSValue.FromInt(30)),
            ("return 1 ? 2 ? 3 : 4 : 5;", JSValue.FromInt(3)),
            ("return 0 ? 2 ? 3 : 4 : 5;", JSValue.FromInt(5)),
        ]);
    }

    [Fact]
    public void InterpreterDelete()
    {
        // Tests for delete for local variables that work both in strict
        // and sloppy modes
        (string body, JSValue expected)[] testDelete =
        [
            ("var a = { x:10, y:'abc', z:30.2}; delete a.x; return a.x;\n", JSValue.Undefined),
            ("var b = { x:10, y:'abc', z:30.2}; delete b.x; return b.y;\n", Str("abc")),
            ("var c = { x:10, y:'abc', z:30.2}; var d = c; delete d.x; return c.x;\n", JSValue.Undefined),
            ("var e = { x:10, y:'abc', z:30.2}; var g = e; delete g.x; return e.y;\n", Str("abc")),
            ("var a = { x:10, y:'abc', z:30.2};\nvar b = a;delete b.x;return b.x;\n", JSValue.Undefined),
            ("var a = {1:10};\n(function f1() {return a;});return delete a[1];", JSValue.True),
            ("return delete this;", JSValue.True),
            ("return delete 'test';", JSValue.True),
        ];

        // Test delete in sloppy mode
        CheckBodies(testDelete);

        // Test delete in strict mode
        var strict = new (string, JSValue)[testDelete.Length];
        for (int i = 0; i < testDelete.Length; i++) strict[i] = ("'use strict'; " + testDelete[i].body, testDelete[i].expected);
        CheckBodies(strict);
    }

    [Fact]
    public void InterpreterDeleteSloppyUnqualifiedIdentifier()
    {
        // These tests generate a syntax error for strict mode. We don't
        // test for it here.
        CheckBodies(
        [
            ("var sloppy_a = { x:10, y:'abc'};\nvar sloppy_b = delete sloppy_a;\nif (delete sloppy_a) {\n  return undefined;\n" +
             "} else {\n  return sloppy_a.x;\n}\n", JSValue.FromInt(10)),
            ("sloppy_a = { x:10, y:'abc'};\nvar sloppy_b = delete sloppy_a;\nreturn sloppy_b;", JSValue.True),
            ("sloppy_a = { x:10, y:'abc'};\nvar sloppy_b = delete sloppy_c;\nreturn sloppy_b;", JSValue.True),
        ]);
    }

    [Fact]
    public void InterpreterGlobalDelete()
    {
        CheckSources(
        [
            ("var a = { x:10, y:'abc', z:30.2 };\nfunction f() {\n  delete a.x;\n  return a.x;\n}\nf();\n", JSValue.Undefined),
            ("var b = {1:10, 2:'abc', 3:30.2 };\nfunction f() {\n  delete b[2];\n  return b[1];\n }\nf();\n", JSValue.FromInt(10)),
            ("var c = { x:10, y:'abc', z:30.2 };\nfunction f() {\n   var d = c;\n   delete d.y;\n   return d.x;\n}\nf();\n",
                JSValue.FromInt(10)),
            ("e = { x:10, y:'abc' };\nfunction f() {\n  return delete e;\n}\nf();\n", JSValue.True),
            ("var g = { x:10, y:'abc' };\nfunction f() {\n  return delete g;\n}\nf();\n", JSValue.False),
            ("function f() {\n  var obj = {h:10, f1() {return delete this;}};\n  return obj.f1();\n}\nf();", JSValue.True),
            ("function f() {\n  var obj = {h:10,\n             f1() {\n              'use strict';\n" +
             "              return delete this.h;}};\n  return obj.f1();\n}\nf();", JSValue.True),
        ]);
    }
}
