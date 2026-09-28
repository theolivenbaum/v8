// Port of src/runtime/runtime-scopes.cc (DeclareGlobals, DeclareEvalVar /
// DeclareEvalFunction, the arguments objects, NewClosure, the context
// constructors, the lookup slot loads, stores and deletes) and of
// Context::Lookup from src/objects/contexts.cc.
using V8Sharp.Interpreter;

namespace V8Sharp.Runtime;

public static class RuntimeScopes
{
    // ---- Context::Lookup ---------------------------------------------------------------

    /// <summary>The result of Context::Lookup (its out parameters).</summary>
    public struct ContextLookupResult
    {
        public int Index;
        public PropertyAttributes Attributes;
        public InitializationFlag InitFlag;
        public VariableMode Mode;
        public bool IsSloppyFunctionName;
    }

    /// <summary>
    /// UnscopableLookup (contexts.cc): HasBinding of an object environment,
    /// taking @@unscopables into account for with contexts.
    /// </summary>
    static bool UnscopableLookup(ref LookupIterator it, bool isWithContext)
    {
        Isolate isolate = it.Isolate;
        bool found = JSReceiver.HasProperty(ref it);
        if (!isWithContext || !found) return found;

        JSValue unscopables = ObjectOps.GetProperty(isolate, it.GetReceiver(), ReadOnlyRoots.unscopables_symbol);
        if (!unscopables.IsJSReceiver) return true;
        JSValue blocklist = ObjectOps.GetProperty(isolate, unscopables, it.Name);
        return !ObjectOps.BooleanValue(blocklist);
    }

    static PropertyAttributes GetAttributesForMode(VariableMode mode) =>
        Globals.IsImmutableLexicalOrPrivateVariableMode(mode) ? PropertyAttributes.READ_ONLY : PropertyAttributes.NONE;

    /// <summary>
    /// Context::Lookup: finds <paramref name="name"/> in the context chain.
    /// Returns the holder (a context, a JSReceiver, or a module), or null.
    /// </summary>
    public static HeapObject? Lookup(Isolate isolate, Context context, JSString name, ContextLookupFlags flags,
        out ContextLookupResult result)
    {
        bool followContextChain = (flags & ContextLookupFlags.FOLLOW_CONTEXT_CHAIN) != 0;
        result = new ContextLookupResult
        {
            Index = Context.kNotFound,
            Attributes = PropertyAttributes.ABSENT,
            InitFlag = InitializationFlag.kCreatedInitialized,
            Mode = VariableMode.Var,
        };

        do
        {
            // 1. Check global objects, subjects of with, and extension objects.
            if ((context.IsNativeContext || context.IsWithContext || context.IsFunctionContext || context.IsBlockContext) &&
                context.HasExtension() && context.ExtensionReceiver() is JSReceiver obj)
            {
                if (context.IsNativeContext)
                {
                    // Try other script contexts.
                    ScriptContextTable scriptContexts = context.NativeContext.ScriptContextTable;
                    if (scriptContexts.Lookup(name, out ScopeInfo.VariableLookupResult r))
                    {
                        Context scriptContext = scriptContexts.Get(r.ContextIndex);
                        result.Index = r.SlotIndex;
                        result.Mode = r.Mode;
                        result.InitFlag = r.InitFlag;
                        result.Attributes = GetAttributesForMode(r.Mode);
                        return scriptContext;
                    }
                }

                // Context extension objects needs to behave as if they have no
                // prototype.  So even if we want to follow prototype chains, we need
                // to only do a local lookup for context extension objects.
                PropertyAttributes attributes;
                if ((flags & ContextLookupFlags.FOLLOW_PROTOTYPE_CHAIN) == 0 ||
                    obj.Map.InstanceType == InstanceType.JSContextExtensionObjectType)
                {
                    attributes = JSReceiver.GetOwnPropertyAttributes(isolate, obj, name);
                }
                else if (ScopeInfo.VariableIsSynthetic(name))
                {
                    // A with context will never bind "this", but debug-eval may look into
                    // a with context when resolving "this".
                    attributes = PropertyAttributes.ABSENT;
                }
                else
                {
                    var it = new LookupIterator(isolate, obj, name, obj);
                    bool found = UnscopableLookup(ref it, context.IsWithContext);
                    // Luckily, consumers of |maybe| only care whether the property
                    // was absent or not, so we can return a dummy |NONE| value
                    // for its attributes when it was present.
                    attributes = found ? PropertyAttributes.NONE : PropertyAttributes.ABSENT;
                }

                result.Attributes = attributes;
                if (attributes != PropertyAttributes.ABSENT) return obj;
            }

            // 2. Check the context proper if it has slots.
            if (context.IsFunctionContext || context.IsBlockContext || context.IsScriptContext || context.IsEvalContext ||
                context.IsModuleContext || context.IsCatchContext)
            {
                // Use serialized scope information of functions and blocks to search
                // for the context index.
                ScopeInfo scopeInfo = context.ScopeInfo;
                int slotIndex = scopeInfo.ContextSlotIndex(name, out ScopeInfo.VariableLookupResult lookupResult);
                if (slotIndex >= 0)
                {
                    // Re-direct lookup to the ScriptContextTable in case we find a hole in
                    // a REPL script context. REPL scripts allow re-declaration of
                    // script-level let bindings. The value itself is stored in the script
                    // context of the first script that declared a variable, all other
                    // script contexts will contain 'the hole' for that particular name.
                    if (scopeInfo.IsReplModeScope && context.IsElementTdzHole(slotIndex))
                    {
                        context = context.Previous!;
                        continue;
                    }

                    result.Index = slotIndex;
                    result.Mode = lookupResult.Mode;
                    result.InitFlag = lookupResult.InitFlag;
                    result.Attributes = GetAttributesForMode(lookupResult.Mode);
                    return context;
                }

                // Check the slot corresponding to the intermediate context holding
                // only the function name variable. It's conceptually (and spec-wise)
                // in an outer scope of the function's declaration scope.
                if (followContextChain && context.IsFunctionContext)
                {
                    int functionIndex = scopeInfo.FunctionContextSlotIndex(name);
                    if (functionIndex >= 0)
                    {
                        result.Index = functionIndex;
                        result.Attributes = PropertyAttributes.READ_ONLY;
                        result.InitFlag = InitializationFlag.kCreatedInitialized;
                        result.Mode = VariableMode.Const;
                        result.IsSloppyFunctionName = scopeInfo.LanguageMode == LanguageMode.Sloppy;
                        return context;
                    }
                }

                // Lookup variable in module imports and exports.
                if (context.IsModuleContext)
                {
                    int cellIndex = scopeInfo.ModuleIndex(name, out VariableMode mode, out InitializationFlag flag, out _, out _);
                    if (cellIndex != 0)
                    {
                        result.Index = cellIndex;
                        result.Mode = mode;
                        result.InitFlag = flag;
                        result.Attributes = RuntimeModules.GetCellIndexKindIsExport(cellIndex)
                            ? GetAttributesForMode(mode)
                            : PropertyAttributes.READ_ONLY;
                        return context.Module();
                    }
                }
            }
            else if (context.IsDebugEvaluateContext)
            {
                // Check materialized locals.
                if (context.Extension.HeapObjectOrNull is JSReceiver extension)
                {
                    var it = new LookupIterator(isolate, extension, name, extension);
                    if (JSReceiver.HasProperty(ref it))
                    {
                        result.Attributes = PropertyAttributes.NONE;
                        return extension;
                    }
                }
            }

            // 3. Prepare to continue with the previous (next outermost) context.
            if (context.IsNativeContext) break;

            context = context.Previous!;
        } while (followContextChain);

        return null;
    }

    // ---- Lookup slots ----------------------------------------------------------------------

    /// <summary>LoadLookupSlot (runtime-scopes.cc).</summary>
    public static JSValue LoadLookupSlot(Isolate isolate, Context context, JSString name, ShouldThrow shouldThrow) =>
        LoadLookupSlot(isolate, context, name, shouldThrow, out _);

    /// <summary>LoadLookupSlot with the receiver of a call (Runtime_LoadLookupSlotForCall).</summary>
    public static JSValue LoadLookupSlot(Isolate isolate, Context context, JSString name, ShouldThrow shouldThrow,
        out JSValue receiver)
    {
        HeapObject? holder = Lookup(isolate, context, name, ContextLookupFlags.FOLLOW_CHAINS, out ContextLookupResult r);

        if (holder is not null && holder is not Context && holder is not JSReceiver)
        {
            // A SourceTextModule.
            receiver = JSValue.Undefined;
            return RuntimeModules.LoadVariable(isolate, holder, r.Index);
        }
        if (r.Index != Context.kNotFound)
        {
            var holderContext = (Context)holder!;
            // If the "property" we were looking for is a local variable, the
            // receiver is the global object; see ECMA-262, 3rd., 10.1.6 and 10.2.3.
            receiver = JSValue.Undefined;
            // Check for uninitialized bindings.
            if (r.InitFlag == InitializationFlag.kNeedsInitialization && holderContext.IsElementTdzHole(r.Index))
            {
                return isolate.ThrowReferenceError(MessageTemplate.NotDefined, name);
            }
            return holderContext.Get(r.Index);
        }

        // Otherwise, if the slot was found the holder is a context extension
        // object, subject of a with, or a global object.  We read the named
        // property from it.
        if (holder is JSReceiver holderObject)
        {
            JSValue value = ObjectOps.GetProperty(isolate, holderObject, name);
            receiver = holderObject is JSGlobalObject ||
                       holderObject.Map.InstanceType == InstanceType.JSContextExtensionObjectType
                ? JSValue.Undefined
                : holderObject;
            return value;
        }

        if (shouldThrow == ShouldThrow.ThrowOnError)
        {
            // The property doesn't exist - throw exception.
            receiver = JSValue.Undefined;
            return isolate.ThrowReferenceError(MessageTemplate.NotDefined, name);
        }

        // The property doesn't exist - return undefined.
        receiver = JSValue.Undefined;
        return JSValue.Undefined;
    }

    /// <summary>StoreLookupSlot (runtime-scopes.cc); the hoisting flavour is Runtime_StoreLookupSlot_SloppyHoisting.</summary>
    public static JSValue StoreLookupSlot(Isolate isolate, Context context, JSString name, JSValue value,
        LanguageMode languageMode, bool lookupHoisting)
    {
        ContextLookupFlags lookupFlags = ContextLookupFlags.FOLLOW_CHAINS;
        if (lookupHoisting)
        {
            // Store into a dynamic declaration context for sloppy-mode block-scoped
            // function hoisting which leaks out of an eval.
            context = context.DeclarationContext();
            lookupFlags = ContextLookupFlags.DONT_FOLLOW_CHAINS;
            languageMode = LanguageMode.Sloppy;
        }

        HeapObject? holder = Lookup(isolate, context, name, lookupFlags, out ContextLookupResult r);
        if (holder is not null && holder is not Context && holder is not JSReceiver)
        {
            // A SourceTextModule.
            if ((r.Attributes & PropertyAttributes.READ_ONLY) == 0)
            {
                RuntimeModules.StoreVariable(isolate, holder, r.Index, value);
            }
            else
            {
                return isolate.ThrowTypeError(MessageTemplate.ConstAssign, name);
            }
            return value;
        }

        // The property was found in a context slot.
        if (r.Index != Context.kNotFound)
        {
            var holderContext = (Context)holder!;
            if (r.InitFlag == InitializationFlag.kNeedsInitialization && holderContext.IsElementTdzHole(r.Index))
            {
                return isolate.ThrowReferenceError(MessageTemplate.NotDefined, name);
            }
            if ((r.Attributes & PropertyAttributes.READ_ONLY) == 0)
            {
                holderContext.Set(r.Index, value);
            }
            else if (!r.IsSloppyFunctionName || languageMode == LanguageMode.Strict)
            {
                return isolate.ThrowTypeError(MessageTemplate.ConstAssign, name);
            }
            return value;
        }

        // Slow case: The property is not in a context slot.  It is either in a
        // context extension object, a property of the subject of a with, or a
        // property of the global object.
        JSReceiver obj;
        if (r.Attributes != PropertyAttributes.ABSENT)
        {
            // The property exists on the holder.
            obj = (JSReceiver)holder!;
        }
        else if (languageMode == LanguageMode.Strict)
        {
            // If absent in strict mode: throw.
            return isolate.ThrowReferenceError(MessageTemplate.NotDefined, name);
        }
        else
        {
            // If absent in sloppy mode: add the property to the global object.
            obj = context.GlobalObject;
        }

        ObjectOps.SetProperty(isolate, obj, name, value, StoreOrigin.MaybeKeyed,
            languageMode == LanguageMode.Strict ? ShouldThrow.ThrowOnError : ShouldThrow.DontThrow);
        return value;
    }

    /// <summary>Runtime_DeleteLookupSlot.</summary>
    public static JSValue DeleteLookupSlot(Isolate isolate, Context context, JSString name)
    {
        HeapObject? holder = Lookup(isolate, context, name, ContextLookupFlags.FOLLOW_CHAINS, out _);

        // If the slot was not found the result is true.
        if (holder is null) return JSValue.True;

        // If the slot was found in a context or in module imports and exports it
        // should be DONT_DELETE.
        if (holder is not JSReceiver obj) return JSValue.False;

        // The slot was found in a JSReceiver, either a context extension object,
        // the global object, or the subject of a with.  Try to delete it
        // (respecting DONT_DELETE).
        return JSValue.FromBoolean(JSReceiver.DeleteProperty(isolate, obj, name));
    }

    /// <summary>Runtime_StoreGlobalNoHoleCheckForReplLetOrConst.</summary>
    public static JSValue StoreGlobalNoHoleCheckForReplLetOrConst(Isolate isolate, JSString name, JSValue value)
    {
        ScriptContextTable scriptContexts = isolate.NativeContext.ScriptContextTable;
        if (!scriptContexts.Lookup(name, out ScopeInfo.VariableLookupResult lookupResult))
        {
            throw new InvalidOperationException("V8Sharp: REPL let/const not found in the script context table");
        }
        scriptContexts.Get(lookupResult.ContextIndex).Set(lookupResult.SlotIndex, value);
        return value;
    }

    // ---- Declarations ----------------------------------------------------------------------

    enum RedeclarationType { kSyntaxError = 0, kTypeError = 1 }

    static JSValue ThrowRedeclarationError(Isolate isolate, JSString name, RedeclarationType redeclarationType)
    {
        if (redeclarationType == RedeclarationType.kSyntaxError)
        {
            return isolate.ThrowSyntaxError(MessageTemplate.VarRedeclaration, name);
        }
        return isolate.ThrowTypeError(MessageTemplate.VarRedeclaration, name);
    }

    /// <summary>DeclareGlobal (runtime-scopes.cc). May throw a RedeclarationError.</summary>
    static void DeclareGlobal(Isolate isolate, JSGlobalObject global, JSString name, JSValue value, PropertyAttributes attr,
        bool isVar, RedeclarationType redeclarationType)
    {
        ScriptContextTable scriptContexts = global.NativeContext.ScriptContextTable;
        if (scriptContexts.Lookup(name, out ScopeInfo.VariableLookupResult lookup) && Globals.IsLexicalVariableMode(lookup.Mode))
        {
            // https://tc39.es/ecma262/#sec-globaldeclarationinstantiation 6.a:
            // If envRec.HasLexicalDeclaration(name) is true, throw a SyntaxError
            // exception.
            ThrowRedeclarationError(isolate, name, RedeclarationType.kSyntaxError);
            return;
        }

        // Do the lookup own properties only, see ES5 erratum.
        LookupIterator.Configuration lookupConfig = LookupIterator.Configuration.OWN_SKIP_INTERCEPTOR;
        if (!isVar)
        {
            // For function declarations, use the interceptor on the declaration. For
            // non-functions, use it only on initialization.
            lookupConfig = LookupIterator.Configuration.OWN;
        }
        var it = new LookupIterator(isolate, global, name, global, lookupConfig);
        PropertyAttributes oldAttributes = JSReceiver.GetPropertyAttributes(ref it);

        if (it.IsFound)
        {
            // The name was declared before; check for conflicting re-declarations.

            // Skip var re-declarations.
            if (isVar) return;

            if ((oldAttributes & PropertyAttributes.DONT_DELETE) != 0)
            {
                // Check whether we can reconfigure the existing property into a
                // function.
                if ((oldAttributes & PropertyAttributes.READ_ONLY) != 0 || (oldAttributes & PropertyAttributes.DONT_ENUM) != 0 ||
                    it.State == LookupIterator.StateKind.ACCESSOR)
                {
                    // ECMA-262 section 15.1.11 GlobalDeclarationInstantiation 5.d:
                    // If hasRestrictedGlobal is true, throw a SyntaxError exception.
                    // ECMA-262 section 18.2.1.3 EvalDeclarationInstantiation 8.a.iv.1.b:
                    // If fnDefinable is false, throw a TypeError exception.
                    ThrowRedeclarationError(isolate, name, redeclarationType);
                    return;
                }
                // If the existing property is not configurable, keep its attributes.
                attr = oldAttributes;
            }

            // If the current state is ACCESSOR, this could mean it's an AccessorInfo
            // type property. We are not allowed to call into such setters during global
            // function declaration since this would break e.g., onload. Meaning
            // 'function onload() {}' would invalidly register that function as the
            // onload callback. To avoid this situation, we first delete the property
            // before readding it as a regular data property below.
            if (it.State == LookupIterator.StateKind.ACCESSOR) it.Delete();
        }

        if (!isVar) it.Restart();

        // Define or redefine own property.
        JSObject.DefineOwnPropertyIgnoreAttributes(ref it, value, attr);
    }

    /// <summary>Runtime_DeclareGlobals.</summary>
    public static JSValue DeclareGlobals(Isolate isolate, JSValue declarationsValue, JSValue closureValue)
    {
        var declarations = declarationsValue.As<FixedArray>();
        var closure = closureValue.As<JSFunction>();

        JSGlobalObject global = isolate.NativeContext.GlobalObject;
        Context context = isolate.Context!;

        ClosureFeedbackCellArray closureFeedbackCellArray = JSFunctionFeedback.GetClosureFeedbackCellArray(closure);

        // Traverse the name/value pairs and set the properties.
        int length = declarations.Length;
        for (int i = 0; i < length; i++)
        {
            JSValue decl = declarations.Get(i);
            JSString name;
            JSValue value;
            bool isVar = decl.IsString;

            if (isVar)
            {
                name = decl.As<JSString>();
                value = JSValue.Undefined;
            }
            else
            {
                var sfi = decl.As<SharedFunctionInfo>();
                name = sfi.Name();
                int index = (int)declarations.Get(++i).Number;
                FeedbackCell feedbackCell = closureFeedbackCellArray.Get(index);
                value = RuntimeClosures.NewClosure(isolate, sfi, context, feedbackCell);
            }

            // Compute the property attributes. According to ECMA-262,
            // the property must be non-configurable except in eval.
            Script script = closure.Shared.Script!;
            PropertyAttributes attr = script.HasEvalOrigin ? PropertyAttributes.NONE : PropertyAttributes.DONT_DELETE;

            // https://tc39.es/ecma262/#sec-globaldeclarationinstantiation 5.d:
            // If hasRestrictedGlobal is true, throw a SyntaxError exception.
            DeclareGlobal(isolate, global, name, value, attr, isVar, RedeclarationType.kSyntaxError);
        }
        return JSValue.Undefined;
    }

    /// <summary>DeclareEvalHelper (runtime-scopes.cc): Runtime_DeclareEvalFunction / Runtime_DeclareEvalVar.</summary>
    public static JSValue DeclareEvalHelper(Isolate isolate, JSString name, JSValue value)
    {
        // Declarations are always made in a function, native, eval, or script
        // context, or a declaration block scope. Since this is called from eval, the
        // context passed is the context of the caller, which may be some nested
        // context and not the declaration context.
        Context context = isolate.Context!.DeclarationContext();

        bool isVar = value.IsUndefined;

        HeapObject? holder = Lookup(isolate, context, name, ContextLookupFlags.DONT_FOLLOW_CHAINS, out ContextLookupResult r);

        JSObject obj;

        if (r.Attributes != PropertyAttributes.ABSENT && holder is JSGlobalObject holderGlobal)
        {
            // https://tc39.es/ecma262/#sec-evaldeclarationinstantiation 8.a.iv.1.b:
            // If fnDefinable is false, throw a TypeError exception.
            DeclareGlobal(isolate, holderGlobal, name, value, PropertyAttributes.NONE, isVar, RedeclarationType.kTypeError);
            return JSValue.Undefined;
        }
        if (context.HasExtension() && context.Extension.HeapObjectOrNull is JSGlobalObject extensionGlobal)
        {
            DeclareGlobal(isolate, extensionGlobal, name, value, PropertyAttributes.NONE, isVar, RedeclarationType.kTypeError);
            return JSValue.Undefined;
        }
        if (context.IsScriptContext)
        {
            DeclareGlobal(isolate, context.GlobalObject, name, value, PropertyAttributes.NONE, isVar, RedeclarationType.kTypeError);
            return JSValue.Undefined;
        }

        if (r.Attributes != PropertyAttributes.ABSENT)
        {
            // Skip var re-declarations.
            if (isVar) return JSValue.Undefined;

            if (r.Index != Context.kNotFound)
            {
                context.Set(r.Index, value);
                return JSValue.Undefined;
            }

            obj = (JSObject)holder!;
        }
        else if (context.HasExtension())
        {
            obj = context.ExtensionObject()!;
        }
        else if (context.ScopeInfo.HasContextExtensionSlot)
        {
            // Sloppy varblock and function contexts might not have an extension object
            // yet. Sloppy eval will never have an extension object, as vars are hoisted
            // out, and lets are known statically.
            obj = isolate.Factory.NewJSObject(isolate.NativeContext.ContextExtensionFunction);
            context.Extension = obj;
            ScopeInfo scopeInfo = context.ScopeInfo;
            if (!scopeInfo.SomeContextHasExtension) scopeInfo.MarkSomeContextHasExtension();
        }
        else
        {
            return isolate.Throw(isolate.Factory.NewEvalError(MessageTemplate.VarNotAllowedInEvalScope, name));
        }

        JSObject.SetOwnPropertyIgnoreAttributes(isolate, obj, name, value, PropertyAttributes.NONE);
        return JSValue.Undefined;
    }

    // ---- Contexts ----------------------------------------------------------------------------

    /// <summary>Runtime_NewFunctionContext / FastNewFunctionContext (function and eval contexts).</summary>
    public static Context NewFunctionContext(Isolate isolate, Context outer, ScopeInfo scopeInfo, bool isEval) =>
        isolate.Factory.NewFunctionContext(outer, scopeInfo);

    /// <summary>Runtime_PushWithContext: the with statement's object environment (ToObject'ed by the bytecode).</summary>
    public static Context PushWithContext(Isolate isolate, Context current, JSValue extension, ScopeInfo scopeInfo) =>
        isolate.Factory.NewWithContext(current, scopeInfo, extension.As<JSReceiver>());

    // ---- Errors ---------------------------------------------------------------------------------

    /// <summary>ThrowReferenceErrorIfHole: "Cannot access 'x' before initialization".</summary>
    public static void ThrowAccessedUninitializedVariable(Isolate isolate, JSValue name) =>
        isolate.ThrowReferenceError(MessageTemplate.AccessedUninitializedVariable, name);

    /// <summary>Runtime_ThrowConstAssignError.</summary>
    public static JSValue ThrowConstAssignError(Isolate isolate) => isolate.ThrowTypeError(MessageTemplate.ConstAssign);
}
