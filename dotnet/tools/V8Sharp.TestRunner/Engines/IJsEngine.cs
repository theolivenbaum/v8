// The engine abstraction the runner drives. V8's run-tests.py drives the d8
// binary; here the d8 shell semantics live in D8Shell (Shell/D8Shell.cs) and
// talk to an engine through these interfaces, so that the same shell runs on
// the oracle (real V8 through ClearScript) and on V8Sharp.
namespace V8Sharp.TestRunner.Engines;

/// <summary>
/// A JavaScript engine: a factory of isolates. One instance per worker
/// process. Values crossing the boundary are <c>object?</c>:
/// <see cref="JsUndefined.Value"/>, <c>null</c> (JS null), <c>bool</c>,
/// <c>double</c>, <c>string</c>, or an engine-specific opaque handle for
/// anything else (objects, symbols, bigints), which the engine must accept
/// back unchanged (including from another realm of the same isolate).
/// </summary>
public interface IJsEngine
{
    /// <summary>"oracle" or "v8sharp".</summary>
    string Name { get; }

    /// <summary>What d8's <c>version()</c> returns (v8::V8::GetVersion).</summary>
    string Version { get; }

    /// <summary>Null when the engine can run scripts; otherwise why not
    /// (the v8sharp stub until the engine lands). Tests then report FAIL
    /// with this text as their output.</summary>
    string? UnavailableReason { get; }

    /// <summary>
    /// Applies V8 flags (<c>--allow-natives-syntax</c>, <c>--expose-gc</c> ...),
    /// before the first isolate is created. Called at most once per process;
    /// the runner groups tests by flag set, one worker process per group.
    /// Unknown flags are ignored, as d8 ignores them.
    /// </summary>
    void SetFlags(IReadOnlyList<string> flags);

    /// <summary>Creates an isolate with its first realm (d8's evaluation context).</summary>
    IJsIsolate CreateIsolate(IJsHost host);
}

/// <summary>Callbacks from the engine into the embedding shell.</summary>
public interface IJsHost
{
    /// <summary>Resolves <paramref name="specifier"/> against the module or
    /// script named <paramref name="referrer"/> and returns the module's
    /// canonical name and source text. Throws <see cref="JsHostError"/> when it
    /// cannot be loaded. <paramref name="type"/> is the import attribute
    /// <c>type</c> ("json", "text" ...) or null.</summary>
    ModuleSource LoadModule(string specifier, string referrer, string? type);

    /// <summary>Promise rejection tracking (v8::PromiseRejectCallback).</summary>
    void OnPromiseRejection(IJsRealm realm, PromiseRejectionKind kind, object promise, object? value);
}

public sealed record ModuleSource(string Name, string Source, bool IsJson = false, byte[]? Bytes = null);

public enum PromiseRejectionKind { RejectedWithoutHandler, HandlerAddedAfterReject }

/// <summary>A host function installed into a realm. <paramref name="args"/>
/// are the JS arguments. Throw <see cref="JsHostError"/> to throw a JS error.</summary>
public delegate object? JsHostFunction(IJsRealm realm, object?[] args);

/// <summary>Thrown by a host function to throw <c>new {ErrorType}(Message)</c>
/// in the calling realm.</summary>
public sealed class JsHostError(string errorType, string message) : Exception(message)
{
    public string ErrorType { get; } = errorType;
}

/// <summary>Thrown by a host function to throw an arbitrary JS value
/// (d8's Realm.eval rethrows the other realm's exception).</summary>
public sealed class JsThrowValue(object? value) : Exception("JS exception")
{
    public object? Value { get; } = value;
}

/// <summary>Thrown by a host function when execution is terminating
/// (quit(), the watchdog): the engine unwinds without catching.</summary>
public sealed class JsTermination() : Exception("terminated");

/// <summary>The JS <c>undefined</c> value on the host side.</summary>
public sealed class JsUndefined
{
    public static readonly JsUndefined Value = new();
    JsUndefined() { }
    public override string ToString() => "undefined";
}

public interface IJsIsolate : IDisposable
{
    /// <summary>The first realm, created with the isolate.</summary>
    IJsRealm MainRealm { get; }

    /// <summary>Creates a realm (v8::Context) in this isolate. With
    /// <paramref name="shareSecurityTokenWith"/>, the new context gets that
    /// realm's security token (d8's Realm.createAllowCrossRealmAccess);
    /// otherwise it keeps its own, so cross-realm access to its global proxy
    /// fails with "no access" (Realm.create).</summary>
    /// <param name="ownMicrotaskQueue">Realm.create({create_own_microtask_queue: true}): the context gets a MicrotaskQueue of its own.</param>
    IJsRealm CreateRealm(IJsRealm? shareSecurityTokenWith, bool ownMicrotaskQueue = false);

    /// <summary>v8::Isolate::TerminateExecution. Thread-safe; used by the
    /// watchdog and by <c>quit()</c>.</summary>
    void TerminateExecution();

    /// <summary>v8::Isolate::CancelTerminateExecution.</summary>
    void CancelTerminateExecution();

    /// <summary>A full garbage collection (d8 runs one at exit with --invoke-weak-callbacks).</summary>
    void CollectGarbage();

    /// <summary>v8::platform::PumpMessageLoop: runs the foreground tasks the
    /// engine posted (FinalizationRegistry cleanup, asynchronous gc()).
    /// Returns whether a task ran.</summary>
    bool PumpMessageLoop();
}

public interface IJsRealm : IDisposable
{
    IJsIsolate Isolate { get; }

    /// <summary>The realm's global proxy (<c>globalThis</c>).</summary>
    object GlobalObject { get; }

    /// <summary>Compiles and runs a classic script, then performs a microtask checkpoint.</summary>
    Completion RunScript(string source, string name);

    /// <summary>Loads, links and evaluates a module (imports go through
    /// <see cref="IJsHost.LoadModule"/>), then performs a microtask checkpoint.
    /// A rejected top-level-await promise is reported as a throw.</summary>
    Completion RunModule(string source, string name);

    /// <summary>Calls <paramref name="function"/> with <paramref name="receiver"/>
    /// and <paramref name="args"/>, then performs a microtask checkpoint.</summary>
    Completion Call(object function, object? receiver, params object?[] args);

    /// <summary><c>target[name]</c> (an ordinary [[Get]]).</summary>
    Completion GetProperty(object target, string name);

    /// <summary>Wraps a host function as a JS function object of this realm.</summary>
    object CreateFunction(string name, JsHostFunction function);

    /// <summary>v8::Context::DetachGlobal.</summary>
    void DetachGlobal();

    /// <summary>
    /// A host function whose arguments the engine converts to strings first
    /// (symbols to their description, everything else with ToString), as
    /// d8's print/printErr/write do in C++, so that no JS frame of the host
    /// shows in stack traces. Null when the engine cannot provide one.
    /// </summary>
    object? CreateStringArgumentsFunction(string name, JsHostFunction function) => null;

    /// <summary>Whether <see cref="SerializeValue"/> and friends work (d8's Worker and d8.serializer).</summary>
    bool SupportsSerialization => false;

    /// <summary>
    /// Shell::SerializeValue: structured-clones <paramref name="value"/> with
    /// the <paramref name="transfer"/> list into an engine-specific message
    /// (the completion's value), for <see cref="DeserializeValue"/> in any
    /// isolate of the process. Throws (as a completion) like d8 does. Called
    /// from inside a host function: no microtask checkpoint.
    /// </summary>
    Completion SerializeValue(object? value, object? transfer) => throw new NotSupportedException();

    /// <summary>Shell::DeserializeValue of a message made by <see cref="SerializeValue"/>.</summary>
    Completion DeserializeValue(object message) => throw new NotSupportedException();

    /// <summary>d8.serializer.serialize: the ValueSerializer bytes of the arguments as an ArrayBuffer.</summary>
    Completion SerializerSerialize(object?[] values) => throw new NotSupportedException();

    /// <summary>d8.serializer.deserialize of an ArrayBuffer.</summary>
    Completion SerializerDeserialize(object? buffer) => throw new NotSupportedException();
}

public enum CompletionKind { Normal, Throw, Terminated }

/// <summary>The result of running JS: a value, an uncaught exception, or termination.</summary>
public sealed record Completion(CompletionKind Kind, object? Value = null, JsExceptionInfo? Exception = null)
{
    public static readonly Completion Terminated = new(CompletionKind.Terminated);
    public static Completion Of(object? value) => new(CompletionKind.Normal, value);
}

/// <summary>
/// An uncaught exception with the v8::Message data d8's ReportException prints:
/// "<c>{ResourceName}:{Line}: {ToString(exception)}</c>", the source line, a
/// <c>^^^</c> underline from <see cref="StartColumn"/> to <see cref="EndColumn"/>,
/// then <c>exception.stack</c>. Location fields are null/-1 when unknown.
/// </summary>
public sealed record JsExceptionInfo(
    object? Exception,
    string? ResourceName = null,
    int Line = -1,
    int StartColumn = -1,
    int EndColumn = -1,
    string? SourceLine = null,
    bool IsSyntaxError = false);
