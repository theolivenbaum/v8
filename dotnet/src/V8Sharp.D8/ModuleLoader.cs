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
// own resolution is D8ModuleSourceProvider. Not ported: source phase imports,
// import defer, WebAssembly, text and bytes modules (behind flags), bundles and
// the code cache.
using V8Sharp.Builtins;
using V8Sharp.Common;
using V8Sharp.Codegen;
using V8Sharp.Json;
using V8Sharp.Objects;
using V8Sharp.Roots;

namespace V8Sharp.D8;

/// <summary>d8's ModuleType.</summary>
public enum ModuleType { kJavaScript, kJSON, kText, kInvalid }

/// <summary>A module's canonical name (its absolute path or URL) and source text.</summary>
public readonly record struct ModuleSourceText(string Name, string Source);

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
        if (moduleSpecifier.StartsWith(kDataURLPrefix, StringComparison.Ordinal))
        {
            sourceText = moduleSpecifier[kDataURLPrefix.Length..];
        }
        else if (IsAbsolutePath(moduleSpecifier))
        {
            try
            {
                sourceText = File.ReadAllText(moduleSpecifier);
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
        return new ModuleSourceText(moduleSpecifier, sourceText);
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
        isolate.HostImportModuleDynamicallyCallback = HostImportModuleDynamically;
        isolate.HostInitializeImportMetaObjectCallback = HostInitializeImportMetaObject;
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
                // JSON and text are currently the only supported non-JS types
                // (bytes and WebAssembly are not ported).
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

    /// <summary>ResolveModuleCallback.</summary>
    Module ResolveModuleCallback(Isolate isolate, JSString specifier, FixedArray importAttributes, Module referrer)
    {
        ModuleType moduleType = ModuleTypeFromImportSpecifierAndAttributes(isolate, importAttributes, true);
        return _resolved[(referrer, specifier.ToString(), moduleType)];
    }

    /// <summary>Instantiates <paramref name="module"/> with this loader's resolution (InstantiateModule).</summary>
    public void Instantiate(Module module) => Module.Instantiate(_isolate, module, ResolveModuleCallback);

    /// <summary>Shell::HostInitializeImportMetaObject: import.meta.url is the module's specifier.</summary>
    static void HostInitializeImportMetaObject(Isolate isolate, SourceTextModule module, JSObject meta)
    {
        ModuleLoader? loader = ForContext(isolate.NativeContext);
        if (loader is null) return;
        string specifier = loader.GetModuleSpecifier(module);
        JSReceiver.CreateDataProperty(isolate, meta, new PropertyKey(isolate, isolate.Factory.InternalizeString("url")),
            isolate.Factory.NewStringFromUtf16(specifier), ShouldThrow.ThrowOnError);
    }

    sealed record DynamicImportData(ModuleLoader Loader, JSValue Referrer, JSString Specifier, FixedArray ImportAttributes,
        JSPromise Resolver);

    /// <summary>Shell::HostImportModuleDynamically.</summary>
    static JSPromise HostImportModuleDynamically(Isolate isolate, JSValue resourceName, JSString specifier,
        FixedArray importAttributes)
    {
        JSPromise resolver = PromiseBuiltins.NewJSPromise(isolate);
        ModuleLoader? loader = ForContext(isolate.NativeContext);
        if (loader is null)
        {
            PromiseBuiltins.RejectPromise(isolate, resolver,
                isolate.Factory.NewError(isolate.NativeContext.ErrorFunction,
                    isolate.Factory.NewStringFromAsciiChecked("Cannot import module from an inactive context")), true);
            return resolver;
        }
        var data = new DynamicImportData(loader, resourceName, specifier, importAttributes, resolver);
        isolate.DefaultMicrotaskQueue.EnqueueMicrotask(DoHostImportModuleDynamically, data);
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
            JSPromise? resultPromise = null;
            JSValue namespaceObject = JSValue.Undefined;
            try
            {
                ModuleType moduleType = ModuleTypeFromImportSpecifierAndAttributes(isolate, importData.ImportAttributes, false);
                if (moduleType == ModuleType.kInvalid) ThrowError(isolate, "Invalid module type was asserted");

                string sourceUrl = importData.Referrer.HeapObjectOrNull is JSString referrer ? referrer.ToString() : loader.Origin;
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
                resultPromise = Module.Evaluate(isolate, rootModule);
                namespaceObject = Module.GetModuleNamespace(isolate, rootModule);
            }
            catch (JavaScriptException e)
            {
                // RejectPromiseIfExecutionIsNotTerminating.
                PromiseBuiltins.RejectPromise(isolate, resolver, e.Value, true);
                return;
            }

            // ChainDynamicImportPromise: resolve with the namespace once the
            // evaluation promise settles, or reject with its reason.
            JSValue ns = namespaceObject;
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
