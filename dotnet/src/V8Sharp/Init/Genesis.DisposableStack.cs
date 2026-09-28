// Port of the "D i s p o s a b l e S t a c k" section at the end of
// Genesis::InitializeGlobal (src/init/bootstrapper.cc 5134-5226).
namespace V8Sharp.Init;

sealed partial class Genesis
{
    void InstallDisposableStack(JSObject global)
    {
        Isolate isolate = _isolate;
        NativeContext nativeContext = _nativeContext;

        Map jsDisposableStackMap = _factory.NewContextfulMapForCurrentContext(InstanceType.JSDisposableStackBaseType,
            JSObject.GetHeaderSize(InstanceType.JSDisposableStackBaseType));
        jsDisposableStackMap.SetConstructor(nativeContext.ObjectFunction);
        nativeContext.JSDisposableStackMap = jsDisposableStackMap;

        // SyncDisposableStack
        JSFunction disposableStackFunction = Bootstrapper.InstallFunction(isolate, global, "DisposableStack",
            InstanceType.JSDisposableStackType, JSObject.GetHeaderSize(InstanceType.JSDisposableStackType), 0,
            JSValue.TheHole, Builtin.DisposableStackConstructor, 0, false);
        var syncDisposableStackPrototype = (JSObject)disposableStackFunction.InstancePrototype;
        Bootstrapper.InstallWithIntrinsicDefaultProto(isolate, disposableStackFunction,
            Context.Field.JS_DISPOSABLE_STACK_FUNCTION_INDEX);

        Bootstrapper.SimpleInstallFunction(isolate, syncDisposableStackPrototype, "use", Builtin.DisposableStackPrototypeUse, 1, true);
        JSFunction dispose = Bootstrapper.SimpleInstallFunction(isolate, syncDisposableStackPrototype, "dispose",
            Builtin.DisposableStackPrototypeDispose, 0, true);
        JSObject.AddProperty(isolate, syncDisposableStackPrototype, ReadOnlyRoots.dispose_symbol, dispose, PropertyAttributes.DONT_ENUM);
        Bootstrapper.SimpleInstallFunction(isolate, syncDisposableStackPrototype, "adopt", Builtin.DisposableStackPrototypeAdopt, 2, true);
        Bootstrapper.SimpleInstallFunction(isolate, syncDisposableStackPrototype, "defer", Builtin.DisposableStackPrototypeDefer, 1, true);
        Bootstrapper.SimpleInstallFunction(isolate, syncDisposableStackPrototype, "move", Builtin.DisposableStackPrototypeMove, 0, true);

        Bootstrapper.InstallToStringTag(isolate, syncDisposableStackPrototype, "DisposableStack");
        Bootstrapper.SimpleInstallGetter(isolate, syncDisposableStackPrototype, ReadOnlyRoots.disposed_string,
            Builtin.DisposableStackPrototypeGetDisposed, true);

        // AsyncDisposableStack
        JSFunction asyncDisposableStackFunction = Bootstrapper.InstallFunction(isolate, global, "AsyncDisposableStack",
            InstanceType.JSAsyncDisposableStackType, JSObject.GetHeaderSize(InstanceType.JSAsyncDisposableStackType), 0,
            JSValue.TheHole, Builtin.AsyncDisposableStackConstructor, 0, false);
        var asyncDisposableStackPrototype = (JSObject)asyncDisposableStackFunction.InstancePrototype;
        Bootstrapper.InstallWithIntrinsicDefaultProto(isolate, asyncDisposableStackFunction,
            Context.Field.JS_ASYNC_DISPOSABLE_STACK_FUNCTION_INDEX);

        Bootstrapper.SimpleInstallFunction(isolate, asyncDisposableStackPrototype, "use", Builtin.AsyncDisposableStackPrototypeUse, 1, true);
        JSFunction disposeAsync = Bootstrapper.SimpleInstallFunction(isolate, asyncDisposableStackPrototype, "disposeAsync",
            Builtin.AsyncDisposableStackPrototypeDisposeAsync, 0, true);
        JSObject.AddProperty(isolate, asyncDisposableStackPrototype, ReadOnlyRoots.async_dispose_symbol, disposeAsync,
            PropertyAttributes.DONT_ENUM);
        Bootstrapper.SimpleInstallFunction(isolate, asyncDisposableStackPrototype, "adopt", Builtin.AsyncDisposableStackPrototypeAdopt, 2, true);
        Bootstrapper.SimpleInstallFunction(isolate, asyncDisposableStackPrototype, "defer", Builtin.AsyncDisposableStackPrototypeDefer, 1, true);
        Bootstrapper.SimpleInstallFunction(isolate, asyncDisposableStackPrototype, "move", Builtin.AsyncDisposableStackPrototypeMove, 0, true);

        Bootstrapper.InstallToStringTag(isolate, asyncDisposableStackPrototype, "AsyncDisposableStack");
        Bootstrapper.SimpleInstallGetter(isolate, asyncDisposableStackPrototype, ReadOnlyRoots.disposed_string,
            Builtin.AsyncDisposableStackPrototypeGetDisposed, true);

        // Add symbols to iterator prototypes
        JSObject iteratorPrototype = nativeContext.InitialIteratorPrototype;
        Bootstrapper.InstallFunctionAtSymbol(isolate, iteratorPrototype, ReadOnlyRoots.dispose_symbol, "[Symbol.dispose]",
            Builtin.IteratorPrototypeDispose, 0, true);

        JSObject asyncIteratorPrototype = nativeContext.InitialAsyncIteratorPrototype;
        Bootstrapper.InstallFunctionAtSymbol(isolate, asyncIteratorPrototype, ReadOnlyRoots.async_dispose_symbol,
            "[Symbol.asyncDispose]", Builtin.AsyncIteratorPrototypeAsyncDispose, 0, true);
    }
}
