// Port of the streaming entry points of src/wasm/wasm-js.cc:
// WebAssembly.compileStreaming and WebAssembly.instantiateStreaming, which V8
// installs when the embedder has a streaming callback. V8Sharp has only the
// testing callback (WasmStreamingCallbackForTesting, --wasm-test-streaming),
// which takes the resolved argument as the module's bytes; there is no
// incremental decoder, so the bytes are compiled as one buffer
// (deviations.md, "WebAssembly").
namespace V8Sharp.Wasm;

public static partial class WasmJs
{
    /// <summary>Installs compileStreaming and instantiateStreaming when the streaming callback is set.</summary>
    static void InstallStreaming(Isolate isolate, JSObject webassembly)
    {
        if (!isolate.Flags.wasm_test_streaming) return;
        InstallFunc(isolate, webassembly, "compileStreaming", WebAssemblyCompileStreaming, 1);
        InstallFunc(isolate, webassembly, "instantiateStreaming", WebAssemblyInstantiateStreaming, 1);
    }

    /// <summary>WebAssembly.compileStreaming(Response | Promise&lt;Response&gt;, options) -> Promise&lt;Module&gt;.</summary>
    static JSValue WebAssemblyCompileStreaming(Isolate isolate, in BuiltinArguments args)
    {
        var thrower = new ErrorThrower(isolate, "WebAssembly.compileStreaming()");
        JSPromise promise = PromiseBuiltins.NewJSPromise(isolate);
        CompileTimeImports imports = CompileTimeImports.FromArgument(isolate, args.AtOrUndefined(2));
        StartAsyncCompilationWithResolver(isolate, thrower, args.AtOrUndefined(1), imports, (module, error) =>
        {
            if (module is { } m) JSPromise.Resolve(isolate, promise, m);
            else JSPromise.Reject(isolate, promise, error);
        });
        return promise;
    }

    /// <summary>
    /// WebAssembly.instantiateStreaming(Response | Promise&lt;Response&gt;, imports, options)
    /// -> Promise&lt;ResultObject&gt;.
    /// </summary>
    static JSValue WebAssemblyInstantiateStreaming(Isolate isolate, in BuiltinArguments args)
    {
        var thrower = new ErrorThrower(isolate, "WebAssembly.instantiateStreaming()");
        JSPromise promise = PromiseBuiltins.NewJSPromise(isolate);
        JSValue ffi = args.AtOrUndefined(2);
        if (!ffi.IsUndefined && !ffi.IsJSReceiver)
        {
            JSPromise.Reject(isolate, promise, thrower.Reify(ErrorThrower.ErrorType.TypeError,
                "Argument 1 must be an object"));
            return promise;
        }
        JSReceiver? imports = ffi.IsUndefined ? null : (JSReceiver)ffi.Object;
        CompileTimeImports compileImports = CompileTimeImports.FromArgument(isolate, args.AtOrUndefined(3));
        StartAsyncCompilationWithResolver(isolate, thrower, args.AtOrUndefined(1), compileImports, (module, error) =>
        {
            if (module is null)
            {
                JSPromise.Reject(isolate, promise, error);
                return;
            }
            AsyncInstantiate(isolate, thrower, promise, (WasmModuleObject)module.Value.Object, imports, returnModule: true);
        });
        return promise;
    }

    /// <summary>
    /// StartAsyncCompilationWithResolver: resolves the argument as a promise
    /// and hands its value to the streaming callback, which compiles it.
    /// </summary>
    static void StartAsyncCompilationWithResolver(Isolate isolate, ErrorThrower thrower, JSValue responseOrPromise,
        CompileTimeImports imports, Action<JSValue?, JSValue> onDone)
    {
        if (!IsWasmCodegenAllowed(isolate))
        {
            onDone(null, thrower.Reify(ErrorThrower.ErrorType.CompileError, ErrorStringForCodegen(isolate)));
            return;
        }
        NativeContext context = isolate.NativeContext;
        JSValue input = PromiseBuiltins.PromiseResolve(isolate, context.PromiseFunction, responseOrPromise);

        // WasmStreamingCallbackForTesting: the resolved value is the bytes.
        JSFunction compileCallback = CreateFunc(isolate, isolate.Factory.EmptyString,
            (Isolate i, in BuiltinArguments a) =>
            {
                var bytesThrower = new ErrorThrower(i, "WebAssembly.compile()");
                byte[] bytes;
                try
                {
                    bytes = GetAndCopyFirstArgumentAsBytes(i, a, bytesThrower);
                }
                catch (JavaScriptException e)
                {
                    onDone(null, e.Value);
                    return JSValue.Undefined;
                }
                AsyncCompile(i, thrower, bytes, imports, onDone);
                return JSValue.Undefined;
            }, hasPrototype: false);
        // WasmStreamingPromiseFailedCallback: aborts with the rejection.
        JSFunction rejectCallback = CreateFunc(isolate, isolate.Factory.EmptyString,
            (Isolate i, in BuiltinArguments a) =>
            {
                onDone(null, a.AtOrUndefined(1));
                return JSValue.Undefined;
            }, hasPrototype: false);
        PromiseBuiltins.PerformPromiseThen(isolate, input.As<JSPromise>(), compileCallback, rejectCallback,
            JSValue.Undefined);
    }
}
