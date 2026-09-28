using System.Runtime.InteropServices;
using System.Text;
using Microsoft.ClearScript;
using Microsoft.ClearScript.V8;

namespace V8Sharp.Oracle;

/// <summary>
/// The real V8, used as the reference engine. It runs a script with a d8-like
/// global surface (<c>print</c>, <c>printErr</c>, <c>quit</c>) and returns
/// everything the script printed, so the same script can be run through the
/// port and the two transcripts compared.
/// </summary>
/// <remarks>
/// ClearScript does not expose V8's flag parser, but its native library
/// exports <c>v8::V8::SetFlagsFromString</c>, so flags such as
/// <c>--allow-natives-syntax</c> or <c>--print-bytecode</c> are set by
/// P/Invoke. V8 flags are process-global and must be set before the first
/// isolate is created; <see cref="SetFlags"/> is therefore one-shot per
/// process for flags that V8 freezes (most of them).
/// </remarks>
public sealed class ReferenceV8 : IDisposable
{
    readonly V8ScriptEngine _engine;
    readonly StringBuilder _out = new();

    /// <summary>The V8 version of the oracle build (ClearScript ships its own).</summary>
    public const string Version = "14.7.173.23";

    public ReferenceV8(bool allowNativesSyntax = true)
    {
        if (allowNativesSyntax) EnsureFlags("--allow-natives-syntax");
        _engine = new V8ScriptEngine(V8ScriptEngineFlags.None);
        _engine.AddHostObject("__oracle_print", new Action<string>(s => { lock (_out) _out.Append(s).Append('\n'); }));
        _engine.Execute("""
            (function () {
              const p = __oracle_print; delete globalThis.__oracle_print;
              const fmt = (a) => Array.prototype.map.call(a, String).join(' ');
              globalThis.print = function print(...a) { p(fmt(a)); };
              globalThis.printErr = function printErr(...a) { p(fmt(a)); };
              globalThis.write = function write(...a) { p(fmt(a)); };
            })();
            """);
    }

    /// <summary>Runs a classic script. Returns stdout plus, on an uncaught
    /// exception, a final line in d8's shape ("Uncaught X").</summary>
    public string Run(string source, string name = "test.js")
    {
        _out.Clear();
        try
        {
            _engine.Execute(new DocumentInfo(name), source);
        }
        catch (ScriptEngineException e)
        {
            _out.Append(UncaughtLine(e)).Append('\n');
        }
        return _out.ToString();
    }

    /// <summary>Evaluates an expression and returns <c>String(result)</c>.</summary>
    public string Eval(string source)
    {
        _engine.Execute("globalThis.__oracle_result = String(eval(" + JsQuote(source) + "));");
        return (string)_engine.Evaluate("globalThis.__oracle_result");
    }

    static string UncaughtLine(ScriptEngineException e)
    {
        // ClearScript's message is "Error: msg"; d8 prints "Uncaught Error: msg".
        string msg = e.Message;
        return "Uncaught " + msg;
    }

    public static string JsQuote(string s)
    {
        var sb = new StringBuilder("\"");
        foreach (char c in s)
        {
            switch (c)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                default:
                    if (c < 0x20 || c is '\u2028' or '\u2029') sb.Append("\\u").Append(((int)c).ToString("x4"));
                    else sb.Append(c);
                    break;
            }
        }
        return sb.Append('"').ToString();
    }

    static readonly HashSet<string> s_flags = [];

    /// <summary>Sets V8 flags once per process. Returns false when the flag
    /// set was already applied.</summary>
    public static bool EnsureFlags(string flags)
    {
        lock (s_flags)
        {
            if (!s_flags.Add(flags)) return false;
            SetFlags(flags);
            return true;
        }
    }

    /// <summary>Calls <c>v8::V8::SetFlagsFromString(const char*)</c> in the
    /// ClearScript native library.</summary>
    public static void SetFlags(string flags)
    {
        // Must run before the first isolate exists: V8 freezes most flags at
        // initialization. The resolver below loads the same file ClearScript
        // loads later, so both see one copy of V8.
        Native.EnsureResolver();
        Native.SetFlagsFromString(flags);
    }

    public void Dispose() => _engine.Dispose();

    static class Native
    {
        const string Lib = "ClearScriptV8";

        static int s_registered;

        /// <summary>A P/Invoke does not run its declaring class's static
        /// constructor, so the resolver is installed explicitly.</summary>
        internal static void EnsureResolver()
        {
            if (Interlocked.Exchange(ref s_registered, 1) == 1) return;
            NativeLibrary.SetDllImportResolver(typeof(Native).Assembly, (name, asm, path) =>
            {
                if (name != Lib) return IntPtr.Zero;
                // RuntimeInformation.RuntimeIdentifier is distro-specific on
                // source-built SDKs (ubuntu.24.04-x64); ClearScript ships portable RIDs.
                string os = OperatingSystem.IsWindows() ? "win" : OperatingSystem.IsMacOS() ? "osx" : "linux";
                string arch = RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "arm64" : "x64";
                string rid = os + "-" + arch;
                string file = OperatingSystem.IsWindows() ? $"ClearScriptV8.{rid}.dll"
                    : OperatingSystem.IsMacOS() ? $"ClearScriptV8.{rid}.dylib" : $"ClearScriptV8.{rid}.so";
                foreach (var dir in new[] { AppContext.BaseDirectory, Path.Combine(AppContext.BaseDirectory, "runtimes", rid, "native") })
                {
                    string p = Path.Combine(dir, file);
                    if (File.Exists(p)) return NativeLibrary.Load(p);
                }
                return NativeLibrary.TryLoad(file, asm, path, out var h) ? h : IntPtr.Zero;
            });
        }

        // Itanium mangling of v8::V8::SetFlagsFromString(const char*).
        [DllImport(Lib, EntryPoint = "_ZN2v82V818SetFlagsFromStringEPKc")]
        internal static extern void SetFlagsFromString([MarshalAs(UnmanagedType.LPUTF8Str)] string flags);
    }
}
