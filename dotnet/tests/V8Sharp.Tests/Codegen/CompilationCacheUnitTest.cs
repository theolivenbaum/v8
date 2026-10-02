// Ports of the script compilation cache tests: test-api.cc's CompilationCache
// and heap-unittest.cc's CompilationCacheCachingBehavior{DiscardScript,
// RetainScript}, plus regression tests for what a cache hit must keep: fresh
// closures and toplevel state per evaluation, Function.prototype.toString,
// source positions and stack traces, and the eval cache's handling of large
// sources.
//
// V8 ages the cache with bytecode flushing (SharedFunctionInfo::
// EnsureOldForTesting and two major GCs); V8Sharp ages it at full .NET
// collections (AgeForTesting stands for them). V8Sharp does not flush
// bytecode, so an aged entry whose Script is alive is still a full hit
// (V8: a partial hit, the Script without its toplevel SharedFunctionInfo).
using System.Runtime.CompilerServices;
using V8Sharp.Codegen;
using V8Sharp.Objects;

namespace V8Sharp.Tests.Codegen;

public class CompilationCacheUnitTest : TestWithContext
{
    JSValue Run(string source, string? name = "test.js", int lineOffset = 0) =>
        Compiler.RunScript(i_isolate, CompileWithOrigin(source, name, lineOffset));

    JSFunction CompileWithOrigin(string source, string? name, int lineOffset = 0) =>
        Compiler.CompileScript(i_isolate, MakeString(source), name is null ? JSValue.Undefined : MakeString(name), lineOffset);

    string RunToString(string source, string? name = "test.js", int lineOffset = 0) =>
        Run(source, name, lineOffset).As<JSString>().ToString();

    CompilationCacheScript ScriptCache => CompilationCacheScript.For(i_isolate, LanguageMode.Sloppy)!;

    // test-api.cc: THREADED_TEST(CompilationCache).
    [Fact]
    public void CompilationCache()
    {
        JSFunction script0 = CompileWithOrigin("1234", "test.js");
        JSFunction script1 = CompileWithOrigin("1234", "test.js");
        JSFunction script2 = CompileWithOrigin("1234", null); // different origin
        Assert.Equal(1234, Compiler.RunScript(i_isolate, script0).Number);
        Assert.Equal(1234, Compiler.RunScript(i_isolate, script1).Number);
        Assert.Equal(1234, Compiler.RunScript(i_isolate, script2).Number);

        // The same source and origin share the Script and its SharedFunctionInfos.
        Assert.Same(script0.Shared, script1.Shared);
        Assert.NotSame(script0, script1);
        Assert.NotSame(script0.Shared, script2.Shared);
    }

    [Fact]
    public void OriginIsPartOfTheKey()
    {
        SharedFunctionInfo a = CompileWithOrigin("1", "a.js").Shared;
        Assert.NotSame(a, CompileWithOrigin("1", "b.js").Shared);
        Assert.NotSame(a, CompileWithOrigin("1", "a.js", lineOffset: 1).Shared);
        Assert.NotSame(a, Compiler.CompileScript(i_isolate, MakeString("1"), MakeString("a.js"), 0, 1).Shared);
        Assert.Same(a, CompileWithOrigin("1", "a.js").Shared);
        // A module with the same source and name is not the script.
        SourceTextModule module = Compiler.CompileModule(i_isolate, MakeString("1"), MakeString("a.js"));
        Assert.NotSame(a, module.GetSharedFunctionInfo());
    }

    // heap-unittest.cc: RunCompilationCacheCachingBehaviorTest.
    [MethodImpl(MethodImplOptions.NoInlining)]
    void RunScriptOnce(string source) => Run(source, null);

    void RunCompilationCacheCachingBehaviorTest(bool retainScript)
    {
        string rawSource = retainScript
            ? "function foo() {  var x = 42;  var y = 42;  var z = x + y;};foo();"
            : "(function foo() {  var x = 42;  var y = 42;  var z = x + y;})();";
        RunScriptOnce(rawSource);
        // The interpreter's frame records keep the functions of the last
        // returned frames until they are reused; another call of the same
        // depth replaces them (V8: the conservative stack scanning the test
        // disables).
        Run("(function () { return 0; })()", "other.js");
        var details = new ScriptDetails(JSValue.Undefined);

        // The script should be in the cache now.
        Assert.NotNull(ScriptCache.Lookup(rawSource, details));

        // Check that the code cache entry survives at least one GC.
        GC.Collect();
        ScriptCache.AgeForTesting();
        Assert.NotNull(ScriptCache.Lookup(rawSource, details));

        // Two ages without a lookup release the toplevel SharedFunctionInfo
        // (V8: the first GC flushes the bytecode, the second clears the entry).
        ScriptCache.AgeForTesting();
        ScriptCache.AgeForTesting();
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        // Ensure code aging cleared the entry from the cache, unless the Script
        // is kept alive by foo (V8: the Script is still found; here its toplevel
        // is still compiled, so the lookup is a full hit).
        Assert.Equal(retainScript, ScriptCache.Lookup(rawSource, details) is not null);
    }

    [Fact]
    public void CompilationCacheCachingBehaviorDiscardScript() => RunCompilationCacheCachingBehaviorTest(false);

    [Fact]
    public void CompilationCacheCachingBehaviorRetainScript() => RunCompilationCacheCachingBehaviorTest(true);

    // Each evaluation of a cached script runs its toplevel afresh: new closures,
    // new script context, the same SharedFunctionInfos.
    [Fact]
    public void CachedScriptCreatesFreshClosures()
    {
        const string source = "var runs = (typeof runs === 'number' ? runs : 0) + 1;\n" +
                              "function f() { return runs; }\n" +
                              "var g = function () { return 1; };\n" +
                              "[f, g]";
        JSArray first = Run(source).As<JSArray>();
        JSArray second = Run(source).As<JSArray>();
        Assert.Equal(2, Run("runs").Number);
        var f1 = ObjectOps.GetElement(i_isolate, first, 0).As<JSFunction>();
        var f2 = ObjectOps.GetElement(i_isolate, second, 0).As<JSFunction>();
        var g1 = ObjectOps.GetElement(i_isolate, first, 1).As<JSFunction>();
        var g2 = ObjectOps.GetElement(i_isolate, second, 1).As<JSFunction>();
        Assert.NotSame(f1, f2);
        Assert.NotSame(g1, g2);
        Assert.Same(f1.Shared, f2.Shared);
        Assert.Same(g1.Shared, g2.Shared);
        Assert.Equal("true", RunToString("String(" + "(function(){ var a = [f]; return a[0] === f; })())", "other.js"));
    }

    [Fact]
    public void CachedScriptLetRedeclarationThrows()
    {
        Run("let cachedLet = 1;");
        var e = Assert.Throws<JavaScriptException>(() => Run("let cachedLet = 1;"));
        Assert.Contains("cachedLet", ObjectOps.ToString(i_isolate, e.Value).ToString());
    }

    [Fact]
    public void CachedScriptToStringAndStackTrace()
    {
        const string source = "function thrower() {\n  throw new Error('boom');\n}\n" +
                              "(function () { try { thrower(); } catch (e) { return thrower.toString() + '|' + e.stack; } })()";
        string first = RunToString(source, "stack.js");
        string second = RunToString(source, "stack.js");
        Assert.Equal(first, second);
        Assert.StartsWith("function thrower() {\n  throw new Error('boom');\n}|Error: boom\n    at thrower (stack.js:2:9)", first);

        // Another line offset is another entry, with its own positions.
        string shifted = RunToString(source, "stack.js", lineOffset: 10);
        Assert.Contains("at thrower (stack.js:12:9)", shifted);
    }

    [Fact]
    public void CachedScriptSyntaxErrorIsNotCached()
    {
        Assert.Throws<JavaScriptException>(() => Run("var ;"));
        Assert.Throws<JavaScriptException>(() => Run("var ;"));
        Assert.Null(ScriptCache.Lookup("var ;", new ScriptDetails(MakeString("test.js"))));
    }

    [Fact]
    public void ReplModeScriptsAreNotCached()
    {
        SharedFunctionInfo a = Compiler.CompileScript(i_isolate, MakeString("1"), MakeString("repl"), isReplMode: true).Shared;
        SharedFunctionInfo b = Compiler.CompileScript(i_isolate, MakeString("1"), MakeString("repl"), isReplMode: true).Shared;
        Assert.NotSame(a, b);
    }

    [Fact]
    public void NoCompilationCacheFlag()
    {
        i_isolate.Flags.compilation_cache = false;
        Assert.NotSame(CompileWithOrigin("1", "a.js").Shared, CompileWithOrigin("1", "a.js").Shared);
        Assert.Null(CompilationCacheScript.For(i_isolate, LanguageMode.Sloppy));
    }

    [Fact]
    public void ModulesShareTheSharedFunctionInfo()
    {
        SourceTextModule m1 = Compiler.CompileModule(i_isolate, MakeString("export let x = 1;"), MakeString("m.mjs"));
        SourceTextModule m2 = Compiler.CompileModule(i_isolate, MakeString("export let x = 1;"), MakeString("m.mjs"));
        Assert.NotSame(m1, m2);
        Assert.Same(m1.GetSharedFunctionInfo(), m2.GetSharedFunctionInfo());
    }

    // The eval cache holds a large source weakly when first compiled: it hits
    // while the eval's code is alive, and is held strongly once compiled again.
    [Fact]
    public void EvalCacheLargeSource()
    {
        string body = new string(' ', CompilationCacheEval.kMaxSourceLength) + "function big() { return 7; }";
        Run("var bigSource = " + QuoteJs(body) + ";");
        Run("var big1 = (0, eval)(bigSource + 'big');");
        Run("var big2 = (0, eval)(bigSource + 'big');");
        var big1 = Run("big1").As<JSFunction>();
        var big2 = Run("big2").As<JSFunction>();
        Assert.Same(big1.Shared, big2.Shared);
        Assert.Equal(7, Run("big2()").Number);
    }

    [Fact]
    public void EvalCacheEachEvaluationRunsAfresh()
    {
        Run("var evalRuns = 0; function evalTwice() { return [(0, eval)('evalRuns++; (function inner() {})'), (0, eval)('evalRuns++; (function inner() {})')]; }");
        JSArray pair = Run("evalTwice()").As<JSArray>();
        var a = ObjectOps.GetElement(i_isolate, pair, 0).As<JSFunction>();
        var b = ObjectOps.GetElement(i_isolate, pair, 1).As<JSFunction>();
        Assert.NotSame(a, b);
        Assert.Same(a.Shared, b.Shared);
        Assert.Equal(2, Run("evalRuns").Number);
    }

    static string QuoteJs(string s) => "'" + s.Replace("\\", "\\\\").Replace("'", "\\'").Replace("\n", "\\n") + "'";
}
