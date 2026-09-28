using Microsoft.ClearScript;
using Microsoft.ClearScript.V8;
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
/// V8Sharp. Wired up once the engine runs scripts (see dotnet/todo.md); until
/// then a measurement reports an error instead of a score.
/// </summary>
sealed class V8SharpHost(string workDir) : IBenchHost
{
    public void LoadFile(string path) => throw new NotSupportedException($"V8Sharp cannot run scripts yet ({workDir})");
    public void Execute(string source, string name) => throw new NotSupportedException("V8Sharp cannot run scripts yet");
    public void Dispose() { }
}
