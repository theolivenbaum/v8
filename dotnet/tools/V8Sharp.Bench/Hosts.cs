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
        if (flags.Length > 0) ReferenceV8.EnsureFlags(flags);
        _engine = new V8ScriptEngine(V8ScriptEngineFlags.DisableGlobalMembers);
        // ClearScript only binds public types; delegates avoid exposing one.
        _engine.AddHostObject("__print", new Action<string>(Console.WriteLine));
        _engine.AddHostObject("__load", new Action<string>(f => LoadFile(Path.Combine(_workDir, f))));
        _engine.AddHostObject("__read", new Func<string, string>(f => File.ReadAllText(Path.Combine(_workDir, f))));
        _engine.AddHostObject("cpuTimeMs", new Func<double>(Program.ThreadCpuTimeMs));
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
        if (failure is not null) throw failure;
    }

    public void LoadFile(string path) => RunOnLargeStack(File.ReadAllText(path), Path.GetFileName(path));
    public void Execute(string source, string name) => RunOnLargeStack(source, name);
    public void Dispose() { }
}
