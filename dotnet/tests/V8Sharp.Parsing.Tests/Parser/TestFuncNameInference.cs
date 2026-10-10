// Port of test/cctest/test-func-name-inference.cc.
//
// V8 compiles the script, finds the innermost SharedFunctionInfo containing a
// position (Debug::FindInnermostContainingFunctionInfo, which compiles lazy
// functions on the way) and reads its inferred name. The port does the same
// with the parser: it lazily compiles the functions around the position and
// reads raw_inferred_name off the FunctionLiteral the enclosing parse created,
// which is where V8's SharedFunctionInfo takes its inferred name from.

#nullable disable

using V8Sharp.Ast;

namespace V8Sharp.Parsing.Tests.Parser;

public class TestFuncNameInference
{
    private static SourceScript Compile(string src)
    {
        var script = new SourceScript(src, PreParserTest.kScriptId);
        // v8::Script::Compile must succeed.
        PreParserTest.ParseScript(script);
        return script;
    }

    // The innermost literal of |literals| whose range contains |position|.
    private static FunctionLiteral InnermostContaining(List<FunctionLiteral> literals, int position)
    {
        FunctionLiteral result = null;
        foreach (FunctionLiteral literal in literals)
        {
            if (literal.start_position() > position || literal.end_position() <= position) continue;
            if (result == null || literal.start_position() >= result.start_position()) result = literal;
        }
        return result;
    }

    private static void CheckFunctionName(SourceScript script, string func_pos_src, string ref_inferred_name)
    {
        // Find the position of a given func source substring in the source.
        int func_pos = script.source().IndexOf(func_pos_src, StringComparison.Ordinal);
        Assert.NotEqual(0, func_pos);

        // Obtain the innermost function containing the position, compiling lazy
        // functions on the way.
        ParseInfo toplevel = PreParserTest.ParseScript(script);
        FunctionLiteral found = null;
        List<FunctionLiteral> literals = PreParserTest.CollectLiterals(toplevel.literal());
        while (true)
        {
            FunctionLiteral inner = InnermostContaining(literals, func_pos);
            if (inner == null) break;
            found = inner;
            if (inner.ShouldEagerCompile())
            {
                literals = PreParserTest.CollectLiterals(inner);
            }
            else
            {
                ParseInfo compiled = PreParserTest.CompileLazily(new PreParserTest.LazyFunction(script, inner), true);
                literals = PreParserTest.CollectLiterals(compiled.literal());
            }
        }
        Assert.NotNull(found);

        // Verify inferred function name.
        string inferred_name = found.raw_inferred_name()?.ToFlatString() ?? "";
        Assert.Equal(ref_inferred_name, inferred_name);
    }

    [Fact]
    public void GlobalProperty()
    {
        var script = Compile("fun1 = function() { return 1; }\nfun2 = function() { return 2; }\n");
        CheckFunctionName(script, "return 1", "fun1");
        CheckFunctionName(script, "return 2", "fun2");
    }

    [Fact]
    public void GlobalVar()
    {
        var script = Compile("var fun1 = function() { return 1; }\nvar fun2 = function() { return 2; }\n");
        CheckFunctionName(script, "return 1", "fun1");
        CheckFunctionName(script, "return 2", "fun2");
    }

    [Fact]
    public void LocalVar()
    {
        var script = Compile("function outer() {\n  var fun1 = function() { return 1; }\n  var fun2 = function() { return 2; }\n}");
        CheckFunctionName(script, "return 1", "fun1");
        CheckFunctionName(script, "return 2", "fun2");
    }

    [Fact]
    public void ObjectProperty()
    {
        var script = Compile("var obj = {\n  fun1: function() { return 1; },\n  fun2: class { constructor() { return 2; } }\n}");
        CheckFunctionName(script, "return 1", "obj.fun1");
        CheckFunctionName(script, "return 2", "obj.fun2");
    }

    [Fact]
    public void InConstructor()
    {
        var script = Compile("function MyClass() {\n  this.method1 = function() { return 1; }\n  this.method2 = function() { return 2; }\n}");
        CheckFunctionName(script, "return 1", "MyClass.method1");
        CheckFunctionName(script, "return 2", "MyClass.method2");
    }

    [Fact]
    public void Factory()
    {
        var script = Compile("function createMyObj() {\n  var obj = {};\n  obj.method1 = function() { return 1; }\n  obj.method2 = function() { return 2; }\n  return obj;\n}");
        CheckFunctionName(script, "return 1", "obj.method1");
        CheckFunctionName(script, "return 2", "obj.method2");
    }

    [Fact]
    public void Static()
    {
        var script = Compile("function MyClass() {}\nMyClass.static1 = function() { return 1; }\nMyClass.static2 = function() { return 2; }\nMyClass.MyInnerClass = {}\nMyClass.MyInnerClass.static3 = function() { return 3; }\nMyClass.MyInnerClass.static4 = function() { return 4; }");
        CheckFunctionName(script, "return 1", "MyClass.static1");
        CheckFunctionName(script, "return 2", "MyClass.static2");
        CheckFunctionName(script, "return 3", "MyClass.MyInnerClass.static3");
        CheckFunctionName(script, "return 4", "MyClass.MyInnerClass.static4");
    }

    [Fact]
    public void Prototype()
    {
        var script = Compile("function MyClass() {}\nMyClass.prototype.method1 = function() { return 1; }\nMyClass.prototype.method2 = function() { return 2; }\nMyClass.MyInnerClass = function() {}\nMyClass.MyInnerClass.prototype.method3 = function() { return 3; }\nMyClass.MyInnerClass.prototype.method4 = function() { return 4; }");
        CheckFunctionName(script, "return 1", "MyClass.method1");
        CheckFunctionName(script, "return 2", "MyClass.method2");
        CheckFunctionName(script, "return 3", "MyClass.MyInnerClass.method3");
        CheckFunctionName(script, "return 4", "MyClass.MyInnerClass.method4");
    }

    [Fact]
    public void ObjectLiteral()
    {
        var script = Compile("function MyClass() {}\nMyClass.prototype = {\n  method1: function() { return 1; },\n  method2: function() { return 2; } }");
        CheckFunctionName(script, "return 1", "MyClass.method1");
        CheckFunctionName(script, "return 2", "MyClass.method2");
    }

    [Fact(Skip = "FAIL in test/cctest/cctest.status")]
    public void UpperCaseClass()
    {
        var script = Compile("'use strict';\nclass MyClass {\n  constructor() {\n    this.value = 1;\n  }\n  method() {\n    this.value = 2;\n  }\n}");
        CheckFunctionName(script, "this.value = 1", "MyClass");
        CheckFunctionName(script, "this.value = 2", "MyClass.method");
    }

    [Fact(Skip = "FAIL in test/cctest/cctest.status")]
    public void LowerCaseClass()
    {
        var script = Compile("'use strict';\nclass myclass {\n  constructor() {\n    this.value = 1;\n  }\n  method() {\n    this.value = 2;\n  }\n}");
        CheckFunctionName(script, "this.value = 1", "myclass");
        CheckFunctionName(script, "this.value = 2", "myclass.method");
    }

    [Fact]
    public void AsParameter()
    {
        // Can't infer names here.
        var script = Compile("function f1(a) { return a(); }\nfunction f2(a, b) { return a() + b(); }\nvar result1 = f1(function() { return 1; })\nvar result2 = f2(function() { return 2; }, function() { return 3; })");
        CheckFunctionName(script, "return 1", "");
        CheckFunctionName(script, "return 2", "");
        CheckFunctionName(script, "return 3", "");
    }

    [Fact]
    public void MultipleFuncsConditional()
    {
        var script = Compile("var x = 0;\nfun1 = x ?\n    function() { return 1; } :\n    function() { return 2; }");
        CheckFunctionName(script, "return 1", "fun1");
        CheckFunctionName(script, "return 2", "fun1");
    }

    [Fact]
    public void MultipleFuncsInLiteral()
    {
        var script = Compile("var x = 0;\nfunction MyClass() {}\nMyClass.prototype = {\n  method1: x ? function() { return 1; } :\n               function() { return 2; } }");
        CheckFunctionName(script, "return 1", "MyClass.method1");
        CheckFunctionName(script, "return 2", "MyClass.method1");
    }

    [Fact]
    public void AnonymousInAnonymousClosure1()
    {
        var script = Compile("(function() {\n  (function() {\n      var a = 1;\n      return;\n  })();\n  var b = function() {\n      var c = 1;\n      return;\n  };\n})();");
        CheckFunctionName(script, "return", "");
    }

    [Fact]
    public void AnonymousInAnonymousClosure2()
    {
        var script = Compile("(function() {\n  (function() {\n      var a = 1;\n      return;\n  })();\n  var c = 1;\n})();");
        CheckFunctionName(script, "return", "");
    }

    [Fact]
    public void NamedInAnonymousClosure()
    {
        var script = Compile("var foo = function() {\n  (function named() {\n      var a = 1;\n  })();\n  var c = 1;\n  return;\n};");
        CheckFunctionName(script, "return", "foo");
    }

    [Fact]
    public void Issue380()
    {
        var script = Compile("function a() {\nvar result = function(p,a,c,k,e,d){return p}(\"if blah blah\",62,1976,\'a|b\'.split(\'|\'),0,{})\n}");
        CheckFunctionName(script, "return p", "");
    }

    [Fact]
    public void MultipleAssignments()
    {
        var script = Compile("var fun1 = fun2 = function () { return 1; }\nvar bar1 = bar2 = bar3 = function () { return 2; }\nfoo1 = foo2 = function () { return 3; }\nbaz1 = baz2 = baz3 = function () { return 4; }");
        CheckFunctionName(script, "return 1", "fun2");
        CheckFunctionName(script, "return 2", "bar3");
        CheckFunctionName(script, "return 3", "foo2");
        CheckFunctionName(script, "return 4", "baz3");
    }

    [Fact]
    public void AsConstructorParameter()
    {
        var script = Compile("function Foo() {}\nvar foo = new Foo(function() { return 1; })\nvar bar = new Foo(function() { return 2; }, function() { return 3; })");
        CheckFunctionName(script, "return 1", "");
        CheckFunctionName(script, "return 2", "");
        CheckFunctionName(script, "return 3", "");
    }

    [Fact]
    public void FactoryHashmap()
    {
        var script = Compile("function createMyObj() {\n  var obj = {};\n  obj[\"method1\"] = function() { return 1; }\n  obj[\"method2\"] = function() { return 2; }\n  return obj;\n}");
        CheckFunctionName(script, "return 1", "obj.method1");
        CheckFunctionName(script, "return 2", "obj.method2");
    }

    [Fact]
    public void FactoryHashmapVariable()
    {
        // Can't infer function names statically.
        var script = Compile("function createMyObj() {\n  var obj = {};\n  var methodName = \"method1\";\n  obj[methodName] = function() { return 1; }\n  methodName = \"method2\";\n  obj[methodName] = function() { return 2; }\n  return obj;\n}");
        CheckFunctionName(script, "return 1", "obj.<computed>");
        CheckFunctionName(script, "return 2", "obj.<computed>");
    }

    [Fact]
    public void FactoryHashmapConditional()
    {
        // Can't infer the function name statically.
        var script = Compile("function createMyObj() {\n  var obj = {};\n  obj[0 ? \"method1\" : \"method2\"] = function() { return 1; }\n  return obj;\n}");
        CheckFunctionName(script, "return 1", "obj.<computed>");
    }

    [Fact]
    public void GlobalAssignmentAndCall()
    {
        // The inferred name is empty, because this is an assignment of a result.
        // See MultipleAssignments test.
        var script = Compile("var Foo = function() {\n  return 1;\n}();\nvar Baz = Bar = function() {\n  return 2;\n}");
        CheckFunctionName(script, "return 1", "");
        CheckFunctionName(script, "return 2", "Bar");
    }

    [Fact]
    public void AssignmentAndCall()
    {
        // The inferred name is empty, because this is an assignment of a result.
        // See MultipleAssignments test.
        // TODO(2276): Lazy compiling the enclosing outer closure would yield
        // in "Enclosing.Bar" being the inferred name here.
        var script = Compile("(function Enclosing() {\n  var Foo;\n  Foo = function() {\n    return 1;\n  }();\n  var Baz = Bar = function() {\n    return 2;\n  }\n})();");
        CheckFunctionName(script, "return 1", "");
        CheckFunctionName(script, "return 2", "Bar");
    }

    [Fact]
    public void MethodAssignmentInAnonymousFunctionCall()
    {
        var script = Compile("(function () {\n    var EventSource = function () { };\n    EventSource.prototype.addListener = function () {\n        return 2012;\n    };\n    this.PublicEventSource = EventSource;\n})();");
        CheckFunctionName(script, "return 2012", "EventSource.addListener");
    }

    [Fact]
    public void ReturnAnonymousFunction()
    {
        var script = Compile("(function() {\n  function wrapCode() {\n    return function () {\n      return 2012;\n    };\n  };\n  var foo = 10;\n  function f() {\n    return wrapCode();\n  }\n  this.ref = f;\n})()");
        CheckFunctionName(script, "return 2012", "");
    }

    [Fact]
    public void IgnoreExtendsClause()
    {
        var script = Compile("(function() {\n  var foo = {};\n  foo.C = class {}\n  class D extends foo.C {}\n  foo.bar = function() { return 1; };\n})()");
        CheckFunctionName(script, "return 1", "foo.bar");
    }

    [Fact]
    public void ParameterAndArrow()
    {
        var script = Compile("(function(param) {\n  (() => { return 2017 })();\n})()");
        CheckFunctionName(script, "return 2017", "");
    }
}
