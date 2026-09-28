// Port of the parts of src/d8/d8.cc that make d8 a script runner: option
// parsing (Shell::SetOptions), the globals (print, printErr, write, read,
// readbuffer, load, quit, version, setTimeout, d8.file.execute), running
// source groups (SourceGroup::Execute / Shell::ExecuteString), the message
// loop (setTimeout tasks and microtask checkpoints) and
// Shell::ReportException.
using System.Text;
using V8Sharp.Builtins;
using V8Sharp.Common;
using V8Sharp.Roots;
using V8Sharp.Codegen;
using V8Sharp.Objects;

namespace V8Sharp.D8;

public sealed class Shell
{
    readonly Isolate _isolate;
    readonly Queue<JSFunction> _timeouts = new();
    int? _exitCode;

    Shell(Isolate isolate) => _isolate = isolate;

    sealed class QuitException(int code) : Exception("quit")
    {
        public int Code { get; } = code;
    }

    /// <summary>Shell::Main.</summary>
    public static int Run(string[] args)
    {
        FlagList flags = FlagList.Default.Clone();
        var sources = new List<(string Kind, string Value)>();
        for (int i = 0; i < args.Length; i++)
        {
            string arg = args[i];
            if (arg == "-e" && i + 1 < args.Length)
            {
                sources.Add(("e", args[++i]));
            }
            else if (arg == "--module" && i + 1 < args.Length)
            {
                sources.Add(("module", args[++i]));
            }
            else if (arg is "--version" or "-v")
            {
                Console.Out.WriteLine("V8 version " + Version);
                return 0;
            }
            else if (arg.EndsWith(".mjs", StringComparison.Ordinal))
            {
                sources.Add(("module", arg));
            }
            else if (arg.StartsWith('-'))
            {
                // V8 flags; unknown ones are ignored as d8 ignores them.
                flags.SetFlagsFromCommandLine([arg]);
            }
            else
            {
                sources.Add(("file", arg));
            }
        }

        Isolate isolate = Isolate.New(flags);
        using (isolate.Enter())
        {
            var shell = new Shell(isolate);
            shell.Register();
            shell.InstallGlobals(isolate.NativeContext);
            shell._moduleLoader = new ModuleLoader(isolate, isolate.NativeContext,
                new D8ModuleSourceProvider(Directory.GetCurrentDirectory()));
            isolate.HostCreateShadowRealmContextCallback = ModuleLoader.HostCreateShadowRealmContext;
            int result = 0;
            try
            {
                foreach ((string kind, string value) in sources)
                {
                    bool ok = kind switch
                    {
                        "e" => shell.ExecuteString(value, "unnamed"),
                        "module" => shell.ExecuteModule(value),
                        _ => shell.ExecuteFile(value),
                    };
                    if (!ok)
                    {
                        result = 1;
                        break;
                    }
                }
                if (result == 0 && !shell.RunMessageLoop()) result = 1;
            }
            catch (QuitException quit)
            {
                result = quit.Code;
            }
            Console.Out.Flush();
            return shell._exitCode ?? result;
        }
    }

    const string Version = "14.7.0 (V8Sharp)";

    ModuleLoader _moduleLoader = null!;

    /// <summary>Shell::ExecuteModule.</summary>
    bool ExecuteModule(string fileName)
    {
        SourceTextModule rootModule;
        JSPromise resultPromise;
        try
        {
            (rootModule, resultPromise) = _moduleLoader.StartModule(fileName);
            Execution.PerformMicrotaskCheckpoint(_isolate);
            // Loop until module execution finishes.
            while (resultPromise.Status == PromiseState.kPending && (_isolate.HasPendingTasks || _timeouts.Count > 0))
            {
                if (!RunMessageLoopOnce()) return false;
            }
        }
        catch (JavaScriptException e)
        {
            ReportException(e);
            return false;
        }
        catch (TerminationException)
        {
            return true;
        }
        catch (Exception e) when (e is NotSupportedException or NotImplementedException or InvalidOperationException)
        {
            Console.Out.Write("V8Sharp internal error: " + e.GetType().Name + ": " + e.Message + "\n\n");
            return false;
        }

        if (resultPromise.Status == PromiseState.kRejected)
        {
            // If the exception has been caught by the promise pipeline, we rethrow
            // here in order to ReportException.
            ReportException(new JavaScriptException(resultPromise.Result, _isolate.CreateMessage(resultPromise.Result, null)));
            return false;
        }

        List<(SourceTextModule Module, JSMessageObject Message)> stalled = rootModule.GetStalledTopLevelAwaitMessages(_isolate);
        if (stalled.Count > 0)
        {
            JSMessageObject message = stalled[0].Message;
            JSString messageText = MessageFormatter.Format(_isolate, message.Type, [message.Argument]);
            ReportException(new JavaScriptException(
                _isolate.Factory.NewError(_isolate.NativeContext.ErrorFunction, messageText), message));
            return false;
        }
        return true;
    }

    // ---- Running scripts --------------------------------------------------------------

    bool ExecuteFile(string path)
    {
        string source;
        try
        {
            source = File.ReadAllText(path);
        }
        catch (IOException)
        {
            Console.Error.WriteLine("Error reading '" + path + "'");
            return false;
        }
        return ExecuteString(source, path);
    }

    /// <summary>Shell::ExecuteString: compiles and runs, reports an uncaught exception.</summary>
    bool ExecuteString(string source, string name)
    {
        try
        {
            _moduleLoader.Origin = name;
            RunScript(source, name);
            Execution.PerformMicrotaskCheckpoint(_isolate);
            return true;
        }
        catch (JavaScriptException e)
        {
            ReportException(e);
            return false;
        }
        catch (TerminationException)
        {
            return false;
        }
        catch (Exception e) when (e is NotSupportedException or NotImplementedException or InvalidOperationException)
        {
            // An engine gap (an unported builtin or runtime function): report it
            // like an uncaught exception instead of crashing the shell.
            Console.Out.Write("V8Sharp internal error: " + e.GetType().Name + ": " + e.Message + "\n\n");
            return false;
        }
    }

    JSValue RunScript(string source, string name)
    {
        JSFunction function = Compiler.CompileScript(_isolate, _isolate.Factory.NewStringFromUtf16(source),
            _isolate.Factory.NewStringFromUtf16(name));
        return Compiler.RunScript(_isolate, function);
    }

    /// <summary>The message loop: pending setTimeout callbacks, each followed by a microtask checkpoint.</summary>
    bool RunMessageLoop()
    {
        while (_timeouts.Count > 0 || _isolate.HasPendingTasks)
        {
            if (!RunMessageLoopOnce()) return false;
        }
        return true;
    }

    /// <summary>
    /// One step of the message loop. Foreground tasks posted by the engine
    /// (FinalizationRegistry cleanup, asynchronous gc()) run before the next
    /// timeout, as d8 pumps the platform's task queue between them.
    /// </summary>
    bool RunMessageLoopOnce()
    {
        try
        {
            if (_isolate.RunPendingTasks()) return true;
            if (_timeouts.Count == 0) return true;
            JSFunction callback = _timeouts.Dequeue();
            Execution.Call(_isolate, callback, JSValue.Undefined, []);
            Execution.PerformMicrotaskCheckpoint(_isolate);
            return true;
        }
        catch (JavaScriptException e)
        {
            ReportException(e);
            return false;
        }
    }

    /// <summary>Shell::ReportException.</summary>
    void ReportException(JavaScriptException e)
    {
        var output = new StringBuilder();
        string exceptionString = ToCString(e.Value);
        JSMessageObject? message = e.MessageObject;
        if (message is null)
        {
            output.Append(exceptionString).Append('\n');
        }
        else
        {
            // Print (filename):(line number): (message).
            string filename = message.Script.Name.HeapObjectOrNull is JSString n ? n.ToString() : "undefined";
            int linenum = message.GetLineNumber();
            output.Append(filename).Append(':').Append(linenum).Append(": ").Append(exceptionString).Append('\n');
            if (message.StartPosition >= 0)
            {
                // Print line of source code.
                output.Append(message.GetSourceLine(_isolate).ToString()).Append('\n');
                // Print wavy underline (GetUnderline is deprecated).
                int start = message.GetColumnNumber();
                for (int i = 0; i < start; i++) output.Append(' ');
                int end = start + (message.EndPosition - message.StartPosition);
                for (int i = start; i < end; i++) output.Append('^');
                output.Append('\n');
            }
        }
        if (e.Value.HeapObjectOrNull is JSReceiver receiver)
        {
            try
            {
                JSValue stack = ObjectOps.GetProperty(_isolate, receiver, ReadOnlyRoots.stack_string);
                if (stack.HeapObjectOrNull is JSString stackString) output.Append(stackString.ToString()).Append('\n');
            }
            catch (JavaScriptException)
            {
            }
        }
        output.Append('\n');
        Console.Out.Write(output.ToString());
    }

    string ToCString(JSValue value)
    {
        try
        {
            return ObjectOps.ToString(_isolate, value).ToString();
        }
        catch (JavaScriptException)
        {
            return "<string conversion failed>";
        }
    }

    // ---- Globals --------------------------------------------------------------------------

    JSFunction CreateFunction(NativeContext context, string name, BuiltinFunction callback, int length)
    {
        var data = new FunctionTemplateInfo(callback) { Length = length };
        SharedFunctionInfo info = _isolate.Factory.NewSharedFunctionInfo(_isolate.Factory.InternalizeString(name), data,
            Builtin.HandleApiCallOrConstruct, length, false);
        info.BuiltinId = Builtin.HandleApiCallOrConstruct;
        info.LanguageMode = LanguageMode.Strict;
        info.Native = true;
        info.UpdateFunctionMapIndex();
        return _isolate.Factory.NewFunction(info, context, context.StrictFunctionWithoutPrototypeMap);
    }

    void Install(NativeContext context, JSObject target, string name, BuiltinFunction callback, int length)
    {
        JSFunction function = CreateFunction(context, name, callback, length);
        JSObject.SetOwnPropertyIgnoreAttributes(_isolate, target, _isolate.Factory.InternalizeString(name), function,
            PropertyAttributes.DONT_ENUM);
    }

    /// <summary>Shell::CreateGlobalTemplate.</summary>
    void InstallGlobals(NativeContext context)
    {
        JSGlobalObject global = context.GlobalObject;
        Install(context, global, "print", Print, 0);
        Install(context, global, "printErr", PrintErr, 0);
        Install(context, global, "write", Write, 0);
        Install(context, global, "read", Read, 1);
        Install(context, global, "readline", ReadLine, 0);
        Install(context, global, "load", Load, 1);
        Install(context, global, "quit", Quit, 0);
        Install(context, global, "version", VersionFunction, 0);
        Install(context, global, "setTimeout", SetTimeout, 2);

        JSObject performance = _isolate.Factory.NewJSObject(context.ObjectFunction);
        Install(context, performance, "now", PerformanceNow, 0);
        JSObject.SetOwnPropertyIgnoreAttributes(_isolate, global, _isolate.Factory.InternalizeString("performance"), performance,
            PropertyAttributes.DONT_ENUM);

        JSObject d8 = _isolate.Factory.NewJSObject(context.ObjectFunction);
        JSObject file = _isolate.Factory.NewJSObject(context.ObjectFunction);
        Install(context, file, "execute", Load, 1);
        Install(context, file, "read", Read, 1);
        JSObject.SetOwnPropertyIgnoreAttributes(_isolate, d8, _isolate.Factory.InternalizeString("file"), file,
            PropertyAttributes.DONT_ENUM);
        JSObject.SetOwnPropertyIgnoreAttributes(_isolate, global, _isolate.Factory.InternalizeString("d8"), d8,
            PropertyAttributes.DONT_ENUM);
    }

    string ArgsToString(in BuiltinArguments args)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < args.ArgcWithoutReceiver; i++)
        {
            if (i > 0) sb.Append(' ');
            JSValue arg = args.Arguments[i];
            sb.Append(ObjectOps.ToString(_isolate, arg).ToString());
        }
        return sb.ToString();
    }

    /// <summary>Shell::Print.</summary>
    static JSValue Print(Isolate isolate, in BuiltinArguments args)
    {
        Console.Out.Write(Get(isolate).ArgsToString(args) + "\n");
        return JSValue.Undefined;
    }

    /// <summary>Shell::PrintErr.</summary>
    static JSValue PrintErr(Isolate isolate, in BuiltinArguments args)
    {
        Console.Out.Flush();
        Console.Error.Write(Get(isolate).ArgsToString(args) + "\n");
        return JSValue.Undefined;
    }

    /// <summary>Shell::WriteStdout.</summary>
    static JSValue Write(Isolate isolate, in BuiltinArguments args)
    {
        Console.Out.Write(Get(isolate).ArgsToString(args));
        return JSValue.Undefined;
    }

    /// <summary>Shell::ReadFile.</summary>
    static JSValue Read(Isolate isolate, in BuiltinArguments args)
    {
        string path = ObjectOps.ToString(isolate, args.AtOrUndefined(1)).ToString();
        try
        {
            return isolate.Factory.NewStringFromUtf16(File.ReadAllText(path));
        }
        catch (IOException)
        {
            return isolate.Throw(isolate.Factory.NewError(isolate.NativeContext.ErrorFunction,
                isolate.Factory.NewStringFromUtf16("Error loading file")));
        }
    }

    /// <summary>Shell::ReadLine.</summary>
    static JSValue ReadLine(Isolate isolate, in BuiltinArguments args)
    {
        string? line = Console.In.ReadLine();
        return line is null ? JSValue.Undefined : isolate.Factory.NewStringFromUtf16(line);
    }

    /// <summary>Shell::ExecuteFile (load).</summary>
    static JSValue Load(Isolate isolate, in BuiltinArguments args)
    {
        string path = ObjectOps.ToString(isolate, args.AtOrUndefined(1)).ToString();
        string source;
        try
        {
            source = File.ReadAllText(path);
        }
        catch (IOException)
        {
            return isolate.Throw(isolate.Factory.NewError(isolate.NativeContext.ErrorFunction,
                isolate.Factory.NewStringFromUtf16("Error loading file")));
        }
        return Get(isolate).RunScript(source, path);
    }

    /// <summary>Shell::Quit.</summary>
    static JSValue Quit(Isolate isolate, in BuiltinArguments args)
    {
        int code = args.ArgcWithoutReceiver > 0 ? (int)ObjectOps.ToNumber(isolate, args.Arguments[0]).Number : 0;
        Console.Out.Flush();
        Environment.Exit(code);
        return JSValue.Undefined;
    }

    /// <summary>Shell::Version.</summary>
    static JSValue VersionFunction(Isolate isolate, in BuiltinArguments args) => isolate.Factory.NewStringFromUtf16(Version);

    static readonly System.Diagnostics.Stopwatch s_timeOrigin = System.Diagnostics.Stopwatch.StartNew();

    /// <summary>Shell::PerformanceNow: milliseconds since the shell started.</summary>
    static JSValue PerformanceNow(Isolate isolate, in BuiltinArguments args) =>
        JSValue.FromNumber(s_timeOrigin.Elapsed.TotalMilliseconds);

    /// <summary>Shell::SetTimeout: queues the callback for the message loop.</summary>
    static JSValue SetTimeout(Isolate isolate, in BuiltinArguments args)
    {
        if (args.AtOrUndefined(1).HeapObjectOrNull is JSFunction callback) Get(isolate)._timeouts.Enqueue(callback);
        return JSValue.Zero;
    }

    static readonly Dictionary<Isolate, Shell> s_shells = [];

    static Shell Get(Isolate isolate)
    {
        lock (s_shells) return s_shells[isolate];
    }

    void Register()
    {
        lock (s_shells) s_shells[_isolate] = this;
    }
}
