// Port of src/builtins/builtins-console.cc (the console builtins, the
// Formatter, console.context()) and of FastConsoleAssert
// (src/builtins/builtins-console-gen.cc). The console calls go to the
// embedder's debug::ConsoleDelegate (src/debug/interface-types.h), here
// Isolate.ConsoleDelegate; without a delegate they do nothing.
using V8Sharp.Init;

namespace V8Sharp.Builtins;

/// <summary>debug::ConsoleContext: the id and name of a console.context() object (0, "anonymous" for the global console).</summary>
public readonly record struct ConsoleContext(int Id, JSString Name);

/// <summary>
/// debug::ConsoleDelegate: the embedder's console. Every method gets the
/// (formatted) arguments without the receiver. The default does nothing.
/// </summary>
public abstract class ConsoleDelegate
{
    public virtual void Debug(Isolate isolate, ReadOnlySpan<JSValue> args, ConsoleContext context) { }
    public virtual void Error(Isolate isolate, ReadOnlySpan<JSValue> args, ConsoleContext context) { }
    public virtual void Info(Isolate isolate, ReadOnlySpan<JSValue> args, ConsoleContext context) { }
    public virtual void Log(Isolate isolate, ReadOnlySpan<JSValue> args, ConsoleContext context) { }
    public virtual void Warn(Isolate isolate, ReadOnlySpan<JSValue> args, ConsoleContext context) { }
    public virtual void Dir(Isolate isolate, ReadOnlySpan<JSValue> args, ConsoleContext context) { }
    public virtual void DirXml(Isolate isolate, ReadOnlySpan<JSValue> args, ConsoleContext context) { }
    public virtual void Table(Isolate isolate, ReadOnlySpan<JSValue> args, ConsoleContext context) { }
    public virtual void Trace(Isolate isolate, ReadOnlySpan<JSValue> args, ConsoleContext context) { }
    public virtual void Group(Isolate isolate, ReadOnlySpan<JSValue> args, ConsoleContext context) { }
    public virtual void GroupCollapsed(Isolate isolate, ReadOnlySpan<JSValue> args, ConsoleContext context) { }
    public virtual void GroupEnd(Isolate isolate, ReadOnlySpan<JSValue> args, ConsoleContext context) { }
    public virtual void Clear(Isolate isolate, ReadOnlySpan<JSValue> args, ConsoleContext context) { }
    public virtual void Count(Isolate isolate, ReadOnlySpan<JSValue> args, ConsoleContext context) { }
    public virtual void CountReset(Isolate isolate, ReadOnlySpan<JSValue> args, ConsoleContext context) { }
    public virtual void Assert(Isolate isolate, ReadOnlySpan<JSValue> args, ConsoleContext context) { }
    public virtual void Profile(Isolate isolate, ReadOnlySpan<JSValue> args, ConsoleContext context) { }
    public virtual void ProfileEnd(Isolate isolate, ReadOnlySpan<JSValue> args, ConsoleContext context) { }
    public virtual void Time(Isolate isolate, ReadOnlySpan<JSValue> args, ConsoleContext context) { }
    public virtual void TimeLog(Isolate isolate, ReadOnlySpan<JSValue> args, ConsoleContext context) { }
    public virtual void TimeEnd(Isolate isolate, ReadOnlySpan<JSValue> args, ConsoleContext context) { }
    public virtual void TimeStamp(Isolate isolate, ReadOnlySpan<JSValue> args, ConsoleContext context) { }
}

public static partial class BuiltinRegistry
{
    static partial void RegisterConsole()
    {
        Register(Builtin.ConsoleDebug, static (Isolate i, in BuiltinArguments a) => BuiltinsConsole.Formatted(i, a, 1, ConsoleMethod.Debug));
        Register(Builtin.ConsoleError, static (Isolate i, in BuiltinArguments a) => BuiltinsConsole.Formatted(i, a, 1, ConsoleMethod.Error));
        Register(Builtin.ConsoleInfo, static (Isolate i, in BuiltinArguments a) => BuiltinsConsole.Formatted(i, a, 1, ConsoleMethod.Info));
        Register(Builtin.ConsoleLog, static (Isolate i, in BuiltinArguments a) => BuiltinsConsole.Formatted(i, a, 1, ConsoleMethod.Log));
        Register(Builtin.ConsoleWarn, static (Isolate i, in BuiltinArguments a) => BuiltinsConsole.Formatted(i, a, 1, ConsoleMethod.Warn));
        Register(Builtin.ConsoleTrace, static (Isolate i, in BuiltinArguments a) => BuiltinsConsole.Formatted(i, a, 1, ConsoleMethod.Trace));
        Register(Builtin.ConsoleGroup, static (Isolate i, in BuiltinArguments a) => BuiltinsConsole.Formatted(i, a, 1, ConsoleMethod.Group));
        Register(Builtin.ConsoleGroupCollapsed, static (Isolate i, in BuiltinArguments a) => BuiltinsConsole.Formatted(i, a, 1, ConsoleMethod.GroupCollapsed));
        Register(Builtin.ConsoleAssert, static (Isolate i, in BuiltinArguments a) => BuiltinsConsole.Formatted(i, a, 2, ConsoleMethod.Assert));
        Register(Builtin.ConsoleDir, static (Isolate i, in BuiltinArguments a) => BuiltinsConsole.Plain(i, a, ConsoleMethod.Dir));
        Register(Builtin.ConsoleDirXml, static (Isolate i, in BuiltinArguments a) => BuiltinsConsole.Plain(i, a, ConsoleMethod.DirXml));
        Register(Builtin.ConsoleTable, static (Isolate i, in BuiltinArguments a) => BuiltinsConsole.Plain(i, a, ConsoleMethod.Table));
        Register(Builtin.ConsoleGroupEnd, static (Isolate i, in BuiltinArguments a) => BuiltinsConsole.Plain(i, a, ConsoleMethod.GroupEnd));
        Register(Builtin.ConsoleClear, static (Isolate i, in BuiltinArguments a) => BuiltinsConsole.Plain(i, a, ConsoleMethod.Clear));
        Register(Builtin.ConsoleCount, static (Isolate i, in BuiltinArguments a) => BuiltinsConsole.Plain(i, a, ConsoleMethod.Count));
        Register(Builtin.ConsoleCountReset, static (Isolate i, in BuiltinArguments a) => BuiltinsConsole.Plain(i, a, ConsoleMethod.CountReset));
        Register(Builtin.ConsoleProfile, static (Isolate i, in BuiltinArguments a) => BuiltinsConsole.Plain(i, a, ConsoleMethod.Profile));
        Register(Builtin.ConsoleProfileEnd, static (Isolate i, in BuiltinArguments a) => BuiltinsConsole.Plain(i, a, ConsoleMethod.ProfileEnd));
        // --log-timer-events is not ported, so LogTimerEvent does nothing.
        Register(Builtin.ConsoleTime, static (Isolate i, in BuiltinArguments a) => BuiltinsConsole.Plain(i, a, ConsoleMethod.Time));
        Register(Builtin.ConsoleTimeLog, static (Isolate i, in BuiltinArguments a) => BuiltinsConsole.Plain(i, a, ConsoleMethod.TimeLog));
        Register(Builtin.ConsoleTimeEnd, static (Isolate i, in BuiltinArguments a) => BuiltinsConsole.Plain(i, a, ConsoleMethod.TimeEnd));
        Register(Builtin.ConsoleTimeStamp, static (Isolate i, in BuiltinArguments a) => BuiltinsConsole.Plain(i, a, ConsoleMethod.TimeStamp));
        Register(Builtin.ConsoleContext, BuiltinsConsole.ConsoleContextBuiltin);
        Register(Builtin.FastConsoleAssert, BuiltinsConsole.FastConsoleAssert);
        // src/builtins/builtins-trace.cc: no trace categories are enabled.
        Register(Builtin.IsTraceCategoryEnabled, static (Isolate i, in BuiltinArguments a) => JSValue.False);
        Register(Builtin.Trace, static (Isolate i, in BuiltinArguments a) => JSValue.False);
    }
}

public enum ConsoleMethod
{
    Debug, Error, Info, Log, Warn, Dir, DirXml, Table, Trace, Group, GroupCollapsed, GroupEnd, Clear, Count, CountReset,
    Assert, Profile, ProfileEnd, Time, TimeLog, TimeEnd, TimeStamp,
}

public static class BuiltinsConsole
{
    // The closures installed on objects returned from `console.context()`
    // get a special builtin context with 2 slots, to hold the unique ID of
    // the console context and its name.
    const int CONSOLE_CONTEXT_ID_INDEX = 0;
    const int CONSOLE_CONTEXT_NAME_INDEX = 1;
    const int CONSOLE_CONTEXT_SLOTS = 2;

    /// <summary>A CONSOLE_METHOD_WITH_FORMATTER_LIST builtin: Formatter, then ConsoleCall.</summary>
    internal static JSValue Formatted(Isolate isolate, in BuiltinArguments args, int index, ConsoleMethod method)
    {
        JSValue[] formatted = args.Arguments.ToArray();
        Formatter(isolate, formatted, index - 1);
        ConsoleCall(isolate, args.Target, formatted, method);
        return JSValue.Undefined;
    }

    /// <summary>A CONSOLE_METHOD_LIST builtin (no formatter).</summary>
    internal static JSValue Plain(Isolate isolate, in BuiltinArguments args, ConsoleMethod method)
    {
        ConsoleCall(isolate, args.Target, args.Arguments, method);
        return JSValue.Undefined;
    }

    /// <summary>
    /// 2.2 Formatter(args) [https://console.spec.whatwg.org/#formatter]: %s, %i,
    /// %f and %d are converted in place; %o, %O, %c and %_ keep their argument.
    /// <paramref name="index"/> is the format string's index in
    /// <paramref name="args"/> (the arguments without the receiver).
    /// </summary>
    static void Formatter(Isolate isolate, JSValue[] args, int index)
    {
        if (args.Length < index + 2 || !args[index].IsString) return;
        // std::stack<State>: the top state is updated in place.
        var strs = new List<JSString>();
        var offs = new List<int>();
        JSString percent = ReadOnlyRoots.percent_sign_string;
        strs.Add(args[index++].As<JSString>());
        offs.Add(0);
        while (strs.Count != 0 && index < args.Length)
        {
            int top = strs.Count - 1;
            JSString str = strs[top];
            int off = JSString.IndexOf(str, percent, offs[top]);
            offs[top] = off;
            if (off < 0 || off == str.Length - 1)
            {
                strs.RemoveAt(top);
                offs.RemoveAt(top);
                continue;
            }
            JSValue current = args[index];
            char specifier = str.Get(off + 1);
            if (specifier is 'd' or 'f' or 'i')
            {
                if (current.IsSymbol)
                {
                    current = JSValue.NaN;
                }
                else
                {
                    JSFunction builtin = specifier == 'f' ? isolate.NativeContext.GlobalParseFloatFun : isolate.NativeContext.GlobalParseIntFun;
                    current = Execution.CallBuiltin(isolate, builtin, JSValue.Undefined, [current, JSValue.FromInt(10)]);
                }
            }
            else if (specifier == 's')
            {
                current = Execution.CallBuiltin(isolate, isolate.NativeContext.StringFunction, JSValue.Undefined, [current]);
                // Recurse into string results from type conversions, as they
                // can themselves contain formatting specifiers.
                strs.Add(current.As<JSString>());
                offs.Add(0);
            }
            else if (specifier is 'c' or 'o' or 'O' or '_')
            {
                // We leave the interpretation of %c (CSS), %o (optimally useful
                // formatting), and %O (generic JavaScript object formatting) as
                // well as the non-standard %_ (bypass formatter in Chrome) to
                // the debugger front-end, and preserve these specifiers as well
                // as their arguments verbatim.
                index++;
                offs[top] = off + 2;
                continue;
            }
            else if (specifier == '%')
            {
                // Chrome also supports %% as a way to generate a single % in the
                // output.
                offs[top] = off + 2;
                continue;
            }
            else
            {
                offs[top] = off + 1;
                continue;
            }

            // Replace the |specifier| (including the '%' character) in |target|
            // with the |current| value. We perform the replacement only morally
            // by updating the argument to the conversion result, but leave it to
            // the debugger front-end to perform the actual substitution.
            args[index++] = current;
            offs[top] = off + 2;
        }
    }

    static void ConsoleCall(Isolate isolate, JSFunction target, ReadOnlySpan<JSValue> args, ConsoleMethod method)
    {
        ConsoleDelegate? d = isolate.ConsoleDelegate;
        if (d is null) return;
        int contextId = 0;
        JSString contextName = ReadOnlyRoots.anonymous_string;
        if (!target.Context.IsNativeContext)
        {
            Context context = target.Context;
            contextId = (int)context.Get(CONSOLE_CONTEXT_ID_INDEX).Number;
            contextName = context.Get(CONSOLE_CONTEXT_NAME_INDEX).As<JSString>();
        }
        var c = new ConsoleContext(contextId, contextName);
        switch (method)
        {
            case ConsoleMethod.Debug: d.Debug(isolate, args, c); break;
            case ConsoleMethod.Error: d.Error(isolate, args, c); break;
            case ConsoleMethod.Info: d.Info(isolate, args, c); break;
            case ConsoleMethod.Log: d.Log(isolate, args, c); break;
            case ConsoleMethod.Warn: d.Warn(isolate, args, c); break;
            case ConsoleMethod.Dir: d.Dir(isolate, args, c); break;
            case ConsoleMethod.DirXml: d.DirXml(isolate, args, c); break;
            case ConsoleMethod.Table: d.Table(isolate, args, c); break;
            case ConsoleMethod.Trace: d.Trace(isolate, args, c); break;
            case ConsoleMethod.Group: d.Group(isolate, args, c); break;
            case ConsoleMethod.GroupCollapsed: d.GroupCollapsed(isolate, args, c); break;
            case ConsoleMethod.GroupEnd: d.GroupEnd(isolate, args, c); break;
            case ConsoleMethod.Clear: d.Clear(isolate, args, c); break;
            case ConsoleMethod.Count: d.Count(isolate, args, c); break;
            case ConsoleMethod.CountReset: d.CountReset(isolate, args, c); break;
            case ConsoleMethod.Assert: d.Assert(isolate, args, c); break;
            case ConsoleMethod.Profile: d.Profile(isolate, args, c); break;
            case ConsoleMethod.ProfileEnd: d.ProfileEnd(isolate, args, c); break;
            case ConsoleMethod.Time: d.Time(isolate, args, c); break;
            case ConsoleMethod.TimeLog: d.TimeLog(isolate, args, c); break;
            case ConsoleMethod.TimeEnd: d.TimeEnd(isolate, args, c); break;
            case ConsoleMethod.TimeStamp: d.TimeStamp(isolate, args, c); break;
        }
    }

    /// <summary>FastConsoleAssert (builtins-console-gen.cc): a truthy condition returns right away.</summary>
    public static JSValue FastConsoleAssert(Isolate isolate, in BuiltinArguments args)
    {
        if (ObjectOps.BooleanValue(args.AtOrUndefined(1))) return JSValue.Undefined;
        return Formatted(isolate, args, 2, ConsoleMethod.Assert);
    }

    static readonly (string Name, Builtin Builtin)[] s_contextMethods =
    [
        ("dir", Builtin.ConsoleDir), ("dirxml", Builtin.ConsoleDirXml), ("table", Builtin.ConsoleTable),
        ("groupEnd", Builtin.ConsoleGroupEnd), ("clear", Builtin.ConsoleClear), ("count", Builtin.ConsoleCount),
        ("countReset", Builtin.ConsoleCountReset), ("profile", Builtin.ConsoleProfile), ("profileEnd", Builtin.ConsoleProfileEnd),
        ("debug", Builtin.ConsoleDebug), ("error", Builtin.ConsoleError), ("info", Builtin.ConsoleInfo),
        ("log", Builtin.ConsoleLog), ("warn", Builtin.ConsoleWarn), ("trace", Builtin.ConsoleTrace),
        ("group", Builtin.ConsoleGroup), ("groupCollapsed", Builtin.ConsoleGroupCollapsed), ("assert", Builtin.ConsoleAssert),
        ("time", Builtin.ConsoleTime), ("timeLog", Builtin.ConsoleTimeLog), ("timeEnd", Builtin.ConsoleTimeEnd),
        ("timeStamp", Builtin.ConsoleTimeStamp),
    ];

    /// <summary>BUILTIN(ConsoleContext): console.context(name).</summary>
    public static JSValue ConsoleContextBuiltin(Isolate isolate, in BuiltinArguments args)
    {
        Factory factory = isolate.Factory;
        // Generate a unique ID for the new `console.context`
        // and convert the parameter to a string (defaults to
        // 'anonymous' if unspecified).
        JSString contextName = ReadOnlyRoots.anonymous_string;
        if (args.Length > 1) contextName = ObjectOps.ToString(isolate, args[1]);
        int contextId = ++isolate.LastConsoleContextId;

        NativeContext nativeContext = isolate.NativeContext;
        SharedFunctionInfo info = factory.NewSharedFunctionInfoForBuiltin(factory.InternalizeString("Context"), Builtin.Illegal, 0, false);
        info.LanguageMode = LanguageMode.Sloppy;
        info.UpdateFunctionMapIndex();
        JSFunction cons = factory.NewFunction(info, nativeContext);
        JSObject prototype = factory.NewJSObject(nativeContext.ObjectFunction);
        JSFunction.SetPrototype(isolate, cons, prototype);

        JSObject consoleContext = factory.NewJSObject(cons);

        Context context = factory.NewBuiltinContext(nativeContext, CONSOLE_CONTEXT_SLOTS);
        context.Set(CONSOLE_CONTEXT_ID_INDEX, JSValue.FromInt(contextId));
        context.Set(CONSOLE_CONTEXT_NAME_INDEX, contextName);

        foreach (var (name, builtin) in s_contextMethods)
        {
            // InstallContextFunction.
            JSString nameString = factory.InternalizeString(name);
            SharedFunctionInfo methodInfo = factory.NewSharedFunctionInfoForBuiltin(nameString, builtin, 1, false);
            methodInfo.LanguageMode = LanguageMode.Sloppy;
            methodInfo.Native = true;
            JSFunction fun = factory.NewFunction(methodInfo, context, nativeContext.SloppyFunctionWithoutPrototypeMap);
            JSObject.AddProperty(isolate, consoleContext, nameString, fun, PropertyAttributes.NONE);
        }
        return consoleContext;
    }

    /// <summary>Genesis::InitializeConsole: the console object on the extras binding and the global.</summary>
    internal static void InitializeConsole(Isolate isolate, NativeContext context, JSObject extrasBinding)
    {
        Factory factory = isolate.Factory;
        // -- C o n s o l e
        JSString name = ReadOnlyRoots.console_string;
        JSGlobalObject global = context.GlobalObject;
        SharedFunctionInfo info = factory.NewSharedFunctionInfoForBuiltin(name, Builtin.Illegal, 0, false);
        info.LanguageMode = LanguageMode.Strict;
        info.UpdateFunctionMapIndex();
        JSFunction cons = factory.NewFunction(info, context);
        JSObject empty = factory.NewJSObject(context.ObjectFunction);
        JSFunction.SetPrototype(isolate, cons, empty);

        JSObject console = factory.NewJSObject(cons);

        JSObject.AddProperty(isolate, extrasBinding, name, console, PropertyAttributes.DONT_ENUM);
        // TODO(v8:11989): remove this in the next release
        JSObject.AddProperty(isolate, global, name, console, PropertyAttributes.DONT_ENUM);

        (string, Builtin, int)[] methods =
        [
            ("debug", Builtin.ConsoleDebug, 0), ("error", Builtin.ConsoleError, 0), ("info", Builtin.ConsoleInfo, 0),
            ("log", Builtin.ConsoleLog, 0), ("warn", Builtin.ConsoleWarn, 0), ("dir", Builtin.ConsoleDir, 0),
            ("dirxml", Builtin.ConsoleDirXml, 0), ("table", Builtin.ConsoleTable, 0), ("trace", Builtin.ConsoleTrace, 0),
            ("group", Builtin.ConsoleGroup, 0), ("groupCollapsed", Builtin.ConsoleGroupCollapsed, 0),
            ("groupEnd", Builtin.ConsoleGroupEnd, 0), ("clear", Builtin.ConsoleClear, 0), ("count", Builtin.ConsoleCount, 0),
            ("countReset", Builtin.ConsoleCountReset, 0), ("assert", Builtin.FastConsoleAssert, 0),
            ("profile", Builtin.ConsoleProfile, 0), ("profileEnd", Builtin.ConsoleProfileEnd, 0),
            ("time", Builtin.ConsoleTime, 0), ("timeLog", Builtin.ConsoleTimeLog, 0), ("timeEnd", Builtin.ConsoleTimeEnd, 0),
            ("timeStamp", Builtin.ConsoleTimeStamp, 0), ("context", Builtin.ConsoleContext, 1),
        ];
        foreach (var (method, builtin, len) in methods)
        {
            Bootstrapper.SimpleInstallFunction(isolate, console, method, builtin, len, false, PropertyAttributes.NONE);
        }
        Bootstrapper.InstallToStringTag(isolate, console, "console");
    }

    /// <summary>Genesis::InstallExtrasBindings.</summary>
    internal static void InstallExtrasBindings(Isolate isolate, NativeContext context)
    {
        JSObject extrasBinding = isolate.Factory.NewJSObjectWithNullProto();

        // binding.isTraceCategoryEnabled(category)
        Bootstrapper.SimpleInstallFunction(isolate, extrasBinding, "isTraceCategoryEnabled", Builtin.IsTraceCategoryEnabled, 1, true);
        // binding.trace(phase, category, name, id, data)
        Bootstrapper.SimpleInstallFunction(isolate, extrasBinding, "trace", Builtin.Trace, 5, true);
        // V8_ENABLE_CONTINUATION_PRESERVED_EMBEDDER_DATA (get/setContinuationPreservedEmbedderData)
        // is not ported.

        InitializeConsole(isolate, context, extrasBinding);

        context.ExtrasBindingObject = extrasBinding;
    }
}
