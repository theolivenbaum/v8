// Port of src/extensions/gc-extension.{h,cc}: the gc() function that
// Bootstrapper::InstallExtensions installs on the global object when
// --expose-gc (or --expose-gc-as=NAME) is set.
//
// V8 compiles the extension's "native function gc();" source into an API
// function; V8Sharp creates the API function directly (the v8::Extension
// mechanism is not ported). The GC types (minor, major, major-snapshot) all
// run Isolate.CollectGarbage: the CLR GC has no V8-style minor-only mode.
namespace V8Sharp.Init;

public static class GCExtension
{
    /// <summary>Installs gc() on the global object when the flags ask for it.</summary>
    public static void InstallIfExposed(Isolate isolate, NativeContext nativeContext)
    {
        string? name = isolate.Flags.expose_gc_as ?? (isolate.Flags.expose_gc ? "gc" : null);
        if (name is null) return;
        Factory factory = isolate.Factory;
        JSString internalizedName = factory.InternalizeString(name);
        var data = new FunctionTemplateInfo(GC) { Length = 0 };
        SharedFunctionInfo info = factory.NewSharedFunctionInfo(internalizedName, data, Builtin.HandleApiCallOrConstruct, 0, false);
        info.BuiltinId = Builtin.HandleApiCallOrConstruct;
        info.Native = true;
        info.UpdateFunctionMapIndex();
        JSFunction function = factory.NewFunction(info, nativeContext, nativeContext.StrictFunctionWithoutPrototypeMap);
        JSObject.SetOwnPropertyIgnoreAttributes(isolate, nativeContext.GlobalObject, internalizedName, function, PropertyAttributes.DONT_ENUM);
    }

    /// <summary>
    /// GCExtension::GC. With an options object whose "execution" is "async",
    /// the collection runs in a foreground task (AsyncGC) and the returned
    /// promise resolves after it.
    /// </summary>
    static JSValue GC(Isolate isolate, in BuiltinArguments args)
    {
        // Immediate bailout if no arguments are provided.
        if (args.ArgcWithoutReceiver == 0)
        {
            isolate.CollectGarbage();
            return JSValue.Undefined;
        }

        JSValue options = args.AtOrUndefined(1);
        bool async = false;
        if (options.IsJSReceiver)
        {
            // ParseType is read for its side effects (getters); the type does not
            // change what V8Sharp collects.
            ObjectOps.GetProperty(isolate, options, isolate.Factory.InternalizeString("type"));
            JSValue execution = ObjectOps.GetProperty(isolate, options, isolate.Factory.InternalizeString("execution"));
            async = execution.HeapObjectOrNull is JSString s && s.ToString() == "async";
        }
        if (!async)
        {
            isolate.CollectGarbage();
            return JSValue.Undefined;
        }

        JSPromise promise = PromiseBuiltins.NewJSPromise(isolate);
        NativeContext context = isolate.NativeContext;
        isolate.PostNonNestableTask(new AsyncGC(promise, context).Run);
        return promise;
    }

    /// <summary>AsyncGC: the task that collects and then resolves the promise.</summary>
    sealed class AsyncGC(JSPromise promise, NativeContext context)
    {
        public void Run(Isolate isolate)
        {
            isolate.CollectGarbage();
            using (isolate.EnterContext(context))
            {
                PromiseBuiltins.ResolvePromise(isolate, promise, JSValue.Undefined);
            }
        }
    }
}
