using System.Runtime.InteropServices;

namespace V8Sharp.Oracle;

/// <summary>
/// The handful of V8 embedder API calls that d8 makes and ClearScript does
/// not expose, called by P/Invoke into the V8 that ClearScript's native
/// library links statically (the entry points are exported with their
/// Itanium-mangled names).
/// </summary>
/// <remarks>
/// V8's <c>Local&lt;T&gt;</c> is one pointer, passed and returned in a register,
/// so it is an <see cref="IntPtr"/> here. A <c>Local</c> is only valid inside
/// the handle scope that produced it: every method that takes or returns a
/// context or value handle must be called from inside a host callback (a
/// ClearScript host delegate invoked by JavaScript), where the calling
/// context is entered and a handle scope is open. <see cref="GetCurrentIsolate"/>
/// and <see cref="TerminateExecution"/> have no such restriction.
/// </remarks>
public static class V8Api
{
    const string Lib = "ClearScriptV8";

    static V8Api() => ReferenceV8.EnsureNativeResolver();

    /// <summary>v8::Isolate::GetCurrent(): the isolate entered on this thread.</summary>
    public static IntPtr GetCurrentIsolate() => Isolate_GetCurrent();

    /// <summary>The <c>Local&lt;Context&gt;</c> of the running JavaScript
    /// (v8::Isolate::GetCurrentContext).</summary>
    public static IntPtr GetCurrentContext() => Isolate_GetCurrentContext(Isolate_GetCurrent());

    /// <summary>v8::Context::GetSecurityToken.</summary>
    public static IntPtr GetSecurityToken(IntPtr context) => Context_GetSecurityToken(context);

    /// <summary>v8::Context::SetSecurityToken. Contexts with the same token can
    /// access each other's global proxy (d8's Realm.createAllowCrossRealmAccess).</summary>
    public static void SetSecurityToken(IntPtr context, IntPtr token) => Context_SetSecurityToken(context, token);

    /// <summary>v8::Context::DetachGlobal.</summary>
    public static void DetachGlobal(IntPtr context) => Context_DetachGlobal(context);

    /// <summary>v8::Isolate::SetAllowAtomicsWait (d8's --no-can-block).</summary>
    public static void SetAllowAtomicsWait(IntPtr isolate, bool allow) => Isolate_SetAllowAtomicsWait(isolate, allow);

    /// <summary>v8::Isolate::TerminateExecution. Callable from any thread.</summary>
    public static void TerminateExecution(IntPtr isolate) => Isolate_TerminateExecution(isolate);

    /// <summary>v8::Isolate::LowMemoryNotification: a full GC that also
    /// runs weak callbacks (d8's --invoke-weak-callbacks).</summary>
    public static void LowMemoryNotification(IntPtr isolate) => Isolate_LowMemoryNotification(isolate);

    [DllImport(Lib, EntryPoint = "_ZN2v87Isolate10GetCurrentEv")]
    static extern IntPtr Isolate_GetCurrent();

    [DllImport(Lib, EntryPoint = "_ZN2v87Isolate17GetCurrentContextEv")]
    static extern IntPtr Isolate_GetCurrentContext(IntPtr isolate);

    [DllImport(Lib, EntryPoint = "_ZN2v87Context16GetSecurityTokenEv")]
    static extern IntPtr Context_GetSecurityToken(IntPtr context);

    [DllImport(Lib, EntryPoint = "_ZN2v87Context16SetSecurityTokenENS_5LocalINS_5ValueEEE")]
    static extern void Context_SetSecurityToken(IntPtr context, IntPtr token);

    [DllImport(Lib, EntryPoint = "_ZN2v87Context12DetachGlobalEv")]
    static extern void Context_DetachGlobal(IntPtr context);

    [DllImport(Lib, EntryPoint = "_ZN2v87Isolate19SetAllowAtomicsWaitEb")]
    static extern void Isolate_SetAllowAtomicsWait(IntPtr isolate, [MarshalAs(UnmanagedType.U1)] bool allow);

    [DllImport(Lib, EntryPoint = "_ZN2v87Isolate18TerminateExecutionEv")]
    static extern void Isolate_TerminateExecution(IntPtr isolate);

    [DllImport(Lib, EntryPoint = "_ZN2v87Isolate21LowMemoryNotificationEv")]
    static extern void Isolate_LowMemoryNotification(IntPtr isolate);
}
