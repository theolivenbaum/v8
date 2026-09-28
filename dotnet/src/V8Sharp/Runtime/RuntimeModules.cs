// Port of src/runtime/runtime-module.cc (DynamicImportCall, GetModuleNamespace,
// GetImportMetaObject, GetModuleNamespaceExport), Runtime_DeclareModuleExports
// (runtime-scopes.cc), Isolate::RunHostImportModuleDynamicallyCallback and
// GetImportAttributesFromArgument (src/execution/isolate.cc), and the module
// variable access of SourceTextModule::LoadVariable / StoreVariable used by
// LdaModuleVariable, StaModuleVariable and the lookup slots.
namespace V8Sharp.Runtime;

public static class RuntimeModules
{
    /// <summary>SourceTextModuleDescriptor::GetCellIndexKind: positive indices are exports.</summary>
    public static bool GetCellIndexKindIsExport(int cellIndex) => cellIndex > 0;

    /// <summary>LdaModuleVariable: SourceTextModule::LoadVariable of the context's module.</summary>
    public static JSValue LoadVariable(Isolate isolate, Context moduleContext, int cellIndex) =>
        SourceTextModule.LoadVariable(isolate, moduleContext.Extension.As<SourceTextModule>(), cellIndex);

    /// <summary>StaModuleVariable: SourceTextModule::StoreVariable of the context's module.</summary>
    public static void StoreVariable(Isolate isolate, Context moduleContext, int cellIndex, JSValue value) =>
        SourceTextModule.StoreVariable(moduleContext.Extension.As<SourceTextModule>(), cellIndex, value);

    /// <summary>SourceTextModule::LoadVariable for a module found by a lookup slot.</summary>
    public static JSValue LoadVariable(Isolate isolate, HeapObject module, int cellIndex) =>
        SourceTextModule.LoadVariable(isolate, (SourceTextModule)module, cellIndex);

    /// <summary>SourceTextModule::StoreVariable for a module found by a lookup slot.</summary>
    public static void StoreVariable(Isolate isolate, HeapObject module, int cellIndex, JSValue value) =>
        SourceTextModule.StoreVariable((SourceTextModule)module, cellIndex, value);

    /// <summary>Runtime_DeclareModuleExports.</summary>
    public static JSValue DeclareModuleExports(Isolate isolate, FixedArray declarations, JSFunction closure)
    {
        ClosureFeedbackCellArray closureFeedbackCellArray = JSFunctionFeedback.GetClosureFeedbackCellArray(closure);
        Context context = isolate.Context!;
        Debug.Assert(context.IsModuleContext);
        Cell?[] exports = context.Extension.As<SourceTextModule>().RegularExports;

        int length = declarations.Length;
        for (int i = 0; i < length; i++)
        {
            JSValue decl = declarations.Get(i);
            int index;
            JSValue value;
            if (decl.IsSmi)
            {
                index = (int)decl.Number;
                value = JSValue.TheHole;
            }
            else
            {
                var sfi = decl.As<SharedFunctionInfo>();
                int feedbackIndex = (int)declarations.Get(++i).Number;
                index = (int)declarations.Get(++i).Number;
                FeedbackCell feedbackCell = closureFeedbackCellArray.Get(feedbackIndex);
                value = RuntimeClosures.NewClosure(isolate, sfi, context, feedbackCell);
            }
            exports[index - 1]!.Value = value;
        }
        return JSValue.Undefined;
    }

    /// <summary>Runtime_GetModuleNamespace.</summary>
    public static JSValue GetModuleNamespace(Isolate isolate, int moduleRequest)
    {
        var module = (SourceTextModule)isolate.Context!.Module();
        return SourceTextModule.GetModuleNamespace(isolate, module, moduleRequest);
    }

    /// <summary>Runtime_GetImportMetaObject.</summary>
    public static JSValue GetImportMetaObject(Isolate isolate)
    {
        var module = (SourceTextModule)isolate.Context!.Module();
        return SourceTextModule.GetImportMeta(isolate, module);
    }

    /// <summary>Runtime_GetModuleNamespaceExport.</summary>
    public static JSValue GetModuleNamespaceExport(Isolate isolate, JSModuleNamespace moduleNamespace, JSString name)
    {
        if (!moduleNamespace.HasExport(isolate, name))
        {
            return isolate.Throw(isolate.Factory.NewReferenceError(MessageTemplate.NotDefined, name));
        }
        return moduleNamespace.GetExport(isolate, name);
    }

    /// <summary>Runtime_DynamicImportCall.</summary>
    public static JSValue DynamicImportCall(Isolate isolate, ReadOnlySpan<JSValue> args)
    {
        var function = args[0].As<JSFunction>();
        JSValue specifier = args[1];
        var phase = (Ast.ModuleImportPhase)(int)args[2].Number;
        JSValue importOptions = args.Length == 4 ? args[3] : default;
        bool hasImportOptions = args.Length == 4;

        Script referrerScript = function.Shared.Script!.GetEvalOrigin();
        return RunHostImportModuleDynamicallyCallback(isolate, referrerScript, specifier, phase, hasImportOptions,
            importOptions);
    }

    /// <summary>NewRejectedPromise (isolate.cc).</summary>
    static JSPromise NewRejectedPromise(Isolate isolate, JSValue exception)
    {
        JSPromise promise = PromiseBuiltins.NewJSPromise(isolate);
        PromiseBuiltins.RejectPromise(isolate, promise, exception, true);
        return promise;
    }

    /// <summary>Isolate::RunHostImportModuleDynamicallyCallback.</summary>
    public static JSValue RunHostImportModuleDynamicallyCallback(Isolate isolate, Script? referrer, JSValue specifier,
        Ast.ModuleImportPhase phase, bool hasImportOptions, JSValue importOptionsArgument)
    {
        if (phase != Ast.ModuleImportPhase.kEvaluation || isolate.HostImportModuleDynamicallyCallback is null)
        {
            JSObject exception = isolate.Factory.NewError(isolate.NativeContext.ErrorFunction, MessageTemplate.Unsupported, []);
            return NewRejectedPromise(isolate, exception);
        }

        JSString specifierStr;
        try
        {
            specifierStr = ObjectOps.ToString(isolate, specifier);
        }
        catch (JavaScriptException e)
        {
            return NewRejectedPromise(isolate, e.Value);
        }

        FixedArray importAttributesArray;
        try
        {
            importAttributesArray = GetImportAttributesFromArgument(isolate, hasImportOptions, importOptionsArgument);
        }
        catch (JavaScriptException e)
        {
            return NewRejectedPromise(isolate, e.Value);
        }

        JSValue resourceName = referrer is null ? JSValue.Null : referrer.Name;
        return isolate.HostImportModuleDynamicallyCallback(isolate, resourceName, specifierStr, importAttributesArray);
    }

    /// <summary>Isolate::GetImportAttributesFromArgument: [key1, value1, key2, value2, ...].</summary>
    static FixedArray GetImportAttributesFromArgument(Isolate isolate, bool hasImportOptions, JSValue importOptionsArgument)
    {
        FixedArray importAttributesArray = FixedArray.Empty;
        if (!hasImportOptions || importOptionsArgument.IsUndefined) return importAttributesArray;

        if (!importOptionsArgument.IsJSReceiver)
        {
            isolate.Throw(isolate.Factory.NewTypeError(MessageTemplate.NonObjectImportArgument));
        }
        var importOptionsArgumentReceiver = importOptionsArgument.As<JSReceiver>();

        JSValue importAttributesObject = ObjectOps.GetProperty(isolate, importOptionsArgumentReceiver, ReadOnlyRoots.with_string);

        // If there is no 'with' option in the options bag, it's not an error. Just do
        // the import() as if no attributes were provided.
        if (importAttributesObject.IsUndefined) return importAttributesArray;

        if (!importAttributesObject.IsJSReceiver)
        {
            isolate.Throw(isolate.Factory.NewTypeError(MessageTemplate.NonObjectAttributesOption));
        }
        var importAttributesObjectReceiver = importAttributesObject.As<JSReceiver>();

        FixedArray attributeKeys = KeyAccumulator.GetKeys(isolate, importAttributesObjectReceiver, KeyCollectionMode.OwnOnly,
            PropertyFilter.ENUMERABLE_STRINGS, GetKeysConversion.ConvertToString);

        bool hasNonStringAttribute = false;

        // The attributes will be passed to the host in the form: [key1,
        // value1, key2, value2, ...].
        const int kAttributeEntrySizeForDynamicImport = 2;
        int attributesKeysLen = attributeKeys.Length;
        importAttributesArray = new FixedArray(attributesKeysLen * kAttributeEntrySizeForDynamicImport);
        for (int i = 0; i < attributesKeysLen; i++)
        {
            var attributeKey = attributeKeys.Get(i).As<JSString>();
            JSValue attributeValue = ObjectOps.GetPropertyOrElement(isolate, importAttributesObjectReceiver, attributeKey);
            if (!attributeValue.IsString) hasNonStringAttribute = true;
            importAttributesArray.Set(i * kAttributeEntrySizeForDynamicImport, attributeKey);
            importAttributesArray.Set(i * kAttributeEntrySizeForDynamicImport + 1, attributeValue);
        }

        if (hasNonStringAttribute)
        {
            isolate.Throw(isolate.Factory.NewTypeError(MessageTemplate.NonStringImportAttributeValue));
        }
        return importAttributesArray;
    }
}
