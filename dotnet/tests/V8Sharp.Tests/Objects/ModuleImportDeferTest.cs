// Tests of import defer (src/objects/module.cc: JSDeferredModuleNamespace,
// source-text-module.cc: ReadyForSyncExecution), the CloneObjectIC fast path
// (src/ic/ic.cc) and spread calls without an argument count cap, through
// modules compiled and linked with a resolve callback over in-memory sources
// (as test/unittests/objects/modules-unittest.cc does with the API).
using V8Sharp.Codegen;

namespace V8Sharp.Tests.Objects;

public class ModuleImportDeferTest : TestWithContext
{
    readonly Dictionary<string, Module> _modules = new(StringComparer.Ordinal);

    public ModuleImportDeferTest()
    {
        i_isolate.Flags.js_defer_import_eval = true;
        i_isolate.Flags.allow_natives_syntax = true;
    }

    Module Compile(string name, string source)
    {
        Module module = Compiler.CompileModule(i_isolate, MakeString(source), MakeString(name));
        _modules[name] = module;
        return module;
    }

    Module Resolve(Isolate isolate, JSString specifier, FixedArray importAttributes, Module referrer) =>
        _modules[specifier.ToString()];

    /// <summary>Instantiates and evaluates <paramref name="main"/>; returns its evaluation promise.</summary>
    JSPromise Run(Module main)
    {
        Module.Instantiate(i_isolate, main, Resolve);
        JSPromise promise = Module.Evaluate(i_isolate, main);
        Execution.PerformMicrotaskCheckpoint(i_isolate);
        Assert.True(promise.Status != PromiseState.kRejected, ObjectOps.NoSideEffectsToString(i_isolate, promise.Result).ToString());
        return promise;
    }

    string Global(string name) =>
        ObjectOps.ToString(i_isolate, ObjectOps.GetProperty(i_isolate, i_isolate.NativeContext.GlobalObject,
            factory.InternalizeString(name))).ToString();

    [Fact]
    public void DeferredNamespaceEvaluatesOnFirstStringKeyAccess()
    {
        Compile("dep", "globalThis.log.push('dep'); export let foo = 42;");
        JSPromise promise = Run(Compile("main", """
            globalThis.log = [];
            import defer * as ns from 'dep';
            const before = log.join();
            const tag = ns[Symbol.toStringTag];
            const hasThen = 'then' in ns;
            const afterSymbolAndThen = log.join();
            const foo = ns.foo;
            globalThis.result = [before, tag, hasThen, afterSymbolAndThen, foo, log.join()].join('|');
            """));
        Assert.Equal(PromiseState.kFulfilled, promise.Status);
        Assert.Equal("|Deferred Module|false||42|dep", Global("result"));
    }

    [Fact]
    public void DeferredNamespaceKeysEvaluate()
    {
        Compile("dep", "globalThis.log.push('dep'); export let b = 1, a = 2;");
        JSPromise promise = Run(Compile("main", """
            globalThis.log = [];
            import defer * as ns from 'dep';
            globalThis.result = [Object.keys(ns).join(), log.join()].join('|');
            """));
        Assert.Equal(PromiseState.kFulfilled, promise.Status);
        Assert.Equal("a,b|dep", Global("result"));
    }

    [Fact]
    public void DeferredAsyncModuleIsEvaluatedEagerly()
    {
        // GatherAsynchronousTransitiveDependencies: a deferred module with
        // top-level await is evaluated before the importer, the importer's own
        // synchronous dependency only on access.
        Compiler.CompileAndRun(i_isolate, "globalThis.log = [];");
        Compile("async", "globalThis.log.push('async'); await 0; export let x = 1;");
        Compile("dep", "import 'async'; globalThis.log.push('dep'); export let y = 2;");
        JSPromise promise = Run(Compile("main", """
            import defer * as ns from 'dep';
            log.push('main');
            const y = ns.y;
            globalThis.result = [y, log.join()].join('|');
            """));
        Assert.Equal(PromiseState.kFulfilled, promise.Status);
        Assert.Equal("2|async,main,dep", Global("result"));
    }

    [Fact]
    public void DeferredModuleEvaluatingItselfIsNotReady()
    {
        Compile("self", """
            import defer * as ns from 'self';
            export let x = 1;
            try { ns.x; globalThis.result = 'no error'; } catch (e) { globalThis.result = e.constructor.name + ': ' + e.message; }
            """);
        JSPromise promise = Run(_modules["self"]);
        Assert.Equal(PromiseState.kFulfilled, promise.Status);
        Assert.Equal("TypeError: Deferred module is not ready for sync execution", Global("result"));
    }

    [Fact]
    public void ObjectSpreadSharesTheSourceMap()
    {
        JSValue result = Compiler.CompileAndRun(i_isolate, """
            var o = {};
            o.x = "1";
            var o2 = {...o};
            function clone(a) { return {...a}; }
            var o3 = clone(o), o4 = clone(o);
            [%HaveSameMap(o, o2), %HaveSameMap(o, o3), %HaveSameMap(o, o4), o4.x].join();
            """);
        Assert.Equal("true,true,true,1", ObjectOps.ToString(i_isolate, result).ToString());
    }

    [Fact]
    public void SpreadCallHasNoArgumentCountLimit()
    {
        JSValue result = Compiler.CompileAndRun(i_isolate, """
            function f() { return arguments.length; }
            var a = [];
            a.length = 81832;
            f(...a);
            """);
        Assert.Equal(81832, result.Number);
    }
}
