// Port of the access-check parts of src/execution/isolate.cc: MayAccess,
// ReportFailedAccessCheck and DetachGlobal. V8Sharp has no embedder API, so
// there is never an AccessCheckInfo callback: an access is allowed exactly
// when the security tokens of the accessing context and of the receiver's
// creation context match (global proxies), as in V8 without callbacks.
namespace V8Sharp;

public sealed partial class Isolate
{
    /// <summary>Isolate::MayAccess.</summary>
    public bool MayAccess(NativeContext accessingContext, JSObject receiver)
    {
        // During bootstrapping, callback functions are not enabled yet.
        if (BootstrapperActive) return true;
        if (receiver is JSGlobalProxy)
        {
            NativeContext? receiverContext = receiver.GetCreationContext();
            if (receiverContext is null) return false;
            // NativeContext::HasSameSecurityTokenAs.
            if (receiverContext.SecurityToken.IsIdenticalTo(accessingContext.SecurityToken)) return true;
        }
        // AccessCheckInfo::Get: no embedder access check callbacks exist.
        return false;
    }

    /// <summary>
    /// IsAccessCheckNeeded(Tagged&lt;Object&gt;): a global proxy needs a check only
    /// when it does not belong to the current context's global object.
    /// </summary>
    public bool IsAccessCheckNeeded(JSObject obj)
    {
        if (obj is JSGlobalProxy proxy)
        {
            return proxy.IsDetachedFrom(Context!.NativeContext.GlobalObject);
        }
        return obj.Map.IsAccessCheckNeeded;
    }

    /// <summary>
    /// Isolate::ReportFailedAccessCheck: without a FailedAccessCheckCallback V8
    /// throws TypeError kNoAccess.
    /// </summary>
    public JSValue ReportFailedAccessCheck(JSObject receiver) => ThrowTypeError(MessageTemplate.NoAccess);

    /// <summary>
    /// Isolate::DetachGlobal (v8::Context::DetachGlobal, d8's Realm.detachGlobal):
    /// the global proxy loses its global object and its creation context, and
    /// the context gets a unique security token again.
    /// </summary>
    public void DetachGlobal(NativeContext env)
    {
        JSGlobalProxy globalProxy = env.GlobalProxyObject;
        if (globalProxy.IsDetached) return;
        // V8 also creates a global_proxy_for_api copy for API callbacks on the
        // detached context; there are no API callbacks on V8Sharp's globals.
        JSObject.ForceSetPrototype(this, globalProxy, null);
        // Detach the global object from the native context by making its map
        // contextless.
        globalProxy.Map.NativeContext = null;
        // Set security token back to default (unique) state making sure that only
        // accesses from the same native context are allowed.
        env.SecurityToken = env.GlobalObject;
    }
}
