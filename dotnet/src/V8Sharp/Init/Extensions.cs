// Port of the extensions Genesis::InstallExtensions installs from flags
// (src/init/bootstrapper.cc) and of src/extensions/gc-extension.cc.
//
// V8 compiles an extension's "native function gc();" source, which declares
// a global function; V8Sharp defines the same non-configurable global
// directly. Deviation: gc({execution: 'async'}) collects synchronously and
// returns undefined instead of a promise.
using V8Sharp.Builtins;
using V8Sharp.Common;
using V8Sharp.Objects;

namespace V8Sharp.Init;

public static class Extensions
{
    /// <summary>Genesis::InstallExtensions for the flag-controlled extensions.</summary>
    public static void Install(Isolate isolate, NativeContext nativeContext)
    {
        if (isolate.Flags.expose_gc)
        {
            string name = string.IsNullOrEmpty(isolate.Flags.expose_gc_as) ? "gc" : isolate.Flags.expose_gc_as!;
            InstallNativeFunction(isolate, nativeContext, name, GC);
        }
    }

    static void InstallNativeFunction(Isolate isolate, NativeContext nativeContext, string name, BuiltinFunction callback)
    {
        Factory factory = isolate.Factory;
        var data = new FunctionTemplateInfo(callback);
        JSString internalized = factory.InternalizeString(name);
        SharedFunctionInfo info = factory.NewSharedFunctionInfo(internalized, data, Builtin.HandleApiCallOrConstruct, 0, false);
        info.BuiltinId = Builtin.HandleApiCallOrConstruct;
        info.LanguageMode = LanguageMode.Strict;
        info.Native = true;
        info.UpdateFunctionMapIndex();
        JSFunction function = factory.NewFunction(info, nativeContext, nativeContext.StrictFunctionWithoutPrototypeMap);
        JSObject.SetOwnPropertyIgnoreAttributes(isolate, nativeContext.GlobalObject, internalized, function,
            PropertyAttributes.DONT_DELETE);
    }

    /// <summary>GCExtension::GC: a full collection.</summary>
    static JSValue GC(Isolate isolate, in BuiltinArguments args)
    {
        System.GC.Collect();
        System.GC.WaitForPendingFinalizers();
        return JSValue.Undefined;
    }
}
