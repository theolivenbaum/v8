namespace V8Sharp.TestRunner.Engines;

/// <summary>
/// The port. Until V8Sharp can run scripts this reports every test as failing
/// with <see cref="UnavailableReason"/>. To wire the engine in, implement
/// <see cref="IJsIsolate"/> and <see cref="IJsRealm"/> over V8Sharp's Isolate
/// and NativeContext (RunScript = compile + Execution.Call + microtask
/// checkpoint; CreateFunction = a builtin-backed JSFunction that calls the
/// delegate; TerminateExecution = the stack-guard interrupt), apply
/// <see cref="SetFlags"/> to the FlagList, and clear <see cref="UnavailableReason"/>.
/// The d8 globals then come from D8Shell and the d8 shim, as for the oracle.
/// </summary>
public sealed class V8SharpEngine : IJsEngine
{
    public string Name => "v8sharp";

    /// <summary>The V8 revision being ported (dotnet/UPSTREAM.md).</summary>
    public string Version => "15.6.0";

    public string? UnavailableReason => "V8Sharp.TestRunner: the v8sharp engine cannot run JavaScript yet";

    public void SetFlags(IReadOnlyList<string> flags)
    {
    }

    public IJsIsolate CreateIsolate(IJsHost host) => throw new NotSupportedException(UnavailableReason);
}
