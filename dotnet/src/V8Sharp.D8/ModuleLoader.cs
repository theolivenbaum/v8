// Port of d8's module loading (src/d8/d8.cc): ModuleEmbedderData (the module
// map and module-to-specifier map of a context), ModuleTypeFromImportSpecifier-
// AndAttributes, IsAbsolutePath / DirName / NormalizePath /
// NormalizeModuleSpecifier, ResolveModuleCallback, Shell::FetchModuleTree,
// Shell::JSONModuleEvaluationSteps, Shell::HostImportModuleDynamically /
// DoHostImportModuleDynamically / ChainDynamicImportPromise,
// Shell::HostInitializeImportMetaObject and Shell::ExecuteModule.
//
// The file system access is behind IModuleSourceProvider so that other
// embedders (the TestRunner) can resolve and read modules their own way; d8's
// own resolution is D8ModuleSourceProvider. Also ported: import defer
// (EvaluateForImportDefer), source phase imports (Shell::FetchModuleSource,
// ResolveModuleSourceCallback: without WebAssembly every module type throws
// d8's SyntaxError), text and bytes modules, HostCreateShadowRealmContext.
// Not ported: WebAssembly modules, bundles and the code cache.
using V8Sharp.Builtins;
using V8Sharp.Common;
using V8Sharp.Codegen;
using V8Sharp.Json;
using V8Sharp.Objects;
using V8Sharp.Roots;

namespace V8Sharp.D8;

/// <summary>d8's ModuleType.</summary>
public enum ModuleType { kJavaScript, kJSON, kText, kBytes, kInvalid }

/// <summary>
/// A module's canonical name (its absolute path or URL) and source text, or
/// the file's bytes for a bytes module.
/// </summary>
public readonly record struct ModuleSourceText(string Name, string Source, byte[]? Bytes = null);

/// <summary>A module load failure: thrown as an Error with this message (d8's ThrowError).</summary>
public sealed class ModuleLoadException(string message, string errorType = "Error") : Exception(message)
{
    public string ErrorType { get; } = errorType;
}

/// <summary>How an embedder finds modules.</summary>
public interface IModuleSourceProvider
{
    /// <summary>
    /// Resolves <paramref name="specifier"/> against the module or script named
    /// <paramref name="referrer"/> (empty for the main module or a script without
    /// a name) and reads it. Throws <see cref="ModuleLoadException"/>.
    /// </summary>
    ModuleSourceText Load(string specifier, string referrer, ModuleType type);
}

/// <summary>d8's module resolution and file reading.</summary>
public sealed class D8ModuleSourceProvider(string workingDirectory) : IModuleSourceProvider
{
    public const string kDataURLPrefix = "data:text/javascript,";

    readonly HashSet<string> _loaded = new(StringComparer.Ordinal);

    public static bool IsAbsolutePath(string path) => path.Length > 0 && path[0] == '/';

    /// <summary>DirName: the directory part of path, without the trailing '/'.</summary>
    public string DirName(string path)
    {
        if (!IsAbsolutePath(path)) return workingDirectory;
        int lastSlash = path.LastIndexOf('/');
        return path[..lastSlash];
    }

    /// <summary>
    /// NormalizePath: resolves path to an absolute path if necessary, and does
    /// some normalization (eliding references to the current directory and
    /// replacing backslashes with slashes).
    /// </summary>
    public static string NormalizePath(string path, string dirName)
    {
        string absolutePath = IsAbsolutePath(path) ? path : dirName + '/' + path;
        absolutePath = absolutePath.Replace('\\', '/');
        var segments = new List<string>();
        foreach (string segment in absolutePath.Split('/'))
        {
            if (segment == "..")
            {
                if (segments.Count > 0) segments.RemoveAt(segments.Count - 1);
            }
            else if (segment != "" && segment != ".")
            {
                segments.Add(segment);
            }
        }
        return "/" + string.Join('/', segments);
    }

    /// <summary>NormalizeModuleSpecifier: data URLs and http(s) URLs are returned unchanged.</summary>
    public static string NormalizeModuleSpecifier(string specifier, string dirName)
    {
        if (specifier.StartsWith(kDataURLPrefix, StringComparison.Ordinal) ||
            specifier.StartsWith("http://", StringComparison.Ordinal) || specifier.StartsWith("https://", StringComparison.Ordinal))
        {
            return specifier;
        }
        return NormalizePath(specifier, dirName);
    }

    public ModuleSourceText Load(string specifier, string referrer, ModuleType type)
    {
        string dirName = referrer.Length == 0 ? workingDirectory : DirName(NormalizeModuleSpecifier(referrer, workingDirectory));
        string moduleSpecifier = NormalizeModuleSpecifier(specifier, dirName);
        string importedBy = referrer.Length > 0 && _loaded.Contains(referrer) ? "\n    imported by " + referrer : "";
        string? sourceText = null;
        byte[]? bytes = null;
        if (moduleSpecifier.StartsWith(kDataURLPrefix, StringComparison.Ordinal))
        {
            sourceText = moduleSpecifier[kDataURLPrefix.Length..];
        }
        else if (IsAbsolutePath(moduleSpecifier))
        {
            try
            {
                if (type == ModuleType.kBytes)
                {
                    bytes = File.ReadAllBytes(moduleSpecifier);
                    sourceText = "";
                }
                else
                {
                    sourceText = File.ReadAllText(moduleSpecifier);
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
            }
        }
        else
        {
            // Loading modules is only allowed for local absolute paths.
            throw new ModuleLoadException("d8: Reading module from " + moduleSpecifier + " is not supported." + importedBy);
        }
        if (sourceText is null) throw new ModuleLoadException("d8: Error reading module from " + moduleSpecifier + importedBy);
        _loaded.Add(moduleSpecifier);
        return new ModuleSourceText(moduleSpecifier, sourceText, bytes);
    }
}

/// <summary>ModuleEmbedderData plus the module callbacks of d8, for one context.</summary>
public sealed class ModuleLoader
{
    readonly Isolate _isolate;
    readonly NativeContext _context;
    readonly IModuleSourceProvider _provider;

    /// <summary>module_map: (canonical name, type) to module.</summary>
    readonly Dictionary<(string, ModuleType), Module> _moduleMap = new();
    /// <summary>module_to_specifier_map.</summary>
    readonly Dictionary<Module, string> _moduleToSpecifierMap = new(ReferenceEqualityComparer.Instance);
    /// <summary>The resolution of each request of each module, recorded while fetching the tree.</summary>
    readonly Dictionary<(Module, string, ModuleType), Module> _resolved = new();

    /// <summary>The main module's name (ModuleEmbedderData::origin).</summary>
    public string Origin = "";

    public ModuleLoader(Isolate isolate, NativeContext context, IModuleSourceProvider provider)
    {
        _isolate = isolate;
        _context = context;
        _provider = provider;
        s_loaders.AddOrUpdate(context, this);
        // The callbacks are per isolate; they find the loader of the current context.
        isolate.HostImportModuleWithPhaseDynamicallyCallback = HostImportModuleWithPhaseDynamically;
        isolate.HostInitializeImportMetaObjectCallback = HostInitializeImportMetaObject;
    }

    /// <summary>
    /// Shell::HostCreateShadowRealmContext: a new context in the initiator's
    /// origin (same security token) with its own module map.
    /// </summary>
    public static NativeContext HostCreateShadowRealmContext(Isolate isolate, NativeContext initiatorContext)
    {
        NativeContext context = V8Sharp.Init.Bootstrapper.CreateEnvironment(isolate);
        // ShadowRealms are synchronously accessible and are always in the same origin
        // as the initiator context.
        context.SecurityToken = initiatorContext.SecurityToken;
        if (ForContext(initiatorContext) is { } initiatorData)
        {
            var shadowRealmData = new ModuleLoader(isolate, context, initiatorData._provider)
            {
                Origin = initiatorData.Origin,
            };
        }
        return context;
    }

    static readonly System.Runtime.CompilerServices.ConditionalWeakTable<NativeContext, ModuleLoader> s_loaders = new();

    static ModuleLoader? ForContext(NativeContext context) => s_loaders.TryGetValue(context, out ModuleLoader? l) ? l : null;

    /// <summary>ModuleEmbedderData::GetModuleSpecifier.</summary>
    public string GetModuleSpecifier(Module module) => _moduleToSpecifierMap[module];

    /// <summary>ModuleEmbedderData::ModuleTypeFromImportSpecifierAndAttributes.</summary>
    public static ModuleType ModuleTypeFromImportSpecifierAndAttributes(Isolate isolate, FixedArray importAttributes,
        bool hasPositions)
    {
        int kV8AssertionEntrySize = hasPositions ? 3 : 2;
        for (int i = 0; i < importAttributes.Length; i += kV8AssertionEntrySize)
        {
            string assertionKey = importAttributes.Get(i).As<JSString>().ToString();
            if (assertionKey == "type")
            {
                string assertionValue = importAttributes.Get(i + 1).As<JSString>().ToString();
                if (assertionValue == "json") return ModuleType.kJSON;
                if (assertionValue == "text" && isolate.Flags.js_import_text) return ModuleType.kText;
                if (assertionValue == "bytes" && isolate.Flags.js_import_bytes) return ModuleType.kBytes;
                // JSON, text and bytes are currently the only supported non-JS
                // types (WebAssembly is not ported).
                return ModuleType.kInvalid;
            }
        }
        // If no type is asserted, default to JS.
        return ModuleType.kJavaScript;
    }

    static JSValue ThrowError(Isolate isolate, string message, string errorType = "Error")
    {
        JSString text = isolate.Factory.NewStringFromUtf16(message);
        JSObject error = errorType switch
        {
            "TypeError" => isolate.Factory.NewError(isolate.NativeContext.TypeErrorFunction, text),
            "SyntaxError" => isolate.Factory.NewError(isolate.NativeContext.SyntaxErrorFunction, text),
            "RangeError" => isolate.Factory.NewError(isolate.NativeContext.RangeErrorFunction, text),
            "ReferenceError" => isolate.Factory.NewError(isolate.NativeContext.ReferenceErrorFunction, text),
            _ => isolate.Factory.NewError(isolate.NativeContext.ErrorFunction, text),
        };
        return isolate.Throw(error);
    }

    /// <summary>
    /// Shell::FetchModuleTree: loads the module <paramref name="specifier"/>
    /// requested by <paramref name="referrer"/> (null for a root), compiles it
    /// and, recursively, the modules it requests. <paramref name="source"/>
    /// supplies the text of a root given as source.
    /// </summary>
    public Module FetchModuleTree(Module? referrer, string specifier, ModuleType moduleType, ModuleSourceText? source = null)
    {
        ModuleSourceText text;
        if (source is { } given)
        {
            text = given;
        }
        else
        {
            try
            {
                text = _provider.Load(specifier, referrer is null ? Origin : GetModuleSpecifier(referrer), moduleType);
            }
            catch (ModuleLoadException e)
            {
                ThrowModuleLoadError(e);
                throw;
            }
        }
        if (_moduleMap.TryGetValue((text.Name, moduleType), out Module? existing)) return existing;

        string moduleSpecifier = text.Name;
        JSString resourceName = _isolate.Factory.NewStringFromUtf16(moduleSpecifier);
        Module module;
        if (moduleType == ModuleType.kJavaScript)
        {
            module = Compiler.CompileModule(_isolate, _isolate.Factory.NewStringFromUtf16(text.Source), resourceName);
        }
        else if (moduleType == ModuleType.kText)
        {
            module = SyntheticModule.New(_isolate, resourceName, [ReadOnlyRoots.default_string], TextModuleEvaluationSteps,
                _isolate.Factory.NewStringFromUtf16(text.Source));
        }
        else if (moduleType == ModuleType.kBytes)
        {
            byte[] bytes = text.Bytes ?? System.Text.Encoding.UTF8.GetBytes(text.Source);
            JSArrayBuffer? buffer = _isolate.Factory.NewJSArrayBufferAndBackingStore((ulong)bytes.Length);
            if (buffer is null) _isolate.ThrowRangeError(MessageTemplate.ArrayBufferAllocationFailed);
            if (bytes.Length > 0) bytes.CopyTo(buffer!.GetBackingStore()!.Buffer, 0);
            buffer!.MakeImmutable(_isolate);
            JSTypedArray uint8Array = _isolate.Factory.NewJSTypedArray(ElementsKind.UINT8_ELEMENTS, buffer, 0, (ulong)bytes.Length);
            module = SyntheticModule.New(_isolate, resourceName, [ReadOnlyRoots.default_string], BytesModuleEvaluationSteps,
                uint8Array);
        }
        else
        {
            JSValue parsedJson = JsonParser.Parse(_isolate, _isolate.Factory.NewStringFromUtf16(text.Source), JSValue.Undefined);
            module = SyntheticModule.New(_isolate, resourceName, [ReadOnlyRoots.default_string], JSONModuleEvaluationSteps,
                parsedJson);
        }

        _moduleMap.Add((moduleSpecifier, moduleType), module);
        _moduleToSpecifierMap.Add(module, moduleSpecifier);

        if (module is SourceTextModule sourceTextModule)
        {
            try
            {
                ModuleRequest[] moduleRequests = sourceTextModule.Info.ModuleRequests;
                for (int i = 0; i < moduleRequests.Length; ++i)
                {
                    ModuleRequest moduleRequest = moduleRequests[i];
                    string requestSpecifier = moduleRequest.Specifier.ToString();
                    ModuleType requestModuleType = ModuleTypeFromImportSpecifierAndAttributes(_isolate, moduleRequest.ImportAttributes, true);
                    if (requestModuleType == ModuleType.kInvalid)
                    {
                        ThrowError(_isolate, "Invalid module type was asserted");
                    }
                    if (moduleRequest.Phase == Ast.ModuleImportPhase.kSource)
                    {
                        FetchModuleSource(module, requestSpecifier, requestModuleType);
                        continue;
                    }
                    if (_resolved.ContainsKey((module, requestSpecifier, requestModuleType))) continue;
                    Module requested = FetchModuleTree(module, requestSpecifier, requestModuleType);
                    _resolved[(module, requestSpecifier, requestModuleType)] = requested;
                }
            }
            catch (JavaScriptException)
            {
                _moduleMap.Remove((moduleSpecifier, moduleType));
                _moduleToSpecifierMap.Remove(module);
                throw;
            }
        }
        return module;
    }

    JSValue ThrowModuleLoadError(ModuleLoadException e) => ThrowError(_isolate, e.Message, e.ErrorType);

    /// <summary>
    /// Shell::FetchModuleSource: the module source object of a source phase
    /// import. Only WebAssembly modules have one, and WebAssembly is not
    /// ported, so this reads the module (reporting d8's read errors) and then
    /// throws d8's SyntaxError, as d8 does for every other module type.
    /// </summary>
    JSReceiver FetchModuleSource(Module? referrer, string specifier, ModuleType moduleType)
    {
        try
        {
            _provider.Load(specifier, referrer is null ? Origin : GetModuleSpecifier(referrer), moduleType);
        }
        catch (ModuleLoadException e)
        {
            ThrowModuleLoadError(e);
        }
        // https://tc39.es/proposal-source-phase-imports/#table-abstract-methods-of-module-records
        // For Module Records that do not have a source representation,
        // GetModuleSource() must always return a throw completion whose [[Value]]
        // is a ReferenceError.
        ThrowError(_isolate, "Module source can not be imported for type", "SyntaxError");
        return null!;
    }

    /// <summary>ResolveModuleSourceCallback: unreachable without module sources (FetchModuleSource throws).</summary>
    JSReceiver ResolveModuleSourceCallback(Isolate isolate, JSString specifier, FixedArray importAttributes, Module referrer) =>
        throw new InvalidOperationException("V8Sharp: no module source for " + specifier);

    /// <summary>Shell::JSONModuleEvaluationSteps.</summary>
    static JSPromise JSONModuleEvaluationSteps(Isolate isolate, SyntheticModule module)
    {
        SyntheticModule.SetExport(isolate, module, ReadOnlyRoots.default_string, module.HostDefinedOptions);
        JSPromise resolver = PromiseBuiltins.NewJSPromise(isolate);
        PromiseBuiltins.ResolvePromise(isolate, resolver, JSValue.Undefined);
        return resolver;
    }

    /// <summary>Shell::TextModuleEvaluationSteps.</summary>
    static JSPromise TextModuleEvaluationSteps(Isolate isolate, SyntheticModule module) =>
        JSONModuleEvaluationSteps(isolate, module);

    /// <summary>Shell::BytesModuleEvaluationSteps.</summary>
    static JSPromise BytesModuleEvaluationSteps(Isolate isolate, SyntheticModule module) =>
        JSONModuleEvaluationSteps(isolate, module);

    /// <summary>ResolveModuleCallback.</summary>
    Module ResolveModuleCallback(Isolate isolate, JSString specifier, FixedArray importAttributes, Module referrer)
    {
        ModuleType moduleType = ModuleTypeFromImportSpecifierAndAttributes(isolate, importAttributes, true);
        return _resolved[(referrer, specifier.ToString(), moduleType)];
    }

    /// <summary>Instantiates <paramref name="module"/> with this loader's resolution (InstantiateModule).</summary>
    public void Instantiate(Module module) => Module.Instantiate(_isolate, module, ResolveModuleCallback, ResolveModuleSourceCallback);

    /// <summary>Shell::HostInitializeImportMetaObject: import.meta.url is the module's specifier.</summary>
    static void HostInitializeImportMetaObject(Isolate isolate, SourceTextModule module, JSObject meta)
    {
        ModuleLoader? loader = ForContext(isolate.NativeContext);
        if (loader is null) return;
        string specifier = loader.GetModuleSpecifier(module);
        JSReceiver.CreateDataProperty(isolate, meta, new PropertyKey(isolate, isolate.Factory.InternalizeString("url")),
            isolate.Factory.NewStringFromUtf16(specifier), ShouldThrow.ThrowOnError);
    }

    sealed record DynamicImportData(ModuleLoader Loader, JSValue Referrer, JSString Specifier, Ast.ModuleImportPhase Phase,
        FixedArray ImportAttributes, JSPromise Resolver);

    /// <summary>Shell::HostImportModuleWithPhaseDynamically.</summary>
    static JSPromise HostImportModuleWithPhaseDynamically(Isolate isolate, JSValue resourceName, JSString specifier,
        Ast.ModuleImportPhase phase, FixedArray importAttributes)
    {
        JSPromise resolver = PromiseBuiltins.NewJSPromise(isolate);
        ModuleLoader? loader = ForContext(isolate.NativeContext);
        if (loader is null)
        {
            // The context is detached, so we reject the import.
            PromiseBuiltins.RejectPromise(isolate, resolver,
                isolate.Factory.NewError(isolate.NativeContext.ErrorFunction,
                    isolate.Factory.NewStringFromAsciiChecked("Cannot import module from an inactive context")), true);
            return resolver;
        }
        var data = new DynamicImportData(loader, resourceName, specifier, phase, importAttributes, resolver);
        (isolate.NativeContext.MicrotaskQueue ?? isolate.DefaultMicrotaskQueue).EnqueueMicrotask(DoHostImportModuleDynamically, data);
        return resolver;
    }

    /// <summary>Shell::DoHostImportModuleDynamically.</summary>
    static void DoHostImportModuleDynamically(Isolate isolate, object? rawData)
    {
        var importData = (DynamicImportData)rawData!;
        ModuleLoader loader = importData.Loader;
        JSPromise resolver = importData.Resolver;
        NativeContext realm = loader._context;
        using (isolate.EnterContext(realm))
        {
            JSPromise resultPromise;
            JSValue namespaceOrSource;
            try
            {
                ModuleType moduleType = ModuleTypeFromImportSpecifierAndAttributes(isolate, importData.ImportAttributes, false);
                if (moduleType == ModuleType.kInvalid) ThrowError(isolate, "Invalid module type was asserted");

                string sourceUrl = importData.Referrer.HeapObjectOrNull is JSString referrer ? referrer.ToString() : loader.Origin;
                if (importData.Phase == Ast.ModuleImportPhase.kSource)
                {
                    try
                    {
                        loader._provider.Load(importData.Specifier.ToString(), sourceUrl, moduleType);
                    }
                    catch (ModuleLoadException e)
                    {
                        loader.ThrowModuleLoadError(e);
                    }
                    // FetchModuleSource: only WebAssembly modules have a source.
                    ThrowError(isolate, "Module source can not be imported for type", "SyntaxError");
                }

                ModuleSourceText text;
                try
                {
                    text = loader._provider.Load(importData.Specifier.ToString(), sourceUrl, moduleType);
                }
                catch (ModuleLoadException e)
                {
                    loader.ThrowModuleLoadError(e);
                    return;
                }
                Module rootModule = loader._moduleMap.TryGetValue((text.Name, moduleType), out Module? found)
                    ? found
                    : loader.FetchModuleTree(null, text.Name, moduleType, text);
                loader.Instantiate(rootModule);
                if (importData.Phase == Ast.ModuleImportPhase.kEvaluation)
                {
                    resultPromise = Module.Evaluate(isolate, rootModule);
                    namespaceOrSource = Module.GetModuleNamespace(isolate, rootModule);
                }
                else
                {
                    resultPromise = Module.EvaluateForImportDefer(isolate, rootModule);
                    namespaceOrSource = Module.GetModuleNamespace(isolate, rootModule, Ast.ModuleImportPhase.kDefer);
                }
            }
            catch (JavaScriptException e)
            {
                // RejectPromiseIfExecutionIsNotTerminating.
                PromiseBuiltins.RejectPromise(isolate, resolver, e.Value, true);
                return;
            }

            // ChainDynamicImportPromise: resolve with the namespace once the
            // evaluation promise settles, or reject with its reason.
            JSValue ns = namespaceOrSource;
            JSFunction onFulfilled = CreateFunction(isolate, realm, (Isolate i, in BuiltinArguments args) =>
            {
                PromiseBuiltins.ResolvePromise(i, resolver, ns);
                return JSValue.Undefined;
            }, 0);
            JSFunction onRejected = CreateFunction(isolate, realm, (Isolate i, in BuiltinArguments args) =>
            {
                PromiseBuiltins.RejectPromise(i, resolver, args.AtOrUndefined(1), true);
                return JSValue.Undefined;
            }, 1);
            PromiseBuiltins.PerformPromiseThen(isolate, resultPromise, onFulfilled, onRejected, JSValue.Undefined);
        }
    }

    static JSFunction CreateFunction(Isolate isolate, NativeContext context, BuiltinFunction callback, int length)
    {
        var data = new FunctionTemplateInfo(callback) { Length = length };
        SharedFunctionInfo info = isolate.Factory.NewSharedFunctionInfo(ReadOnlyRoots.empty_string, data,
            Builtin.HandleApiCallOrConstruct, length, false);
        info.BuiltinId = Builtin.HandleApiCallOrConstruct;
        info.LanguageMode = LanguageMode.Strict;
        info.Native = true;
        info.UpdateFunctionMapIndex();
        return isolate.Factory.NewFunction(info, context, context.StrictFunctionWithoutPrototypeMap);
    }

    /// <summary>
    /// Shell::ExecuteModule up to the evaluation: fetches, instantiates and
    /// evaluates the main module; returns its top-level promise. Throws the
    /// JavaScriptException of a load, compile or link failure.
    /// </summary>
    public (SourceTextModule Module, JSPromise Promise) StartModule(string specifier, ModuleSourceText? source = null)
    {
        Module rootModule = FetchModuleTree(null, specifier, ModuleType.kJavaScript, source);
        Origin = GetModuleSpecifier(rootModule);
        Instantiate(rootModule);
        JSPromise promise = Module.Evaluate(_isolate, rootModule);
        return ((SourceTextModule)rootModule, promise);
    }
}
