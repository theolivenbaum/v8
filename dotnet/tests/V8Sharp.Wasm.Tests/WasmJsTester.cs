// Runs JavaScript that uses the WebAssembly JS API in a fresh isolate with a
// d8-like `print` and test/mjsunit/wasm/wasm-module-builder.js loaded, pumps
// the foreground tasks (asynchronous compilation), and returns the output.
using System.Text;
using V8Sharp.Codegen;
using V8Sharp.Builtins;
using V8Sharp.Objects;

namespace V8Sharp.Wasm.Tests;

public static class WasmJsTester
{
    static readonly Lazy<string> s_builder = new(() =>
        File.ReadAllText(Path.Combine(RepositoryRoot(), "test", "mjsunit", "wasm", "wasm-module-builder.js")));

    /// <summary>The V8 checkout this test runs in (the directory with test/mjsunit).</summary>
    public static string RepositoryRoot()
    {
        string? dir = AppContext.BaseDirectory;
        while (dir is not null && !Directory.Exists(Path.Combine(dir, "test", "mjsunit", "wasm")))
        {
            dir = Path.GetDirectoryName(dir);
        }
        return dir ?? throw new InvalidOperationException("V8 repository root not found");
    }

    /// <summary>Runs <paramref name="source"/> and returns what it printed (and an uncaught exception).</summary>
    public static string Run(string source, bool withBuilder = true, bool interpret = false, string? flags = null,
        Action<Isolate>? inspect = null)
    {
        var output = new StringBuilder();
        string result = "";
        var thread = new Thread(() => result = RunOnThread(source, withBuilder, output, interpret, flags, inspect), 256 * 1024 * 1024);
        thread.Start();
        thread.Join();
        return result;
    }

    static string RunOnThread(string source, bool withBuilder, StringBuilder output, bool interpret, string? flags,
        Action<Isolate>? inspect)
    {
        Isolate isolate = Isolate.New();
        using (isolate.Enter())
        {
            // The interpreter (--wasm-jitless) or the compiler (the default).
            isolate.Flags.wasm_jitless = interpret;
            if (flags is not null) isolate.Flags.SetFlagsFromString(flags);
            InstallPrint(isolate, output);
            try
            {
                if (withBuilder) Compiler.CompileAndRun(isolate, s_builder.Value, "wasm-module-builder.js");
                Compiler.CompileAndRun(isolate, source, "test.js");
                while (isolate.RunPendingTasks())
                {
                }
            }
            catch (JavaScriptException e)
            {
                output.Append("uncaught: ").Append(ObjectOps.ToString(isolate, e.Value).ToString()).Append('\n');
            }
            inspect?.Invoke(isolate);
            isolate.Deinit();
        }
        return output.ToString();
    }

    static void InstallPrint(Isolate isolate, StringBuilder output)
    {
        Factory factory = isolate.Factory;
        JSString name = factory.InternalizeString("print");
        var data = new FunctionTemplateInfo((Isolate i, in BuiltinArguments args) =>
        {
            for (int k = 0; k < args.ArgcWithoutReceiver; k++)
            {
                if (k > 0) output.Append(' ');
                output.Append(ObjectOps.ToString(i, args.Arguments[k]).ToString());
            }
            output.Append('\n');
            return JSValue.Undefined;
        }) { Length = 0 };
        SharedFunctionInfo info = factory.NewSharedFunctionInfo(name, data, Builtin.HandleApiCallOrConstruct, 0, false);
        info.BuiltinId = Builtin.HandleApiCallOrConstruct;
        info.Native = true;
        info.UpdateFunctionMapIndex();
        JSFunction print = factory.NewFunction(info, isolate.NativeContext, isolate.NativeContext.StrictFunctionWithoutPrototypeMap);
        JSObject.SetOwnPropertyIgnoreAttributes(isolate, isolate.NativeContext.GlobalObject, name, print,
            PropertyAttributes.DONT_ENUM);
    }
}
