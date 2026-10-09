// The wrappers between JavaScript and wasm calls: V8's JSToWasm wrapper
// (src/wasm/wrappers.cc, builtins-wasm-gen: the generic JS-to-wasm wrapper)
// and WasmToJS wrapper (the import call into a JS callable), with the
// exception and trap conversions of src/runtime/runtime-wasm.cc
// (Runtime_ThrowWasmError, Runtime_WasmThrow, Runtime_WasmReThrow).
//
// A trap becomes a WebAssembly.RuntimeError that wasm cannot catch (V8 marks
// it with wasm_uncatchable_symbol). A wasm exception that leaves wasm is a
// WebAssembly.Exception object, or the original JS value for one of
// WebAssembly.JSTag; a JS exception that enters wasm is a wasm exception of
// WebAssembly.JSTag that try_table can catch, and leaves again as itself.
using System.Runtime.CompilerServices;
using Wacs.Core.Runtime;
using Wacs.Core.Runtime.Exceptions;
using Wacs.Core.Runtime.Types;
using Wacs.Core.Types;
using Wacs.Core.Types.Defs;

namespace V8Sharp.Wasm;

public static partial class WasmJs
{
    // The JS exceptions that entered wasm, by the wasm exception they became,
    // so that the same JavaScriptException (with its message) leaves wasm.
    static readonly ConditionalWeakTable<ExnInstance, JavaScriptException> s_jsExceptions = new();

    /// <summary>Runtime_ThrowWasmError: the RuntimeError of a trap, which wasm cannot catch.</summary>
    internal static JavaScriptException TrapToJS(Isolate isolate, TrapException trap)
    {
        MessageTemplate template = WasmErrorMessages.TrapTemplate(trap);
        RecordUnwoundFrames(isolate, trap.WasmFrames);
        JSValue[] arguments = template == MessageTemplate.AtomicsOperationNotAllowed
            ? [isolate.Factory.InternalizeString("Atomics.wait")]
            : trap is WasmTemplateTrapException { Argument: { } argument }
                ? [JSValue.FromNumber(argument)]
                : [];
        JSObject error = isolate.Factory.NewError(isolate.NativeContext.WasmRuntimeErrorFunction, template, arguments);
        JSObject.AddProperty(isolate, error, ReadOnlyRoots.wasm_uncatchable_symbol, JSValue.True, PropertyAttributes.NONE);
        try
        {
            isolate.Throw(error);
        }
        catch (JavaScriptException e)
        {
            return e;
        }
        throw new InvalidOperationException("unreachable");
    }

    /// <summary>
    /// Calls wasm (the JS-to-wasm direction): runs the function and turns
    /// what can leave wasm into JS exceptions.
    /// </summary>
    internal static Value[] InvokeWasm(WasmEngine engine, FuncAddr address, Value[] args)
    {
        Isolate isolate = engine.Isolate;
        try
        {
            return engine.Runtime.Invoke(address, args);
        }
        catch (TrapException trap)
        {
            throw TrapToJS(isolate, trap);
        }
        catch (UnhandledWasmException e)
        {
            throw ExceptionLeavingWasm(engine, e.ExnRef);
        }
        catch (WasmHostException e)
        {
            throw ExceptionLeavingWasm(engine, e.ExnRef);
        }
        catch (Exception e) when (WasmErrorMessages.IsStackExhaustion(e))
        {
            RecordUnwoundFrames(isolate, (e as WasmRuntimeException)?.WasmFrames,
                (e as WasmRuntimeException)?.CalleeFuncAddr ?? -1);
            isolate.StackOverflow();
            throw;
        }
        catch (WasmRuntimeException e)
        {
            JSObject error = isolate.Factory.NewError(isolate.NativeContext.WasmRuntimeErrorFunction,
                isolate.Factory.NewStringFromAsciiChecked(e.Message));
            isolate.Throw(error);
            throw;
        }
    }

    /// <summary>
    /// The frames an exception unwound, for the stack of the error created
    /// for it (V8 creates the error while the wasm frames are on the stack).
    /// </summary>
    static void RecordUnwoundFrames(Isolate isolate, WasmStackFrame[]? frames, int calleeFuncAddr = -1)
    {
        if (isolate.WasmEngineField?.CurrentActivation is not { } activation || frames is null) return;
        int own = frames.Length - activation.BaseHeight;
        WasmStackFrame[] result = own <= 0 ? [] : frames[..own];
        if (calleeFuncAddr >= 0)
        {
            // A stack overflow shows the function that could not be entered.
            result = [new WasmStackFrame((uint)calleeFuncAddr, null, -1, WasmStackTraces.FunctionEntryPc), .. result];
        }
        activation.TrapFrames = result;
    }

    static Exception ExceptionLeavingWasm(WasmEngine engine, Value exnRef)
    {
        if (exnRef.GcRef is ExnInstance exn && s_jsExceptions.TryGetValue(exn, out JavaScriptException? original))
        {
            return original;
        }
        JSValue value = engine.ExceptionToJS(exnRef);
        try
        {
            engine.Isolate.Throw(value);
        }
        catch (JavaScriptException e)
        {
            return e;
        }
        return new InvalidOperationException("unreachable");
    }

    static bool IsJSCompatibleSignature(FunctionType signature)
    {
        foreach (ValType t in signature.ParameterTypes.Types)
        {
            if (!IsJSCompatibleType(t)) return false;
        }
        foreach (ValType t in signature.ResultType.Types)
        {
            if (!IsJSCompatibleType(t)) return false;
        }
        return true;
    }

    static bool IsJSCompatibleType(ValType t) =>
        t != ValType.V128 && (t.IsDefType() || !t.IsRefType() ||
                              t.GetHeapType() is not (HeapType.Exn or HeapType.NoExn or HeapType.Cont or HeapType.NoCont));

    /// <summary>
    /// The JS-to-wasm wrapper: the callback of an exported function. Converts
    /// the arguments (ToWebAssemblyValue), calls the function and converts the
    /// results (none: undefined; one: its value; several: an array).
    /// </summary>
    internal static JSValue CallExportedFunction(Isolate isolate, in BuiltinArguments args)
    {
        var data = (WasmExportedFunctionData)args.Target.Shared.FunctionData!;
        WasmEngine.Activation activation = data.Engine.EnterActivation();
        try
        {
            if (TryCallCompiled(isolate, data, args.Arguments, out JSValue result)) return result;
            return ResultsToJS(data, CallWasmInActivation(isolate, data, args.Arguments));
        }
        finally
        {
            data.Engine.LeaveActivation(activation);
        }
    }

    static Value[] CallWasm(Isolate isolate, WasmExportedFunctionData data, ReadOnlySpan<JSValue> arguments)
    {
        // The activation covers the argument conversions too, so that every
        // exported function frame on the JS stack has one.
        WasmEngine.Activation activation = data.Engine.EnterActivation();
        try
        {
            return CallWasmInActivation(isolate, data, arguments);
        }
        finally
        {
            data.Engine.LeaveActivation(activation);
        }
    }

    static Value[] CallWasmInActivation(Isolate isolate, WasmExportedFunctionData data, ReadOnlySpan<JSValue> arguments)
    {
        WasmEngine engine = data.Engine;
        FunctionType signature = data.Signature;
        if (!IsJSCompatibleSignature(signature))
        {
            isolate.ThrowTypeError(MessageTemplate.WasmTrapJSTypeError);
        }
        ModuleInstance? module = (engine.Store[data.Address] as FunctionInstance)?.Module;
        ValType[] paramTypes = signature.ParameterTypes.Types;
        var wasmArgs = paramTypes.Length == 0 ? [] : new Value[paramTypes.Length];
        for (int i = 0; i < paramTypes.Length; i++)
        {
            JSValue arg = i < arguments.Length ? arguments[i] : JSValue.Undefined;
            wasmArgs[i] = engine.ToWasmValue(arg, paramTypes[i], module);
        }
        return InvokeWasm(engine, data.Address, wasmArgs);
    }

    static JSValue ResultsToJS(WasmExportedFunctionData data, Value[] results)
    {
        WasmEngine engine = data.Engine;
        ValType[] resultTypes = data.Signature.ResultType.Types;
        switch (resultTypes.Length)
        {
            case 0:
                return JSValue.Undefined;
            case 1:
                return engine.ToJSValue(results[0], resultTypes[0]);
            default:
            {
                Isolate isolate = engine.Isolate;
                var elements = isolate.Factory.NewFixedArray(resultTypes.Length);
                for (int i = 0; i < resultTypes.Length; i++)
                {
                    elements[i] = engine.ToJSValue(results[i], resultTypes[i]);
                }
                return isolate.Factory.NewJSArrayWithElements(elements);
            }
        }
    }

    /// <summary>
    /// The function WebAssembly.promising returns: calls the wasm function and
    /// returns a promise of its result (rejected with what it throws).
    /// </summary>
    internal static JSValue CallPromisingFunction(Isolate isolate, in BuiltinArguments args)
    {
        var data = (WasmExportedFunctionData)args.Target.Shared.FunctionData!;
        JSPromise promise = PromiseBuiltins.NewJSPromise(isolate);
        try
        {
            Value[] results = CallWasm(isolate, data, args.Arguments);
            JSPromise.Resolve(isolate, promise, ResultsToJS(data, results));
        }
        catch (JavaScriptException e)
        {
            JSPromise.Reject(isolate, promise, e.Value);
        }
        return promise;
    }

    /// <summary>
    /// The WasmToJS wrapper of an imported JS callable: a host function of
    /// the import's signature that converts the arguments (ToJSValue), calls
    /// the callable with an undefined receiver, and converts the result.
    /// </summary>
    internal static FuncAddr NewImportWrapper(WasmEngine engine, JSReceiver callable, FunctionType signature,
        string moduleName, string name, bool suspending, ModuleInstance? typesModule)
    {
        Isolate isolate = engine.Isolate;
        ValType[] paramTypes = signature.ParameterTypes.Types;
        ValType[] resultTypes = signature.ResultType.Types;
        bool compatible = IsJSCompatibleSignature(signature);
        return engine.Runtime.AllocateHostFunction(moduleName, name, signature, (ctx, wasmArgs, results) =>
        {
            try
            {
                if (!compatible)
                {
                    isolate.ThrowTypeError(MessageTemplate.WasmTrapJSTypeError);
                }
                var jsArgs = paramTypes.Length == 0 ? [] : new JSValue[paramTypes.Length];
                for (int i = 0; i < paramTypes.Length; i++)
                {
                    jsArgs[i] = engine.ToJSValue(wasmArgs[i], paramTypes[i]);
                }
                JSValue result = Execution.Call(isolate, callable, JSValue.Undefined, jsArgs);
                if (suspending && result.HeapObjectOrNull is JSPromise)
                {
                    // V8Sharp cannot suspend the wasm stack (deviations.md).
                    isolate.Throw(isolate.Factory.NewError(isolate.NativeContext.WasmSuspendErrorFunction,
                        MessageTemplate.WasmSuspendError, []));
                }
                switch (resultTypes.Length)
                {
                    case 0:
                        break;
                    case 1:
                        results[0] = engine.ToWasmValue(result, resultTypes[0], typesModule);
                        break;
                    default:
                    {
                        JSValue[] values = IterableToList(isolate, result);
                        if (values.Length != resultTypes.Length)
                        {
                            isolate.ThrowTypeError(MessageTemplate.WasmTrapMultiReturnLengthMismatch);
                        }
                        for (int i = 0; i < resultTypes.Length; i++)
                        {
                            results[i] = engine.ToWasmValue(values[i], resultTypes[i], typesModule);
                        }
                        break;
                    }
                }
            }
            catch (JavaScriptException e) when (!WasmEngine.IsUncatchable(e.Value))
            {
                Value exnRef = engine.JSExceptionToWasm(e.Value);
                if (exnRef.GcRef is ExnInstance exn) s_jsExceptions.AddOrUpdate(exn, e);
                throw new WasmHostException(exnRef);
            }
        }, callable);
    }

    /// <summary>IterableToFixedArray: the values of an iterable (the multi-value return of an import).</summary>
    static JSValue[] IterableToList(Isolate isolate, JSValue iterable)
    {
        JSValue method = ObjectOps.GetProperty(isolate, iterable, ReadOnlyRoots.iterator_symbol);
        if (!ObjectOps.IsCallable(method))
        {
            isolate.ThrowTypeError(MessageTemplate.NotIterable, iterable);
        }
        JSValue iterator = Execution.Call(isolate, method, iterable, []);
        if (!iterator.IsJSReceiver)
        {
            isolate.ThrowTypeError(MessageTemplate.SymbolIteratorInvalid);
        }
        JSValue next = ObjectOps.GetProperty(isolate, iterator, ReadOnlyRoots.next_string);
        var list = new List<JSValue>();
        while (true)
        {
            JSValue result = Execution.Call(isolate, next, iterator, []);
            if (!result.IsJSReceiver)
            {
                isolate.ThrowTypeError(MessageTemplate.IteratorResultNotAnObject, result);
            }
            if (ObjectOps.BooleanValue(ObjectOps.GetProperty(isolate, result, ReadOnlyRoots.done_string))) break;
            list.Add(ObjectOps.GetProperty(isolate, result, ReadOnlyRoots.value_string));
        }
        return list.ToArray();
    }
}
