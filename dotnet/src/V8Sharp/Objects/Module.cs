// Port of src/objects/module.{h,cc} (Module: status, Instantiate, Evaluate,
// ResolveExport, GetModuleNamespace, RecordError, ResetGraph, IsGraphAsync),
// src/objects/source-text-module.{h,cc} (SourceTextModule, ModuleRequest,
// SourceTextModuleInfo, SourceTextModuleInfoEntry), src/objects/synthetic-module.cc,
// module.tq / source-text-module.tq, SourceTextModuleInfo::New
// (src/objects/scope-info.cc), the Serialize methods of src/ast/modules.cc,
// Factory::NewSourceTextModule / NewSyntheticModule, and the
// CallAsyncModuleFulfilled / CallAsyncModuleRejected / AsyncModuleEvaluate
// builtins (builtins-async-module.cc, builtins-generator-gen.cc).
//
// Layout: V8 keeps the regular exports and imports, the requested modules and
// the SourceTextModuleInfo parts in FixedArrays; V8Sharp keeps them in typed
// arrays of the same shape and order. The embedder API (v8::Module::
// InstantiateModule's ResolveModuleCallback, SyntheticModuleEvaluationSteps)
// is a set of delegates. Also: import defer (JSDeferredModuleNamespace,
// GatherAsynchronousTransitiveDependencies, ReadyForSyncExecution and
// v8::Module::EvaluateForImportDefer of src/api/api.cc) and source phase
// imports (the module source objects the embedder returns). Not ported: the
// module status tracing and the esm_* counters.
using V8Sharp.Ast;
using V8Sharp.Interpreter;

namespace V8Sharp.Objects
{

/// <summary>
/// The embedder's module resolution (v8::Module::ResolveModuleCallback):
/// returns the module <paramref name="specifier"/> names from
/// <paramref name="referrer"/>, or throws a JavaScriptException.
/// </summary>
public delegate Module ResolveModuleCallback(Isolate isolate, JSString specifier, FixedArray importAttributes, Module referrer);

/// <summary>
/// The embedder's module source resolution (v8::Module::ResolveSourceCallback):
/// returns the module source object of a source phase import, or throws a
/// JavaScriptException.
/// </summary>
public delegate JSReceiver ResolveSourceCallback(Isolate isolate, JSString specifier, FixedArray importAttributes,
    Module referrer);

/// <summary>v8::Module::SyntheticModuleEvaluationSteps: sets the exports and returns a promise.</summary>
public delegate JSPromise SyntheticModuleEvaluationSteps(Isolate isolate, SyntheticModule module);

/// <summary>V8's Module: the base of SourceTextModule and SyntheticModule (Abstract Module Record).</summary>
public abstract class Module(InstanceType type) : HeapObject(type)
{
    public enum Status
    {
        // Order matters!
        kUnlinked,
        kPreLinking,
        kLinking,
        kLinked,
        kEvaluating,
        kEvaluatingAsync,
        kEvaluated,
        kErrored,
    }

    /// <summary>The complete export table, mapping an export name to its cell.</summary>
    public ObjectHashTable Exports = ObjectHashTable.New(0);
    /// <summary>Hash for this object (a random non-zero Smi).</summary>
    public int Hash;
    public Status ModuleStatus = Status.kUnlinked;
    /// <summary>The Cell holding the namespace object, or null.</summary>
    public Cell? ModuleNamespaceCell;
    /// <summary>The Cell holding the deferred namespace object (import defer), or null.</summary>
    public Cell? DeferredModuleNamespaceCell;
    /// <summary>The exception in the case the status is kErrored (the hole otherwise).</summary>
    public JSValue Exception = JSValue.TheHole;
    /// <summary>The top level promise capability of this module (only for cycle roots).</summary>
    public JSPromise? TopLevelCapability;

    /// <summary>Module::SetStatus.</summary>
    public void SetStatus(Status newStatus)
    {
        Debug.Assert(ModuleStatus <= newStatus);
        Debug.Assert(newStatus != Status.kErrored);
        ModuleStatus = newStatus;
    }

    /// <summary>
    /// Module::RecordError. A null <paramref name="error"/> is a termination
    /// (V8 stores null for exceptions not catchable by JavaScript).
    /// </summary>
    public void RecordError(Isolate isolate, JSValue? error)
    {
        if (this is SourceTextModule self)
        {
            // Revert to minimal SFI in case we have already been instantiating or
            // evaluating.
            self.Code = self.GetSharedFunctionInfo();
        }
        ModuleStatus = Status.kErrored;
        // v8::TryCatch uses `null` for termination exceptions.
        Exception = error ?? JSValue.Null;
    }

    /// <summary>Module::GetException.</summary>
    public JSValue GetException()
    {
        Debug.Assert(ModuleStatus == Status.kErrored);
        return Exception;
    }

    /// <summary>
    /// The register stack the C++ frames of module linking and evaluation
    /// (Module::Evaluate, SourceTextModule::Evaluate with its TryCatch,
    /// InnerModuleEvaluation) take on V8's machine stack.
    /// </summary>
    const int kModuleFrameSlots = 32;

    /// <summary>
    /// STACK_CHECK of the module code. V8 checks the machine stack, which the
    /// C++ frames of linking and evaluation use; V8Sharp runs them on the .NET
    /// stack and limits JavaScript recursion on the register stack, so it also
    /// requires kModuleFrameSlots of register stack (deviations.md, Stack
    /// limit), which makes a deferred module evaluated at the recursion limit
    /// fail with the RangeError as in V8.
    /// </summary>
    internal static void StackCheck(Isolate isolate)
    {
        if (StackGuard.HasOverflowed() || isolate.RegisterStackTop + kModuleFrameSlots > isolate.RegisterStackLimit)
        {
            isolate.StackOverflow();
        }
    }

    /// <summary>Module::ResetGraph.</summary>
    public static void ResetGraph(Isolate isolate, Module module)
    {
        Debug.Assert(module.ModuleStatus != Status.kEvaluating);
        if (module.ModuleStatus != Status.kPreLinking && module.ModuleStatus != Status.kLinking) return;

        HeapObject?[]? requestedModules = (module as SourceTextModule)?.RequestedModules;
        Reset(isolate, module);
        if (requestedModules is null) return;
        for (int i = 0; i < requestedModules.Length; ++i)
        {
            if (requestedModules[i] is Module descendant) ResetGraph(isolate, descendant);
        }
    }

    /// <summary>Module::Reset.</summary>
    static void Reset(Isolate isolate, Module module)
    {
        Debug.Assert(module.ModuleStatus is Status.kPreLinking or Status.kLinking);
        Debug.Assert(module.Exception.IsTheHole);
        // The namespace cells could have been created during ResolveExport for
        // 'export * as ns', reset them here.
        module.ModuleNamespaceCell = null;
        module.DeferredModuleNamespaceCell = null;
        int exportCount = module is SourceTextModule stm ? stm.RegularExports.Length : ((SyntheticModule)module).ExportNames.Length;
        ObjectHashTable exports = ObjectHashTable.New(exportCount);
        if (module is SourceTextModule sourceTextModule) SourceTextModule.Reset(isolate, sourceTextModule);
        module.Exports = exports;
        module.ModuleStatus = Status.kUnlinked;
    }

    /// <summary>Module::ResolveExport.</summary>
    internal static Cell? ResolveExport(Isolate isolate, Module module, JSString? moduleSpecifier, JSString exportName,
        MessageLocation loc, bool mustResolve, ResolveSet resolveSet)
    {
        Debug.Assert(module.ModuleStatus >= Status.kPreLinking);
        Debug.Assert(module.ModuleStatus != Status.kEvaluating);
        if (module is SourceTextModule sourceTextModule)
        {
            return SourceTextModule.ResolveExport(isolate, sourceTextModule, moduleSpecifier, exportName, loc, mustResolve,
                resolveSet);
        }
        return SyntheticModule.ResolveExport(isolate, (SyntheticModule)module, moduleSpecifier, exportName, loc, mustResolve);
    }

    /// <summary>
    /// Module::Instantiate (ModuleDeclarationInstantiation). Throws the
    /// JavaScriptException of a failed resolution or link after resetting the graph.
    /// </summary>
    public static void Instantiate(Isolate isolate, Module module, ResolveModuleCallback callback,
        ResolveSourceCallback? sourceCallback = null)
    {
        try
        {
            PrepareInstantiate(isolate, module, callback, sourceCallback);
        }
        catch (Exception e) when (e is JavaScriptException or TerminationException)
        {
            ResetGraph(isolate, module);
            Debug.Assert(module.ModuleStatus == Status.kUnlinked);
            throw;
        }
        var stack = new List<SourceTextModule>();
        int dfsIndex = 0;
        try
        {
            FinishInstantiate(isolate, module, stack, ref dfsIndex);
        }
        catch (Exception e) when (e is JavaScriptException or TerminationException)
        {
            ResetGraph(isolate, module);
            Debug.Assert(module.ModuleStatus == Status.kUnlinked);
            throw;
        }
        Debug.Assert(module.ModuleStatus >= Status.kLinked);
        Debug.Assert(stack.Count == 0);
    }

    /// <summary>Module::PrepareInstantiate.</summary>
    internal static void PrepareInstantiate(Isolate isolate, Module module, ResolveModuleCallback callback,
        ResolveSourceCallback? sourceCallback)
    {
        Debug.Assert(module.ModuleStatus != Status.kEvaluating);
        Debug.Assert(module.ModuleStatus != Status.kLinking);
        if (module.ModuleStatus >= Status.kPreLinking) return;
        module.SetStatus(Status.kPreLinking);
        StackCheck(isolate);

        if (module is SourceTextModule sourceTextModule)
        {
            SourceTextModule.PrepareInstantiate(isolate, sourceTextModule, callback, sourceCallback);
        }
        else
        {
            SyntheticModule.PrepareInstantiate(isolate, (SyntheticModule)module);
        }
    }

    /// <summary>Module::FinishInstantiate.</summary>
    internal static void FinishInstantiate(Isolate isolate, Module module, List<SourceTextModule> stack, ref int dfsIndex)
    {
        Debug.Assert(module.ModuleStatus != Status.kEvaluating);
        if (module.ModuleStatus >= Status.kLinking) return;
        Debug.Assert(module.ModuleStatus == Status.kPreLinking);
        StackCheck(isolate);

        if (module is SourceTextModule sourceTextModule)
        {
            SourceTextModule.FinishInstantiate(isolate, sourceTextModule, stack, ref dfsIndex);
        }
        else
        {
            SyntheticModule.FinishInstantiate(isolate, (SyntheticModule)module);
        }
    }

    /// <summary>
    /// Module::Evaluate (ModuleEvaluation): the module's top-level capability
    /// promise. Throws only on termination.
    /// </summary>
    public static JSPromise Evaluate(Isolate isolate, Module module)
    {
        Status moduleStatus = module.ModuleStatus;

        // In the event of errored evaluation, return a rejected promise.
        if (moduleStatus == Status.kErrored)
        {
            // If we have a top level capability we assume it has already been
            // rejected, and return it here. Otherwise create a new promise and
            // reject it with the module's exception.
            if (module.TopLevelCapability is { } topLevelCapability)
            {
                Debug.Assert(topLevelCapability.Status == PromiseState.kRejected);
                return topLevelCapability;
            }
            JSPromise capability = PromiseBuiltins.NewJSPromise(isolate);
            PromiseBuiltins.RejectPromise(isolate, capability, module.Exception, true);
            return capability;
        }

        // Start of Evaluate () Concrete Method
        // 2. Assert: module.[[Status]] is one of LINKED, EVALUATING-ASYNC, or
        //    EVALUATED.
        if (moduleStatus is not (Status.kLinked or Status.kEvaluatingAsync or Status.kEvaluated))
        {
            throw new InvalidOperationException("V8Sharp: Module::Evaluate on a module with status " + moduleStatus);
        }

        // 3. If module.[[Status]] is either EVALUATING-ASYNC or EVALUATED, then
        //    a. If module.[[CycleRoot]] is not empty, then
        //       i. Set module to module.[[CycleRoot]].
        // A Synthetic Module has no children so it is its own cycle root.
        if (moduleStatus >= Status.kEvaluatingAsync && module is SourceTextModule stm)
        {
            module = stm.GetCycleRoot();
        }

        // 4. If module.[[TopLevelCapability]] is not EMPTY, then
        //    a. Return module.[[TopLevelCapability]].[[Promise]].
        if (module.TopLevelCapability is { } existing) return existing;

        if (module is SourceTextModule sourceTextModule) return SourceTextModule.Evaluate(isolate, sourceTextModule);
        return SyntheticModule.Evaluate(isolate, (SyntheticModule)module);
    }

    /// <summary>Module::GetModuleNamespaceCell.</summary>
    internal static Cell GetModuleNamespaceCell(Isolate isolate, Module module,
        ModuleImportPhase phase = ModuleImportPhase.kEvaluation)
    {
        Debug.Assert(phase is ModuleImportPhase.kEvaluation or ModuleImportPhase.kDefer);
        Cell? maybeCell = phase == ModuleImportPhase.kEvaluation ? module.ModuleNamespaceCell : module.DeferredModuleNamespaceCell;
        if (maybeCell is not null) return maybeCell;
        var cell = new Cell(JSValue.Undefined);
        if (phase == ModuleImportPhase.kEvaluation)
        {
            module.ModuleNamespaceCell = cell;
        }
        else
        {
            module.DeferredModuleNamespaceCell = cell;
        }
        return cell;
    }

    /// <summary>Module::GetModuleNamespace: the namespace object, created on first use.</summary>
    public static JSModuleNamespace GetModuleNamespace(Isolate isolate, Module module,
        ModuleImportPhase phase = ModuleImportPhase.kEvaluation)
    {
        Debug.Assert(phase is ModuleImportPhase.kEvaluation or ModuleImportPhase.kDefer);
        Cell nsCell = GetModuleNamespaceCell(isolate, module, phase);
        if (nsCell.Value.HeapObjectOrNull is JSModuleNamespace cur) return cur;

        // Collect the export names.
        if (module is SourceTextModule sourceTextModule)
        {
            SourceTextModule.FetchStarExports(isolate, sourceTextModule, new HashSet<Module>(ReferenceEqualityComparer.Instance));
        }

        ObjectHashTable exports = module.Exports;
        var names = new List<JSString>(exports.NumberOfElements);
        for (int i = 0; i < exports.Capacity; i++)
        {
            if (!exports.ToKey(new InternalIndex(i), out JSValue key)) continue;
            names.Add(key.As<JSString>());
        }
        Debug.Assert(names.Count == exports.NumberOfElements);

        // Sort them alphabetically.
        names.Sort(static (a, b) => (int)JSString.Compare(a, b));

        // Create the namespace object (initially empty).
        // (Factory::NewJSModuleNamespace / NewJSDeferredModuleNamespace, with the
        // @@toStringTag in-object field.)
        JSModuleNamespace ns;
        if (phase == ModuleImportPhase.kEvaluation)
        {
            ns = (JSModuleNamespace)isolate.Factory.NewJSObjectFromMap(isolate.NativeContext.JSModuleNamespaceMap);
            ns.FastPropertyAtPut(FieldIndex.ForPropertyIndex(ns.Map, 0), ReadOnlyRoots.Module_string);
        }
        else
        {
            ns = (JSModuleNamespace)isolate.Factory.NewJSObjectFromMap(isolate.NativeContext.JSDeferredModuleNamespaceMap);
            ns.FastPropertyAtPut(FieldIndex.ForPropertyIndex(ns.Map, 0), ReadOnlyRoots.Deferred_Module_string);
        }
        ns.Module = module;
        nsCell.Value = ns;

        // Create the properties in the namespace object. Transition the object
        // to dictionary mode so that property addition is faster.
        const PropertyAttributes attr = PropertyAttributes.DONT_DELETE;
        JSObject.NormalizeProperties(isolate, ns, PropertyNormalizationMode.CLEAR_INOBJECT_PROPERTIES, names.Count,
            "JSModuleNamespace");
        JSObject.NormalizeElements(isolate, ns);
        AccessorInfo moduleNamespacePropertyAccessor = Accessors.ModuleNamespaceEntryAccessor;
        foreach (JSString name in names)
        {
            var details = new PropertyDetails(PropertyKind.Accessor, attr, PropertyCellType.Mutable);
            if (name.AsArrayIndex(out uint index))
            {
                JSObject.SetNormalizedElement(isolate, ns, index, moduleNamespacePropertyAccessor, details);
            }
            else
            {
                JSObject.SetNormalizedProperty(isolate, ns, name, moduleNamespacePropertyAccessor, details);
            }
        }
        JSObject.PreventExtensions(isolate, ns, ShouldThrow.ThrowOnError);

        // Optimize the namespace object as a prototype, for two reasons:
        // - The object's map is guaranteed not to be shared. ICs rely on this.
        // - We can store a pointer from the map back to the namespace object.
        //   Turbofan can use this for inlining the access.
        JSObject.OptimizeAsPrototype(isolate, ns);
        PrototypeInfo protoInfo = Map.GetOrCreatePrototypeInfo(ns, isolate);
        protoInfo.ModuleNamespace = ns;
        return ns;
    }

    /// <summary>Module::IsGraphAsync: whether this module or a transitively requested one has a top-level await.</summary>
    public bool IsGraphAsync(Isolate isolate)
    {
        // Only SourceTextModules may be async.
        if (this is not SourceTextModule root) return false;
        Debug.Assert(root.ModuleStatus >= Status.kLinked);
        var visited = new HashSet<SourceTextModule>(ReferenceEqualityComparer.Instance) { root };
        var worklist = new List<SourceTextModule> { root };
        do
        {
            SourceTextModule current = worklist[^1];
            worklist.RemoveAt(worklist.Count - 1);
            if (current.HasToplevelAwait) return true;
            foreach (HeapObject? rawDescendant in current.RequestedModules)
            {
                if (rawDescendant is SourceTextModule descendant && visited.Add(descendant)) worklist.Add(descendant);
            }
        } while (worklist.Count > 0);
        return false;
    }

    /// <summary>
    /// v8::Module::EvaluateForImportDefer (src/api/api.cc): evaluates the
    /// asynchronous transitive dependencies of a module imported with
    /// `import.defer()`; the promise settles once they are evaluated.
    /// </summary>
    public static JSPromise EvaluateForImportDefer(Isolate isolate, Module module)
    {
        var evaluationList = new List<SourceTextModule>();
        var seenModules = new HashSet<Module>(ReferenceEqualityComparer.Instance);
        var evaluationSet = new HashSet<Module>(ReferenceEqualityComparer.Instance);
        if (module is SourceTextModule)
        {
            SourceTextModule.GatherAsynchronousTransitiveDependencies(isolate, module, evaluationSet, evaluationList,
                seenModules);
        }

        if (evaluationList.Count == 0)
        {
            JSModuleNamespace moduleNamespace = GetModuleNamespace(isolate, module, ModuleImportPhase.kDefer);
            JSPromise moduleResolver = PromiseBuiltins.NewJSPromise(isolate);
            PromiseBuiltins.ResolvePromise(isolate, moduleResolver, moduleNamespace);
            return moduleResolver;
        }

        var promises = new JSPromise[evaluationList.Count];
        for (int i = 0; i < evaluationList.Count; i++) promises[i] = Evaluate(isolate, evaluationList[i]);

        // TODO(caiolima): The call to native Promise "then" is yet to be approved
        // on https://github.com/tc39/proposal-defer-import-eval/pull/77. Revisit it
        // after a decision is made.
        return PromiseBuiltins.PerformPromiseAll(isolate, promises);
    }

    /// <summary>Module::ResolveSet: per module, the export names being resolved (cycle detection).</summary>
    internal sealed class ResolveSet : Dictionary<Module, HashSet<JSString>>
    {
        public ResolveSet() : base(ReferenceEqualityComparer.Instance) { }
    }
}

/// <summary>A HashSet of export names compared by content (UnorderedStringSet).</summary>
sealed class StringContentComparer : IEqualityComparer<JSString>
{
    public static readonly StringContentComparer Instance = new();
    public bool Equals(JSString? x, JSString? y) => JSString.Equals(x!, y!);
    public int GetHashCode(JSString obj) => (int)obj.EnsureHash();
}

/// <summary>V8's ModuleRequest: a specifier, its import attributes and phase.</summary>
public sealed class ModuleRequest(JSString specifier, ModuleImportPhase phase, FixedArray importAttributes, int position)
    : HeapObject(InstanceType.ModuleRequestType)
{
    /// <summary>The number of entries in the import_attributes FixedArray that are used for a single attribute.</summary>
    public const int kAttributeEntrySize = 3;

    public readonly JSString Specifier = specifier;
    /// <summary>[key1, value1, location1, key2, value2, location2, ...].</summary>
    public readonly FixedArray ImportAttributes = importAttributes;
    public readonly ModuleImportPhase Phase = phase;
    /// <summary>Source text position of the module request.</summary>
    public readonly int Position = position;
}

/// <summary>V8's SourceTextModuleInfoEntry.</summary>
public sealed class SourceTextModuleInfoEntry(JSString? exportName, JSString? localName, JSString? importName,
    int moduleRequest, int cellIndex, int begPos, int endPos) : HeapObject(InstanceType.SourceTextModuleInfoEntryType)
{
    public readonly JSString? ExportName = exportName;
    public readonly JSString? LocalName = localName;
    public readonly JSString? ImportName = importName;
    public readonly int ModuleRequest = moduleRequest;
    public readonly int CellIndex = cellIndex;
    public readonly int BegPos = begPos;
    public readonly int EndPos = endPos;
}

/// <summary>
/// V8's SourceTextModuleInfo: the module descriptor of a module scope,
/// serialized from the parser's SourceTextModuleDescriptor.
/// </summary>
public sealed class SourceTextModuleInfo
{
    public ModuleRequest[] ModuleRequests = [];
    public SourceTextModuleInfoEntry[] SpecialExports = [];
    public SourceTextModuleInfoEntry[] NamespaceImports = [];
    public SourceTextModuleInfoEntry[] RegularImports = [];

    /// <summary>The regular exports: one record per local name (kRegularExportLength entries in V8).</summary>
    public RegularExport[] RegularExports = [];

    public readonly record struct RegularExport(JSString LocalName, int CellIndex, JSString[] ExportNames);

    public int RegularExportCount() => RegularExports.Length;
    public JSString RegularExportLocalName(int i) => RegularExports[i].LocalName;
    public int RegularExportCellIndex(int i) => RegularExports[i].CellIndex;
    public JSString[] RegularExportExportNames(int i) => RegularExports[i].ExportNames;

    /// <summary>Whether the module has at least one `export * from '...'` statement.</summary>
    public bool HasStarExports()
    {
        foreach (SourceTextModuleInfoEntry entry in SpecialExports)
        {
            // Star exports are the only special exports without a name.
            if (entry.ExportName is null) return true;
        }
        return false;
    }

    static JSString? ToStringOrNull(Factory factory, AstRawString? s) => s is null ? null : factory.InternalizeString(s.Value);

    /// <summary>SourceTextModuleDescriptor::Entry::Serialize.</summary>
    static SourceTextModuleInfoEntry Serialize(Factory factory, SourceTextModuleDescriptor.Entry entry) =>
        new(ToStringOrNull(factory, entry.export_name), ToStringOrNull(factory, entry.local_name),
            ToStringOrNull(factory, entry.import_name), entry.module_request, entry.cell_index, entry.location.beg_pos,
            entry.location.end_pos);

    /// <summary>SourceTextModuleDescriptor::AstModuleRequest::Serialize.</summary>
    static ModuleRequest Serialize(Factory factory, SourceTextModuleDescriptor.AstModuleRequest request)
    {
        // The import attributes will be stored in this array in the form:
        // [key1, value1, location1, key2, value2, location2, ...]
        ImportAttributes attributes = request.import_attributes();
        var importAttributesArray = new FixedArray(attributes.Count * ModuleRequest.kAttributeEntrySize);
        int i = 0;
        foreach (KeyValuePair<AstRawString, (AstRawString value, Parsing.Scanner.Location location)> kv in attributes)
        {
            importAttributesArray.Set(i, factory.InternalizeString(kv.Key.Value));
            importAttributesArray.Set(i + 1, factory.InternalizeString(kv.Value.value.Value));
            importAttributesArray.Set(i + 2, JSValue.FromInt(kv.Value.location.beg_pos));
            i += ModuleRequest.kAttributeEntrySize;
        }
        return new ModuleRequest(factory.InternalizeString(request.specifier().Value), request.phase(), importAttributesArray,
            request.position());
    }

    /// <summary>SourceTextModuleInfo::New.</summary>
    public static SourceTextModuleInfo New(Factory factory, SourceTextModuleDescriptor descr)
    {
        var result = new SourceTextModuleInfo();

        // Serialize module requests.
        result.ModuleRequests = new ModuleRequest[descr.module_requests().Count];
        foreach (SourceTextModuleDescriptor.AstModuleRequest elem in descr.module_requests())
        {
            result.ModuleRequests[elem.index()] = Serialize(factory, elem);
        }

        // Serialize special exports.
        var specialExports = new List<SourceTextModuleInfoEntry>(descr.special_exports().Count);
        foreach (SourceTextModuleDescriptor.Entry entry in descr.special_exports()) specialExports.Add(Serialize(factory, entry));
        result.SpecialExports = [.. specialExports];

        // Serialize namespace imports.
        var namespaceImports = new List<SourceTextModuleInfoEntry>(descr.namespace_imports().Count);
        foreach (KeyValuePair<AstRawString, SourceTextModuleDescriptor.Entry> entry in descr.namespace_imports())
        {
            namespaceImports.Add(Serialize(factory, entry.Value));
        }
        result.NamespaceImports = [.. namespaceImports];

        // Serialize regular exports (SerializeRegularExports): one record per
        // local name with all its export names.
        var regularExports = new List<RegularExport>();
        SourceTextModuleDescriptor.RegularExportMap map = descr.regular_exports();
        for (int it = 0; it < map.Count;)
        {
            // Find out how many export names this local name has.
            int next = it;
            do { ++next; } while (next < map.Count && ReferenceEquals(map[next].Key, map[it].Key));
            var exportNames = new JSString[next - it];
            SourceTextModuleDescriptor.Entry first = map[it].Value;
            for (int i = 0; it < next; ++it) exportNames[i++] = factory.InternalizeString(map[it].Value.export_name!.Value);
            regularExports.Add(new RegularExport(factory.InternalizeString(first.local_name!.Value), first.cell_index,
                exportNames));
        }
        result.RegularExports = [.. regularExports];

        // Serialize regular imports.
        var regularImports = new List<SourceTextModuleInfoEntry>(descr.regular_imports().Count);
        foreach (KeyValuePair<AstRawString, SourceTextModuleDescriptor.Entry> elem in descr.regular_imports())
        {
            regularImports.Add(Serialize(factory, elem.Value));
        }
        result.RegularImports = [.. regularImports];
        return result;
    }
}

/// <summary>V8's SourceTextModule (Source Text Module Record).</summary>
public sealed class SourceTextModule() : Module(InstanceType.SourceTextModuleType)
{
    public const int kFirstAsyncEvaluationOrdinal = 2;
    const int kNotAsyncEvaluated = 0;
    const int kAsyncEvaluateDidFinish = 1;

    /// <summary>ExecuteAsyncModuleContextSlots::kModule.</summary>
    public const int kExecuteAsyncModuleContextModuleSlot = (int)Context.Field.MIN_CONTEXT_SLOTS;
    public const int kExecuteAsyncModuleContextLength = kExecuteAsyncModuleContextModuleSlot + 1;

    /// <summary>The code: a SharedFunctionInfo, then a JSFunction, then the JSGeneratorObject (or JSAsyncFunctionObject).</summary>
    public HeapObject Code = null!;
    /// <summary>The cells of the regular exports, by export index.</summary>
    public Cell?[] RegularExports = [];
    /// <summary>The cells of the regular imports, by import index.</summary>
    public Cell?[] RegularImports = [];
    /// <summary>Modules imported or re-exported by this module (1-to-1 with the info's module requests).</summary>
    public HeapObject?[] RequestedModules = [];
    /// <summary>The value of import.meta (null before first access).</summary>
    public JSObject? ImportMeta;
    /// <summary>The first visited module of a cycle (null before the module is evaluated).</summary>
    public SourceTextModule? CycleRoot;
    public readonly List<SourceTextModule> AsyncParentModules = [];
    public int DfsIndex = -1;
    public int DfsAncestorIndex = -1;
    /// <summary>The number of currently evaluating async dependencies of this module.</summary>
    public int PendingAsyncDependencies;
    public bool HasToplevelAwait;
    public int AsyncEvaluationOrdinal = kNotAsyncEvaluated;

    /// <summary>Factory::NewSourceTextModule.</summary>
    public static SourceTextModule New(Isolate isolate, SharedFunctionInfo sfi)
    {
        SourceTextModuleInfo moduleInfo = sfi.ScopeInfo.ModuleDescriptorInfo();
        var module = new SourceTextModule
        {
            Code = sfi,
            Exports = ObjectHashTable.New(moduleInfo.RegularExportCount()),
            RegularExports = new Cell?[moduleInfo.RegularExportCount()],
            RegularImports = new Cell?[moduleInfo.RegularImports.Length],
            RequestedModules = new HeapObject?[moduleInfo.ModuleRequests.Length],
            HasToplevelAwait = Globals.IsModuleWithTopLevelAwait(sfi.Kind),
        };
        return module;
    }

    /// <summary>The SourceTextModuleInfo associated with the code.</summary>
    public SourceTextModuleInfo Info => GetSharedFunctionInfo().ScopeInfo.ModuleDescriptorInfo();

    /// <summary>SourceTextModule::GetSharedFunctionInfo.</summary>
    public SharedFunctionInfo GetSharedFunctionInfo() => Code switch
    {
        SharedFunctionInfo sfi => sfi,
        JSGeneratorObject generator => generator.Function.Shared,
        JSFunction function => function.Shared,
        _ => throw new InvalidOperationException("V8Sharp: bad SourceTextModule code"),
    };

    public Script GetScript() => GetSharedFunctionInfo().Script!;

    /// <summary>GetCycleRoot: the non-hole cycle root. Only valid when status >= kEvaluated.</summary>
    public SourceTextModule GetCycleRoot()
    {
        Debug.Assert(ModuleStatus >= Status.kEvaluatingAsync);
        return CycleRoot!;
    }

    bool HasAsyncEvaluationOrdinal => AsyncEvaluationOrdinal >= kFirstAsyncEvaluationOrdinal;
    bool HasPendingAsyncDependencies => PendingAsyncDependencies > 0;

    public static int ExportIndex(int cellIndex)
    {
        Debug.Assert(SourceTextModuleDescriptor.GetCellIndexKind(cellIndex) == SourceTextModuleDescriptor.CellIndexKind.kExport);
        return cellIndex - 1;
    }

    public static int ImportIndex(int cellIndex)
    {
        Debug.Assert(SourceTextModuleDescriptor.GetCellIndexKind(cellIndex) == SourceTextModuleDescriptor.CellIndexKind.kImport);
        return -cellIndex - 1;
    }

    /// <summary>SourceTextModule::GetCell.</summary>
    public Cell GetCell(int cellIndex) => SourceTextModuleDescriptor.GetCellIndexKind(cellIndex) switch
    {
        SourceTextModuleDescriptor.CellIndexKind.kImport => RegularImports[ImportIndex(cellIndex)]!,
        SourceTextModuleDescriptor.CellIndexKind.kExport => RegularExports[ExportIndex(cellIndex)]!,
        _ => throw new InvalidOperationException("V8Sharp: invalid module cell index"),
    };

    /// <summary>SourceTextModule::LoadVariable.</summary>
    public static JSValue LoadVariable(Isolate isolate, SourceTextModule module, int cellIndex) => module.GetCell(cellIndex).Value;

    /// <summary>SourceTextModule::StoreVariable.</summary>
    public static void StoreVariable(SourceTextModule module, int cellIndex, JSValue value)
    {
        Debug.Assert(SourceTextModuleDescriptor.GetCellIndexKind(cellIndex) == SourceTextModuleDescriptor.CellIndexKind.kExport);
        module.GetCell(cellIndex).Value = value;
    }

    /// <summary>SourceTextModule::CreateIndirectExport.</summary>
    static void CreateIndirectExport(Isolate isolate, SourceTextModule module, JSString name, SourceTextModuleInfoEntry entry)
    {
        Debug.Assert(module.Exports.Lookup(isolate, name).IsTheHole);
        module.Exports = ObjectHashTable.Put(isolate, module.Exports, name, entry);
    }

    /// <summary>SourceTextModule::CreateExport.</summary>
    static void CreateExport(Isolate isolate, SourceTextModule module, int cellIndex, JSString[] names)
    {
        Debug.Assert(names.Length > 0);
        var cell = new Cell(JSValue.Undefined);
        module.RegularExports[ExportIndex(cellIndex)] = cell;
        ObjectHashTable exports = module.Exports;
        foreach (JSString name in names)
        {
            Debug.Assert(exports.Lookup(isolate, name).IsTheHole);
            exports = ObjectHashTable.Put(isolate, exports, name, cell);
        }
        module.Exports = exports;
    }

    /// <summary>SourceTextModule::ResolveExport.</summary>
    internal static Cell? ResolveExport(Isolate isolate, SourceTextModule module, JSString? moduleSpecifier,
        JSString exportName, MessageLocation loc, bool mustResolve, ResolveSet resolveSet)
    {
        JSValue obj = module.Exports.Lookup(isolate, exportName);
        if (obj.HeapObjectOrNull is Cell resolved)
        {
            // Already resolved (e.g. because it's a local export).
            return resolved;
        }

        // Check for cycle before recursing.
        if (!resolveSet.TryGetValue(module, out HashSet<JSString>? nameSet))
        {
            // |module| wasn't in the map previously, so allocate a new name set.
            nameSet = new HashSet<JSString>(StringContentComparer.Instance);
            resolveSet.Add(module, nameSet);
        }
        else if (nameSet.Contains(exportName))
        {
            // Cycle detected.
            if (mustResolve)
            {
                isolate.ThrowAt(isolate.Factory.NewSyntaxError(MessageTemplate.CyclicModuleDependency, exportName,
                    SpecifierOrUndefined(moduleSpecifier)), loc);
            }
            return null;
        }
        nameSet.Add(exportName);

        if (obj.IsTheHole)
        {
            return ResolveExportUsingStarExports(isolate, module, moduleSpecifier, exportName, loc, mustResolve, resolveSet);
        }

        // Not yet resolved indirect export.
        var entry = obj.As<SourceTextModuleInfoEntry>();
        var newLoc = new MessageLocation(module.GetScript(), entry.BegPos, entry.EndPos);
        Cell? cell = ResolveImport(isolate, module, entry.ImportName, entry.ModuleRequest, newLoc, mustResolve, resolveSet);
        if (cell is null) return null;

        // The export table may have changed but the entry in question should be
        // unchanged.
        Debug.Assert(module.Exports.Lookup(isolate, exportName).HeapObjectOrNull is SourceTextModuleInfoEntry);
        module.Exports = ObjectHashTable.Put(isolate, module.Exports, exportName, cell);
        return cell;
    }

    static JSValue SpecifierOrUndefined(JSString? specifier) => specifier is null ? JSValue.Undefined : specifier;

    /// <summary>SourceTextModule::ResolveImport.</summary>
    static Cell? ResolveImport(Isolate isolate, SourceTextModule module, JSString? name, int moduleRequestIndex,
        MessageLocation loc, bool mustResolve, ResolveSet resolveSet)
    {
        ModuleRequest moduleRequest = module.Info.ModuleRequests[moduleRequestIndex];
        ModuleImportPhase phase = moduleRequest.Phase;
        if (phase == ModuleImportPhase.kSource)
        {
            // https://tc39.es/proposal-source-phase-imports/#sec-source-text-module-record-initialize-environment
            // InitializeEnvironment
            // 7.c. Else if in.[[ImportName]] is source, then
            // 7.c.i. Let moduleSourceObject be ? importedModule.GetModuleSource().
            // 7.c.ii. Perform ! env.CreateImmutableBinding(in.[[LocalName]], true).
            // 7.c.iii. Perform ! env.InitializeBinding(in.[[LocalName]],
            //          moduleSourceObject).
            return new Cell(module.RequestedModules[moduleRequestIndex]!);
        }
        var requestedModule = (Module)module.RequestedModules[moduleRequestIndex]!;
        JSString moduleSpecifier = moduleRequest.Specifier;
        if (name is not null)
        {
            return Module.ResolveExport(isolate, requestedModule, moduleSpecifier, name, loc, mustResolve, resolveSet);
        }
        // This is to resolve an indirect include of the * as namespace.
        // b. If in.[[ImportName]] is namespace-object, then
        //   i. Let namespace be GetModuleNamespace(importedModule,
        //   in.[[ModuleRequest]].[[Phase]]).
        return GetModuleNamespaceCell(isolate, requestedModule, phase);
    }

    /// <summary>SourceTextModule::ResolveExportUsingStarExports.</summary>
    static Cell? ResolveExportUsingStarExports(Isolate isolate, SourceTextModule module, JSString? moduleSpecifier,
        JSString exportName, MessageLocation loc, bool mustResolve, ResolveSet resolveSet)
    {
        if (!JSString.Equals(exportName, ReadOnlyRoots.default_string))
        {
            // Go through all star exports looking for the given name.  If multiple star
            // exports provide the name, make sure they all map it to the same cell.
            Cell? uniqueCell = null;
            foreach (SourceTextModuleInfoEntry entry in module.Info.SpecialExports)
            {
                if (entry.ExportName is not null) continue;  // Indirect export.

                var newLoc = new MessageLocation(module.GetScript(), entry.BegPos, entry.EndPos);
                Cell? cell = ResolveImport(isolate, module, exportName, entry.ModuleRequest, newLoc, false, resolveSet);
                if (cell is not null)
                {
                    uniqueCell ??= cell;
                    if (!ReferenceEquals(uniqueCell, cell))
                    {
                        isolate.ThrowAt(isolate.Factory.NewSyntaxError(MessageTemplate.AmbiguousExport,
                            SpecifierOrUndefined(moduleSpecifier), exportName), loc);
                    }
                }
            }

            if (uniqueCell is not null)
            {
                // Found a unique star export for this name.
                Debug.Assert(module.Exports.Lookup(isolate, exportName).IsTheHole);
                module.Exports = ObjectHashTable.Put(isolate, module.Exports, exportName, uniqueCell);
                return uniqueCell;
            }
        }

        // Unresolvable.
        if (mustResolve)
        {
            isolate.ThrowAt(isolate.Factory.NewSyntaxError(MessageTemplate.UnresolvableExport,
                SpecifierOrUndefined(moduleSpecifier), exportName), loc);
        }
        return null;
    }

    /// <summary>SourceTextModule::PrepareInstantiate.</summary>
    internal static void PrepareInstantiate(Isolate isolate, SourceTextModule module, ResolveModuleCallback callback,
        ResolveSourceCallback? sourceCallback)
    {
        // Obtain requested modules.
        SourceTextModuleInfo moduleInfo = module.Info;
        ModuleRequest[] moduleRequests = moduleInfo.ModuleRequests;
        HeapObject?[] requestedModules = module.RequestedModules;
        for (int i = 0; i < moduleRequests.Length; ++i)
        {
            ModuleRequest moduleRequest = moduleRequests[i];
            switch (moduleRequest.Phase)
            {
                case ModuleImportPhase.kDefer:
                case ModuleImportPhase.kEvaluation:
                    requestedModules[i] = callback(isolate, moduleRequest.Specifier, moduleRequest.ImportAttributes, module);
                    break;
                case ModuleImportPhase.kSource:
                    Debug.Assert(isolate.Flags.js_source_phase_imports);
                    if (sourceCallback is null)
                    {
                        throw new InvalidOperationException("V8Sharp: a source phase import without a ResolveSourceCallback");
                    }
                    requestedModules[i] = sourceCallback(isolate, moduleRequest.Specifier, moduleRequest.ImportAttributes,
                        module);
                    break;
            }
        }

        // Recurse.
        for (int i = 0; i < requestedModules.Length; ++i)
        {
            if (moduleRequests[i].Phase == ModuleImportPhase.kSource) continue;
            Module.PrepareInstantiate(isolate, (Module)requestedModules[i]!, callback, sourceCallback);
        }

        // Set up local exports.
        for (int i = 0, n = moduleInfo.RegularExportCount(); i < n; ++i)
        {
            CreateExport(isolate, module, moduleInfo.RegularExportCellIndex(i), moduleInfo.RegularExportExportNames(i));
        }

        // Partially set up indirect exports.
        // For each indirect export, we create the appropriate slot in the export
        // table and store its SourceTextModuleInfoEntry there.  When we later find
        // the correct Cell in the module that actually provides the value, we replace
        // the SourceTextModuleInfoEntry by that Cell (see ResolveExport).
        foreach (SourceTextModuleInfoEntry entry in moduleInfo.SpecialExports)
        {
            if (entry.ExportName is null) continue;  // Star export.
            CreateIndirectExport(isolate, module, entry.ExportName, entry);
        }

        Debug.Assert(module.ModuleStatus == Status.kPreLinking);
    }

    /// <summary>SourceTextModule::RunInitializationCode: runs the module function up to its initial yield.</summary>
    static void RunInitializationCode(Isolate isolate, SourceTextModule module)
    {
        Debug.Assert(module.ModuleStatus == Status.kLinking);
        var function = (JSFunction)module.Code;
        Debug.Assert(function.Shared.ScopeInfo.ScopeType == ScopeType.MODULE_SCOPE);
        ScopeInfo scopeInfo = function.Shared.ScopeInfo;
        Context context = isolate.Factory.NewModuleContext(module, isolate.NativeContext, scopeInfo);
        function.Context = context;

        JSValue generator = Execution.Call(isolate, function, JSValue.Undefined, []);
        Debug.Assert(ReferenceEquals(generator.As<JSGeneratorObject>().Function, function));
        module.Code = generator.As<JSGeneratorObject>();
    }

    /// <summary>SourceTextModule::MaybeTransitionComponent.</summary>
    static void MaybeTransitionComponent(Isolate isolate, SourceTextModule module, List<SourceTextModule> stack,
        Status newStatus)
    {
        Debug.Assert(newStatus is Status.kLinked or Status.kEvaluated);

        // Below, N/M means step N in InnerModuleEvaluation and step M in
        // InnerModuleLinking.

        // 15/12. Assert: module.[[DFSAncestorIndex]] <= module.[[DFSIndex]].
        Debug.Assert(module.DfsAncestorIndex <= module.DfsIndex);

        // 16/13. If module.[[DFSAncestorIndex]] = module.[[DFSIndex]], then
        if (module.DfsAncestorIndex != module.DfsIndex) return;

        // This is the root of its strongly connected component.
        SourceTextModule cycleRoot = module;
        if (newStatus == Status.kLinked)
        {
            // V8 splits InitializeEnvironment() into two parts: static import and
            // indirect-export resolution runs at the step-10 position in
            // FinishInstantiate, while environment and namespace instantiation
            // (RunInitializationCode) is deferred until all imports and indirect
            // exports across the SCC have been resolved. Because
            // RunInitializationCode can fail (e.g. stack overflow or termination), it
            // must complete for every module in the SCC before any module in the SCC
            // transitions to kLinked below.
            bool found = false;
            // {stack} is V8's forward list with the most recent module at the front,
            // i.e. the end of the List here.
            for (int i = stack.Count - 1; i >= 0; i--)
            {
                SourceTextModule requiredModule = stack[i];
                Debug.Assert(requiredModule.ModuleStatus == Status.kLinking);
                RunInitializationCode(isolate, requiredModule);
                if (ReferenceEquals(requiredModule, module))
                {
                    found = true;
                    break;
                }
            }
            if (!found) throw new InvalidOperationException("V8Sharp: module not on the linking stack");
        }
        SourceTextModule ancestor;
        do
        {
            ancestor = stack[^1];
            stack.RemoveAt(stack.Count - 1);
            Debug.Assert(ancestor.ModuleStatus == (newStatus == Status.kLinked ? Status.kLinking : Status.kEvaluating));
            if (newStatus == Status.kLinked)
            {
                ancestor.SetStatus(Status.kLinked);
            }
            else
            {
                Debug.Assert(ancestor.CycleRoot is null);
                ancestor.CycleRoot = cycleRoot;
                ancestor.SetStatus(ancestor.HasAsyncEvaluationOrdinal ? Status.kEvaluatingAsync : Status.kEvaluated);
            }
        } while (!ReferenceEquals(ancestor, module));
    }

    /// <summary>SourceTextModule::FinishInstantiate.</summary>
    internal static void FinishInstantiate(Isolate isolate, SourceTextModule module, List<SourceTextModule> stack,
        ref int dfsIndex)
    {
        // Instantiate SharedFunctionInfo and mark module as instantiating for
        // the recursion.
        var shared = (SharedFunctionInfo)module.Code;
        JSFunction function = isolate.Factory.NewFunction(shared, isolate.NativeContext);
        module.Code = function;
        module.SetStatus(Status.kLinking);
        module.DfsIndex = dfsIndex;
        module.DfsAncestorIndex = dfsIndex;
        stack.Add(module);
        dfsIndex++;

        // Recurse.
        ModuleRequest[] moduleRequests = module.Info.ModuleRequests;
        HeapObject?[] requestedModules = module.RequestedModules;
        for (int i = 0; i < requestedModules.Length; ++i)
        {
            if (moduleRequests[i].Phase == ModuleImportPhase.kSource) continue;
            var requestedModule = (Module)requestedModules[i]!;
            Module.FinishInstantiate(isolate, requestedModule, stack, ref dfsIndex);

            Debug.Assert(requestedModule.ModuleStatus != Status.kEvaluating);
            Debug.Assert(requestedModule.ModuleStatus >= Status.kLinking);
            if (requestedModule.ModuleStatus == Status.kLinking)
            {
                // SyntheticModules go straight to kLinked so this must be a
                // SourceTextModule
                module.DfsAncestorIndex = Math.Min(module.DfsAncestorIndex,
                    ((SourceTextModule)requestedModule).DfsAncestorIndex);
            }
        }

        Script script = module.GetScript();
        SourceTextModuleInfo moduleInfo = module.Info;

        // Resolve imports (InitializeEnvironment step 7) and indirect exports
        // (InitializeEnvironment step 1).
        foreach (SourceTextModuleInfoEntry entry in moduleInfo.RegularImports)
        {
            var loc = new MessageLocation(script, entry.BegPos, entry.EndPos);
            var resolveSet = new ResolveSet();
            Debug.Assert(module.ModuleStatus == Status.kLinking);
            Cell cell = ResolveImport(isolate, module, entry.ImportName, entry.ModuleRequest, loc, true, resolveSet)!;
            module.RegularImports[ImportIndex(entry.CellIndex)] = cell;
        }

        // Resolve indirect exports.
        foreach (SourceTextModuleInfoEntry entry in moduleInfo.SpecialExports)
        {
            if (entry.ExportName is null) continue;  // Star export.
            var loc = new MessageLocation(script, entry.BegPos, entry.EndPos);
            var resolveSet = new ResolveSet();
            ResolveExport(isolate, module, null, entry.ExportName, loc, true, resolveSet);
        }

        MaybeTransitionComponent(isolate, module, stack, Status.kLinked);
    }

    /// <summary>SourceTextModule::FetchStarExports.</summary>
    internal static void FetchStarExports(Isolate isolate, SourceTextModule module, HashSet<Module> visited)
    {
        Debug.Assert(module.ModuleStatus >= Status.kLinking);

        // Shortcut.
        if (module.ModuleNamespaceCell?.Value.HeapObjectOrNull is JSModuleNamespace) return;

        if (!visited.Add(module)) return;
        ObjectHashTable exports = module.Exports;
        // Ambiguities (conflicting exports) are marked by mapping the name to
        // null instead of a Cell. Insertion order is kept for determinism.
        var moreExports = new Dictionary<JSString, Cell?>(StringContentComparer.Instance);
        var moreExportsOrder = new List<JSString>();

        foreach (SourceTextModuleInfoEntry entry in module.Info.SpecialExports)
        {
            if (entry.ExportName is not null) continue;  // Indirect export.

            var requestedModule = (Module)module.RequestedModules[entry.ModuleRequest]!;

            // Recurse.
            if (requestedModule is SourceTextModule requestedSourceTextModule)
            {
                FetchStarExports(isolate, requestedSourceTextModule, visited);
            }

            // Collect all of [requested_module]'s exports that must be added to
            // [module]'s exports (i.e. to [exports]).  We record these in
            // [more_exports].
            ObjectHashTable requestedExports = requestedModule.Exports;
            for (int index = 0; index < requestedExports.Capacity; index++)
            {
                var entryIndex = new InternalIndex(index);
                if (!requestedExports.ToKey(entryIndex, out JSValue key)) continue;
                var name = key.As<JSString>();

                if (JSString.Equals(name, ReadOnlyRoots.default_string)) continue;
                if (!exports.Lookup(isolate, name).IsTheHole) continue;

                var cell = requestedExports.ValueAt(entryIndex).As<Cell>();
                if (!moreExports.TryGetValue(name, out Cell? existing))
                {
                    moreExports.Add(name, cell);
                    moreExportsOrder.Add(name);
                }
                else if (existing is not null && !ReferenceEquals(existing, cell))
                {
                    // Different star exports provide different cells for this name, hence
                    // mark the name as ambiguous.
                    moreExports[name] = null;
                }
            }
        }

        // Copy [more_exports] into [exports].
        foreach (JSString name in moreExportsOrder)
        {
            Cell? cell = moreExports[name];
            if (cell is null) continue;  // Ambiguous export.
            exports = ObjectHashTable.Put(isolate, exports, name, cell);
        }
        module.Exports = exports;
    }

    /// <summary>SourceTextModule::GatherAvailableAncestors (iterative), in async evaluation order.</summary>
    static void GatherAvailableAncestors(SourceTextModule start, SortedSet<SourceTextModule> execList)
    {
        var worklist = new Stack<SourceTextModule>();
        worklist.Push(start);
        while (worklist.Count > 0)
        {
            SourceTextModule module = worklist.Pop();
            // 1. For each Cyclic Module Record m of module.[[AsyncParentModules]], do
            for (int i = module.AsyncParentModules.Count; i-- > 0;)
            {
                SourceTextModule m = module.AsyncParentModules[i];
                // a. If execList does not contain m and m.[[EvaluationError]] is empty,
                //    then
                if (m.ModuleStatus != Status.kErrored && !execList.Contains(m))
                {
                    // i. Assert: m.[[Status]] is EVALUATING-ASYNC.
                    Debug.Assert(m.ModuleStatus == Status.kEvaluatingAsync);
                    // iii. If m.[[CycleRoot]].[[EvaluationError]] is empty, then
                    if (m.GetCycleRoot().ModuleStatus != Status.kErrored)
                    {
                        // 3. Set m.[[PendingAsyncDependencies]] to
                        //    m.[[PendingAsyncDependencies]] - 1.
                        m.PendingAsyncDependencies--;
                        // 4. If m.[[PendingAsyncDependencies]] = 0, then
                        if (!m.HasPendingAsyncDependencies)
                        {
                            // a. Append m to execList.
                            execList.Add(m);
                            // b. If m.[[HasTLA]] is false,
                            //    perform ! GatherAvailableAncestors(m, execList).
                            if (!m.HasToplevelAwait) worklist.Push(m);
                        }
                    }
                }
            }
        }
    }

    sealed class AsyncEvaluationOrdinalCompare : IComparer<SourceTextModule>
    {
        public static readonly AsyncEvaluationOrdinalCompare Instance = new();
        public int Compare(SourceTextModule? lhs, SourceTextModule? rhs) =>
            lhs!.AsyncEvaluationOrdinal.CompareTo(rhs!.AsyncEvaluationOrdinal);
    }

    /// <summary>SourceTextModule::GetModuleNamespace (for [module_request] of [module]).</summary>
    public static JSModuleNamespace GetModuleNamespace(Isolate isolate, SourceTextModule module, int moduleRequestIndex)
    {
        ModuleRequest moduleRequest = module.Info.ModuleRequests[moduleRequestIndex];
        // Source phase imports store a JSReceiver (not a Module) in
        // requested_modules.
        if (moduleRequest.Phase == ModuleImportPhase.kSource)
        {
            throw new InvalidOperationException("V8Sharp: GetModuleNamespace of a source phase import");
        }
        var requestedModule = (Module)module.RequestedModules[moduleRequestIndex]!;
        return Module.GetModuleNamespace(isolate, requestedModule, moduleRequest.Phase);
    }

    /// <summary>SourceTextModule::GetImportMeta: created on first use and passed to the embedder.</summary>
    public static JSObject GetImportMeta(Isolate isolate, SourceTextModule module)
    {
        if (module.ImportMeta is { } importMeta) return importMeta;
        // Isolate::RunHostInitializeImportMetaObjectCallback.
        importMeta = isolate.Factory.NewJSObjectWithNullProto();
        isolate.HostInitializeImportMetaObjectCallback?.Invoke(isolate, module, importMeta);
        module.ImportMeta = importMeta;
        return importMeta;
    }

    /// <summary>
    /// SourceTextModule::MaybeHandleEvaluationException. <paramref name="exception"/>
    /// null is a termination; returns whether the exception was catchable.
    /// </summary>
    bool MaybeHandleEvaluationException(Isolate isolate, List<SourceTextModule> stack, JSValue? exception)
    {
        // Step 9.
        if (exception is { } error)
        {
            // a. For each Cyclic Module Record m in stack, do
            foreach (SourceTextModule descendant in stack)
            {
                //   i. Assert: m.[[Status]] is EVALUATING.
                Debug.Assert(descendant.ModuleStatus == Status.kEvaluating);
                //  ii. Set m.[[Status]] to EVALUATED.
                // iii. Set m.[[EvaluationError]] to result.
                descendant.RecordError(isolate, error);
            }
            // A stack overflow at the InnerModuleEvaluation entry STACK_CHECK can throw
            // before this module was appended to `stack`, leaving its
            // [[EvaluationError]] empty. Record it directly so the top-level capability
            // rejects with the actual exception rather than the EMPTY (TheHole)
            // sentinel.
            if (Exception.IsTheHole) RecordError(isolate, error);
            return true;
        }
        // If the exception was a termination exception, rejecting the promise
        // would resume execution, and our API contract is to return an empty
        // handle. The module's status should be set to kErrored and the
        // exception field should be set to `null`.
        RecordError(isolate, null);
        foreach (SourceTextModule descendant in stack) descendant.RecordError(isolate, null);
        return false;
    }

    /// <summary>SourceTextModule::Evaluate.</summary>
    internal static JSPromise Evaluate(Isolate isolate, SourceTextModule module)
    {
        Debug.Assert(module.ModuleStatus is Status.kLinked or Status.kEvaluatingAsync or Status.kEvaluated or Status.kErrored);

        // 5. Let stack be a new empty List.
        var stack = new List<SourceTextModule>();
        int dfsIndex = 0;

        // 6. Let capability be ! NewPromiseCapability(%Promise%).
        JSPromise capability = PromiseBuiltins.NewJSPromise(isolate);

        // 7. Set module.[[TopLevelCapability]] to capability.
        module.TopLevelCapability = capability;

        // 8. Let result be InnerModuleEvaluation(module, stack, 0).
        // 9. If result is an abrupt completion, then
        bool abrupt = false;
        try
        {
            InnerModuleEvaluation(isolate, module, stack, ref dfsIndex);
        }
        catch (JavaScriptException e)
        {
            module.MaybeHandleEvaluationException(isolate, stack, e.Value);
            abrupt = true;
        }
        catch (TerminationException)
        {
            module.MaybeHandleEvaluationException(isolate, stack, null);
            throw;
        }

        if (abrupt)
        {
            // d. Perform ! Call(capability.[[Reject]], undefined,
            //                   «result.[[Value]]»).
            PromiseBuiltins.RejectPromise(isolate, capability, module.Exception, true);
        }
        else
        {
            // a. Assert: module.[[Status]] is either EVALUATING-ASYNC or EVALUATED.
            Debug.Assert(module.ModuleStatus >= Status.kEvaluatingAsync);

            // c. If module.[[AsyncEvaluation]] is false, then
            if (!module.HasAsyncEvaluationOrdinal)
            {
                // ii. Perform ! Call(capability.[[Resolve]], undefined,
                //                    «undefined»).
                PromiseBuiltins.ResolvePromise(isolate, capability, JSValue.Undefined);
            }

            // d. Assert: stack is empty.
            Debug.Assert(stack.Count == 0);
        }

        // 11. Return capability.[[Promise]].
        return capability;
    }

    /// <summary>SourceTextModule::AsyncModuleExecutionFulfilled.</summary>
    internal static void AsyncModuleExecutionFulfilled(Isolate isolate, SourceTextModule module)
    {
        // 1. If module.[[Status]] is EVALUATED, then
        if (module.ModuleStatus == Status.kErrored)
        {
            // a. Assert: module.[[EvaluationError]] is not EMPTY.
            // b. Return UNUSED.
            return;
        }

        // 2. Assert: module.[[Status]] is EVALUATING-ASYNC.
        Debug.Assert(module.ModuleStatus == Status.kEvaluatingAsync);

        // 5. Set module.[[AsyncEvaluation]] to false.
        module.AsyncEvaluationOrdinal = kAsyncEvaluateDidFinish;

        // 6. Set module.[[Status]] to EVALUATED.
        module.SetStatus(Status.kEvaluated);

        // 7. If module.[[TopLevelCapability]] is not EMPTY, then
        if (module.TopLevelCapability is { } capability)
        {
            //  a. Assert: module.[[CycleRoot]] is equal to module.
            Debug.Assert(ReferenceEquals(module.GetCycleRoot(), module));
            //   i. Perform ! Call(module.[[TopLevelCapability]].[[Resolve]], undefined,
            //                     «undefined»).
            PromiseBuiltins.ResolvePromise(isolate, capability, JSValue.Undefined);
        }

        // 8. Let execList be a new empty List.
        var execList = new SortedSet<SourceTextModule>(AsyncEvaluationOrdinalCompare.Instance);

        // 9. Perform GatherAvailableAncestors(module, execList).
        // 10. Let sortedExecList be a List of elements that are the elements of
        //    execList, in the order in which they had their [[AsyncEvaluation]]
        //    fields set to true in InnerModuleEvaluation.
        GatherAvailableAncestors(module, execList);

        // 12. For each Module m of sortedExecList, do
        foreach (SourceTextModule m in execList)
        {
            if (m.ModuleStatus == Status.kErrored)
            {
                // a. If m.[[Status]] is EVALUATED, then
                //    i. Assert: m.[[EvaluationError]] is not EMPTY.
            }
            else if (m.HasToplevelAwait)
            {
                // b. Else if m.[[HasTLA]] is true, then
                //    i. Perform ExecuteAsyncModule(m).
                ExecuteAsyncModule(isolate, m);
            }
            else
            {
                // c. Else,
                //    i. Let result be m.ExecuteModule().
                JSValue exception;
                try
                {
                    ExecuteModule(isolate, m);
                }
                catch (JavaScriptException e)
                {
                    exception = e.Value;
                    // ii. If result is an abrupt completion, then
                    //     1. Perform AsyncModuleExecutionRejected(m, result.[[Value]]).
                    AsyncModuleExecutionRejected(isolate, m, exception);
                    continue;
                }
                // iii. Else,
                //      1. Set m.[[AsyncEvaluation]] to false.
                m.AsyncEvaluationOrdinal = kAsyncEvaluateDidFinish;
                //      2. Set m.[[Status]] to EVALUATED.
                m.SetStatus(Status.kEvaluated);
                //      3. If m.[[TopLevelCapability]] is not EMPTY, then
                if (m.TopLevelCapability is { } mCapability)
                {
                    // a. Assert: m.[[CycleRoot]] and m are the same Module Record.
                    Debug.Assert(ReferenceEquals(m.GetCycleRoot(), m));
                    // b. Perform ! Call(m.[[TopLevelCapability]].[[Resolve]], undefined,
                    //    « undefined »).
                    PromiseBuiltins.ResolvePromise(isolate, mCapability, JSValue.Undefined);
                }
            }
        }
    }

    /// <summary>SourceTextModule::AsyncModuleExecutionRejected.</summary>
    internal static void AsyncModuleExecutionRejected(Isolate isolate, SourceTextModule module, JSValue exception)
    {
        // 1. If module.[[Status]] is EVALUATED, then
        if (module.ModuleStatus == Status.kErrored)
        {
            // a. Assert: module.[[EvaluationError]] is not empty.
            // b. Return UNUSED.
            return;
        }

        // 2. Assert: module.[[Status]] is EVALUATING-ASYNC.
        if (module.ModuleStatus != Status.kEvaluatingAsync)
        {
            throw new InvalidOperationException("V8Sharp: AsyncModuleExecutionRejected on a module that is not evaluating");
        }
        // 4. Assert: module.[[EvaluationError]] is EMPTY.
        Debug.Assert(module.Exception.IsTheHole);

        // 5. Set module.[[EvaluationError]] to ThrowCompletion(error).
        module.RecordError(isolate, exception);

        // 6. Set module.[[Status]] to EVALUATED.
        // (We have a status for kErrored, so don't set to kEvaluated.)
        module.AsyncEvaluationOrdinal = kAsyncEvaluateDidFinish;

        // 7. If module.[[TopLevelCapability]] is not EMPTY, then
        if (module.TopLevelCapability is { } capability)
        {
            // a. Assert: module.[[CycleRoot]] and module are the same Module Record.
            Debug.Assert(ReferenceEquals(module.GetCycleRoot(), module));
            //  b. Perform ! Call(module.[[TopLevelCapability]].[[Reject]],
            //                    undefined, «error»).
            PromiseBuiltins.RejectPromise(isolate, capability, exception, true);
        }

        // 8. For each Cyclic Module Record m of module.[[AsyncParentModules]], do
        for (int i = 0; i < module.AsyncParentModules.Count; i++)
        {
            // a. Perform AsyncModuleExecutionRejected(m, error).
            AsyncModuleExecutionRejected(isolate, module.AsyncParentModules[i], exception);
        }
        // 9. Return UNUSED.
    }

    /// <summary>SourceTextModule::ExecuteAsyncModule.</summary>
    static void ExecuteAsyncModule(Isolate isolate, SourceTextModule module)
    {
        // 1. Assert: module.[[Status]] is either EVALUATING or EVALUATING-ASYNC.
        Debug.Assert(module.ModuleStatus is Status.kEvaluating or Status.kEvaluatingAsync);
        // 2. Assert: module.[[HasTLA]] is true.
        Debug.Assert(module.HasToplevelAwait);

        // 3. Let capability be ! NewPromiseCapability(%Promise%).
        JSPromise capability = PromiseBuiltins.NewJSPromise(isolate);

        NativeContext nativeContext = isolate.NativeContext;
        Context executeAsyncModuleContext = isolate.Factory.NewBuiltinContext(nativeContext, kExecuteAsyncModuleContextLength);
        executeAsyncModuleContext[kExecuteAsyncModuleContextModuleSlot] = module;

        // 4. Let fulfilledClosure be a new Abstract Closure with no parameters that
        //    captures module and performs the following steps when called:
        //   a. Perform AsyncModuleExecutionFulfilled(module).
        //   b. Return undefined.
        // 5. Let onFulfilled be CreateBuiltinFunction(fulfilledClosure, 0, "", « »).
        JSFunction onFulfilled = RootSharedFunctions.AllocateRootFunctionWithContext(isolate, Builtin.CallAsyncModuleFulfilled,
            executeAsyncModuleContext, nativeContext);

        // 6. Let rejectedClosure be a new Abstract Closure with parameters (error)
        //    that captures module and performs the following steps when called:
        //   a. Perform AsyncModuleExecutionRejected(module, error).
        //   b. Return undefined.
        // 7. Let onRejected be CreateBuiltinFunction(rejectedClosure, 0, "", « »).
        JSFunction onRejected = RootSharedFunctions.AllocateRootFunctionWithContext(isolate, Builtin.CallAsyncModuleRejected,
            executeAsyncModuleContext, nativeContext);

        // 8. Perform PerformPromiseThen(capability.[[Promise]],
        //                               onFulfilled, onRejected).
        PromiseBuiltins.PerformPromiseThen(isolate, capability, onFulfilled, onRejected, JSValue.Undefined);

        // 9. Perform ! module.ExecuteModule(capability).
        InnerExecuteAsyncModule(isolate, module, capability);
    }

    /// <summary>SourceTextModule::InnerExecuteAsyncModule.</summary>
    static void InnerExecuteAsyncModule(Isolate isolate, SourceTextModule module, JSPromise capability)
    {
        // If we have an async module, then it has an associated
        // JSAsyncFunctionObject, which we then evaluate with the passed in promise
        // capability.
        var asyncFunctionObject = (JSAsyncFunctionObject)module.Code;
        asyncFunctionObject.Promise = capability;
        // AsyncModuleEvaluate: resume the async function object.
        InterpreterGenerators.InnerResume(isolate, asyncFunctionObject, JSValue.Undefined, JSGeneratorObject.ResumeMode.kNext);
    }

    /// <summary>SourceTextModule::ExecuteModule: resumes the module's generator to completion.</summary>
    static JSValue ExecuteModule(Isolate isolate, SourceTextModule module)
    {
        // Synchronous modules have an associated JSGeneratorObject.
        var generator = (JSGeneratorObject)module.Code;
        JSValue result = InterpreterGenerators.InnerResume(isolate, generator, JSValue.Undefined,
            JSGeneratorObject.ResumeMode.kNext);
        return ObjectOps.GetProperty(isolate, result, ReadOnlyRoots.value_string);
    }

    /// <summary>SourceTextModule::InnerModuleEvaluation. Throws the evaluation error.</summary>
    static void InnerModuleEvaluation(Isolate isolate, SourceTextModule module, List<SourceTextModule> stack,
        ref int dfsIndex)
    {
        StackCheck(isolate);
        Status moduleStatus = module.ModuleStatus;
        // InnerModuleEvaluation(module, stack, index)

        // 2. If module.[[Status]] is either EVALUATING-ASYNC or EVALUATED, then
        if (moduleStatus is Status.kEvaluatingAsync or Status.kEvaluating or Status.kEvaluated)
        {
            // a. If module.[[EvaluationError]] is undefined, return index.
            // 3. If module.[[Status]] is EVALUATING, return index.
            return;
        }
        if (moduleStatus == Status.kErrored)
        {
            // b. Otherwise return module.[[EvaluationError]].
            isolate.ReThrow(module.Exception);
        }

        // 4. Assert: module.[[Status]] is LINKED.
        if (moduleStatus != Status.kLinked)
        {
            throw new InvalidOperationException("V8Sharp: InnerModuleEvaluation on a module with status " + moduleStatus);
        }

        // 5. Set module.[[Status]] to EVALUATING.
        module.SetStatus(Status.kEvaluating);
        // 6. Set module.[[DFSIndex]] to index.
        module.DfsIndex = dfsIndex;
        // 7. Set module.[[DFSAncestorIndex]] to index.
        module.DfsAncestorIndex = dfsIndex;
        // 8. Set module.[[PendingAsyncDependencies]] to 0.
        Debug.Assert(!module.HasPendingAsyncDependencies);
        // 9. Set index to index + 1.
        dfsIndex++;
        // 10. Append module to stack.
        stack.Add(module);

        // There's an evaluation set to perform optimized check if a module is already
        // in evaluation_list. It's necessary to keep evaluation order as it's seen to
        // be spec compliant.
        ModuleRequest[] moduleRequests = module.Info.ModuleRequests;
        HeapObject?[] requestedModules = module.RequestedModules;
        var evaluationSet = new HashSet<Module>(ReferenceEqualityComparer.Instance);
        var evaluationList = new List<Module>(requestedModules.Length);
        HashSet<Module>? seenModules = null;
        for (int i = 0; i < requestedModules.Length; ++i)
        {
            ModuleImportPhase phase = moduleRequests[i].Phase;
            if (phase == ModuleImportPhase.kSource) continue;

            var requestedModule = (Module)requestedModules[i]!;
            if (phase == ModuleImportPhase.kDefer)
            {
                seenModules ??= new HashSet<Module>(ReferenceEqualityComparer.Instance);
                var asyncEvaluationList = new List<SourceTextModule>();
                GatherAsynchronousTransitiveDependencies(isolate, requestedModule, evaluationSet, asyncEvaluationList,
                    seenModules);
                foreach (SourceTextModule asyncModule in asyncEvaluationList) evaluationList.Add(asyncModule);
            }
            else if (evaluationSet.Add(requestedModule))
            {
                evaluationList.Add(requestedModule);
            }
        }

        // 11. For each ModuleRequest Record required of module.[[RequestedModules]],
        foreach (Module requestedModule in evaluationList)
        {
            // c. If requiredModule is a Cyclic Module Record, then
            if (requestedModule is SourceTextModule requiredModule)
            {
                // b. Set index to ? InnerModuleEvaluation(requiredModule, stack, index).
                InnerModuleEvaluation(isolate, requiredModule, stack, ref dfsIndex);
                Status requiredModuleStatus = requiredModule.ModuleStatus;

                // i. Assert: requiredModule.[[Status]] is one of EVALUATING,
                //    EVALUATING-ASYNC, or EVALUATED.
                Debug.Assert(requiredModuleStatus >= Status.kEvaluating && requiredModuleStatus != Status.kErrored);

                // iii. If requiredModule.[[Status]] is EVALUATING, then
                if (requiredModuleStatus == Status.kEvaluating)
                {
                    // 1. Set module.[[DFSAncestorIndex]] to
                    //    min(module.[[DFSAncestorIndex]],
                    //        requiredModule.[[DFSAncestorIndex]]).
                    module.DfsAncestorIndex = Math.Min(module.DfsAncestorIndex, requiredModule.DfsAncestorIndex);
                }
                else
                {
                    // iv. Else,
                    // 2. Set requiredModule to requiredModule.[[CycleRoot]].
                    requiredModule = requiredModule.GetCycleRoot();
                    requiredModuleStatus = requiredModule.ModuleStatus;

                    // 3. Assert: requiredModule.[[Status]] is either EVALUATING-ASYNC or
                    //    EVALUATED.
                    Debug.Assert(requiredModuleStatus >= Status.kEvaluatingAsync);

                    // 4. If requiredModule.[[EvaluationError]] is not EMPTY,
                    //    return ? module.[[EvaluationError]].
                    if (requiredModuleStatus == Status.kErrored) isolate.ReThrow(requiredModule.Exception);
                }
                // v. If requiredModule.[[AsyncEvaluation]] is true, then
                if (requiredModule.HasAsyncEvaluationOrdinal)
                {
                    // 1. Set module.[[PendingAsyncDependencies]] to
                    //    module.[[PendingAsyncDependencies]] + 1.
                    module.PendingAsyncDependencies++;
                    // 2. Append module to requiredModule.[[AsyncParentModules]].
                    requiredModule.AsyncParentModules.Add(module);
                }
            }
            else
            {
                // b. Set index to ? InnerModuleEvaluation(requiredModule, stack, index).
                // (Out of order because InnerModuleEvaluation is type-driven.)
                Module.Evaluate(isolate, requestedModule);
            }
        }

        // 12. If module.[[PendingAsyncDependencies]] > 0 or module.[[HasTLA]] is
        //     true, then
        if (module.HasPendingAsyncDependencies || module.HasToplevelAwait)
        {
            // a. Assert: module.[[AsyncEvaluation]] is false and was never previously
            //    set to true.
            Debug.Assert(module.AsyncEvaluationOrdinal == kNotAsyncEvaluated);

            // b. Set module.[[AsyncEvaluation]] to true.
            // c. NOTE: The order in which module records have their [[AsyncEvaluation]]
            //    fields transition to true is significant.
            module.AsyncEvaluationOrdinal = isolate.NextModuleAsyncEvaluationOrdinal();

            // c. If module.[[PendingAsyncDependencies]] = 0, perform
            //    ExecuteAsyncModule(module).
            if (!module.HasPendingAsyncDependencies) ExecuteAsyncModule(isolate, module);
        }
        else
        {
            // 13. Else,
            // a. Perform ? module.ExecuteModule().
            ExecuteModule(isolate, module);
        }

        MaybeTransitionComponent(isolate, module, stack, Status.kEvaluated);
    }

    /// <summary>SourceTextModule::IsModuleSCCEvaluated (https://tc39.es/proposal-defer-import-eval/#sec-IsModuleSCCEvaluated).</summary>
    static bool IsModuleSCCEvaluated(SourceTextModule module)
    {
        // It's necessary to check if [[CycleRoot]] is not empty here because:
        //   1. A module starts with its [[CycleRoot]] as `TheHole` and it's set
        //   once the cycle is detected, or when the module finishes its evaluation
        //   without errors.
        //   2. GatherAsynchronousTransitiveDependencies can be called with a module
        //   where it's `[[CycleRoot]]` is not set yet, and since it depends on
        //   `IsModuleSCCEvaluated`, we need such guard. A later call from
        //   `ReadyForSyncExecution` for the same module will have its `[[CycleRoot]]`
        //   set, unless its evaluation errored.
        if (module.CycleRoot is { } cycleRoot)
        {
            return cycleRoot.ModuleStatus is Status.kEvaluated or Status.kErrored;
        }
        return module.ModuleStatus is Status.kEvaluated or Status.kErrored;
    }

    /// <summary>
    /// SourceTextModule::GatherAsynchronousTransitiveDependencies
    /// (https://tc39.es/proposal-defer-import-eval/#sec-GatherAsynchronousTransitiveDependencies).
    /// </summary>
    public static void GatherAsynchronousTransitiveDependencies(Isolate isolate, Module module, HashSet<Module> evaluationSet,
        List<SourceTextModule> evaluationList, HashSet<Module> seenSet)
    {
        if (!seenSet.Add(module)) return;
        if (module is not SourceTextModule sourceTextModule) return;

        if (sourceTextModule.ModuleStatus == Status.kEvaluating || IsModuleSCCEvaluated(sourceTextModule)) return;

        if (sourceTextModule.HasToplevelAwait)
        {
            if (evaluationSet.Add(sourceTextModule)) evaluationList.Add(sourceTextModule);
            return;
        }

        ModuleRequest[] moduleRequests = sourceTextModule.Info.ModuleRequests;
        HeapObject?[] requestedModules = sourceTextModule.RequestedModules;
        for (int i = 0; i < requestedModules.Length; ++i)
        {
            // Only process evaluation phase modules (skip source phase)
            if (moduleRequests[i].Phase == ModuleImportPhase.kSource) continue;
            GatherAsynchronousTransitiveDependencies(isolate, (Module)requestedModules[i]!, evaluationSet, evaluationList,
                seenSet);
        }
    }

    /// <summary>SourceTextModule::ReadyForSyncExecution (https://tc39.es/proposal-defer-import-eval/#sec-ReadyForSyncExecution).</summary>
    public static bool ReadyForSyncExecution(Isolate isolate, Module module, HashSet<Module> seen)
    {
        if (!seen.Add(module)) return true;
        if (module is not SourceTextModule sourceTextModule) return true;
        if (IsModuleSCCEvaluated(sourceTextModule)) return true;
        if (sourceTextModule.ModuleStatus is Status.kEvaluating or Status.kEvaluatingAsync) return false;
        if (sourceTextModule.HasToplevelAwait) return false;

        ModuleRequest[] moduleRequests = sourceTextModule.Info.ModuleRequests;
        HeapObject?[] requestedModules = sourceTextModule.RequestedModules;
        for (int i = 0; i < requestedModules.Length; ++i)
        {
            if (moduleRequests[i].Phase == ModuleImportPhase.kSource) continue;
            if (!ReadyForSyncExecution(isolate, (Module)requestedModules[i]!, seen)) return false;
        }
        return true;
    }

    /// <summary>SourceTextModule::Reset.</summary>
    internal static void Reset(Isolate isolate, SourceTextModule module)
    {
        Debug.Assert(module.ImportMeta is null);
        if (module.ModuleStatus == Status.kLinking)
        {
            // A kLinking module on stack holds either a JSFunction (before
            // RunInitializationCode) or a JSGeneratorObject (if a later module in the
            // same SCC failed RunInitializationCode). Restore the underlying
            // SharedFunctionInfo for kUnlinked.
            module.Code = module.GetSharedFunctionInfo();
        }
        module.RegularExports = new Cell?[module.RegularExports.Length];
        module.RegularImports = new Cell?[module.RegularImports.Length];
        module.RequestedModules = new HeapObject?[module.RequestedModules.Length];
        module.DfsIndex = -1;
        module.DfsAncestorIndex = -1;
    }

    /// <summary>
    /// SourceTextModule::GetStalledTopLevelAwaitMessages: the modules whose
    /// top-level await never settled, with a message at their suspension point.
    /// </summary>
    public List<(SourceTextModule Module, JSMessageObject Message)> GetStalledTopLevelAwaitMessages(Isolate isolate)
    {
        var visited = new HashSet<SourceTextModule>(ReferenceEqualityComparer.Instance);
        var stalledModules = new List<SourceTextModule>();
        InnerGetStalledTopLevelAwaitModule(visited, stalledModules);
        var result = new List<(SourceTextModule, JSMessageObject)>(stalledModules.Count);
        foreach (SourceTextModule found in stalledModules)
        {
            var code = (JSGeneratorObject)found.Code;
            SharedFunctionInfo shared = found.GetSharedFunctionInfo();
            // JSGeneratorObject::code_offset: the continuation of a suspended generator.
            var location = new MessageLocation(shared.Script!, shared, code.ContinuationValue);
            JSMessageObject message = MessageHandler.MakeMessageObject(isolate, MessageTemplate.TopLevelAwaitStalled, location,
                JSValue.Null);
            result.Add((found, message));
        }
        return result;
    }

    void InnerGetStalledTopLevelAwaitModule(HashSet<SourceTextModule> visited, List<SourceTextModule> result)
    {
        // If it's a module that is waiting for no other modules but itself,
        // it's what we are looking for. Add it to the results.
        if (!HasPendingAsyncDependencies && HasAsyncEvaluationOrdinal)
        {
            result.Add(this);
            return;
        }
        // The module isn't what we are looking for, continue looking in the graph.
        ModuleRequest[] requests = Info.ModuleRequests;
        for (int i = 0; i < RequestedModules.Length; ++i)
        {
            if (requests[i].Phase != ModuleImportPhase.kEvaluation) continue;
            if (RequestedModules[i] is SourceTextModule sourceTextModule && visited.Add(sourceTextModule))
            {
                sourceTextModule.InnerGetStalledTopLevelAwaitModule(visited, result);
            }
        }
    }
}

/// <summary>V8's SyntheticModule (Synthetic Module Record).</summary>
public sealed class SyntheticModule() : Module(InstanceType.SyntheticModuleType)
{
    public JSString Name = ReadOnlyRoots.empty_string;
    public JSString[] ExportNames = [];
    public SyntheticModuleEvaluationSteps EvaluationSteps = null!;
    /// <summary>The embedder's data (v8::Module::GetSyntheticModuleHostDefinedOptions).</summary>
    public JSValue HostDefinedOptions;

    /// <summary>Factory::NewSyntheticModule.</summary>
    public static SyntheticModule New(Isolate isolate, JSString moduleName, JSString[] exportNames,
        SyntheticModuleEvaluationSteps evaluationSteps, JSValue hostDefinedOptions = default) => new()
    {
        Name = moduleName,
        ExportNames = exportNames,
        EvaluationSteps = evaluationSteps,
        HostDefinedOptions = hostDefinedOptions,
        Exports = ObjectHashTable.New(exportNames.Length),
    };

    /// <summary>SyntheticModule::SetExport (SetSyntheticModuleBinding).</summary>
    public static void SetExport(Isolate isolate, SyntheticModule module, JSString exportName, JSValue exportValue)
    {
        JSValue exportObject = module.Exports.Lookup(isolate, exportName);
        if (exportObject.HeapObjectOrNull is not Cell cell)
        {
            isolate.Throw(isolate.Factory.NewReferenceError(MessageTemplate.ModuleExportUndefined, exportName));
            return;
        }
        // Spec step 2: Set the mutable binding of export_name to export_value
        cell.Value = exportValue;
    }

    /// <summary>SyntheticModule::ResolveExport.</summary>
    internal static Cell? ResolveExport(Isolate isolate, SyntheticModule module, JSString? moduleSpecifier,
        JSString exportName, MessageLocation loc, bool mustResolve)
    {
        JSValue obj = module.Exports.Lookup(isolate, exportName);
        if (obj.HeapObjectOrNull is Cell cell) return cell;
        if (!mustResolve) return null;
        isolate.ThrowAt(isolate.Factory.NewSyntaxError(MessageTemplate.UnresolvableExport,
            moduleSpecifier is null ? JSValue.Undefined : moduleSpecifier, exportName), loc);
        return null;
    }

    /// <summary>SyntheticModule::PrepareInstantiate.</summary>
    internal static void PrepareInstantiate(Isolate isolate, SyntheticModule module)
    {
        ObjectHashTable exports = module.Exports;
        // Spec step 7: For each export_name in module->export_names...
        foreach (JSString name in module.ExportNames)
        {
            // Spec step 7.1: Create a new mutable binding for export_name.
            // Spec step 7.2: Initialize the new mutable binding to undefined.
            Debug.Assert(exports.Lookup(isolate, name).IsTheHole);
            exports = ObjectHashTable.Put(isolate, exports, name, new Cell(JSValue.Undefined));
        }
        module.Exports = exports;
    }

    /// <summary>SyntheticModule::FinishInstantiate.</summary>
    internal static void FinishInstantiate(Isolate isolate, SyntheticModule module)
    {
        module.SetStatus(Status.kLinked);
        // Ensure that if the namespace binding was created it is not empty.
        if (module.ModuleNamespaceCell is not null) GetModuleNamespace(isolate, module);
    }

    /// <summary>SyntheticModule::Evaluate.</summary>
    internal static JSPromise Evaluate(Isolate isolate, SyntheticModule module)
    {
        module.SetStatus(Status.kEvaluating);
        JSPromise capability;
        try
        {
            capability = module.EvaluationSteps(isolate, module);
        }
        catch (JavaScriptException e)
        {
            module.RecordError(isolate, e.Value);
            throw;
        }
        module.SetStatus(Status.kEvaluated);
        module.TopLevelCapability = capability;
        return capability;
    }
}

}

namespace V8Sharp
{
    public sealed partial class Isolate
    {
        int _nextModuleAsyncEvaluationOrdinal = Objects.SourceTextModule.kFirstAsyncEvaluationOrdinal;

        /// <summary>Isolate::NextModuleAsyncEvaluationOrdinal.</summary>
        public int NextModuleAsyncEvaluationOrdinal() => _nextModuleAsyncEvaluationOrdinal++;

        /// <summary>The embedder's HostInitializeImportMetaObjectCallback.</summary>
        public Action<Isolate, Objects.SourceTextModule, JSObject>? HostInitializeImportMetaObjectCallback;

        /// <summary>
        /// The embedder's HostImportModuleWithPhaseDynamicallyCallback: (referrer
        /// resource name or null, specifier, phase, import attributes [key, value,
        /// ...]) returns the promise of the import. (V8 also has the older
        /// HostImportModuleDynamicallyCallback without the phase; d8 sets only
        /// this one.)
        /// </summary>
        public Func<Isolate, JSValue, JSString, V8Sharp.Ast.ModuleImportPhase, FixedArray, JSPromise>?
            HostImportModuleWithPhaseDynamicallyCallback;
    }
}
