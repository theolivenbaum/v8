// Port of src/builtins/builtins-shadow-realm.cc (ShadowRealmConstructor,
// ShadowRealmPrototypeEvaluate), of the ImportValue parts of
// builtins-shadow-realm-gen.cc (ShadowRealmPrototypeImportValue,
// ShadowRealmImportValueFulfilled, ShadowRealmImportValueRejected) and of
// src/runtime/runtime-shadow-realm.cc
// (Runtime_ShadowRealmImportValue, Runtime_ShadowRealmThrow).
// ShadowRealmGetWrappedValue and CallWrappedFunction are JSWrappedFunction.
namespace V8Sharp.Builtins;

public static partial class BuiltinRegistry
{
    static partial void RegisterShadowRealm()
    {
        Register(Builtin.ShadowRealmConstructor, BuiltinsShadowRealm.ShadowRealmConstructor);
        Register(Builtin.ShadowRealmPrototypeEvaluate, BuiltinsShadowRealm.ShadowRealmPrototypeEvaluate);
        Register(Builtin.ShadowRealmPrototypeImportValue, BuiltinsShadowRealm.ShadowRealmPrototypeImportValue);
        Register(Builtin.ShadowRealmImportValueFulfilled, BuiltinsShadowRealm.ShadowRealmImportValueFulfilled);
        Register(Builtin.ShadowRealmImportValueRejected, BuiltinsShadowRealm.ShadowRealmImportValueRejected);
    }
}

public static class BuiltinsShadowRealm
{
    /// <summary>https://tc39.es/proposal-shadowrealm/#sec-shadowrealm-constructor</summary>
    public static JSValue ShadowRealmConstructor(Isolate isolate, in BuiltinArguments args)
    {
        // 1. If NewTarget is undefined, throw a TypeError exception.
        if (args.NewTarget.IsUndefined)
        {
            return isolate.ThrowTypeError(MessageTemplate.ConstructorNotFunction, ReadOnlyRoots.ShadowRealm_string);
        }
        // [[Construct]]
        JSFunction target = args.Target;
        var newTarget = (JSReceiver)args.NewTarget.Object;

        // 3. Let realmRec be CreateRealm().
        // 5. Let context be a new execution context.
        // ...
        // 12. Perform ? HostInitializeShadowRealm(O.[[ShadowRealm]]).
        // These steps are combined in
        // Isolate::RunHostCreateShadowRealmContextCallback and Context::New.
        // The host operation is hoisted for not creating a half-initialized
        // ShadowRealm object, which can fail the heap verification.
        NativeContext nativeContext = isolate.RunHostCreateShadowRealmContextCallback();

        // 2. Let O be ? OrdinaryCreateFromConstructor(NewTarget,
        // "%ShadowRealm.prototype%", « [[ShadowRealm]], [[ExecutionContext]] »).
        var o = (JSShadowRealm)JSObject.New(isolate, target, newTarget, null);

        // 4. Set O.[[ShadowRealm]] to realmRec.
        // 9. Set O.[[ExecutionContext]] to context.
        o.NativeContext = nativeContext;

        // 13. Return O.
        return o;
    }

    /// <summary>https://tc39.es/proposal-shadowrealm/#sec-shadowrealm.prototype.evaluate</summary>
    public static JSValue ShadowRealmPrototypeEvaluate(Isolate isolate, in BuiltinArguments args)
    {
        JSValue sourceText = args.AtOrUndefined(1);
        // 1. Let O be this value.
        // 2. Perform ? ValidateShadowRealmObject(O).
        if (args.Receiver.HeapObjectOrNull is not JSShadowRealm shadowRealm)
        {
            return isolate.ThrowTypeError(MessageTemplate.IncompatibleMethodReceiver);
        }

        // 3. If Type(sourceText) is not String, throw a TypeError exception.
        if (!sourceText.IsString) return isolate.ThrowTypeError(MessageTemplate.InvalidShadowRealmEvaluateSourceText);

        // 4. Let callerRealm be the current Realm Record.
        NativeContext callerContext = isolate.NativeContext;

        // 5. Let evalRealm be O.[[ShadowRealm]].
        NativeContext evalContext = shadowRealm.NativeContext;
        // 6. Return ? PerformShadowRealmEval(sourceText, callerRealm, evalRealm).

        // PerformShadowRealmEval
        // 1. Perform ? HostEnsureCanCompileStrings(callerRealm, evalRealm).
        // Run embedder pre-checks before executing the source code.
        JSString? validatedSource = BuiltinsGlobal.ValidateDynamicCompilationSource(isolate, evalContext, sourceText,
            out bool unhandledObject);
        if (unhandledObject) return isolate.ThrowTypeError(MessageTemplate.InvalidShadowRealmEvaluateSourceText);

        JSValue evalGlobalProxy = evalContext.GlobalProxyObject;
        JSValue result = default;
        JSValue exception = default;
        bool failed = false;
        bool isParseFailed = false;
        {
            // 8.-16. Push evalContext onto the execution context stack (SaveAndSwitchContext).
            Context? saved = isolate.Context;
            isolate.Context = evalContext;
            try
            {
                // 2. Perform the following substeps in an implementation-defined order,
                // possibly interleaving parsing and error detection:
                // 2a. Let script be ParseText(! StringToCodePoints(sourceText), Script).
                // 2b. If script is a List of errors, throw a SyntaxError exception.
                // ...
                // 7. If strictEval is true, set varEnv to lexEnv.
                JSFunction function;
                try
                {
                    if (validatedSource is null)
                    {
                        isolate.Throw(isolate.Factory.NewEvalError(MessageTemplate.CodeGenFromStrings,
                            BuiltinsGlobal.ErrorMessageForCodeGenerationFromStrings(isolate, evalContext)));
                    }
                    IDynamicFunctionCompiler compiler = isolate.DynamicFunctionCompiler ??
                        throw new InvalidOperationException("V8Sharp: no compiler registered (Isolate.DynamicFunctionCompiler)");
                    function = compiler.GetFunctionFromValidatedString(isolate, evalContext, validatedSource!,
                        ParseRestriction.NO_PARSE_RESTRICTION, Globals.kNoSourcePosition);
                }
                catch (JavaScriptException e)
                {
                    isParseFailed = true;
                    throw new ParseFailed(e.Value);
                }

                // 17. Let result be EvalDeclarationInstantiation(body, varEnv,
                // lexEnv, null, strictEval).
                // 18. If result.[[Type]] is normal, then
                // 18a. a. Set result to Completion(Evaluation of body).
                // 19. If result.[[Type]] is normal and result.[[Value]] is empty, then
                // 19a. Set result to NormalCompletion(undefined).
                result = Execution.Call(isolate, function, evalGlobalProxy, []);

                // 20. Suspend evalContext and remove it from the execution context stack.
                // 21. Resume the context that is now on the top of the execution context
                // stack as the running execution context. Done by the scope.
            }
            catch (ParseFailed e)
            {
                failed = true;
                exception = e.Value;
            }
            catch (JavaScriptException e)
            {
                failed = true;
                exception = e.Value;
            }
            finally
            {
                isolate.Context = saved;
            }
        }

        if (failed)
        {
            if (isParseFailed)
            {
                var errorObject = (JSObject)exception.Object;
                JSValue message = JSReceiver.GetDataProperty(isolate, errorObject, ReadOnlyRoots.message_string);
                return isolate.ReThrow(isolate.Factory.NewError(isolate.NativeContext.SyntaxErrorFunction, message.As<JSString>()));
            }
            // 22. If result.[[Type]] is not NORMAL, then
            // 22a. Let copiedError be CreateTypeErrorCopy(callerRealm,
            // result.[[Value]]). 22b. Return ThrowCompletion(copiedError).
            JSString str = ObjectOps.NoSideEffectsToString(isolate, exception);
            return isolate.Throw(ErrorUtils.ShadowRealmConstructTypeErrorCopy(isolate, exception,
                MessageTemplate.CallShadowRealmEvaluateThrew, [str]));
        }
        // 23. Return ? GetWrappedValue(callerRealm, result.[[Value]]).
        return JSWrappedFunction.GetWrappedValue(isolate, callerContext, result);
    }

    /// <summary>The compile error of PerformShadowRealmEval, kept apart from exceptions of the evaluation.</summary>
    sealed class ParseFailed(JSValue value) : Exception
    {
        public JSValue Value { get; } = value;
    }

    /// <summary>https://tc39.es/proposal-shadowrealm/#sec-shadowrealm.prototype.importvalue</summary>
    public static JSValue ShadowRealmPrototypeImportValue(Isolate isolate, in BuiltinArguments args)
    {
        const string kMethodName = "ShadowRealm.prototype.importValue";
        // 1. Let O be this value.
        // 2. Perform ? ValidateShadowRealmObject(O).
        if (args.Receiver.HeapObjectOrNull is not JSShadowRealm shadowRealm)
        {
            return isolate.ThrowTypeError(MessageTemplate.IncompatibleMethodReceiver,
                isolate.Factory.NewStringFromAsciiChecked(kMethodName), args.Receiver);
        }

        // 3. Let specifierString be ? ToString(specifier).
        JSString specifierString = ObjectOps.ToString(isolate, args.AtOrUndefined(1));
        // 4. Let exportNameString be ? ToString(exportName).
        JSString exportNameString = ObjectOps.ToString(isolate, args.AtOrUndefined(2));
        // 5. Let callerRealm be the current Realm Record.
        NativeContext callerContext = isolate.NativeContext;
        // 6. Let evalRealm be O.[[ShadowRealm]].
        // 7. Let evalContext be O.[[ExecutionContext]].
        NativeContext evalContext = shadowRealm.NativeContext;
        // 8. Return ? ShadowRealmImportValue(specifierString, exportNameString,
        // callerRealm, evalRealm, evalContext).
        return ImportValue(isolate, callerContext, evalContext, specifierString, exportNameString);
    }

    /// <summary>ImportValueFulfilledFunctionContextSlot.</summary>
    const int kEvalContextSlot = (int)Context.Field.MIN_CONTEXT_SLOTS;
    const int kSpecifierSlot = kEvalContextSlot + 1;
    const int kExportNameSlot = kEvalContextSlot + 2;
    const int kContextLength = kEvalContextSlot + 3;

    /// <summary>ShadowRealmBuiltinsAssembler::ImportValue (https://tc39.es/proposal-shadowrealm/#sec-shadowrealmimportvalue).</summary>
    static JSValue ImportValue(Isolate isolate, NativeContext callerContext, NativeContext evalContext, JSString specifier,
        JSString exportName)
    {
        // 1. Assert: evalContext is an execution context associated to a ShadowRealm
        // instance's [[ExecutionContext]].
        // 2. Let innerCapability be ! NewPromiseCapability(%Promise%).
        // 3. Let runningContext be the running execution context.
        // 4. If runningContext is not already suspended, suspend runningContext.
        // 5. Push evalContext onto the execution context stack; evalContext is now
        // the running execution context.
        // 6. Perform ! HostImportModuleDynamically(null, specifierString,
        // innerCapability).
        // 7. Suspend evalContext and remove it from the execution context stack.
        // 8. Resume the context that is now on the top of the execution context stack
        // as the running execution context.
        JSPromise innerCapability = ShadowRealmImportValueRuntime(isolate, evalContext, specifier);

        // 9. Let steps be the steps of an ExportGetter function as described below.
        // 10. Let onFulfilled be ! CreateBuiltinFunction(steps, 1, "", «
        // [[ExportNameString]] », callerRealm).
        // 11. Set onFulfilled.[[ExportNameString]] to exportNameString.
        Context functionContext = RootSharedFunctions.AllocateSyntheticFunctionContext(isolate, callerContext, kContextLength);
        functionContext[kEvalContextSlot] = evalContext;
        functionContext[kSpecifierSlot] = specifier;
        functionContext[kExportNameSlot] = exportName;
        JSFunction onFulfilled = RootSharedFunctions.AllocateRootFunctionWithContext(isolate,
            Builtin.ShadowRealmImportValueFulfilled, functionContext, callerContext);

        JSFunction onRejected = callerContext.ShadowRealmImportValueRejected;
        // 12. Let promiseCapability be ! NewPromiseCapability(%Promise%).
        Context? saved = isolate.Context;
        isolate.Context = callerContext;
        try
        {
            JSPromise promise = PromiseBuiltins.NewJSPromise(isolate);
            // 13. Return ! PerformPromiseThen(innerCapability.[[Promise]], onFulfilled,
            // callerRealm.[[Intrinsics]].[[%ThrowTypeError%]], promiseCapability).
            return PromiseBuiltins.PerformPromiseThen(isolate, innerCapability, onFulfilled, onRejected, promise);
        }
        finally
        {
            isolate.Context = saved;
        }
    }

    /// <summary>
    /// Runtime_ShadowRealmImportValue: Isolate::RunHostImportModuleDynamicallyCallback
    /// (no referrer, evaluation phase) in the eval realm.
    /// </summary>
    static JSPromise ShadowRealmImportValueRuntime(Isolate isolate, NativeContext evalContext, JSString specifier)
    {
        Context? saved = isolate.Context;
        isolate.Context = evalContext;
        try
        {
            JSValue innerCapability = V8Sharp.Runtime.RuntimeModules.RunHostImportModuleDynamicallyCallback(isolate, null, specifier,
                Ast.ModuleImportPhase.kEvaluation, false, JSValue.Undefined);
            return innerCapability.As<JSPromise>();
        }
        finally
        {
            isolate.Context = saved;
        }
    }

    /// <summary>
    /// ShadowRealmImportValueFulfilled: the ExportGetter of
    /// https://tc39.es/proposal-shadowrealm/#sec-shadowrealmimportvalue.
    /// </summary>
    public static JSValue ShadowRealmImportValueFulfilled(Isolate isolate, in BuiltinArguments args)
    {
        // An ExportGetter function is an anonymous built-in function with a
        // [[ExportNameString]] internal slot. When an ExportGetter function is called
        // with argument exports, it performs the following steps:
        // 8. Let realm be f.[[Realm]].
        Context context = args.Target.Context;
        var evalContext = context[kEvalContextSlot].As<NativeContext>();

        // 2. Let f be the active function object.
        // 3. Let string be f.[[ExportNameString]].
        // 4. Assert: Type(string) is String.
        var exportNameString = context[kExportNameSlot].As<JSString>();

        // 1. Assert: exports is a module namespace exotic object.
        // Spec issue: https://github.com/tc39/proposal-shadowrealm/issues/424
        var exports = args.AtOrUndefined(1).As<JSModuleNamespace>();

        // 5. Let hasOwn be ? HasOwnProperty(exports, string).
        // 6. If hasOwn is false, throw a TypeError exception.
        // 7. Let value be ? Get(exports, string).

        // The only exceptions thrown by Runtime::kGetModuleNamespaceExport are
        // either the export is not found or the module is not initialized.
        JSValue value;
        Context? saved = isolate.Context;
        isolate.Context = evalContext;
        try
        {
            value = V8Sharp.Runtime.RuntimeModules.GetModuleNamespaceExport(isolate, exports, exportNameString);
        }
        catch (JavaScriptException)
        {
            isolate.Context = saved;
            var specifierString = context[kSpecifierSlot].As<JSString>();
            return isolate.ThrowTypeError(MessageTemplate.UnresolvableExport, specifierString, exportNameString);
        }
        finally
        {
            isolate.Context = saved;
        }

        // 9. Return ? GetWrappedValue(realm, value).
        NativeContext callerContext = context.NativeContext;
        return JSWrappedFunction.GetWrappedValue(isolate, callerContext, value);
    }

    /// <summary>ShadowRealmImportValueRejected: ShadowRealmThrow(kImportShadowRealmRejected, exception).</summary>
    public static JSValue ShadowRealmImportValueRejected(Isolate isolate, in BuiltinArguments args) =>
        ShadowRealmThrow(isolate, MessageTemplate.ImportShadowRealmRejected, args.AtOrUndefined(1));

    /// <summary>Runtime_ShadowRealmThrow.</summary>
    public static JSValue ShadowRealmThrow(Isolate isolate, MessageTemplate messageId, JSValue value)
    {
        JSString str = ObjectOps.NoSideEffectsToString(isolate, value);
        return isolate.Throw(ErrorUtils.ShadowRealmConstructTypeErrorCopy(isolate, value, messageId, [str]));
    }
}
