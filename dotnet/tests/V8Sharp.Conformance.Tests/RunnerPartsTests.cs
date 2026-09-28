// Tests for the pieces of the runner that decide what runs and whether it passed.
using V8Sharp.TestRunner;
using V8Sharp.TestRunner.OutProc;
using V8Sharp.TestRunner.Shell;
using V8Sharp.TestRunner.Status;
using V8Sharp.TestRunner.Suites;

namespace V8Sharp.Conformance.Tests;

public class RunnerPartsTests
{
    [Fact]
    public void ShlexSplit()
    {
        Assert.Equal(["--allow-natives-syntax", "--expose-gc"], TestCase.ShlexSplit("--allow-natives-syntax  --expose-gc"));
        Assert.Equal(["--json", "data:text/javascript,{\"a\":1}"], TestCase.ShlexSplit("--json data:text/javascript,{\"a\":1}".Replace("\"", "\\\"")));
        Assert.Equal(["--foo=a b", "c"], TestCase.ShlexSplit("'--foo=a b' c"));
        Assert.Equal(["--x=\"q\""], TestCase.ShlexSplit("--x=\\\"q\\\""));
    }

    [Fact]
    public void SourceFlags()
    {
        var flags = TestCase.ParseSourceFlags("// Copyright\n// Flags: --allow-natives-syntax --turbofan\n//  Flags: --expose-gc\ncode();");
        Assert.Equal(["--allow-natives-syntax", "--turbofan", "--expose-gc"], flags);
    }

    [Fact]
    public void D8CommandLine()
    {
        var o = D8Options.Parse(["--allow-natives-syntax", "--throws", "--ignore-unhandled-promises", "a.js", "--module", "b.js", "c.mjs", "--no-can-block", "--test", "-e", "print(1)"]);
        Assert.Equal(["--allow-natives-syntax"], o.V8Flags);
        Assert.True(o.ExpectedToThrow);
        Assert.True(o.IgnoreUnhandledPromises);
        Assert.True(o.NoCanBlock);
        Assert.Equal(4, o.Sources.Count);
        Assert.False(o.Sources[0].IsModule);
        Assert.True(o.Sources[1].IsModule);
        Assert.True(o.Sources[2].IsModule);
        Assert.Equal("print(1)", o.Sources[3].EvalSource);
    }

    [Fact]
    public void StatusOutcomesToExpectations()
    {
        Assert.Equal(OutcomeSets.Pass, OutcomeSets.FromStatus(new HashSet<string> { "PASS", "SLOW" }, []));
        Assert.Equal(["FAIL"], OutcomeSets.FromStatus(new HashSet<string> { "FAIL" }, []));
        Assert.Equal(["FAIL"], OutcomeSets.FromStatus(new HashSet<string> { "FAIL_OK" }, []));
        Assert.Equal(["FAIL", "PASS"], OutcomeSets.FromStatus(new HashSet<string> { "PASS", "FAIL" }, []));
        Assert.Equal(["FAIL"], OutcomeSets.FromStatus(new HashSet<string> { "FAIL_SLOPPY" }, []));
        Assert.Equal(OutcomeSets.Pass, OutcomeSets.FromStatus(new HashSet<string> { "FAIL_SLOPPY" }, ["--use-strict"]));
    }

    [Fact]
    public void FlagContradictions()
    {
        var vars = Runner.LoadBuildConfig("oracle");
        Assert.Null(OutcomeSets.FlagContradiction(["--allow-natives-syntax", "--turbofan"], "default", vars));
        Assert.NotNull(OutcomeSets.FlagContradiction(["--turbofan", "--no-turbofan"], "default", vars));
        Assert.NotNull(OutcomeSets.FlagContradiction(["--no_lazy", "--lazy"], "default", vars));
        // Release builds have no --print-ast (DEBUG_defined is false).
        Assert.NotNull(OutcomeSets.FlagContradiction(["--print-ast"], "default", vars));
        Assert.Null(OutcomeSets.FlagContradiction(["--fuzzing", "--turbofan", "--no-turbofan"], "default", vars));
    }

    [Fact]
    public void Filters()
    {
        TestCase T(string suite, string name, string suffix = "") => new()
        {
            Suite = suite, Name = name, VariantSuffix = suffix, SourcePath = "", Files = [], Flags = [],
            StatusOutcomes = new HashSet<string>(), OutProc = new OutputProcessor(OutcomeSets.Pass),
        };
        var f = Runner.CompileFilter(["mjsunit/es6"]);
        Assert.True(f(T("mjsunit", "es6/foo")));
        Assert.False(f(T("mjsunit", "es6foo")));
        Assert.False(f(T("test262", "es6/foo")));
        var g = Runner.CompileFilter(["built-ins/Array/**"]);
        Assert.True(g(T("test262", "built-ins/Array/prototype/map/x", "@strict")));
        Assert.False(g(T("test262", "built-ins/ArrayBuffer/x")));
        var h = Runner.CompileFilter(["test262/built-ins/*/length"]);
        Assert.True(h(T("test262", "built-ins/Map/length")));
        Assert.False(h(T("test262", "built-ins/Map/prototype/length")));
    }

    [Fact]
    public void Test262ExceptionParsing()
    {
        Assert.Equal("SyntaxError", Test262OutputProcessor.ParseException("/abs/path/x.js:12: SyntaxError: Unexpected token\n  foo\n"));
        Assert.Equal("Test262Error", Test262OutputProcessor.ParseException("x.js:3: Test262Error: Expected true"));
        Assert.Equal("ReferenceError", Test262OutputProcessor.ParseException("noise\ntest/x.js:1: ReferenceError\n"));
        Assert.Null(Test262OutputProcessor.ParseException("Test262:AsyncTestComplete\n"));
    }

    static RunOutput Out(int exit, string stdout) => new(exit, stdout, "", false, false, TimeSpan.Zero);

    [Fact]
    public void Test262Outcomes()
    {
        var negative = new Test262OutputProcessor(OutcomeSets.Pass, "SyntaxError", negative: false, isAsync: false);
        // With --throws, d8 exits 0 when the script threw.
        Assert.Equal(Outcome.Pass, negative.GetOutcome(Out(0, "x.js:1: SyntaxError: bad\n")));
        Assert.Equal(Outcome.Fail, negative.GetOutcome(Out(0, "x.js:1: TypeError: bad\n")));
        Assert.Equal(Outcome.Fail, negative.GetOutcome(Out(1, "")));
        var asyncTest = new Test262OutputProcessor(OutcomeSets.Pass, null, negative: false, isAsync: true);
        Assert.Equal(Outcome.Pass, asyncTest.GetOutcome(Out(0, "Test262:AsyncTestComplete\n")));
        Assert.Equal(Outcome.Fail, asyncTest.GetOutcome(Out(0, "")));
        Assert.Equal(Outcome.Fail, asyncTest.GetOutcome(Out(0, "Test262:AsyncTestComplete\nFAILED!\n")));
        Assert.Equal(Outcome.Timeout, asyncTest.GetOutcome(new RunOutput(-1, "", "", true, false, TimeSpan.Zero)));
        Assert.Equal(Outcome.Crash, asyncTest.GetOutcome(new RunOutput(139, "", "", false, true, TimeSpan.Zero)));
    }

    [Fact]
    public void MessageOutputMatching()
    {
        string dir = Path.Combine(Path.GetTempPath(), "v8sharp-message-" + Environment.ProcessId);
        Directory.CreateDirectory(Path.Combine(dir, "fail"));
        try
        {
            string basePath = Path.Combine(dir, "fail", "throw");
            File.WriteAllText(basePath + ".js", "throw new Error('x');");
            File.WriteAllText(basePath + ".out", """
                # Copyright header lines are ignored

                *%(basename)s:1: Error: x
                throw new Error('x');
                ^
                Error: x
                    at *%(basename)s:{NUMBER}:7
                """);
            var p = new MessageOutputProcessor(OutcomeSets.Pass, basePath, expectedFail: true);
            string actual = "/abs/fail/throw.js:1: Error: x\nthrow new Error('x');\n^\nError: x\n    at /abs/fail/throw.js:1:7\n\n";
            Assert.Equal(Outcome.Pass, p.GetOutcome(Out(1, actual)));
            Assert.Equal(Outcome.Fail, p.GetOutcome(Out(0, actual)));
            Assert.Equal(Outcome.Fail, p.GetOutcome(Out(1, actual.Replace("Error: x\n    at", "Error: y\n    at"))));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void ExpectationsFileRoundTrip()
    {
        string dir = Path.Combine(Path.GetTempPath(), "v8sharp-expectations-" + Environment.ProcessId);
        try
        {
            var e = new V8Sharp.TestRunner.Results.ExpectationsFile(dir, "mjsunit", "oracle");
            Assert.False(e.Exists);
            e.Update(new HashSet<string> { "b", "a", "c" }, new Dictionary<string, string> { ["b"] = "FAIL", ["a"] = "TIMEOUT" });
            string text = File.ReadAllText(e.Path);
            Assert.EndsWith("a  # TIMEOUT\nb  # FAIL\n", text);

            // Hand-written reasons survive; passing tests are dropped; tests that did not run are kept.
            File.WriteAllText(e.Path, text.Replace("b  # FAIL", "b  # needs Intl"));
            var e2 = new V8Sharp.TestRunner.Results.ExpectationsFile(dir, "mjsunit", "oracle");
            Assert.Equal("needs Intl", e2.Failing["b"]);
            e2.Update(new HashSet<string> { "a", "b" }, new Dictionary<string, string> { ["b"] = "CRASH" });
            var e3 = new V8Sharp.TestRunner.Results.ExpectationsFile(dir, "mjsunit", "oracle");
            Assert.Equal(["b"], e3.Failing.Keys);
            Assert.Equal("needs Intl", e3.Failing["b"]);
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }
}
