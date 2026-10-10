using Microsoft.ClearScript;
using Microsoft.ClearScript.V8;
using V8Sharp.Builtins;
using V8Sharp.Codegen;
using V8Sharp.Common;
using V8Sharp.Objects;
using V8Sharp.Oracle;

namespace V8Sharp.Bench;

/// <summary>The d8 surface the benchmarks use: print, load, read, d8.file.execute.</summary>
interface IBenchHost : IDisposable
{
    void LoadFile(string path);
    void Execute(string source, string name);
}

/// <summary>The real V8 through ClearScript, with the tiering flags of one mode.</summary>
sealed class OracleHost : IBenchHost
{
    readonly V8ScriptEngine _engine;
    readonly string _workDir;

    public OracleHost(string flags, string workDir)
    {
        _workDir = workDir;
        Directory.SetCurrentDirectory(workDir);
        // --allow-natives-syntax for waitForCompilations (%WaitForBackgroundOptimization);
        // it only lets the parser accept %-calls.
        ReferenceV8.EnsureFlags((flags + " --allow-natives-syntax").Trim());
        _engine = new V8ScriptEngine(V8ScriptEngineFlags.DisableGlobalMembers);
        // ClearScript only binds public types; delegates avoid exposing one.
        _engine.AddHostObject("__print", new Action<string>(Console.WriteLine));
        _engine.AddHostObject("__load", new Action<string>(f => LoadFile(Path.Combine(_workDir, f))));
        _engine.AddHostObject("__read", new Func<string, string>(f => File.ReadAllText(Path.Combine(_workDir, f))));
        _engine.AddHostObject("cpuTimeMs", new Func<double>(Program.ThreadCpuTimeMs));
        _engine.AddHostObject("settle", new Action(Program.Settle));
        _engine.Execute("""
            (function () {
              const p = __print, l = __load, r = __read;
              delete globalThis.__print; delete globalThis.__load; delete globalThis.__read;
              globalThis.print = function print(...a) { p(a.map(String).join(' ')); };
              globalThis.printErr = globalThis.print;
              globalThis.load = function load(f) { l(String(f)); };
              globalThis.read = function read(f) { return r(String(f)); };
              globalThis.quit = function quit() { };
              globalThis.d8 = { file: { execute: globalThis.load, read: globalThis.read } };
              globalThis.waitForCompilations = function waitForCompilations() { %WaitForBackgroundOptimization(); };
            })();
            """);
    }

    public void LoadFile(string path) => _engine.Execute(new DocumentInfo(Path.GetFileName(path)), File.ReadAllText(path));
    public void Execute(string source, string name) => _engine.Execute(new DocumentInfo(name), source);
    public void Dispose() => _engine.Dispose();
}

/// <summary>
/// V8Sharp in-process, with the V8 flags of one mode (the tiers: Ignition
/// only, Ignition + baseline IL). The d8 surface is installed from API
/// functions, as d8 does; scripts run on a thread with d8's deep stack.
/// </summary>
sealed class V8SharpHost : IBenchHost
{
    readonly Isolate _isolate;
    readonly string _workDir;

    public V8SharpHost(string flags, string workDir)
    {
        _workDir = workDir;
        Directory.SetCurrentDirectory(workDir);
        var flagList = new FlagList();
        if (flags.Length > 0) flagList.SetFlagsFromString(flags);
        _isolate = Isolate.New(flagList);
        using (_isolate.Enter())
        {
            NativeContext context = _isolate.NativeContext;
            JSGlobalObject global = context.GlobalObject;
            Install(context, global, "print", Print);
            Install(context, global, "printErr", Print);
            Install(context, global, "load", Load);
            Install(context, global, "read", Read);
            Install(context, global, "quit", static (Isolate i, in BuiltinArguments a) => JSValue.Undefined);
            Install(context, global, "cpuTimeMs",
                static (Isolate i, in BuiltinArguments a) => JSValue.FromNumber(Program.ThreadCpuTimeMs()));
            // octane-steady: compiles queued during the warm-up finish before the
            // measured runs, so the score is the generated code, not RyuJIT.
            Install(context, global, "settle",
                static (Isolate i, in BuiltinArguments a) => { Program.Settle(); return JSValue.Undefined; });
            Install(context, global, "waitForCompilations",
                static (Isolate i, in BuiltinArguments a) => { i.WaitForBackgroundCompilation(); return JSValue.Undefined; });
            // Bytes the CLR allocated on this thread (micro/cpu.js prints bytes per iteration).
            Install(context, global, "allocatedBytes",
                static (Isolate i, in BuiltinArguments a) => JSValue.FromNumber(GC.GetAllocatedBytesForCurrentThread()));
            // octane-steady: the CLR heap's work over the measured runs (MB
            // allocated, collections per generation, pause time), printed as
            // @gc lines by gcReport; gcMark starts the window.
            Install(context, global, "gcMark",
                static (Isolate i, in BuiltinArguments a) => { GcWindow.Mark(); return JSValue.Undefined; });
            Install(context, global, "gcReport",
                static (Isolate i, in BuiltinArguments a) => { GcWindow.Report(); return JSValue.Undefined; });
            JSObject d8 = _isolate.Factory.NewJSObject(context.ObjectFunction);
            JSObject file = _isolate.Factory.NewJSObject(context.ObjectFunction);
            Install(context, file, "execute", Load);
            Install(context, file, "read", Read);
            JSObject.SetOwnPropertyIgnoreAttributes(_isolate, d8, _isolate.Factory.InternalizeString("file"), file,
                PropertyAttributes.DONT_ENUM);
            JSObject.SetOwnPropertyIgnoreAttributes(_isolate, global, _isolate.Factory.InternalizeString("d8"), d8,
                PropertyAttributes.DONT_ENUM);
        }
    }

    void Install(NativeContext context, JSObject target, string name, BuiltinFunction callback)
    {
        var data = new FunctionTemplateInfo(callback);
        SharedFunctionInfo info = _isolate.Factory.NewSharedFunctionInfo(_isolate.Factory.InternalizeString(name), data,
            Builtin.HandleApiCallOrConstruct, 0, false);
        info.BuiltinId = Builtin.HandleApiCallOrConstruct;
        info.LanguageMode = LanguageMode.Strict;
        info.Native = true;
        info.UpdateFunctionMapIndex();
        JSFunction function = _isolate.Factory.NewFunction(info, context, context.StrictFunctionWithoutPrototypeMap);
        JSObject.SetOwnPropertyIgnoreAttributes(_isolate, target, _isolate.Factory.InternalizeString(name), function,
            PropertyAttributes.DONT_ENUM);
    }

    static JSValue Print(Isolate isolate, in BuiltinArguments args)
    {
        var sb = new System.Text.StringBuilder();
        for (int i = 0; i < args.ArgcWithoutReceiver; i++)
        {
            if (i > 0) sb.Append(' ');
            sb.Append(ObjectOps.ToString(isolate, args.Arguments[i]).ToString());
        }
        Console.WriteLine(sb.ToString());
        return JSValue.Undefined;
    }

    JSValue Load(Isolate isolate, in BuiltinArguments args)
    {
        string path = ObjectOps.ToString(isolate, args.AtOrUndefined(1)).ToString();
        Run(File.ReadAllText(Path.Combine(_workDir, path)), path);
        return JSValue.Undefined;
    }

    JSValue Read(Isolate isolate, in BuiltinArguments args)
    {
        string path = ObjectOps.ToString(isolate, args.AtOrUndefined(1)).ToString();
        return isolate.Factory.NewStringFromUtf16(File.ReadAllText(Path.Combine(_workDir, path)));
    }

    void Run(string source, string name)
    {
        JSFunction function = Compiler.CompileScript(_isolate, _isolate.Factory.NewStringFromUtf16(source),
            _isolate.Factory.NewStringFromUtf16(name));
        Compiler.RunScript(_isolate, function);
    }

    void RunOnLargeStack(string source, string name)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            using (_isolate.Enter())
            {
                try
                {
                    Run(source, name);
                    Execution.PerformMicrotaskCheckpoint(_isolate);
                }
                catch (JavaScriptException e)
                {
                    failure = new InvalidOperationException(name + ": uncaught " + ObjectOps.ToString(_isolate, e.Value));
                }
                catch (Exception e)
                {
                    failure = e;
                }
            }
        }, 256 * 1024 * 1024);
        thread.Start();
        thread.Join();
        if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }

    public void LoadFile(string path) => RunOnLargeStack(File.ReadAllText(path), Path.GetFileName(path));
    public void Execute(string source, string name) => RunOnLargeStack(source, name);
    public void Dispose() { }
}

/// <summary>The CLR heap's work over a window of a measurement (octane-steady's measured runs).</summary>
static class GcWindow
{
    static long _bytes;
    static int _g0, _g1, _g2;
    static TimeSpan _pause;
    static bool _marked;

    public static void Mark()
    {
        if (_marked) return;
        _marked = true;
        _bytes = GC.GetTotalAllocatedBytes(precise: true);
        _g0 = GC.CollectionCount(0);
        _g1 = GC.CollectionCount(1);
        _g2 = GC.CollectionCount(2);
        _pause = GC.GetTotalPauseDuration();
    }

    public static void Report()
    {
        if (!_marked) return;
        var ci = System.Globalization.CultureInfo.InvariantCulture;
        double mb = (GC.GetTotalAllocatedBytes(precise: true) - _bytes) / 1048576.0;
        double pause = (GC.GetTotalPauseDuration() - _pause).TotalMilliseconds;
        Console.WriteLine(string.Format(ci, "@gc allocMB {0:F1} gen0 {1} gen1 {2} gen2 {3} pauseMs {4:F1} heapMB {5:F1}",
            mb, GC.CollectionCount(0) - _g0, GC.CollectionCount(1) - _g1, GC.CollectionCount(2) - _g2, pause,
            GC.GetGCMemoryInfo().HeapSizeBytes / 1048576.0));
    }
}
