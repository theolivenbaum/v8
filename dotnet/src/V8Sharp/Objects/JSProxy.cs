// Port of src/objects/js-proxy.h (+ -inl.h) and the JSProxy operations of
// src/objects/objects.cc: every proxy trap with its invariant checks
// (ES #sec-proxy-object-internal-methods-and-internal-slots). [[Call]] and
// [[Construct]] port the CallProxy / ConstructProxy builtins of
// src/builtins/builtins-proxy-gen.cc; [[OwnPropertyKeys]] is in Keys.cs
// (KeyAccumulator::CollectOwnJSProxyKeys, as in V8).
using V8Sharp.Common;
using V8Sharp.Roots;

namespace V8Sharp.Objects;

/// <summary>
/// V8's JSProxy. Proxy maps are dictionary maps: private symbols set on a
/// proxy are stored in its property dictionary.
/// </summary>
public sealed class JSProxy : JSReceiver
{
    /// <summary>JSProxy::kMaxIterationLimit: how deep IsArray follows proxy targets.</summary>
    public const int kMaxIterationLimit = 100 * 1024;

    /// <summary>The [[ProxyTarget]] slot (null once revoked).</summary>
    public JSValue Target;

    /// <summary>The [[ProxyHandler]] slot (null once revoked).</summary>
    public JSValue Handler;

    /// <summary>JSProxy::IsRevocableBit: created by Proxy.revocable.</summary>
    public bool IsRevocable { get; init; }

    public JSProxy(Map map, JSReceiver target, JSReceiver handler) : base(map)
    {
        Target = target;
        Handler = handler;
    }

    /// <summary>JSProxy::IsRevoked.</summary>
    public bool IsRevoked => Handler.IsNull;

    /// <summary>JSProxy::Revoke (ES #sec-proxy-revocation-functions).</summary>
    public static void Revoke(JSProxy proxy)
    {
        // If this fails then some Proxy allocation code path that created
        // revocable Proxies didn't set the bit correctly.
        if (!proxy.IsRevocable) throw new InvalidOperationException("Revoke on a non-revocable proxy");
        if (!proxy.IsRevoked)
        {
            // 5. Set p.[[ProxyTarget]] to null.
            proxy.Target = JSValue.Null;
            // 6. Set p.[[ProxyHandler]] to null.
            proxy.Handler = JSValue.Null;
        }
    }

    /// <summary>JSProxy::New (ES6 section 9.5.15 ProxyCreate).</summary>
    public static JSProxy New(Isolate isolate, JSValue target, JSValue handler, bool revocable = false)
    {
        if (target.HeapObjectOrNull is not JSReceiver targetReceiver)
        {
            isolate.Throw(isolate.Factory.NewTypeError(MessageTemplate.ProxyNonObject));
            return null!;
        }
        if (handler.HeapObjectOrNull is not JSReceiver handlerReceiver)
        {
            isolate.Throw(isolate.Factory.NewTypeError(MessageTemplate.ProxyNonObject));
            return null!;
        }
        return isolate.Factory.NewJSProxy(targetReceiver, handlerReceiver, revocable);
    }

    static void ThrowRevoked(Isolate isolate, JSValue trapName) =>
        isolate.Throw(isolate.Factory.NewTypeError(MessageTemplate.ProxyRevoked, trapName));

    /// <summary>JSProxy::IsArray (ES #sec-isarray step 3).</summary>
    public static bool IsArray(Isolate isolate, JSProxy proxy)
    {
        JSReceiver obj = proxy;
        for (int i = 0; i < kMaxIterationLimit; i++)
        {
            proxy = (JSProxy)obj;
            if (proxy.IsRevoked)
            {
                ThrowRevoked(isolate, isolate.Factory.NewStringFromAsciiChecked("IsArray"));
            }
            obj = proxy.Target.As<JSReceiver>();
            if (obj is JSArray) return true;
            if (obj is not JSProxy) return false;
        }

        // Too deep recursion, throw a RangeError.
        isolate.StackOverflow();
        return false;
    }

    /// <summary>JSProxy::GetPrototype (ES6 9.5.1 [[GetPrototypeOf]]).</summary>
    public static JSReceiver? GetPrototype(Isolate isolate, JSProxy proxy)
    {
        JSString trapName = ReadOnlyRoots.getPrototypeOf_string;

        isolate.StackGuard.StackCheck(isolate);

        // 1. Let handler be the value of the [[ProxyHandler]] internal slot.
        // 2. If handler is null, throw a TypeError exception.
        // 3. Assert: Type(handler) is Object.
        // 4. Let target be the value of the [[ProxyTarget]] internal slot.
        if (proxy.IsRevoked) ThrowRevoked(isolate, trapName);
        var target = proxy.Target.As<JSReceiver>();
        var handler = proxy.Handler.As<JSReceiver>();

        // 5. Let trap be ? GetMethod(handler, "getPrototypeOf").
        JSValue trap = ObjectOps.GetMethod(isolate, handler, trapName);
        // 6. If trap is undefined, then return target.[[GetPrototypeOf]]().
        if (trap.IsUndefined) return JSReceiver.GetPrototype(isolate, target);
        // 7. Let handlerProto be ? Call(trap, handler, «target»).
        JSValue handlerProtoResult = Execution.Call(isolate, trap, handler, [target]);
        // 8. If Type(handlerProto) is neither Object nor Null, throw a TypeError.
        if (!handlerProtoResult.IsJSReceiver && !handlerProtoResult.IsNull)
        {
            isolate.Throw(isolate.Factory.NewTypeError(MessageTemplate.ProxyGetPrototypeOfInvalid));
        }
        JSReceiver? handlerProto = handlerProtoResult.AsOrNull<JSReceiver>();
        // 9. Let extensibleTarget be ? IsExtensible(target).
        bool isExtensible = JSReceiver.IsExtensible(isolate, target);
        // 10. If extensibleTarget is true, return handlerProto.
        if (isExtensible) return handlerProto;
        // 11. Let targetProto be ? target.[[GetPrototypeOf]]().
        JSReceiver? targetProto = JSReceiver.GetPrototype(isolate, target);
        // 12. If SameValue(handlerProto, targetProto) is false, throw a TypeError.
        if (!ReferenceEquals(handlerProto, targetProto))
        {
            isolate.Throw(isolate.Factory.NewTypeError(MessageTemplate.ProxyGetPrototypeOfNonExtensible));
        }
        // 13. Return handlerProto.
        return handlerProto;
    }

    /// <summary>JSProxy::SetPrototype (ES6 9.5.2 [[SetPrototypeOf]]).</summary>
    public static bool SetPrototype(Isolate isolate, JSProxy proxy, JSReceiver? value, bool fromJavaScript, ShouldThrow shouldThrow)
    {
        isolate.StackGuard.StackCheck(isolate);
        JSString trapName = ReadOnlyRoots.setPrototypeOf_string;
        // 1. Assert: Either Type(V) is Object or Type(V) is Null.
        // 2. Let handler be the value of the [[ProxyHandler]] internal slot of O.
        // 3. If handler is null, throw a TypeError exception.
        // 4. Assert: Type(handler) is Object.
        if (proxy.IsRevoked) ThrowRevoked(isolate, trapName);
        // 5. Let target be the value of the [[ProxyTarget]] internal slot.
        var target = proxy.Target.As<JSReceiver>();
        // 6. Let trap be ? GetMethod(handler, "getPrototypeOf").
        JSValue trap = ObjectOps.GetMethod(isolate, proxy.Handler.As<JSReceiver>(), trapName);
        // 7. If trap is undefined, then return target.[[SetPrototypeOf]]().
        JSValue valueArg = value is null ? JSValue.Null : value;
        if (trap.IsUndefined) return JSReceiver.SetPrototype(isolate, target, valueArg, fromJavaScript, shouldThrow);
        // 8. Let booleanTrapResult be ToBoolean(? Call(trap, handler, «target, V»)).
        JSValue trapResult = Execution.Call(isolate, trap, proxy.Handler, [target, valueArg]);
        bool boolTrapResult = ObjectOps.BooleanValue(trapResult);
        // 9. If booleanTrapResult is false, return false.
        if (!boolTrapResult)
        {
            return ObjectOps.ReturnFailure(isolate, shouldThrow, MessageTemplate.ProxyTrapReturnedFalsish, trapName);
        }
        // 10. Let extensibleTarget be ? IsExtensible(target).
        bool isExtensible = JSReceiver.IsExtensible(isolate, target);
        // 11. If extensibleTarget is true, return true.
        if (isExtensible) return true;
        // 12. Let targetProto be ? target.[[GetPrototypeOf]]().
        JSReceiver? targetProto = JSReceiver.GetPrototype(isolate, target);
        // 13. If SameValue(V, targetProto) is false, throw a TypeError exception.
        if (!ReferenceEquals(value, targetProto))
        {
            isolate.Throw(isolate.Factory.NewTypeError(MessageTemplate.ProxySetPrototypeOfNonExtensible));
        }
        // 14. Return true.
        return true;
    }

    /// <summary>JSProxy::IsExtensible (ES6 9.5.3 [[IsExtensible]]).</summary>
    public static bool IsExtensible(Isolate isolate, JSProxy proxy)
    {
        isolate.StackGuard.StackCheck(isolate);
        JSString trapName = ReadOnlyRoots.isExtensible_string;

        if (proxy.IsRevoked) ThrowRevoked(isolate, trapName);
        var target = proxy.Target.As<JSReceiver>();
        var handler = proxy.Handler.As<JSReceiver>();

        JSValue trap = ObjectOps.GetMethod(isolate, handler, trapName);
        if (trap.IsUndefined) return JSReceiver.IsExtensible(isolate, target);

        JSValue trapResult = Execution.Call(isolate, trap, handler, [target]);

        // Enforce the invariant.
        bool targetResult = JSReceiver.IsExtensible(isolate, target);
        if (targetResult != ObjectOps.BooleanValue(trapResult))
        {
            isolate.Throw(isolate.Factory.NewTypeError(MessageTemplate.ProxyIsExtensibleInconsistent,
                JSValue.FromBoolean(targetResult)));
        }
        return targetResult;
    }

    /// <summary>JSProxy::PreventExtensions (ES6 9.5.4 [[PreventExtensions]]).</summary>
    public static bool PreventExtensions(Isolate isolate, JSProxy proxy, ShouldThrow shouldThrow)
    {
        isolate.StackGuard.StackCheck(isolate);
        JSString trapName = ReadOnlyRoots.preventExtensions_string;

        if (proxy.IsRevoked) ThrowRevoked(isolate, trapName);
        var target = proxy.Target.As<JSReceiver>();
        var handler = proxy.Handler.As<JSReceiver>();

        JSValue trap = ObjectOps.GetMethod(isolate, handler, trapName);
        if (trap.IsUndefined) return JSReceiver.PreventExtensions(isolate, target, shouldThrow);

        JSValue trapResult = Execution.Call(isolate, trap, handler, [target]);
        if (!ObjectOps.BooleanValue(trapResult))
        {
            return ObjectOps.ReturnFailure(isolate, shouldThrow, MessageTemplate.ProxyTrapReturnedFalsish, trapName);
        }

        // Enforce the invariant.
        bool targetResult = JSReceiver.IsExtensible(isolate, target);
        if (targetResult)
        {
            isolate.Throw(isolate.Factory.NewTypeError(MessageTemplate.ProxyPreventExtensionsExtensible));
        }
        return true;
    }

    /// <summary>JSProxy::GetOwnPropertyDescriptor (ES6 9.5.5 [[GetOwnProperty]]).</summary>
    public static bool GetOwnPropertyDescriptor(Isolate isolate, JSProxy proxy, Name name, ref PropertyDescriptor desc)
    {
        Debug.Assert(!name.IsAnyPrivate);
        isolate.StackGuard.StackCheck(isolate);

        JSString trapName = ReadOnlyRoots.getOwnPropertyDescriptor_string;
        // 1. (Assert)
        // 2. Let handler be the value of the [[ProxyHandler]] internal slot of O.
        // 3. If handler is null, throw a TypeError exception.
        // 4. Assert: Type(handler) is Object.
        if (proxy.IsRevoked) ThrowRevoked(isolate, trapName);
        // 5. Let target be the value of the [[ProxyTarget]] internal slot of O.
        var target = proxy.Target.As<JSReceiver>();
        // 6. Let trap be ? GetMethod(handler, "getOwnPropertyDescriptor").
        JSValue trap = ObjectOps.GetMethod(isolate, proxy.Handler.As<JSReceiver>(), trapName);
        // 7. If trap is undefined, then
        if (trap.IsUndefined)
        {
            // 7a. Return target.[[GetOwnProperty]](P).
            return JSReceiver.GetOwnPropertyDescriptor(isolate, target, name, ref desc);
        }
        // 8. Let trapResultObj be ? Call(trap, handler, «target, P»).
        JSValue trapResultObj = Execution.Call(isolate, trap, proxy.Handler, [target, name]);
        // 9. If Type(trapResultObj) is neither Object nor Undefined, throw a
        //    TypeError exception.
        if (!trapResultObj.IsJSReceiver && !trapResultObj.IsUndefined)
        {
            isolate.Throw(isolate.Factory.NewTypeError(MessageTemplate.ProxyGetOwnPropertyDescriptorInvalid, name));
        }
        // 10. Let targetDesc be ? target.[[GetOwnProperty]](P).
        var targetDesc = new PropertyDescriptor();
        bool found = JSReceiver.GetOwnPropertyDescriptor(isolate, target, name, ref targetDesc);
        // 11. If trapResultObj is undefined, then
        if (trapResultObj.IsUndefined)
        {
            // 11a. If targetDesc is undefined, return undefined.
            if (!found) return false;
            // 11b. If targetDesc.[[Configurable]] is false, throw a TypeError
            //      exception.
            if (!targetDesc.Configurable)
            {
                isolate.Throw(isolate.Factory.NewTypeError(MessageTemplate.ProxyGetOwnPropertyDescriptorUndefined, name));
            }
            // 11c. Let extensibleTarget be ? IsExtensible(target).
            bool extensibleTarget0 = JSReceiver.IsExtensible(isolate, target);
            // 11d. (Assert)
            // 11e. If extensibleTarget is false, throw a TypeError exception.
            if (!extensibleTarget0)
            {
                isolate.Throw(isolate.Factory.NewTypeError(MessageTemplate.ProxyGetOwnPropertyDescriptorNonExtensible, name));
            }
            // 11f. Return undefined.
            return false;
        }
        // 12. Let extensibleTarget be ? IsExtensible(target).
        bool extensibleTarget = JSReceiver.IsExtensible(isolate, target);
        // 13. Let resultDesc be ? ToPropertyDescriptor(trapResultObj).
        PropertyDescriptor.ToPropertyDescriptor(isolate, trapResultObj, ref desc);
        // 14. Call CompletePropertyDescriptor(resultDesc).
        PropertyDescriptor.CompletePropertyDescriptor(isolate, ref desc);
        // 15. Let valid be IsCompatiblePropertyDescriptor (extensibleTarget,
        //     resultDesc, targetDesc).
        bool valid = JSReceiver.IsCompatiblePropertyDescriptor(isolate, extensibleTarget, ref desc, ref targetDesc, name,
            ShouldThrow.DontThrow);
        // 16. If valid is false, throw a TypeError exception.
        if (!valid)
        {
            isolate.Throw(isolate.Factory.NewTypeError(MessageTemplate.ProxyGetOwnPropertyDescriptorIncompatible, name));
        }
        // 17. If resultDesc.[[Configurable]] is false, then
        if (!desc.Configurable)
        {
            // 17a. If targetDesc is undefined or targetDesc.[[Configurable]] is true:
            if (targetDesc.IsEmpty || targetDesc.Configurable)
            {
                // 17a i. Throw a TypeError exception.
                isolate.Throw(isolate.Factory.NewTypeError(MessageTemplate.ProxyGetOwnPropertyDescriptorNonConfigurable,
                    name));
            }
            // 17b. If resultDesc has a [[Writable]] field and resultDesc.[[Writable]]
            // is false, then
            if (desc.HasWritable && !desc.Writable)
            {
                // 17b i. If targetDesc.[[Writable]] is true, throw a TypeError exception.
                if (targetDesc.Writable)
                {
                    isolate.Throw(isolate.Factory.NewTypeError(
                        MessageTemplate.ProxyGetOwnPropertyDescriptorNonConfigurableWritable, name));
                }
            }
        }
        // 18. Return resultDesc.
        return true;
    }

    /// <summary>JSProxy::DefineOwnProperty (ES6 9.5.6 [[DefineOwnProperty]]).</summary>
    public static bool DefineOwnProperty(Isolate isolate, JSProxy proxy, JSValue key, ref PropertyDescriptor desc,
        ShouldThrow? shouldThrow)
    {
        isolate.StackGuard.StackCheck(isolate);
        if (key.HeapObjectOrNull is Symbol symbol && symbol.IsPrivateInternal)
        {
            return SetPrivateSymbol(isolate, proxy, symbol, ref desc, shouldThrow);
        }
        JSString trapName = ReadOnlyRoots.defineProperty_string;
        // 1. Assert: IsPropertyKey(P) is true.
        Debug.Assert(key.IsName || key.IsNumber);
        // 2. Let handler be the value of the [[ProxyHandler]] internal slot of O.
        // 3. If handler is null, throw a TypeError exception.
        // 4. Assert: Type(handler) is Object.
        if (proxy.IsRevoked) ThrowRevoked(isolate, trapName);
        // 5. Let target be the value of the [[ProxyTarget]] internal slot of O.
        var target = proxy.Target.As<JSReceiver>();
        // 6. Let trap be ? GetMethod(handler, "defineProperty").
        JSValue trap = ObjectOps.GetMethod(isolate, proxy.Handler.As<JSReceiver>(), trapName);
        // 7. If trap is undefined, then:
        if (trap.IsUndefined)
        {
            // 7a. Return target.[[DefineOwnProperty]](P, Desc).
            return JSReceiver.DefineOwnProperty(isolate, target, key, ref desc, shouldThrow);
        }
        // 8. Let descObj be FromPropertyDescriptor(Desc).
        JSValue descObj = desc.ToObject(isolate);
        // 9. Let booleanTrapResult be
        //    ToBoolean(? Call(trap, handler, «target, P, descObj»)).
        Name propertyName = key.HeapObjectOrNull as Name ?? isolate.Factory.NumberToString(key);
        // Do not leak private property names.
        Debug.Assert(!propertyName.IsAnyPrivate);
        JSValue trapResultObj = Execution.Call(isolate, trap, proxy.Handler, [target, propertyName, descObj]);
        // 10. If booleanTrapResult is false, return false.
        if (!ObjectOps.BooleanValue(trapResultObj))
        {
            return ObjectOps.ReturnFailure(isolate, ObjectOps.GetShouldThrow(isolate, shouldThrow),
                MessageTemplate.ProxyTrapReturnedFalsishFor, trapName, propertyName);
        }
        // 11. Let targetDesc be ? target.[[GetOwnProperty]](P).
        var targetDesc = new PropertyDescriptor();
        bool targetFound = JSReceiver.GetOwnPropertyDescriptor(isolate, target, key, ref targetDesc);
        // 12. Let extensibleTarget be ? IsExtensible(target).
        bool extensibleTarget = JSReceiver.IsExtensible(isolate, target);
        // 13. If Desc has a [[Configurable]] field and if Desc.[[Configurable]]
        //     is false, then:
        // 13a. Let settingConfigFalse be true.
        // 14. Else let settingConfigFalse be false.
        bool settingConfigFalse = desc.HasConfigurable && !desc.Configurable;
        // 15. If targetDesc is undefined, then
        if (!targetFound)
        {
            // 15a. If extensibleTarget is false, throw a TypeError exception.
            if (!extensibleTarget)
            {
                isolate.Throw(isolate.Factory.NewTypeError(MessageTemplate.ProxyDefinePropertyNonExtensible, propertyName));
            }
            // 15b. If settingConfigFalse is true, throw a TypeError exception.
            if (settingConfigFalse)
            {
                isolate.Throw(isolate.Factory.NewTypeError(MessageTemplate.ProxyDefinePropertyNonConfigurable, propertyName));
            }
        }
        else
        {
            // 16. Else targetDesc is not undefined,
            // 16a. If IsCompatiblePropertyDescriptor(extensibleTarget, Desc,
            //      targetDesc) is false, throw a TypeError exception.
            bool valid = JSReceiver.IsCompatiblePropertyDescriptor(isolate, extensibleTarget, ref desc, ref targetDesc,
                propertyName, ShouldThrow.DontThrow);
            if (!valid)
            {
                isolate.Throw(isolate.Factory.NewTypeError(MessageTemplate.ProxyDefinePropertyIncompatible, propertyName));
            }
            // 16b. If settingConfigFalse is true and targetDesc.[[Configurable]] is
            //      true, throw a TypeError exception.
            if (settingConfigFalse && targetDesc.Configurable)
            {
                isolate.Throw(isolate.Factory.NewTypeError(MessageTemplate.ProxyDefinePropertyNonConfigurable, propertyName));
            }
            // 16c. If IsDataDescriptor(targetDesc) is true,
            // targetDesc.[[Configurable]] is
            //       false, and targetDesc.[[Writable]] is true, then
            if (PropertyDescriptor.IsDataDescriptor(in targetDesc) && !targetDesc.Configurable && targetDesc.Writable)
            {
                // 16c i. If Desc has a [[Writable]] field and Desc.[[Writable]] is false,
                // throw a TypeError exception.
                if (desc.HasWritable && !desc.Writable)
                {
                    isolate.Throw(isolate.Factory.NewTypeError(MessageTemplate.ProxyDefinePropertyNonConfigurableWritable,
                        propertyName));
                }
            }
        }
        // 17. Return true.
        return true;
    }

    /// <summary>JSProxy::SetPrivateSymbol: private data properties stored on the proxy itself.</summary>
    public static bool SetPrivateSymbol(Isolate isolate, JSProxy proxy, Symbol privateName, ref PropertyDescriptor desc,
        ShouldThrow? shouldThrow)
    {
        // Despite the generic name, this can only add private data properties.
        if (!PropertyDescriptor.IsDataDescriptor(in desc) || desc.ToAttributes() != PropertyAttributes.DONT_ENUM)
        {
            return ObjectOps.ReturnFailure(isolate, ObjectOps.GetShouldThrow(isolate, shouldThrow), MessageTemplate.ProxyPrivate);
        }
        Debug.Assert(proxy.Map.IsDictionaryMap);
        JSValue value = desc.HasValue ? desc.Value : JSValue.Undefined;

        var it = new LookupIterator(isolate, proxy, privateName, proxy);

        if (it.IsFound)
        {
            // We are not tracking constness for private symbols added to JSProxy
            // objects.
            it.WriteDataValue(value, false);
            return true;
        }

        var details = new PropertyDetails(PropertyKind.Data, PropertyAttributes.DONT_ENUM, PropertyConstness.Mutable);
        NameDictionary dict = proxy.PropertyDictionary;
        NameDictionary result = NameDictionary.Add(isolate, dict, privateName, value, details);
        if (!ReferenceEquals(dict, result)) proxy.SetProperties(result);
        return true;
    }

    /// <summary>JSProxy::HasProperty (ES6 9.5.7 [[HasProperty]]).</summary>
    public static bool HasProperty(Isolate isolate, JSProxy proxy, Name name)
    {
        Debug.Assert(!name.IsAnyPrivate);
        isolate.StackGuard.StackCheck(isolate);
        // 1. (Assert)
        // 2. Let handler be the value of the [[ProxyHandler]] internal slot of O.
        // 3. If handler is null, throw a TypeError exception.
        // 4. Assert: Type(handler) is Object.
        if (proxy.IsRevoked) ThrowRevoked(isolate, ReadOnlyRoots.has_string);
        // 5. Let target be the value of the [[ProxyTarget]] internal slot of O.
        var target = proxy.Target.As<JSReceiver>();
        // 6. Let trap be ? GetMethod(handler, "has").
        JSValue trap = ObjectOps.GetMethod(isolate, proxy.Handler.As<JSReceiver>(), ReadOnlyRoots.has_string);
        // 7. If trap is undefined, then
        if (trap.IsUndefined)
        {
            // 7a. Return target.[[HasProperty]](P).
            return JSReceiver.HasProperty(isolate, target, name);
        }
        // 8. Let booleanTrapResult be ToBoolean(? Call(trap, handler, «target, P»)).
        JSValue trapResultObj = Execution.Call(isolate, trap, proxy.Handler, [target, name]);
        bool booleanTrapResult = ObjectOps.BooleanValue(trapResultObj);
        // 9. If booleanTrapResult is false, then:
        if (!booleanTrapResult) CheckHasTrap(isolate, name, target);
        // 10. Return booleanTrapResult.
        return booleanTrapResult;
    }

    /// <summary>JSProxy::CheckHasTrap: the invariants of a falsish "has" result.</summary>
    public static bool CheckHasTrap(Isolate isolate, Name name, JSReceiver target)
    {
        // 9a. Let targetDesc be ? target.[[GetOwnProperty]](P).
        var targetDesc = new PropertyDescriptor();
        bool targetFound = JSReceiver.GetOwnPropertyDescriptor(isolate, target, name, ref targetDesc);
        // 9b. If targetDesc is not undefined, then:
        if (targetFound)
        {
            // 9b i. If targetDesc.[[Configurable]] is false, throw a TypeError
            //       exception.
            if (!targetDesc.Configurable)
            {
                isolate.Throw(isolate.Factory.NewTypeError(MessageTemplate.ProxyHasNonConfigurable, name));
            }
            // 9b ii. Let extensibleTarget be ? IsExtensible(target).
            bool extensibleTarget = JSReceiver.IsExtensible(isolate, target);
            // 9b iii. If extensibleTarget is false, throw a TypeError exception.
            if (!extensibleTarget)
            {
                isolate.Throw(isolate.Factory.NewTypeError(MessageTemplate.ProxyHasNonExtensible, name));
            }
        }
        return true;
    }

    /// <summary>JSProxy::GetProperty (ES6 9.5.8 [[Get]]).</summary>
    public static JSValue GetProperty(Isolate isolate, JSProxy proxy, Name name, JSValue receiver, out bool wasFound)
    {
        wasFound = true;

        Debug.Assert(!name.IsAnyPrivate);
        isolate.StackGuard.StackCheck(isolate);
        JSString trapName = ReadOnlyRoots.get_string;
        // 1. Assert: IsPropertyKey(P) is true.
        // 2. Let handler be the value of the [[ProxyHandler]] internal slot of O.
        // 3. If handler is null, throw a TypeError exception.
        // 4. Assert: Type(handler) is Object.
        if (proxy.IsRevoked) ThrowRevoked(isolate, trapName);
        // 5. Let target be the value of the [[ProxyTarget]] internal slot of O.
        var target = proxy.Target.As<JSReceiver>();
        // 6. Let trap be ? GetMethod(handler, "get").
        JSValue trap = ObjectOps.GetMethod(isolate, proxy.Handler.As<JSReceiver>(), trapName);
        // 7. If trap is undefined, then
        if (trap.IsUndefined)
        {
            // 7.a Return target.[[Get]](P, Receiver).
            var key = new PropertyKey(isolate, name);
            var it = new LookupIterator(isolate, receiver, key, target);
            JSValue result = ObjectOps.GetProperty(ref it);
            wasFound = it.IsFound;
            return result;
        }
        // 8. Let trapResult be ? Call(trap, handler, «target, P, Receiver»).
        JSValue trapResult = Execution.Call(isolate, trap, proxy.Handler, [target, name, receiver]);

        CheckGetSetTrapResult(isolate, name, target, trapResult, AccessKind.Get);

        // 11. Return trap_result
        return trapResult;
    }

    /// <summary>V8's AccessKind.</summary>
    public enum AccessKind { Get, Set }

    /// <summary>JSProxy::CheckGetSetTrapResult: the invariants of the "get" and "set" traps.</summary>
    public static void CheckGetSetTrapResult(Isolate isolate, Name name, JSReceiver target, JSValue trapResult,
        AccessKind accessKind)
    {
        // 9. Let targetDesc be ? target.[[GetOwnProperty]](P).
        var targetDesc = new PropertyDescriptor();
        bool targetFound = JSReceiver.GetOwnPropertyDescriptor(isolate, target, name, ref targetDesc);
        // 10. If targetDesc is not undefined, then
        if (targetFound)
        {
            // 10.a. If IsDataDescriptor(targetDesc) and targetDesc.[[Configurable]] is
            //       false and targetDesc.[[Writable]] is false, then
            // 10.a.i. If SameValue(trapResult, targetDesc.[[Value]]) is false,
            //        throw a TypeError exception.
            bool inconsistent = PropertyDescriptor.IsDataDescriptor(in targetDesc) && !targetDesc.Configurable &&
                                !targetDesc.Writable && !ObjectOps.SameValue(trapResult, targetDesc.Value);
            if (inconsistent)
            {
                if (accessKind == AccessKind.Get)
                {
                    isolate.Throw(isolate.Factory.NewTypeError(MessageTemplate.ProxyGetNonConfigurableData, name,
                        targetDesc.Value, trapResult));
                }
                isolate.Throw(isolate.Factory.NewTypeError(MessageTemplate.ProxySetFrozenData, name));
            }
            // 10.b. If IsAccessorDescriptor(targetDesc) and targetDesc.[[Configurable]]
            //       is false and targetDesc.[[Get]] is undefined, then
            // 10.b.i. If trapResult is not undefined, throw a TypeError exception.
            if (accessKind == AccessKind.Get)
            {
                inconsistent = PropertyDescriptor.IsAccessorDescriptor(in targetDesc) && !targetDesc.Configurable &&
                               targetDesc.Get.IsUndefined && !trapResult.IsUndefined;
            }
            else
            {
                inconsistent = PropertyDescriptor.IsAccessorDescriptor(in targetDesc) && !targetDesc.Configurable &&
                               targetDesc.Set.IsUndefined;
            }
            if (inconsistent)
            {
                if (accessKind == AccessKind.Get)
                {
                    isolate.Throw(isolate.Factory.NewTypeError(MessageTemplate.ProxyGetNonConfigurableAccessor, name,
                        trapResult));
                }
                isolate.Throw(isolate.Factory.NewTypeError(MessageTemplate.ProxySetFrozenAccessor, name));
            }
        }
    }

    /// <summary>JSProxy::SetProperty (ES6 9.5.9 [[Set]]).</summary>
    public static bool SetProperty(Isolate isolate, JSProxy proxy, Name name, JSValue value, JSValue receiver,
        ShouldThrow? shouldThrow)
    {
        Debug.Assert(!name.IsAnyPrivate);
        isolate.StackGuard.StackCheck(isolate);
        JSString trapName = ReadOnlyRoots.set_string;

        if (proxy.IsRevoked) ThrowRevoked(isolate, trapName);
        var target = proxy.Target.As<JSReceiver>();
        var handler = proxy.Handler.As<JSReceiver>();

        JSValue trap = ObjectOps.GetMethod(isolate, handler, trapName);
        if (trap.IsUndefined)
        {
            var key = new PropertyKey(isolate, name);
            var it = new LookupIterator(isolate, receiver, key, target);
            return ObjectOps.SetSuperProperty(ref it, value, StoreOrigin.MaybeKeyed, shouldThrow);
        }

        JSValue trapResult = Execution.Call(isolate, trap, handler, [target, name, value, receiver]);
        if (!ObjectOps.BooleanValue(trapResult))
        {
            return ObjectOps.ReturnFailure(isolate, ObjectOps.GetShouldThrow(isolate, shouldThrow),
                MessageTemplate.ProxyTrapReturnedFalsishFor, trapName, name);
        }

        CheckGetSetTrapResult(isolate, name, target, value, AccessKind.Set);
        return true;
    }

    /// <summary>JSProxy::DeletePropertyOrElement (ES6 9.5.10 [[Delete]]).</summary>
    public static bool DeletePropertyOrElement(Isolate isolate, JSProxy proxy, Name name, LanguageMode languageMode)
    {
        Debug.Assert(!name.IsAnyPrivate);
        ShouldThrow shouldThrow = languageMode == LanguageMode.Sloppy ? ShouldThrow.DontThrow : ShouldThrow.ThrowOnError;
        isolate.StackGuard.StackCheck(isolate);
        JSString trapName = ReadOnlyRoots.deleteProperty_string;

        if (proxy.IsRevoked) ThrowRevoked(isolate, trapName);
        var target = proxy.Target.As<JSReceiver>();
        var handler = proxy.Handler.As<JSReceiver>();

        JSValue trap = ObjectOps.GetMethod(isolate, handler, trapName);
        if (trap.IsUndefined) return JSReceiver.DeletePropertyOrElement(isolate, target, name, languageMode);

        JSValue trapResult = Execution.Call(isolate, trap, handler, [target, name]);
        if (!ObjectOps.BooleanValue(trapResult))
        {
            return ObjectOps.ReturnFailure(isolate, shouldThrow, MessageTemplate.ProxyTrapReturnedFalsishFor, trapName, name);
        }

        // Enforce the invariant.
        return CheckDeleteTrap(isolate, name, target);
    }

    /// <summary>JSProxy::CheckDeleteTrap.</summary>
    public static bool CheckDeleteTrap(Isolate isolate, Name name, JSReceiver target)
    {
        // 10. Let targetDesc be ? target.[[GetOwnProperty]](P).
        var targetDesc = new PropertyDescriptor();
        bool targetFound = JSReceiver.GetOwnPropertyDescriptor(isolate, target, name, ref targetDesc);
        // 11. If targetDesc is undefined, return true.
        if (targetFound)
        {
            // 12. If targetDesc.[[Configurable]] is false, throw a TypeError exception.
            if (!targetDesc.Configurable)
            {
                isolate.Throw(isolate.Factory.NewTypeError(MessageTemplate.ProxyDeletePropertyNonConfigurable, name));
            }
            // 13. Let extensibleTarget be ? IsExtensible(target).
            bool extensibleTarget = JSReceiver.IsExtensible(isolate, target);
            // 14. If extensibleTarget is false, throw a TypeError exception.
            if (!extensibleTarget)
            {
                isolate.Throw(isolate.Factory.NewTypeError(MessageTemplate.ProxyDeletePropertyNonExtensible, name));
            }
        }
        return true;
    }

    /// <summary>JSProxy::GetPropertyAttributes.</summary>
    public static new PropertyAttributes GetPropertyAttributes(ref LookupIterator it)
    {
        var desc = new PropertyDescriptor();
        bool found = GetOwnPropertyDescriptor(it.Isolate, it.GetHolder<JSProxy>(), it.GetName(), ref desc);
        if (!found) return PropertyAttributes.ABSENT;
        return desc.ToAttributes();
    }

    /// <summary>The CallProxy builtin (ES6 9.5.12 [[Call]]).</summary>
    public static JSValue Call(Isolate isolate, JSProxy proxy, JSValue receiver, ReadOnlySpan<JSValue> args)
    {
        isolate.StackGuard.StackCheck(isolate);

        // 1. Let handler be the value of the [[ProxyHandler]] internal slot of O.
        // 2. If handler is null, throw a TypeError exception.
        if (proxy.IsRevoked) ThrowRevoked(isolate, isolate.Factory.NewStringFromAsciiChecked("apply"));

        // 3. Assert: Type(handler) is Object.
        // 4. Let target be the value of the [[ProxyTarget]] internal slot of O.
        JSValue target = proxy.Target;

        // 5. Let trap be ? GetMethod(handler, "apply").
        JSValue trap = ObjectOps.GetMethod(isolate, proxy.Handler.As<JSReceiver>(), ReadOnlyRoots.apply_string);
        // 6. If trap is undefined, then
        if (trap.IsUndefined)
        {
            // 6.a. Return Call(target, thisArgument, argumentsList).
            return Execution.Call(isolate, target, receiver, args);
        }

        // 7. Let argArray be CreateArrayFromList(argumentsList).
        JSArray array = isolate.Factory.NewJSArrayWithElements(isolate.Factory.NewFixedArrayFrom(args));

        // 8. Return Call(trap, handler, «target, thisArgument, argArray»).
        return Execution.Call(isolate, trap, proxy.Handler, [target, receiver, array]);
    }

    /// <summary>The ConstructProxy builtin (ES6 9.5.13 [[Construct]]).</summary>
    public static JSValue Construct(Isolate isolate, JSProxy proxy, ReadOnlySpan<JSValue> args, JSValue newTarget)
    {
        isolate.StackGuard.StackCheck(isolate);

        // 1. Let handler be the value of the [[ProxyHandler]] internal slot of O.
        // 2. If handler is null, throw a TypeError exception.
        if (proxy.IsRevoked) ThrowRevoked(isolate, isolate.Factory.NewStringFromAsciiChecked("construct"));

        // 3. Assert: Type(handler) is Object.
        // 4. Let target be the value of the [[ProxyTarget]] internal slot of O.
        JSValue target = proxy.Target;

        // 5. Let trap be ? GetMethod(handler, "construct").
        JSValue trap = ObjectOps.GetMethod(isolate, proxy.Handler.As<JSReceiver>(), ReadOnlyRoots.construct_string);
        // 6. If trap is undefined, then
        if (trap.IsUndefined)
        {
            // 6.a. Assert: target has a [[Construct]] internal method.
            // 6.b. Return ? Construct(target, argumentsList, newTarget).
            return Execution.New(isolate, target, newTarget, args);
        }

        // 7. Let argArray be CreateArrayFromList(argumentsList).
        JSArray array = isolate.Factory.NewJSArrayWithElements(isolate.Factory.NewFixedArrayFrom(args));

        // 8. Let newObj be ? Call(trap, handler, « target, argArray, newTarget »).
        JSValue newObj = Execution.Call(isolate, trap, proxy.Handler, [target, array, newTarget]);

        // 9. If Type(newObj) is not Object, throw a TypeError exception.
        if (!newObj.IsJSReceiver)
        {
            isolate.Throw(isolate.Factory.NewTypeError(MessageTemplate.ProxyConstructNonObject, newObj));
        }

        // 10. Return newObj.
        return newObj;
    }
}
