// Port of src/objects/contexts.{h,cc,-inl.h}: Context, NativeContext and
// ScriptContextTable. The NATIVE_CONTEXT_FIELDS slot enum and typed accessors
// are generated into Contexts.Generated.cs.
//
// V8 distinguishes context kinds by the context's map instance type
// (FUNCTION_CONTEXT_TYPE ...); V8Sharp's contexts have no map, so the kind is
// a field (ContextKind) and the native context is a direct reference.
using System.Runtime.CompilerServices;
using V8Sharp.Common;

namespace V8Sharp.Objects;

/// <summary>The context instance types of V8 (FUNCTION_CONTEXT_TYPE ...), as a kind field.</summary>
public enum ContextKind : byte
{
    AwaitContext,
    BlockContext,
    CatchContext,
    DebugEvaluateContext,
    EvalContext,
    FunctionContext,
    ModuleContext,
    NativeContext,
    ScriptContext,
    WithContext,
}

/// <summary>V8's ContextLookupFlags.</summary>
[Flags]
public enum ContextLookupFlags
{
    DONT_FOLLOW_CHAINS = 0,
    FOLLOW_CONTEXT_CHAIN = 1 << 0,
    FOLLOW_PROTOTYPE_CHAIN = 1 << 1,
    FOLLOW_CHAINS = FOLLOW_CONTEXT_CHAIN | FOLLOW_PROTOTYPE_CHAIN,
}

/// <summary>
/// V8's Context: a heap-allocated activation record. Slot 0 is the scope info,
/// slot 1 the previous context, slot 2 (when the scope info says so) the
/// extension object; context locals follow.
/// </summary>
public partial class Context : HeapObject
{
    public const int kNotFound = -1;
    public const int kNoContext = 0;

    public static readonly int FIRST_FUNCTION_MAP_INDEX = (int)Field.SLOPPY_FUNCTION_MAP_INDEX;
    public static readonly int LAST_FUNCTION_MAP_INDEX = (int)Field.CLASS_FUNCTION_MAP_INDEX;

    /// <summary>The slots (V8's elements), including the header slots.</summary>
    public readonly JSValue[] Slots;

    /// <summary>The kind (V8: the context map's instance type).</summary>
    public readonly ContextKind Kind;

    NativeContext? _nativeContext;

    public Context(ContextKind kind, int length, NativeContext? nativeContext) : base(InstanceType.ContextType)
    {
        Kind = kind;
        Slots = new JSValue[length];
        _nativeContext = nativeContext;
    }

    public int Length => Slots.Length;

    public JSValue this[int index]
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => Slots[index];
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        set => Slots[index] = value;
    }

    public JSValue Get(int index) => Slots[index];
    public void Set(int index, JSValue value) => Slots[index] = value;

    public ScopeInfo ScopeInfo
    {
        get => Slots[(int)Field.SCOPE_INFO_INDEX].As<ScopeInfo>();
        set => Slots[(int)Field.SCOPE_INFO_INDEX] = value;
    }

    /// <summary>The enclosing context; null for the native context.</summary>
    public Context? Previous
    {
        get => Slots[(int)Field.PREVIOUS_INDEX].AsOrNull<Context>();
        set => Slots[(int)Field.PREVIOUS_INDEX] = JSValue.FromObject(value);
    }

    public bool HasExtension() => ScopeInfo.HasContextExtensionSlot && !Slots[(int)Field.EXTENSION_INDEX].IsUndefined;

    public JSValue Extension
    {
        get => Slots[(int)Field.EXTENSION_INDEX];
        set => Slots[(int)Field.EXTENSION_INDEX] = value;
    }

    public NativeContext NativeContext => _nativeContext ?? (NativeContext)this;

    internal void SetNativeContext(NativeContext nativeContext) => _nativeContext = nativeContext;

    public JSGlobalObject GlobalObject => NativeContext.GlobalObject;
    public JSGlobalProxy GlobalProxy => NativeContext.GlobalProxyObject;

    public bool IsFunctionContext => Kind == ContextKind.FunctionContext;
    public bool IsCatchContext => Kind == ContextKind.CatchContext;
    public bool IsWithContext => Kind == ContextKind.WithContext;
    public bool IsDebugEvaluateContext => Kind == ContextKind.DebugEvaluateContext;
    public bool IsAwaitContext => Kind == ContextKind.AwaitContext;
    public bool IsBlockContext => Kind == ContextKind.BlockContext;
    public bool IsModuleContext => Kind == ContextKind.ModuleContext;
    public bool IsEvalContext => Kind == ContextKind.EvalContext;
    public bool IsScriptContext => Kind == ContextKind.ScriptContext;
    public bool IsNativeContext => Kind == ContextKind.NativeContext;
    public bool HasContextCells => ScopeInfo.HasContextCells;

    /// <summary>Context::IsElementTdzHole.</summary>
    public bool IsElementTdzHole(int index) => Slots[index].IsTheHole;

    /// <summary>Context::Initialize: context locals that need initialization start as the TDZ hole.</summary>
    public void Initialize()
    {
        ScopeInfo scopeInfo = ScopeInfo;
        int header = scopeInfo.ContextHeaderLength();
        for (int var = 0; var < scopeInfo.ContextLocalCount; var++)
        {
            if (scopeInfo.ContextLocalInitFlag(var) == InitializationFlag.kNeedsInitialization)
            {
                Slots[header + var] = JSValue.TheHole;
            }
        }
    }

    public bool IsDeclarationContext()
    {
        if (IsFunctionContext || IsNativeContext || IsScriptContext || IsModuleContext) return true;
        if (IsEvalContext) return ScopeInfo.LanguageMode == LanguageMode.Strict;
        if (!IsBlockContext) return false;
        return ScopeInfo.IsDeclarationScope;
    }

    public Context DeclarationContext()
    {
        Context current = this;
        while (!current.IsDeclarationContext()) current = current.Previous!;
        return current;
    }

    public Context ClosureContext()
    {
        Context current = this;
        while (!current.IsFunctionContext && !current.IsScriptContext && !current.IsModuleContext &&
               !current.IsNativeContext && !current.IsEvalContext)
        {
            current = current.Previous!;
        }
        return current;
    }

    public JSObject? ExtensionObject()
    {
        JSValue obj = Extension;
        if (obj.IsUndefined) return null;
        return obj.As<JSObject>();
    }

    public JSReceiver? ExtensionReceiver() => IsWithContext ? Extension.As<JSReceiver>() : ExtensionObject();

    public Context ScriptContext()
    {
        Context current = this;
        while (!current.IsScriptContext) current = current.Previous!;
        return current;
    }

    /// <summary>The module of the closest module context (a SourceTextModule).</summary>
    public HeapObject Module()
    {
        Context current = this;
        while (!current.IsModuleContext) current = current.Previous!;
        return current.Extension.Object;
    }

    /// <summary>Context::FunctionMapIndex: the native context slot holding the initial map of a closure.</summary>
    public static int FunctionMapIndex(LanguageMode languageMode, FunctionKind kind, bool hasSharedName)
    {
        if (Globals.IsClassConstructor(kind))
        {
            // Like the strict function map, but with no 'name' accessor. 'name'
            // needs to be the last property and it is added during instantiation,
            // in case a static property with the same name exists"
            return (int)Field.CLASS_FUNCTION_MAP_INDEX;
        }

        int baseIndex;
        if (Globals.IsGeneratorFunction(kind))
        {
            baseIndex = Globals.IsAsyncFunction(kind)
                ? (int)Field.ASYNC_GENERATOR_FUNCTION_MAP_INDEX
                : (int)Field.GENERATOR_FUNCTION_MAP_INDEX;
        }
        else if (Globals.IsAsyncFunction(kind) || Globals.IsModuleWithTopLevelAwait(kind))
        {
            baseIndex = (int)Field.ASYNC_FUNCTION_MAP_INDEX;
        }
        else if (Globals.IsStrictFunctionWithoutPrototype(kind))
        {
            baseIndex = (int)Field.STRICT_FUNCTION_WITHOUT_PROTOTYPE_MAP_INDEX;
        }
        else
        {
            baseIndex = languageMode == LanguageMode.Strict
                ? (int)Field.STRICT_FUNCTION_MAP_INDEX
                : (int)Field.SLOPPY_FUNCTION_MAP_INDEX;
        }
        int offset = hasSharedName ? 0 : 1;
        return baseIndex + offset;
    }

    public static int ArrayMapIndex(ElementsKind elementsKind) =>
        (int)elementsKind + (int)Field.FIRST_JS_ARRAY_MAP_SLOT;

    public override string ToString() => $"<{Kind}[{Slots.Length}]>";
}

/// <summary>
/// V8's NativeContext: the realm. Holds the intrinsics (NATIVE_CONTEXT_FIELDS),
/// the global object (in the extension slot) and the realm's microtask queue.
/// </summary>
public sealed partial class NativeContext : Context
{
    public NativeContext() : base(ContextKind.NativeContext, (int)Field.NATIVE_CONTEXT_SLOTS, null)
    {
        SetNativeContext(this);
    }

    /// <summary>
    /// Whether the context is a ShadowRealm's: V8 gives such a context the
    /// shadow_realm_scope_info (scope type SHADOW_REALM_SCOPE).
    /// </summary>
    public bool IsShadowRealm;

    /// <summary>The global object is kept in the native context's extension slot.</summary>
    public new JSGlobalObject GlobalObject
    {
        get => Extension.As<JSGlobalObject>();
        set => Extension = value;
    }

    /// <summary>NativeContext::microtask_queue.</summary>
    public MicrotaskQueue? MicrotaskQueue;

    // V8 keeps error_stack_getter_fun_template and error_stack_setter_fun_template
    // as isolate roots (FunctionTemplateInfos) and instantiates them per context on
    // first use. V8Sharp has no function templates; the Bootstrapper creates the two
    // JSFunctions eagerly for each native context.
    public JSFunction? ErrorStackGetterFun;
    public JSFunction? ErrorStackSetterFun;

    public Map? GetInitialJSArrayMap(ElementsKind kind)
    {
        if (!ElementsKinds.IsFastElementsKind(kind)) return null;
        return Slots[ArrayMapIndex(kind)].As<Map>();
    }

    /// <summary>NativeContext::IncrementErrorsThrown.</summary>
    public void IncrementErrorsThrown()
    {
        JSValue current = ErrorsThrown;
        ErrorsThrown = JSValue.FromNumber((current.IsNumber ? current.Number : 0) + 1);
    }

    public int GetErrorsThrown() => ErrorsThrown.IsNumber ? (int)ErrorsThrown.Number : 0;

    /// <summary>NativeContext::RunPromiseHook is not ported (no promise hooks); see todo.md.</summary>
    public bool HasPromiseHooks => false;
}

/// <summary>V8's ScriptContextTable: the script contexts of a native context and a name index over their lexical declarations.</summary>
public sealed class ScriptContextTable : HeapObject
{
    Context[] _contexts;
    int _length;
    readonly Dictionary<JSString, int> _namesToContextIndex = new(ReferenceEqualityComparer.Instance);

    public ScriptContextTable(int capacity = 16) : base(InstanceType.FixedArrayType)
    {
        _contexts = new Context[Math.Max(capacity, 1)];
    }

    public int Length => _length;

    public Context Get(int index) => _contexts[index];

    /// <summary>ScriptContextTable::Add: appends a script context, indexing its local names.</summary>
    public static ScriptContextTable Add(Isolate isolate, ScriptContextTable table, Context scriptContext, bool ignoreDuplicates)
    {
        int oldLength = table._length;
        if (oldLength == table._contexts.Length)
        {
            Array.Resize(ref table._contexts, oldLength + (oldLength >> 1) + 16);
        }
        ScopeInfo scopeInfo = scriptContext.ScopeInfo;
        int localCount = scopeInfo.ContextLocalCount;
        for (int i = 0; i < localCount; i++)
        {
            JSString name = scopeInfo.ContextLocalName(i);
            if (ignoreDuplicates && table._namesToContextIndex.ContainsKey(name)) continue;
            // NameToIndexHashTable::Add overwrites an existing entry.
            table._namesToContextIndex[name] = oldLength;
        }
        table._contexts[oldLength] = scriptContext;
        table._length = oldLength + 1;
        return table;
    }

    /// <summary>ScriptContextTable::Lookup: finds a lexical variable of a script context.</summary>
    public bool Lookup(JSString name, out ScopeInfo.VariableLookupResult result)
    {
        result = default;
        if (!_namesToContextIndex.TryGetValue(name, out int index)) return false;
        Context context = _contexts[index];
        int slotIndex = context.ScopeInfo.ContextSlotIndex(name, out result);
        if (slotIndex < 0) return false;
        result.ContextIndex = index;
        result.SlotIndex = slotIndex;
        return true;
    }
}
