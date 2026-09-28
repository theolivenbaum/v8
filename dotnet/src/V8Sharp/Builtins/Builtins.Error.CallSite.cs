// Port of src/builtins/builtins-callsite.cc: the methods of the CallSite
// objects that Error.prepareStackTrace receives, and CallSiteInfo::GetScriptHash
// (src/objects/call-site-info.cc) with Script::GetScriptHash (script.cc).
using System.Security.Cryptography;
using System.Text;

namespace V8Sharp.Builtins;

public static partial class BuiltinRegistry
{
    static void RegisterCallSite()
    {
        Register(Builtin.CallSitePrototypeGetColumnNumber, BuiltinsCallSite.CallSitePrototypeGetColumnNumber);
        Register(Builtin.CallSitePrototypeGetEnclosingColumnNumber, BuiltinsCallSite.CallSitePrototypeGetEnclosingColumnNumber);
        Register(Builtin.CallSitePrototypeGetEnclosingLineNumber, BuiltinsCallSite.CallSitePrototypeGetEnclosingLineNumber);
        Register(Builtin.CallSitePrototypeGetEvalOrigin, BuiltinsCallSite.CallSitePrototypeGetEvalOrigin);
        Register(Builtin.CallSitePrototypeGetFileName, BuiltinsCallSite.CallSitePrototypeGetFileName);
        Register(Builtin.CallSitePrototypeGetFunction, BuiltinsCallSite.CallSitePrototypeGetFunction);
        Register(Builtin.CallSitePrototypeGetFunctionName, BuiltinsCallSite.CallSitePrototypeGetFunctionName);
        Register(Builtin.CallSitePrototypeGetLineNumber, BuiltinsCallSite.CallSitePrototypeGetLineNumber);
        Register(Builtin.CallSitePrototypeGetMethodName, BuiltinsCallSite.CallSitePrototypeGetMethodName);
        Register(Builtin.CallSitePrototypeGetPosition, BuiltinsCallSite.CallSitePrototypeGetPosition);
        Register(Builtin.CallSitePrototypeGetPromiseIndex, BuiltinsCallSite.CallSitePrototypeGetPromiseIndex);
        Register(Builtin.CallSitePrototypeGetScriptHash, BuiltinsCallSite.CallSitePrototypeGetScriptHash);
        Register(Builtin.CallSitePrototypeGetScriptNameOrSourceURL, BuiltinsCallSite.CallSitePrototypeGetScriptNameOrSourceURL);
        Register(Builtin.CallSitePrototypeGetThis, BuiltinsCallSite.CallSitePrototypeGetThis);
        Register(Builtin.CallSitePrototypeGetTypeName, BuiltinsCallSite.CallSitePrototypeGetTypeName);
        Register(Builtin.CallSitePrototypeIsAsync, BuiltinsCallSite.CallSitePrototypeIsAsync);
        Register(Builtin.CallSitePrototypeIsConstructor, BuiltinsCallSite.CallSitePrototypeIsConstructor);
        Register(Builtin.CallSitePrototypeIsEval, BuiltinsCallSite.CallSitePrototypeIsEval);
        Register(Builtin.CallSitePrototypeIsNative, BuiltinsCallSite.CallSitePrototypeIsNative);
        Register(Builtin.CallSitePrototypeIsPromiseAll, BuiltinsCallSite.CallSitePrototypeIsPromiseAll);
        Register(Builtin.CallSitePrototypeIsToplevel, BuiltinsCallSite.CallSitePrototypeIsToplevel);
        Register(Builtin.CallSitePrototypeToString, BuiltinsCallSite.CallSitePrototypeToString);
    }
}

/// <summary>The CallSite builtins.</summary>
public static class BuiltinsCallSite
{
    /// <summary>CHECK_CALLSITE: the receiver's CallSiteInfo, or TypeError.</summary>
    static CallSiteInfo CheckCallSite(Isolate isolate, in BuiltinArguments args, string method)
    {
        // CHECK_RECEIVER(JSObject, receiver, method).
        if (args.Receiver.HeapObjectOrNull is not JSObject receiver)
        {
            isolate.ThrowTypeError(MessageTemplate.IncompatibleMethodReceiver, isolate.Factory.NewStringFromAsciiChecked(method),
                args.Receiver);
            return null!;
        }
        var it = new LookupIterator(isolate, receiver, ReadOnlyRoots.call_site_info_symbol,
            LookupIterator.Configuration.OWN_SKIP_INTERCEPTOR);
        if (it.State != LookupIterator.StateKind.DATA)
        {
            isolate.ThrowTypeError(MessageTemplate.CallSiteMethod, isolate.Factory.NewStringFromAsciiChecked(method));
            return null!;
        }
        return it.GetDataValue().As<CallSiteInfo>();
    }

    static bool IsSecurityTokenCompatible(Isolate isolate, CallSiteInfo frame)
    {
        NativeContext currentNativeContext = isolate.NativeContext;
        return IsCompatible(currentNativeContext, frame.Function) && IsCompatible(currentNativeContext, frame.ReceiverOrInstance);

        static bool IsCompatible(NativeContext current, JSValue obj)
        {
            if (obj.HeapObjectOrNull is not JSReceiver receiver) return true;
            NativeContext? creationContext = receiver.GetCreationContext();
            if (creationContext is null) return false;
            // NativeContext::HasSameSecurityTokenAs.
            return creationContext.SecurityToken.IsIdenticalTo(current.SecurityToken);
        }
    }

    static JSValue PositiveNumberOrNull(int value) => value > 0 ? JSValue.FromInt(value) : JSValue.Null;

    public static JSValue CallSitePrototypeGetColumnNumber(Isolate isolate, in BuiltinArguments args)
    {
        CallSiteInfo frame = CheckCallSite(isolate, in args, "getColumnNumber");
        if (!IsSecurityTokenCompatible(isolate, frame)) return JSValue.Null;
        return PositiveNumberOrNull(CallSiteInfo.GetColumnNumber(isolate, frame));
    }

    public static JSValue CallSitePrototypeGetEnclosingColumnNumber(Isolate isolate, in BuiltinArguments args)
    {
        CallSiteInfo frame = CheckCallSite(isolate, in args, "getEnclosingColumnNumber");
        if (!IsSecurityTokenCompatible(isolate, frame)) return JSValue.Null;
        return PositiveNumberOrNull(CallSiteInfo.GetEnclosingColumnNumber(isolate, frame));
    }

    public static JSValue CallSitePrototypeGetEnclosingLineNumber(Isolate isolate, in BuiltinArguments args)
    {
        CallSiteInfo frame = CheckCallSite(isolate, in args, "getEnclosingLineNumber");
        if (!IsSecurityTokenCompatible(isolate, frame)) return JSValue.Null;
        return PositiveNumberOrNull(CallSiteInfo.GetEnclosingLineNumber(isolate, frame));
    }

    public static JSValue CallSitePrototypeGetEvalOrigin(Isolate isolate, in BuiltinArguments args)
    {
        CallSiteInfo frame = CheckCallSite(isolate, in args, "getEvalOrigin");
        if (!IsSecurityTokenCompatible(isolate, frame)) return JSValue.Null;
        return CallSiteInfo.GetEvalOrigin(isolate, frame);
    }

    public static JSValue CallSitePrototypeGetFileName(Isolate isolate, in BuiltinArguments args)
    {
        CallSiteInfo frame = CheckCallSite(isolate, in args, "getFileName");
        if (!IsSecurityTokenCompatible(isolate, frame)) return JSValue.Null;
        return frame.GetScriptName();
    }

    public static JSValue CallSitePrototypeGetFunction(Isolate isolate, in BuiltinArguments args)
    {
        CallSiteInfo frame = CheckCallSite(isolate, in args, "getFunction");
        if (!IsSecurityTokenCompatible(isolate, frame)) return JSValue.Undefined;
        JSFunction function = frame.Function;
        if (frame.IsStrict || function.Shared.IsToplevel) return JSValue.Undefined;
        // Get function's creation context. Return undefined if not available.
        // (ShadowRealm boundaries are not checked: ShadowRealm is not ported.)
        if (function.GetCreationContext() is null) return JSValue.Undefined;
        isolate.CountUsage("kCallSiteAPIGetFunctionSloppyCall");
        return function;
    }

    public static JSValue CallSitePrototypeGetFunctionName(Isolate isolate, in BuiltinArguments args)
    {
        CallSiteInfo frame = CheckCallSite(isolate, in args, "getFunctionName");
        if (!IsSecurityTokenCompatible(isolate, frame)) return JSValue.Null;
        // Get function's creation context. Return null if not available.
        if (frame.Function.GetCreationContext() is null) return JSValue.Null;
        return CallSiteInfo.GetFunctionName(isolate, frame);
    }

    public static JSValue CallSitePrototypeGetLineNumber(Isolate isolate, in BuiltinArguments args)
    {
        CallSiteInfo frame = CheckCallSite(isolate, in args, "getLineNumber");
        if (!IsSecurityTokenCompatible(isolate, frame)) return JSValue.Null;
        return PositiveNumberOrNull(CallSiteInfo.GetLineNumber(isolate, frame));
    }

    public static JSValue CallSitePrototypeGetMethodName(Isolate isolate, in BuiltinArguments args)
    {
        CallSiteInfo frame = CheckCallSite(isolate, in args, "getMethodName");
        if (!IsSecurityTokenCompatible(isolate, frame)) return JSValue.Null;
        return CallSiteInfo.GetMethodName(isolate, frame);
    }

    public static JSValue CallSitePrototypeGetPosition(Isolate isolate, in BuiltinArguments args)
    {
        CallSiteInfo frame = CheckCallSite(isolate, in args, "getPosition");
        if (!IsSecurityTokenCompatible(isolate, frame)) return JSValue.Null;
        return JSValue.FromInt(CallSiteInfo.GetSourcePosition(frame));
    }

    public static JSValue CallSitePrototypeGetPromiseIndex(Isolate isolate, in BuiltinArguments args)
    {
        CallSiteInfo frame = CheckCallSite(isolate, in args, "getPromiseIndex");
        if (!IsSecurityTokenCompatible(isolate, frame)) return JSValue.Null;
        if (!frame.IsPromiseAll() && !frame.IsPromiseAny() && !frame.IsPromiseAllSettled()) return JSValue.Null;
        return JSValue.FromInt(CallSiteInfo.GetSourcePosition(frame));
    }

    public static JSValue CallSitePrototypeGetScriptHash(Isolate isolate, in BuiltinArguments args)
    {
        CallSiteInfo frame = CheckCallSite(isolate, in args, "getScriptHash");
        if (!IsSecurityTokenCompatible(isolate, frame)) return JSValue.Null;
        return GetScriptHash(isolate, frame);
    }

    public static JSValue CallSitePrototypeGetScriptNameOrSourceURL(Isolate isolate, in BuiltinArguments args)
    {
        CallSiteInfo frame = CheckCallSite(isolate, in args, "getScriptNameOrSourceUrl");
        if (!IsSecurityTokenCompatible(isolate, frame)) return JSValue.Null;
        return frame.GetScriptNameOrSourceURL();
    }

    public static JSValue CallSitePrototypeGetThis(Isolate isolate, in BuiltinArguments args)
    {
        CallSiteInfo frame = CheckCallSite(isolate, in args, "getThis");
        if (!IsSecurityTokenCompatible(isolate, frame)) return JSValue.Undefined;
        if (frame.IsStrict) return JSValue.Undefined;
        isolate.CountUsage("kCallSiteAPIGetThisSloppyCall");
        JSValue thisObj = frame.ReceiverOrInstance;
        // Get receiver's creation context. Return undefined if not available.
        if (thisObj.HeapObjectOrNull is JSReceiver receiver && receiver.GetCreationContext() is null) return JSValue.Undefined;
        // The receiver of a constructor frame without a receiver yet is the hole.
        return thisObj.IsTheHole ? JSValue.Undefined : thisObj;
    }

    public static JSValue CallSitePrototypeGetTypeName(Isolate isolate, in BuiltinArguments args)
    {
        CallSiteInfo frame = CheckCallSite(isolate, in args, "getTypeName");
        if (!IsSecurityTokenCompatible(isolate, frame)) return JSValue.Null;
        return CallSiteInfo.GetTypeName(isolate, frame);
    }

    public static JSValue CallSitePrototypeIsAsync(Isolate isolate, in BuiltinArguments args)
    {
        CallSiteInfo frame = CheckCallSite(isolate, in args, "isAsync");
        if (!IsSecurityTokenCompatible(isolate, frame)) return JSValue.Null;
        return JSValue.FromBoolean(frame.IsAsync);
    }

    public static JSValue CallSitePrototypeIsConstructor(Isolate isolate, in BuiltinArguments args)
    {
        CallSiteInfo frame = CheckCallSite(isolate, in args, "isConstructor");
        if (!IsSecurityTokenCompatible(isolate, frame)) return JSValue.Null;
        return JSValue.FromBoolean(frame.IsConstructor);
    }

    public static JSValue CallSitePrototypeIsEval(Isolate isolate, in BuiltinArguments args)
    {
        CallSiteInfo frame = CheckCallSite(isolate, in args, "isEval");
        if (!IsSecurityTokenCompatible(isolate, frame)) return JSValue.Null;
        return JSValue.FromBoolean(frame.IsEval());
    }

    public static JSValue CallSitePrototypeIsNative(Isolate isolate, in BuiltinArguments args)
    {
        CallSiteInfo frame = CheckCallSite(isolate, in args, "isNative");
        if (!IsSecurityTokenCompatible(isolate, frame)) return JSValue.Null;
        return JSValue.FromBoolean(frame.IsNative());
    }

    public static JSValue CallSitePrototypeIsPromiseAll(Isolate isolate, in BuiltinArguments args)
    {
        CallSiteInfo frame = CheckCallSite(isolate, in args, "isPromiseAll");
        if (!IsSecurityTokenCompatible(isolate, frame)) return JSValue.Null;
        return JSValue.FromBoolean(frame.IsPromiseAll());
    }

    public static JSValue CallSitePrototypeIsToplevel(Isolate isolate, in BuiltinArguments args)
    {
        CallSiteInfo frame = CheckCallSite(isolate, in args, "isToplevel");
        if (!IsSecurityTokenCompatible(isolate, frame)) return JSValue.Null;
        return JSValue.FromBoolean(frame.IsToplevel());
    }

    public static JSValue CallSitePrototypeToString(Isolate isolate, in BuiltinArguments args)
    {
        CallSiteInfo frame = CheckCallSite(isolate, in args, "toString");
        if (!IsSecurityTokenCompatible(isolate, frame)) return ReadOnlyRoots.empty_string;
        return CallSiteInfo.SerializeCallSiteInfo(isolate, frame);
    }

    /// <summary>
    /// CallSiteInfo::GetScriptHash / Script::GetScriptHash: the lower-case hex
    /// SHA-256 of the script source's UTF-8 C string (up to the first NUL).
    /// V8 caches the hash on the script (source_hash) and returns "" for
    /// scripts with opaque origins; V8Sharp has no opaque origins and
    /// computes the hash on each call.
    /// </summary>
    public static JSString GetScriptHash(Isolate isolate, CallSiteInfo info)
    {
        if (info.GetScript() is not Script script) return ReadOnlyRoots.empty_string;
        // HasValidSource.
        if (script.Source.HeapObjectOrNull is not JSString source) return ReadOnlyRoots.empty_string;

        string text = source.Flatten();
        int nul = text.IndexOf('\0');
        if (nul >= 0) text = text[..nul];
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(text));
        return isolate.Factory.NewStringFromAsciiChecked(Convert.ToHexStringLower(hash));
    }
}
