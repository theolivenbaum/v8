// Port of the Proxy builtins: src/builtins/proxy-constructor.tq,
// proxy-revocable.tq, proxy-revoke.tq and the allocation helpers of
// builtins-proxy-gen.cc (AllocateProxy, AllocateProxyRevokeFunction,
// CreateProxyRevokeFunctionContext).
//
// The trap builtins (ProxyGetProperty, ProxyHasProperty, ProxySetProperty,
// ProxyDeleteProperty, ProxyGetPrototypeOf, ...; proxy-*.tq) and CallProxy /
// ConstructProxy are stubs that V8's ICs and other builtins call with their
// own calling conventions; in V8Sharp every caller reaches the same logic
// through JSProxy (Objects/JSProxy.cs), so they are not registered here.
namespace V8Sharp.Builtins;

public static partial class BuiltinRegistry
{
    static partial void RegisterProxy()
    {
        Register(Builtin.ProxyConstructor, BuiltinsProxy.ProxyConstructor);
        Register(Builtin.ProxyRevocable, BuiltinsProxy.ProxyRevocable);
        Register(Builtin.ProxyRevoke, BuiltinsProxy.ProxyRevoke);
    }
}

/// <summary>The Proxy builtins.</summary>
public static class BuiltinsProxy
{
    /// <summary>ProxiesCodeStubAssembler::ProxyRevokeFunctionContextSlot.</summary>
    const int kProxySlot = (int)Context.Field.MIN_CONTEXT_SLOTS;
    const int kProxyContextLength = kProxySlot + 1;

    /// <summary>ES #sec-proxy-constructor: Proxy ( target, handler ).</summary>
    public static JSValue ProxyConstructor(Isolate isolate, in BuiltinArguments args)
    {
        // 1. If NewTarget is undefined, throw a TypeError exception.
        if (args.NewTarget.IsUndefined)
        {
            return isolate.ThrowTypeError(MessageTemplate.ConstructorNotFunction, ReadOnlyRoots.Proxy_string);
        }
        // 2. Return ? ProxyCreate(target, handler).
        // https://tc39.es/ecma262/#sec-proxycreate
        // 1. If Type(target) is not Object, throw a TypeError exception.
        // 2. If Type(handler) is not Object, throw a TypeError exception.
        if (args.AtOrUndefined(1).HeapObjectOrNull is not JSReceiver target ||
            args.AtOrUndefined(2).HeapObjectOrNull is not JSReceiver handler)
        {
            return isolate.ThrowTypeError(MessageTemplate.ProxyNonObject);
        }
        // 5. Let P be a newly created object.
        // 6. Set P's essential internal methods (except for [[Call]] and
        //    [[Construct]]) to the definitions specified in 9.5.
        // 7. If IsCallable(target) is true, then
        //    a. Set P.[[Call]] as specified in 9.5.12.
        //    b. If IsConstructor(target) is true, then
        //       1. Set P.[[Construct]] as specified in 9.5.13.
        // 8. Set P.[[ProxyTarget]] to target.
        // 9. Set P.[[ProxyHandler]] to handler.
        // 10. Return P.
        return isolate.Factory.NewJSProxy(target, handler, revocable: false);
    }

    /// <summary>ES #sec-proxy.revocable: Proxy.revocable ( target, handler ).</summary>
    public static JSValue ProxyRevocable(Isolate isolate, in BuiltinArguments args)
    {
        // 1. Let p be ? ProxyCreate(target, handler).
        if (args.AtOrUndefined(1).HeapObjectOrNull is not JSReceiver target ||
            args.AtOrUndefined(2).HeapObjectOrNull is not JSReceiver handler)
        {
            return isolate.ThrowTypeError(MessageTemplate.ProxyNonObject,
                isolate.Factory.NewStringFromAsciiChecked("Proxy.revocable"));
        }
        JSProxy proxy = isolate.Factory.NewJSProxy(target, handler, revocable: true);
        // 2. Let steps be the algorithm steps defined in Proxy Revocation
        // Functions.
        // 3. Let revoker be CreateBuiltinFunction(steps, « [[RevocableProxy]] »).
        // 4. Set revoker.[[RevocableProxy]] to p.
        JSFunction revoke = AllocateProxyRevokeFunction(isolate, proxy);
        // 5. Let result be ObjectCreate(%ObjectPrototype%).
        // 6. Perform CreateDataProperty(result, "proxy", p).
        // 7. Perform CreateDataProperty(result, "revoke", revoker).
        // 8. Return result.
        return NewJSProxyRevocableResult(isolate, proxy, revoke);
    }

    /// <summary>NewJSProxyRevocableResult: {proxy, revoke} with the native context's result map.</summary>
    static JSObject NewJSProxyRevocableResult(Isolate isolate, JSProxy proxy, JSFunction revoke)
    {
        Map map = isolate.NativeContext.ProxyRevocableResultMap;
        JSObject result = isolate.Factory.NewJSObjectFromMap(map);
        JSValue[] fields = result.RawFields;
        fields[0] = proxy;
        fields[1] = revoke;
        return result;
    }

    /// <summary>ProxiesCodeStubAssembler::AllocateProxyRevokeFunction.</summary>
    static JSFunction AllocateProxyRevokeFunction(Isolate isolate, JSProxy proxy)
    {
        NativeContext nativeContext = isolate.NativeContext;
        // CreateProxyRevokeFunctionContext.
        Context proxyContext = isolate.Factory.NewBuiltinContext(nativeContext, kProxyContextLength);
        proxyContext[kProxySlot] = proxy;
        // AllocateRootFunctionWithContext(RootIndex::kProxyRevokeSharedFun, ...).
        SharedFunctionInfo shared = isolate.ProxyRevokeSharedFun ??= CreateProxyRevokeSharedFun(isolate);
        return isolate.Factory.NewFunction(shared, proxyContext, nativeContext.StrictFunctionWithoutPrototypeMap);
    }

    /// <summary>
    /// setup-heap-internal.cc CreateSharedFunctionInfo(isolate,
    /// Builtin::kProxyRevoke, 0): an anonymous strict builtin of length 0.
    /// </summary>
    static SharedFunctionInfo CreateProxyRevokeSharedFun(Isolate isolate)
    {
        SharedFunctionInfo shared = isolate.Factory.NewSharedFunctionInfoForBuiltin(ReadOnlyRoots.empty_string,
            Builtin.ProxyRevoke, 0, adapt: true);
        shared.LanguageMode = LanguageMode.Strict;
        shared.UpdateFunctionMapIndex();
        return shared;
    }

    /// <summary>Proxy Revocation Functions (proxy-revoke.tq).</summary>
    public static JSValue ProxyRevoke(Isolate isolate, in BuiltinArguments args)
    {
        Context context = args.Target.Context;
        // 1. Let p be F.[[RevocableProxy]].
        JSValue proxySlot = context[kProxySlot];
        // 2. If p is null, return undefined
        if (proxySlot.HeapObjectOrNull is not JSProxy proxy) return JSValue.Undefined;
        // 3. Set F.[[RevocableProxy]] to null.
        context[kProxySlot] = JSValue.Null;
        // 4. Assert: p is a Proxy object.
        // 5. Set p.[[ProxyTarget]] to null.
        // 6. Set p.[[ProxyHandler]] to null.
        JSProxy.Revoke(proxy);
        // 7. Return undefined.
        return JSValue.Undefined;
    }
}
