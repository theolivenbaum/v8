// Port of test/unittests/interpreter/interpreter-unittest.cc (continued):
// loops, for-in, for-of, switch, this, new.target, assignments in
// expressions, ToName and temporary registers.
using V8Sharp.Objects;

namespace V8Sharp.Tests.Interpreter;

public partial class InterpreterUnitTest
{
    [Fact]
    public void InterpreterBasicLoops()
    {
        CheckBodies(
        [
            ("var a = 10; var b = 1;\nwhile (a) {\n  b = b * 2;\n  a = a - 1;\n};\nreturn b;\n", Num(1024)),
            ("var a = 1; var b = 1;\ndo {\n  b = b * 2;\n  --a;\n} while(a);\nreturn b;\n", JSValue.FromInt(2)),
            ("var b = 1;\nfor ( var a = 10; a; a--) {\n  b *= 2;\n}\nreturn b;", Num(1024)),
            ("var a = 10; var b = 1;\nwhile (a > 0) {\n  b = b * 2;\n  a = a - 1;\n};\nreturn b;\n", Num(1024)),
            ("var a = 1; var b = 1;\ndo {\n  b = b * 2;\n  --a;\n} while(a);\nreturn b;\n", JSValue.FromInt(2)),
            ("var b = 1;\nfor ( var a = 10; a > 0; a--) {\n  b *= 2;\n}\nreturn b;", Num(1024)),
            ("var a = 10; var b = 1;\nwhile (false) {\n  b = b * 2;\n  a = a - 1;\n}\nreturn b;\n", JSValue.FromInt(1)),
            ("var a = 10; var b = 1;\nwhile (true) {\n  b = b * 2;\n  a = a - 1;\n  if (a == 0) break;  continue;}\nreturn b;\n",
                Num(1024)),
            ("var a = 10; var b = 1;\ndo {\n  b = b * 2;\n  a = a - 1;\n  if (a == 0) break;} while(true);\nreturn b;\n", Num(1024)),
            ("var a = 10; var b = 1;\ndo {\n  b = b * 2;\n  a = a - 1;\n  if (a == 0) break;} while(false);\nreturn b;\n",
                JSValue.FromInt(2)),
            ("var a = 10; var b = 1;\nfor ( a = 1, b = 30; false; ) {\n  b = b * 2;\n}\nreturn b;\n", JSValue.FromInt(30)),
        ]);
    }

    [Fact]
    public void InterpreterForIn()
    {
        (string body, int expected)[] forInSamples =
        [
            ("var r = -1;\nfor (var a in null) { r = a; }\nreturn r;\n", -1),
            ("var r = -1;\nfor (var a in undefined) { r = a; }\nreturn r;\n", -1),
            ("var r = 0;\nfor (var a in [0,6,7,9]) { r = r + (1 << a); }\nreturn r;\n", 0xF),
            ("var r = 0;\nfor (var a in [0,6,7,9]) { r = r + (1 << a); }\nvar r = 0;\nfor (var a in [0,6,7,9]) { r = r + (1 << a); }\nreturn r;\n",
                0xF),
            ("var r = 0;\nfor (var a in 'foobar') { r = r + (1 << a); }\nreturn r;\n", 0x3F),
            ("var r = 0;\nfor (var a in {1:0, 10:1, 100:2, 1000:3}) {\n  r = r + Number(a);\n }\n return r;\n", 1111),
            ("var r = 0;\nvar data = {1:0, 10:1, 100:2, 1000:3};\nfor (var a in data) {\n  if (a == 1) delete data[1];\n" +
             "  r = r + Number(a);\n }\n return r;\n", 1111),
            ("var r = 0;\nvar data = {1:0, 10:1, 100:2, 1000:3};\nfor (var a in data) {\n  if (a == 10) delete data[100];\n" +
             "  r = r + Number(a);\n }\n return r;\n", 1011),
            ("var r = 0;\nvar data = {1:0, 10:1, 100:2, 1000:3};\nfor (var a in data) {\n  if (a == 10) data[10000] = 4;\n" +
             "  r = r + Number(a);\n }\n return r;\n", 1111),
            ("var r = 0;\nvar input = 'foobar';\nfor (var a in input) {\n  if (input[a] == 'b') break;\n  r = r + (1 << a);\n}\nreturn r;\n",
                0x7),
            ("var r = 0;\nvar input = 'foobar';\nfor (var a in input) {\n if (input[a] == 'b') continue;\n r = r + (1 << a);\n}\nreturn r;\n",
                0x37),
            ("var r = 0;\nvar data = {1:0, 10:1, 100:2, 1000:3};\nfor (var a in data) {\n  if (a == 10) {\n     data[10000] = 4;\n  }\n" +
             "  r = r + Number(a);\n}\nreturn r;\n", 1111),
            ("var r = [ 3 ];\nvar data = {1:0, 10:1, 100:2, 1000:3};\nfor (r[10] in data) {\n}\nreturn Number(r[10]);\n", 1000),
            ("var r = [ 3 ];\nvar data = {1:0, 10:1, 100:2, 1000:3};\nfor (r['100'] in data) {\n}\nreturn Number(r['100']);\n", 1000),
            ("var obj = {}\nvar descObj = new Boolean(false);\nvar accessed = 0;\ndescObj.enumerable = true;\n" +
             "Object.defineProperties(obj, { prop:descObj });\nfor (var p in obj) {\n  if (p === 'prop') { accessed = 1; }\n}\nreturn accessed;",
                1),
            ("var appointment = {};\nObject.defineProperty(appointment, 'startTime', {\n    value: 1001,\n    writable: false,\n" +
             "    enumerable: false,\n    configurable: true\n});\nObject.defineProperty(appointment, 'name', {\n    value: 'NAME',\n" +
             "    writable: false,\n    enumerable: false,\n    configurable: true\n});\nvar meeting = Object.create(appointment);\n" +
             "Object.defineProperty(meeting, 'conferenceCall', {\n    value: 'In-person meeting',\n    writable: false,\n" +
             "    enumerable: false,\n    configurable: true\n});\n\nvar teamMeeting = Object.create(meeting);\n\nvar flags = 0;\n" +
             "for (var p in teamMeeting) {\n    if (p === 'startTime') {\n        flags |= 1;\n    }\n    if (p === 'name') {\n" +
             "        flags |= 2;\n    }\n    if (p === 'conferenceCall') {\n        flags |= 4;\n    }\n}\n\n" +
             "var hasOwnProperty = !teamMeeting.hasOwnProperty('name') &&\n    !teamMeeting.hasOwnProperty('startTime') &&\n" +
             "    !teamMeeting.hasOwnProperty('conferenceCall');\nif (!hasOwnProperty) {\n    flags |= 8;\n}\nreturn flags;\n", 0),
            ("var data = {x:23, y:34};\n var result = 0;\nvar o = {};\nvar arr = [o];\nfor (arr[0].p in data)\n" +
             "  result += data[arr[0].p];\nreturn result;\n", 57),
            ("var data = {x:23, y:34};\nvar result = 0;\nvar o = {};\nvar i = 0;\nfor (o[i++] in data)\n  result += data[o[i-1]];\n" +
             "return result;\n", 57),
        ];

        // Two passes are made for this test. On the first, 8-bit register
        // operands are employed, and on the 16-bit register operands are
        // used.
        for (int pass = 0; pass < 2; pass++)
        {
            var wide = new System.Text.StringBuilder();
            if (pass == 1)
            {
                for (int i = 0; i < 200; i++) wide.Append("var local").Append(i).Append(" = 0;\n");
            }
            foreach (var (body, expected) in forInSamples)
            {
                var tester = new InterpreterTester(i_isolate, InterpreterTester.SourceForBody(wide + body));
                JSValue returnVal = tester.Call();
                Assert.True(returnVal.IsSmi, body);
                Assert.Equal(expected, (int)returnVal.Number);
            }
        }
    }

    [Fact]
    public void InterpreterForOf()
    {
        CheckSources(
        [
            ("function f() {\n  var r = 0;\n  for (var a of [0,6,7,9]) { r += a; }\n  return r;\n}", JSValue.FromInt(22)),
            ("function f() {\n  var r = '';\n  for (var a of 'foobar') { r = a + r; }\n  return r;\n}", Str("raboof")),
            ("function f() {\n  var a = [1, 2, 3];\n  a.name = 4;\n  var r = 0;\n  for (var x of a) { r += x; }\n  return r;\n}",
                JSValue.FromInt(6)),
            ("function f() {\n  var r = '';\n  var data = [1, 2, 3]; \n  for (a of data) { delete data[0]; r += a; } return r; }",
                Str("123")),
            ("function f() {\n  var r = '';\n  var data = [1, 2, 3]; \n  for (a of data) { delete data[2]; r += a; } return r; }",
                Str("12undefined")),
            ("function f() {\n  var r = '';\n  var data = [1, 2, 3]; \n  for (a of data) { delete data; r += a; } return r; }",
                Str("123")),
            ("function f() {\n  var r = '';\n  var input = 'foobar';\n  for (var a of input) {\n    if (a == 'b') break;\n    r += a;\n" +
             "  }\n  return r;\n}", Str("foo")),
            ("function f() {\n  var r = '';\n  var input = 'foobar';\n  for (var a of input) {\n    if (a == 'b') continue;\n" +
             "    r += a;\n  }\n  return r;\n}", Str("fooar")),
            ("function f() {\n  var r = '';\n  var data = [1, 2, 3, 4]; \n  for (a of data) { data[2] = 567; r += a; }\n  return r;\n}",
                Str("125674")),
            ("function f() {\n  var r = '';\n  var data = [1, 2, 3, 4]; \n  for (a of data) { data[4] = 567; r += a; }\n  return r;\n}",
                Str("1234567")),
            ("function f() {\n  var r = '';\n  var data = [1, 2, 3, 4]; \n  for (a of data) { data[5] = 567; r += a; }\n  return r;\n}",
                Str("1234undefined567")),
            ("function f() {\n  var r = '';\n  var obj = new Object();\n  obj[Symbol.iterator] = function() { return {\n" +
             "    index: 3,\n    data: ['a', 'b', 'c', 'd'],    next: function() {      return {        done: this.index == -1,\n" +
             "        value: this.index < 0 ? undefined : this.data[this.index--]\n      }\n    }\n    }}\n" +
             "  for (a of obj) { r += a }\n  return r;\n}", Str("dcba")),
        ]);
    }

    [Fact]
    public void InterpreterSwitch()
    {
        CheckBodies(
        [
            ("var a = 1;\nswitch(a) {\n case 1: return 2;\n case 2: return 3;\n}\n", JSValue.FromInt(2)),
            ("var a = 1;\nswitch(a) {\n case 2: a = 2; break;\n case 1: a = 3; break;\n}\nreturn a;", JSValue.FromInt(3)),
            ("var a = 1;\nswitch(a) {\n case 1: a = 2; // fall-through\n case 2: a = 3; break;\n}\nreturn a;", JSValue.FromInt(3)),
            ("var a = 100;\nswitch(a) {\n case 1: return 100;\n case 2: return 200;\n}\nreturn undefined;", JSValue.Undefined),
            ("var a = 100;\nswitch(a) {\n case 1: return 100;\n case 2: return 200;\n default: return 300;\n}\nreturn undefined;",
                JSValue.FromInt(300)),
            ("var a = 100;\nswitch(typeof(a)) {\n case 'string': return 1;\n case 'number': return 2;\n default: return 3;\n}\n",
                JSValue.FromInt(2)),
            ("var a = 100;\nswitch(a) {\n case a += 20: return 1;\n case a -= 10: return 2;\n case a -= 10: return 3;\n" +
             " default: return 3;\n}\n", JSValue.FromInt(3)),
            ("var a = 1;\nswitch(a) {\n case 1: \n   switch(a + 1) {\n      case 2 : a += 1; break;\n      default : a += 2; break;\n" +
             "   }  // fall-through\n case 2: a += 3;\n}\nreturn a;", JSValue.FromInt(5)),
        ]);
    }

    [Fact]
    public void InterpreterSloppyThis()
    {
        CheckSources(
        [
            ("var global_val = 100;\nfunction f() { return this.global_val; }\n", JSValue.FromInt(100)),
            ("var global_val = 110;\nfunction g() { return this.global_val; };function f() { return g(); }\n", JSValue.FromInt(110)),
            ("var global_val = 110;\nfunction g() { return this.global_val };function f() { 'use strict'; return g(); }\n",
                JSValue.FromInt(110)),
            ("function f() { 'use strict'; return this; }\n", JSValue.Undefined),
            ("function g() { 'use strict'; return this; };function f() { return g(); }\n", JSValue.Undefined),
        ]);
    }

    [Fact]
    public void InterpreterThisFunction()
    {
        var tester = new InterpreterTester(i_isolate, "var f;\n f = function f() { return f.name; }");
        Assert.Equal("f", tester.Call().As<JSString>().ToString());
    }

    [Fact]
    public void InterpreterNewTarget()
    {
        var tester = new InterpreterTester(i_isolate, "function f() { this.a = new.target; }");
        tester.Call();
        JSValue newTargetName = CompileRun("(function() { return (new f()).a.name; })();");
        Assert.Equal("f", newTargetName.As<JSString>().ToString());
    }

    [Fact]
    public void InterpreterAssignmentInExpressions()
    {
        (string source, int expected)[] samples =
        [
            ("function f() {\n  var x = 7;\n  var y = x + (x = 1) + (x = 2);\n  return y;\n}", 10),
            ("function f() {\n  var x = 7;\n  var y = x + (x = 1) + (x = 2);\n  return x;\n}", 2),
            ("function f() {\n  var x = 55;\n  x = x + (x = 100) + (x = 101);\n  return x;\n}", 256),
            ("function f() {\n  var x = 7;\n  return ++x + x + x++;\n}", 24),
            ("function f() {\n  var x = 7;\n  var y = 1 + ++x + x + x++;\n  return x;\n}", 9),
            ("function f() {\n  var x = 7;\n  var y = ++x + x + x++;\n  return x;\n}", 9),
            ("function f() {\n  var x = 7, y = 100, z = 1000;\n  return x + (x += 3) + y + (y *= 10) + (z *= 7) + z;\n}", 15117),
            ("function f() {\n  var inner = function (x) { return x + (x = 2) + (x = 4) + x; };\n  return inner(1);\n}", 11),
            ("function f() {\n  var x = 1, y = 2;\n  x = x + (x = 3) + y + (y = 4), y = y + (y = 5) + y + x;\n  return x + y;\n}", 10 + 24),
            ("function f() {\n  var x = 0;\n  var y = x | (x = 1) | (x = 2);\n  return x;\n}", 2),
            ("function f() {\n  var x = 0;\n  var y = x || (x = 1);\n  return x;\n}", 1),
            ("function f() {\n  var x = 1;\n  var y = x && (x = 2) && (x = 3);\n  return x;\n}", 3),
            ("function f() {\n  var x = 1;\n  var y = x || (x = 2);\n  return x;\n}", 1),
            ("function f() {\n  var x = 1;\n  x = (x << (x = 3)) | (x = 16);\n  return x;\n}", 24),
            ("function f() {\n  var r = 7;\n  var s = 11;\n  var t = 13;\n  var u = r + s + t + (r = 10) + (s = 20) +" +
             "          (t = (r + s)) + r + s + t;\n  return r + s + t + u;\n}", 211),
            ("function f() {\n  var r = 7;\n  var s = 11;\n  var t = 13;\n  return r > (3 * s * (s = 1)) ? (t + (t += 1)) : (r + (r = 4));\n}",
                11),
            ("function f() {\n  var r = 7;\n  var s = 11;\n  var t = 13;\n  return r > (3 * s * (s = 0)) ? (t + (t += 1)) : (r + (r = 4));\n}",
                27),
            ("function f() {\n  var r = 7;\n  var s = 11;\n  var t = 13;\n  return (r + (r = 5)) > s ? r : t;\n}", 5),
            ("function f(a) {\n  return a + (arguments[0] = 10);\n}", 50),
            ("function f(a) {\n  return a + (arguments[0] = 10) + a;\n}", 60),
            ("function f(a) {\n  return a + (arguments[0] = 10) + arguments[0];\n}", 60),
        ];
        const int argValue = 40;
        foreach (var (source, expected) in samples)
        {
            var tester = new InterpreterTester(i_isolate, source);
            JSValue returnVal = tester.Call(JSValue.FromInt(argValue));
            Assert.True(returnVal.IsSmi, source);
            Assert.Equal(expected, (int)returnVal.Number);
        }
    }

    [Fact]
    public void InterpreterToName()
    {
        CheckBodies(
        [
            ("var a = 'val'; var obj = {[a] : 10}; return obj.val;", JSValue.FromInt(10)),
            ("var a = 20; var obj = {[a] : 10}; return obj['20'];", JSValue.FromInt(10)),
            ("var a = 20; var obj = {[a] : 10}; return obj[20];", JSValue.FromInt(10)),
            ("var a = {val:23}; var obj = {[a] : 10}; return obj[a];", JSValue.FromInt(10)),
            ("var a = {val:23}; var obj = {[a] : 10};\nreturn obj['[object Object]'];", JSValue.FromInt(10)),
            ("var a = {toString : function() { return 'x'}};\nvar obj = {[a] : 10};\nreturn obj.x;", JSValue.FromInt(10)),
            ("var a = {valueOf : function() { return 'x'}};\nvar obj = {[a] : 10};\nreturn obj.x;", JSValue.Undefined),
            ("var a = {[Symbol.toPrimitive] : function() { return 'x'}};\nvar obj = {[a] : 10};\nreturn obj.x;", JSValue.FromInt(10)),
        ]);
    }

    [Fact]
    public void TemporaryRegisterAllocation()
    {
        CheckSources(
        [
            ("function add(a, b, c) {   return a + b + c;}function f() {  var a = 10, b = 10;   return add(a, b++, b);}",
                JSValue.FromInt(31)),
            ("function add(a, b, c, d) {  return a + b + c + d;}function f() {  var x = 10, y = 20, z = 30;" +
             "  return x + add(x, (y= x++), x, z);}", JSValue.FromInt(71)),
        ]);
    }
}
