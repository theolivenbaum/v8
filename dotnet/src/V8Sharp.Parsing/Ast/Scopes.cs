// Copyright 2012 the V8 project authors. All rights reserved.
// Use of this source code is governed by a BSD-style license that can be
// found in the LICENSE file.

// Port of src/ast/scopes.h and src/ast/scopes.cc.
//
// V8 packs scope flags into flags_; here they are plain bool fields with the
// same accessor names. VariableMap is a hash map keyed by AstRawString identity
// (AstRawStrings are unique per AstValueFactory) that iterates in insertion
// order; V8's ZoneHashMap iterates in hash-bucket order, which is only
// observable in FindVariableDeclaredIn (which duplicate name an error
// message mentions when there are several) and in debug printing.

using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using V8Sharp.Common;
using V8Sharp.Parsing;
using static V8Sharp.Common.Globals;

namespace V8Sharp.Ast;

// Context slot layout constants from src/objects/contexts.h.
public static class ContextSlots
{
    public const int SCOPE_INFO_INDEX = 0;
    public const int PREVIOUS_INDEX = 1;
    public const int EXTENSION_INDEX = 2;
    public const int MIN_CONTEXT_SLOTS = EXTENSION_INDEX;
    public const int MIN_CONTEXT_EXTENDED_SLOTS = EXTENSION_INDEX + 1;
    // This slot holds the thrown value in catch contexts.
    public const int THROWN_OBJECT_INDEX = MIN_CONTEXT_SLOTS;
    // These slots hold values in debug evaluate contexts.
    public const int WRAPPED_CONTEXT_INDEX = MIN_CONTEXT_EXTENDED_SLOTS;
}

// base::ThreadedList<VariableProxy, VariableProxy::UnresolvedNext>: skips
// proxies marked as removed.
public sealed class UnresolvedList : ThreadedList<VariableProxy>
{
    private Slot Filter(Slot n)
    {
        // Skip over possibly removed values.
        VariableProxy? v;
        while ((v = n.Get()) != null && v.is_removed_from_unresolved())
        {
            n = new Slot(n.List, v);
        }
        return n;
    }

    protected override Slot StartSlot(Slot head) => Filter(head);
    protected override Slot NextSlot(VariableProxy t) => Filter(new Slot(this, t));
}

// A hash map to support fast variable declaration and lookup.
public sealed class VariableMap
{
    private readonly Dictionary<AstRawString, Variable> _map;

    public VariableMap() => _map = new Dictionary<AstRawString, Variable>(8);

    public VariableMap(VariableMap other) => _map = new Dictionary<AstRawString, Variable>(other._map);

    public Variable Declare(Scope? scope, AstRawString name, VariableMode mode, VariableKind kind,
                            InitializationFlag initialization_flag, MaybeAssignedFlag maybe_assigned_flag,
                            IsStaticFlag is_static_flag, out bool was_added)
    {
        // AstRawStrings are unambiguous, i.e., the same string is always represented
        // by the same AstRawString*.
        ref Variable? slot = ref CollectionsMarshal.GetValueRefOrAddDefault(_map, name, out bool exists);
        was_added = !exists;
        if (was_added)
        {
            // The variable has not been declared yet -> insert it.
            slot = new Variable(scope, name, mode, kind, initialization_flag, maybe_assigned_flag, is_static_flag);
        }
        return slot!;
    }

    public Variable? Lookup(AstRawString name) => _map.GetValueOrDefault(name);

    public void Remove(Variable var) => _map.Remove(var.raw_name());

    public void RemoveDynamic()
    {
        List<AstRawString>? toRemove = null;
        foreach (var kv in _map)
        {
            if (kv.Value.is_dynamic()) (toRemove ??= []).Add(kv.Key);
        }
        if (toRemove == null) return;
        foreach (var key in toRemove) _map.Remove(key);
    }

    public void Add(Variable var) => _map[var.raw_name()] = var;

    public int occupancy() => _map.Count;

    public int capacity() => _map.Count == 0 ? 8 : _map.EnsureCapacity(0);

    public Dictionary<AstRawString, Variable>.Enumerator GetEnumerator() => _map.GetEnumerator();

    public Dictionary<AstRawString, Variable>.ValueCollection Values => _map.Values;

    public Variable? First()
    {
        foreach (var kv in _map) return kv.Value;
        return null;
    }
}

// Global invariants after AST construction: Each reference (i.e. identifier)
// to a JavaScript variable (including global properties) is represented by a
// VariableProxy node. Immediately after AST construction and before variable
// allocation, most VariableProxy nodes are "unresolved", i.e. not bound to a
// corresponding variable (though some are bound during parse time). Variable
// allocation binds each unresolved VariableProxy to one Variable and assigns
// a location. Note that many VariableProxy nodes may refer to the same Java-
// Script variable.

// JS environments are represented in the parser using Scope, DeclarationScope
// and ModuleScope. DeclarationScope is used for any scope that hosts 'var'
// declarations. This includes script, module, eval, varblock, and function
// scope. ModuleScope further specializes DeclarationScope.
public class Scope
{
    // ---------------------------------------------------------------------------
    // Construction

    // Scope tree.
    internal Scope? outer_scope_; // the immediately enclosing outer scope, or null
    internal Scope? inner_scope_; // an inner scope of this scope
    internal Scope? sibling_;     // a sibling inner scope of the outer scope of this scope.

    // The variables declared in this scope:
    //
    // All user-declared variables (incl. parameters).  For script scopes
    // variables may be implicitly 'declared' by being used (possibly in
    // an inner scope) with no intervening with statements or eval calls.
    internal VariableMap variables_ = new();
    // In case of non-scopeinfo-backed scopes, this contains the variables of the
    // map above in order of addition.
    internal readonly ThreadedList<Variable> locals_ = new();
    // Unresolved variables referred to from this scope. The proxies themselves
    // form a linked list of all unresolved proxies.
    internal UnresolvedList unresolved_list_ = new();
    // Declarations.
    internal readonly ThreadedList<Declaration> decls_ = new();

    // Serialized scope info support.
    internal IScopeInfo? scope_info_;

    // Debugging support.
    private AstRawString? _scopeName;
    internal bool already_resolved_;
    internal bool reparsing_for_class_initializer_;

    // Source positions.
    private int _startPosition;
    private int _endPosition;

    // Computed via AllocateVariables.
    internal int num_stack_slots_;
    internal int num_heap_slots_;

    // The scope type.
    private ScopeType _scopeType;

    // flags_
    private bool _isStrict;
    private bool _callsEval;
    private bool _sloppyEvalCanExtendVars;
    private bool _scopeNonlinear;
    private bool _isHidden;
    private bool _isDynamicScope;
    private bool _innerScopeCallsEval;
    private bool _forceContextAllocationForParameters;
    private bool _isDeclarationScope;
    private bool _privateNameLookupSkipsOuterClass;
    private bool _mustUsePreparsedScopeData;
    private bool _needsHomeObject;
    // IsWrappedFunctionField shares the bit with IsBlockScopeForObjectLiteralField.
    private bool _isBlockScopeForObjectLiteralOrWrappedFunction;
    private bool _hasUsingDeclaration;
    private bool _hasAwaitUsingDeclaration;
    private bool _hasContextCells;
    private bool _isHoistedInContext;

    // Creates the script scope (V8: Scope(Zone*, ScopeType)).
    protected Scope(ScopeType scope_type, ParsingFlags flags)
    {
        outer_scope_ = null;
        _scopeType = scope_type;
        SetDefaults(flags);
    }

    public Scope(Scope outer_scope, ScopeType scope_type)
    {
        outer_scope_ = outer_scope;
        _scopeType = scope_type;
        SetDefaults(outer_scope.flags());
        set_language_mode(outer_scope.language_mode());
        set_private_name_lookup_skips_outer_class(
            outer_scope.is_class_scope() && outer_scope.AsClassScope().IsParsingHeritage());
        outer_scope_.AddInnerScope(this);
    }

    // Construct a scope based on the scope info.
    internal Scope(ScopeType type, AstValueFactory? ast_value_factory, IScopeInfo scope_info, ParsingFlags flags)
    {
        outer_scope_ = null;
        scope_info_ = scope_info;
        _scopeType = type;
        SetDefaults(flags);
        already_resolved_ = true;
        set_language_mode(scope_info.language_mode());
        set_private_name_lookup_skips_outer_class(scope_info.PrivateNameLookupSkipsOuterClass());
        // We don't really need to use the preparsed scope data; this is just to
        // shorten the recursion in SetMustUsePreparseData.
        set_must_use_preparsed_scope_data(true);

        if (type == ScopeType.BLOCK_SCOPE)
        {
            // Set is_block_scope_for_object_literal_ based on the existence of the home
            // object variable (we don't store it explicitly).
            int home_object_index = scope_info.ContextSlotIndex(ast_value_factory!.dot_home_object_string().Value);
            if (home_object_index >= 0)
            {
                set_is_block_scope_for_object_literal(true);
            }
        }
        set_has_context_cells(scope_info.HasContextCells());
        set_is_hoisted_in_context(scope_info.is_hoisted_in_context());
    }

    // Construct a catch scope with a binding for the name.
    internal Scope(AstRawString catch_variable_name, MaybeAssignedFlag maybe_assigned, IScopeInfo scope_info, ParsingFlags flags)
    {
        outer_scope_ = null;
        scope_info_ = scope_info;
        _scopeType = ScopeType.CATCH_SCOPE;
        SetDefaults(flags);
        already_resolved_ = true;
        // Cache the catch variable, even though it's also available via the
        // scope_info, as the parser expects that a catch scope always has the catch
        // variable as first and only variable.
        Variable variable = Declare(catch_variable_name, VariableMode.Var, VariableKind.NORMAL_VARIABLE,
                                    InitializationFlag.kCreatedInitialized, maybe_assigned, out _);
        AllocateHeapSlot(variable);
        set_is_hoisted_in_context(scope_info.is_hoisted_in_context());
    }

    // The flags (V8's v8_flags) that affect scope analysis.
    private ParsingFlags _flags = ParsingFlags.Default;
    public ParsingFlags flags() => _flags;

    private void SetDefaults(ParsingFlags flags)
    {
        _flags = flags;
        _scopeName = null;
        already_resolved_ = false;
        inner_scope_ = null;
        sibling_ = null;
        unresolved_list_.Clear();

        _startPosition = kNoSourcePosition;
        _endPosition = kNoSourcePosition;

        bool is_dynamic_scope = _scopeType == ScopeType.WITH_SCOPE;
        bool has_context_cells =
            (is_script_scope() && flags.script_context_cells) ||
            (is_function_scope() && flags.function_context_cells);
        _isStrict = false;
        _callsEval = false;
        _sloppyEvalCanExtendVars = false;
        _scopeNonlinear = false;
        _isHidden = false;
        _isDynamicScope = is_dynamic_scope;
        _innerScopeCallsEval = false;
        _forceContextAllocationForParameters = false;
        _isDeclarationScope = false;
        _privateNameLookupSkipsOuterClass = false;
        _mustUsePreparsedScopeData = false;
        _needsHomeObject = false;
        _isBlockScopeForObjectLiteralOrWrappedFunction = false;
        _hasUsingDeclaration = false;
        _hasAwaitUsingDeclaration = false;
        _hasContextCells = has_context_cells;
        _isHoistedInContext = false;

        num_stack_slots_ = 0;
        num_heap_slots_ = 0;

        set_language_mode(LanguageMode.Sloppy);
    }

    // The scope name is only used for printing/debugging.
    public void SetScopeName(AstRawString? scope_name) => _scopeName = scope_name;

    // An ID that uniquely identifies this scope within the script. Inner scopes
    // have a higher ID than their outer scopes. ScopeInfo created from a scope
    // has the same ID as the scope.
    // Needs to be kept in sync with ScopeInfo::UniqueIdInScript and
    // SharedFunctionInfo::UniqueIdInScript.
    public int UniqueIdInScript()
    {
        // Script scopes start "before" the script to avoid clashing with a scope that
        // starts on character 0.
        if (is_script_scope() || scope_type() == ScopeType.EVAL_SCOPE || scope_type() == ScopeType.MODULE_SCOPE)
        {
            return -2;
        }
        // Wrapped functions start before the function body, but after the script
        // start, to avoid clashing with a scope starting on character 0.
        if (is_wrapped_function())
        {
            return -1;
        }
        if (is_declaration_scope())
        {
            // Default constructors have the same start position as their parent class
            // scope. Use the next char position to distinguish this scope.
            return start_position() + (IsDefaultConstructor(AsDeclarationScope().function_kind()) ? 1 : 0);
        }
        return start_position();
    }

    public DeclarationScope AsDeclarationScope()
    {
        if (!is_declaration_scope()) throw new InvalidOperationException("SBXCHECK(is_declaration_scope())");
        return (DeclarationScope)this;
    }

    public ModuleScope AsModuleScope()
    {
        if (!is_module_scope()) throw new InvalidOperationException("SBXCHECK(is_module_scope())");
        return (ModuleScope)this;
    }

    public ClassScope AsClassScope()
    {
        if (!is_class_scope()) throw new InvalidOperationException("SBXCHECK(is_class_scope())");
        return (ClassScope)this;
    }

    public bool from_scope_info() => scope_info_ != null;

    // Re-writes the {VariableLocation} of top-level 'let' bindings from CONTEXT
    // to REPL_GLOBAL. Should only be called on REPL scripts.
    public void RewriteReplGlobalVariables()
    {
        if (!GetScriptScope().is_repl_mode_scope()) return;

        for (Scope? scope = this; scope != null; scope = scope.outer_scope_)
        {
            foreach (var p in scope.variables_)
            {
                Variable var = p.Value;
                if (var.scope()!.is_repl_mode_scope()) var.RewriteLocationForRepl();
            }
        }
    }

    public sealed class Snapshot : IDisposable
    {
        private readonly Scope _outerScope;
        private readonly Scope _declarationScope;
        private readonly Scope? _topInnerScope;
        private readonly ThreadedList<VariableProxy>.Iterator _topUnresolved;
        private readonly ThreadedList<Variable>.Iterator _topLocal;
        // While the scope is active, the scope caches the flag values for
        // outer_scope_ / declaration_scope_ they can be used to know what happened
        // while parsing the arrow head. If this turns out to be an arrow head, new
        // values on the respective scopes will be cleared and moved to the inner
        // scope. Otherwise the cached flags will be merged with the flags from the
        // arrow head.
        private readonly bool _callsEval;
        private readonly bool _sloppyEvalCanExtendVars;

        public Snapshot(Scope scope)
        {
            _outerScope = scope;
            _declarationScope = scope.GetDeclarationScope();
            _topInnerScope = scope.inner_scope_;
            _topUnresolved = scope.unresolved_list_.end();
            _topLocal = scope.GetClosureScope().locals_.end();
            _callsEval = _outerScope.calls_eval();
            _sloppyEvalCanExtendVars = _declarationScope.sloppy_eval_can_extend_vars();
            // Reset in order to record (sloppy) eval calls during this Snapshot's
            // lifetime.
            _outerScope.set_calls_eval(false);
            _declarationScope.set_sloppy_eval_can_extend_vars(false);
        }

        // ~Snapshot()
        public void Dispose()
        {
            // Restore eval flags from before the scope was active.
            if (_sloppyEvalCanExtendVars)
            {
                _declarationScope.set_is_dynamic_scope(true);
                _declarationScope.set_sloppy_eval_can_extend_vars(true);
            }
            if (_callsEval)
            {
                _outerScope.set_calls_eval(true);
            }
        }

        public void Reparent(DeclarationScope new_parent)
        {
            Scope? inner_scope = new_parent.sibling_;
            if (inner_scope != _topInnerScope)
            {
                for (; inner_scope!.sibling() != _topInnerScope; inner_scope = inner_scope.sibling())
                {
                    inner_scope.outer_scope_ = new_parent;
                    if (inner_scope.inner_scope_calls_eval())
                    {
                        new_parent.set_inner_scope_calls_eval(true);
                    }
                }
                inner_scope.outer_scope_ = new_parent;
                if (inner_scope.inner_scope_calls_eval())
                {
                    new_parent.set_inner_scope_calls_eval(true);
                }
                new_parent.inner_scope_ = new_parent.sibling_;
                inner_scope.sibling_ = null;
                // Reset the sibling rather than the inner_scope_ since we
                // want to keep new_parent there.
                new_parent.sibling_ = _topInnerScope;
            }

            new_parent.unresolved_list_.MoveTail(_outerScope.unresolved_list_, _topUnresolved);

            // Move temporaries allocated for complex parameter initializers.
            DeclarationScope outer_closure = _outerScope.GetClosureScope();
            for (var it = _topLocal; it != outer_closure.locals().end(); it.MoveNext())
            {
                Variable local = it.Current!;
                local.set_scope(new_parent);
            }
            new_parent.locals_.MoveTail(outer_closure.locals(), _topLocal);
            outer_closure.locals_.Rewind(_topLocal);

            // Move eval calls since Snapshot's creation into new_parent.
            if (_outerScope.calls_eval())
            {
                new_parent.RecordEvalCall();
                _outerScope.set_calls_eval(false);
                _declarationScope.set_sloppy_eval_can_extend_vars(false);
                _declarationScope.set_is_dynamic_scope(false);
            }
        }
    }

    public enum DeserializationMode { kIncludingVariables, kScopesOnly }

    // Reconstruct the outer scope chain from a closure's context chain.
    public static Scope DeserializeScopeChain(IScopeInfoProvider provider, IScopeInfo? scope_info,
                                              DeclarationScope script_scope, AstValueFactory ast_value_factory,
                                              DeserializationMode deserialization_mode,
                                              IScriptEvalOrigin? script = null, IScopeInfo? eval_outer_info = null,
                                              ParseInfo? parse_info = null)
    {
        Scope? current_scope = null;
        Scope? innermost_scope = null;
        Scope? outer_scope = null;
        ParsingFlags flags = script_scope.flags();

        while (scope_info != null)
        {
            if (scope_info == eval_outer_info)
            {
                // This block handles the reconstruction of `eval` scopes during
                // deserialization. The `ScopeInfo` chain for `eval`'d code doesn't always
                // contain a scope for the `eval` call itself. We need to inject a
                // synthetic `EVAL_SCOPE` into the deserialized scope chain to represent
                // it.
                //
                // `eval_outer_info` holds the `ScopeInfo` of the scope *from which* the
                // `eval` was called. When the deserialization process (walking up the
                // `scope_info` chain) reaches this outer scope, we know it's time to
                // inject our synthetic `EVAL_SCOPE`.
                //
                // This also handles nested `evals`. After injecting a scope, `script` and
                // `eval_outer_info` are updated to point to the next outer `eval`'s
                // context, allowing us to correctly reconstruct the entire chain of
                // nested `eval` calls.
                var eval_scope = new DeclarationScope(ScopeType.EVAL_SCOPE, ast_value_factory, provider.empty_scope_info(), flags);
                int position = script!.eval_from_position();
                eval_scope.set_start_position(position);
                eval_scope.set_end_position(position);
                eval_scope.set_eval_position(position);

                if (current_scope != null)
                {
                    eval_scope.AddInnerScope(current_scope);
                }
                current_scope = eval_scope;
                innermost_scope ??= current_scope;

                if (script.has_eval_from_shared())
                {
                    script = script.eval_from_shared_script();
                    eval_outer_info = script?.eval_from_scope_info();
                }
                else
                {
                    script = null;
                    eval_outer_info = null;
                }
                continue;
            }

            if (scope_info.scope_type() == ScopeType.FUNCTION_SCOPE)
            {
                outer_scope = new DeclarationScope(ScopeType.FUNCTION_SCOPE, ast_value_factory, scope_info, flags);
            }
            else if (scope_info.scope_type() == ScopeType.CLASS_SCOPE)
            {
                outer_scope = new ClassScope(ast_value_factory, scope_info, flags);
            }
            else if (scope_info.scope_type() == ScopeType.BLOCK_SCOPE)
            {
                if (scope_info.is_declaration_scope())
                {
                    outer_scope = new DeclarationScope(ScopeType.BLOCK_SCOPE, ast_value_factory, scope_info, flags);
                }
                else
                {
                    outer_scope = new Scope(ScopeType.BLOCK_SCOPE, ast_value_factory, scope_info, flags);
                }
            }
            else if (scope_info.is_script_scope())
            {
                // If we reach a script scope, it's the outermost scope. Install the
                // scope info of this script context onto the existing script scope to
                // avoid nesting script scopes.
                if (scope_info.scope_type() == ScopeType.REPL_MODE_SCOPE)
                {
                    script_scope.SwitchScriptScopeToREPLMode();
                }
                if (deserialization_mode == DeserializationMode.kIncludingVariables)
                {
                    script_scope.SetScriptScopeInfo(scope_info);
                }
                script_scope.num_heap_slots_ = scope_info.ContextLength();
                script_scope.set_start_position(scope_info.StartPosition());
                script_scope.set_end_position(scope_info.EndPosition());
                break;
            }
            else if (scope_info.scope_type() == ScopeType.EVAL_SCOPE)
            {
                outer_scope = new DeclarationScope(ScopeType.EVAL_SCOPE, ast_value_factory, scope_info, flags);
                if (script != null && script.has_eval_from_shared())
                {
                    outer_scope.AsDeclarationScope().set_eval_position(script.eval_from_position());
                    script = script.eval_from_shared_script();
                    eval_outer_info = script?.eval_from_scope_info();
                }
                else
                {
                    script = null;
                    eval_outer_info = null;
                }
            }
            else if (scope_info.scope_type() == ScopeType.WITH_SCOPE)
            {
                if (scope_info.IsDebugEvaluateScope())
                {
                    outer_scope = new DeclarationScope(ScopeType.FUNCTION_SCOPE, ast_value_factory, scope_info, flags);
                    outer_scope.set_is_dynamic_scope();
                }
                else
                {
                    // For scope analysis, debug-evaluate is equivalent to a with scope.
                    outer_scope = new Scope(ScopeType.WITH_SCOPE, ast_value_factory, scope_info, flags);
                }
            }
            else if (scope_info.scope_type() == ScopeType.MODULE_SCOPE)
            {
                outer_scope = new ModuleScope(scope_info, ast_value_factory, flags);
                parse_info?.set_has_module_in_scope_chain();
            }
            else
            {
                string name = scope_info.ContextInlinedLocalName(0);
                MaybeAssignedFlag maybe_assigned = scope_info.ContextLocalMaybeAssignedFlag(0);
                outer_scope = new Scope(ast_value_factory.GetString(name), maybe_assigned, scope_info, flags);
            }

            if (deserialization_mode == DeserializationMode.kScopesOnly)
            {
                outer_scope.scope_info_ = null;
            }

            if (current_scope != null)
            {
                outer_scope.AddInnerScope(current_scope);
            }
            outer_scope.num_heap_slots_ = scope_info.ContextLength();
            outer_scope.set_start_position(scope_info.StartPosition());
            outer_scope.set_end_position(scope_info.EndPosition());

            current_scope = outer_scope;
            innermost_scope ??= current_scope;

            scope_info = scope_info.HasOuterScopeInfo() ? scope_info.OuterScopeInfo() : null;
        }

        if (deserialization_mode == DeserializationMode.kIncludingVariables)
        {
            SetScriptScopeInfo(provider, script_scope);
        }

        if (innermost_scope == null) return script_scope;
        script_scope.AddInnerScope(current_scope!);
        return innermost_scope;
    }

    public static void SetScriptScopeInfo(IScopeInfoProvider provider, DeclarationScope script_scope)
    {
        if (script_scope.scope_info_ == null)
        {
            script_scope.SetScriptScopeInfo(provider.global_this_binding_scope_info());
        }
    }

    // Checks if the block scope is redundant, i.e. it does not contain any
    // block scoped declarations. In that case it is removed from the scope
    // tree and its children are reparented.
    public Scope? FinalizeBlockScope()
    {
        if (variables_.occupancy() > 0 ||
            (is_declaration_scope() && AsDeclarationScope().sloppy_eval_can_extend_vars()))
        {
            return this;
        }

        // Remove this scope from outer scope.
        outer_scope()!.RemoveInnerScope(this);

        // Reparent inner scopes.
        if (inner_scope_ != null)
        {
            Scope scope = inner_scope_;
            while (true)
            {
                scope.outer_scope_ = outer_scope();
                if (private_name_lookup_skips_outer_class())
                {
                    scope.set_private_name_lookup_skips_outer_class(true);
                }
                if (scope.is_function_scope())
                {
                    scope.set_is_hoisted_in_context(false);
                }
                if (scope.sibling_ == null) break;
                scope = scope.sibling_;
            }

            scope.sibling_ = outer_scope()!.inner_scope_;
            outer_scope()!.inner_scope_ = inner_scope_;
            inner_scope_ = null;
        }

        // Move unresolved variables
        if (!unresolved_list_.is_empty())
        {
            outer_scope()!.unresolved_list_.Append(unresolved_list_);
            unresolved_list_.Clear();
        }

        if (inner_scope_calls_eval())
        {
            outer_scope()!.set_inner_scope_calls_eval(true);
        }

        // No need to propagate sloppy_eval_can_extend_vars_, since if it was relevant
        // to this scope we would have had to bail out at the top.

        return null;
    }

    public void SetMustUsePreparseData()
    {
        if (must_use_preparsed_scope_data())
        {
            return;
        }
        set_must_use_preparsed_scope_data(true);
        outer_scope_?.SetMustUsePreparseData();
    }

    public bool must_use_preparsed_scope_data() => _mustUsePreparsedScopeData;

    // ---------------------------------------------------------------------------
    // Declarations

    // Lookup a variable in this scope. Returns the variable or null if not
    // found.
    public Variable? LookupLocal(AstRawString name) => variables_.Lookup(name);

    public Variable? LookupInScopeInfo(AstRawString name, Scope cache)
    {
        string tagged_name = name.Value;
        IScopeInfo scope_info = scope_info_!;
        // The Scope is backed up by ScopeInfo. This means it cannot operate in a
        // heap-independent mode, and all strings must be internalized immediately.
        bool found;

        VariableLocation location;
        int index;
        var lookup_result = new VariableLookupResult { initializer_position = kNoSourcePosition };

        {
            location = VariableLocation.CONTEXT;
            index = scope_info.ContextSlotIndex(tagged_name, ref lookup_result);
            found = index >= 0;
        }

        if (!found && is_module_scope())
        {
            location = VariableLocation.MODULE;
            index = scope_info.ModuleIndex(tagged_name, out lookup_result.mode, out lookup_result.init_flag,
                                           out lookup_result.maybe_assigned_flag, out lookup_result.initializer_position);
            found = index != 0;
        }

        if (!found)
        {
            index = scope_info.FunctionContextSlotIndex(tagged_name);
            if (index < 0) return null; // Nowhere found.
            Variable fvar = AsDeclarationScope().DeclareFunctionVar(name, cache);
            fvar.AllocateTo(VariableLocation.CONTEXT, index);
            return cache.variables_.Lookup(name);
        }

        if (is_class_scope() && scope_info.HasSavedClassVariable())
        {
            var (class_name, _) = scope_info.SavedClassVariable();
            if (class_name == tagged_name)
            {
                cache.variables_.Add(AsClassScope().class_variable()!);
                return AsClassScope().class_variable();
            }
        }

        Variable var = cache.variables_.Declare(this, name, lookup_result.mode, VariableKind.NORMAL_VARIABLE,
                                                lookup_result.init_flag, lookup_result.maybe_assigned_flag,
                                                IsStaticFlag.NotStatic, out _);
        var.AllocateTo(location, index);
        var.set_initializer_position(lookup_result.initializer_position);

        return var;
    }

    // Declare a local variable in this scope. If the variable has been
    // declared before, the previously declared variable is returned.
    public Variable DeclareLocal(AstRawString name, VariableMode mode, VariableKind kind, out bool was_added,
                                 InitializationFlag init_flag = InitializationFlag.kCreatedInitialized)
    {
        // This function handles VariableMode::kVar, VariableMode::kLet,
        // VariableMode::kConst, VariableMode::kUsing, and VariableMode::kAwaitUsing
        // modes. VariableMode::kDynamic variables are introduced during variable
        // allocation, and VariableMode::kTemporary variables are allocated via
        // NewTemporary().
        return Declare(name, mode, kind, init_flag, MaybeAssignedFlag.kNotAssigned, out was_added);
    }

    public Variable DeclareVariable(Declaration declaration, AstRawString name, int pos, VariableMode mode,
                                    VariableKind kind, InitializationFlag init, out bool was_added,
                                    ref bool sloppy_mode_block_scope_function_redefinition, ref bool ok)
    {
        if (mode == VariableMode.Var && !is_declaration_scope())
        {
            return GetDeclarationScope().DeclareVariable(declaration, name, pos, mode, kind, init, out was_added,
                ref sloppy_mode_block_scope_function_redefinition, ref ok);
        }

        Variable? var = LookupLocal(name);
        // Declare the variable in the declaration scope.
        was_added = var == null;
        if (was_added)
        {
            if (is_eval_scope() && is_sloppy(language_mode()) && mode == VariableMode.Var)
            {
                // In a var binding in a sloppy direct eval, pollute the enclosing scope
                // with this new binding by doing the following:
                // The proxy is bound to a lookup variable to force a dynamic declaration
                // using the DeclareEvalVar or DeclareEvalFunction runtime functions.
                var = NonLocal(name, VariableMode.Dynamic);
                // Mark the var as used in case anyone outside the eval wants to use it.
                var.set_is_used();
            }
            else
            {
                // Declare the name.
                var = DeclareLocal(name, mode, kind, out was_added, init);
            }
        }
        else
        {
            var!.SetMaybeAssigned();
            if (IsLexicalVariableMode(mode) || IsLexicalVariableMode(var.mode()))
            {
                // The name was declared in this scope before; check for conflicting
                // re-declarations. We have a conflict if either of the declarations is
                // not a var (in script scope, we also have to ignore legacy const for
                // compatibility). There is similar code in runtime.cc in the Declare
                // functions. The function CheckConflictingVarDeclarations checks for
                // var and let bindings from different scopes whereas this is a check
                // for conflicting declarations within the same scope. This check also
                // covers the special case
                //
                // function () { let x; { var x; } }
                //
                // because the var declaration is hoisted to the function scope where
                // 'x' is already bound.
                //
                // In harmony we treat re-declarations as early errors. See ES5 16 for a
                // definition of early errors.
                //
                // Allow duplicate function decls for web compat, see bug 4693.
                ok = var.is_sloppy_block_function() && kind == VariableKind.SLOPPY_BLOCK_FUNCTION_VARIABLE;
                sloppy_mode_block_scope_function_redefinition = ok;
            }
        }

        // We add a declaration node for every declaration. The compiler
        // will only generate code if necessary. In particular, declarations
        // for inner local variables that do not represent functions won't
        // result in any generated code.
        //
        // This will lead to multiple declaration nodes for the
        // same variable if it is declared several times. This is not a
        // semantic issue, but it may be a performance issue since it may
        // lead to repeated DeclareEvalVar or DeclareEvalFunction calls.
        decls_.Add(declaration);
        declaration.set_var(var);
        return var;
    }

    // Returns null if there was a declaration conflict.
    public Variable? DeclareVariableName(AstRawString name, VariableMode mode, out bool was_added,
                                         VariableKind kind = VariableKind.NORMAL_VARIABLE)
    {
        if (mode == VariableMode.Var && !is_declaration_scope())
        {
            return GetDeclarationScope().DeclareVariableName(name, mode, out was_added, kind);
        }

        // Declare the variable in the declaration scope.
        Variable var = DeclareLocal(name, mode, kind, out was_added);
        if (!was_added)
        {
            if (IsLexicalVariableMode(mode) || IsLexicalVariableMode(var.mode()))
            {
                if (!var.is_sloppy_block_function() || kind != VariableKind.SLOPPY_BLOCK_FUNCTION_VARIABLE)
                {
                    // Duplicate functions are allowed in the sloppy mode, but if this is
                    // not a function declaration, it's an error. This is an error PreParser
                    // hasn't previously detected.
                    return null;
                }
                // Sloppy block function redefinition.
            }
            var.SetMaybeAssigned();
        }
        var.set_is_used();
        return var;
    }

    public Variable DeclareCatchVariableName(AstRawString name)
    {
        Variable result = Declare(name, VariableMode.Var, VariableKind.NORMAL_VARIABLE,
                                  InitializationFlag.kCreatedInitialized, MaybeAssignedFlag.kNotAssigned, out _);
        result.set_is_used();
        return result;
    }

    public Variable DeclareHomeObjectVariable(AstValueFactory ast_value_factory)
    {
        Variable home_object_variable = Declare(ast_value_factory.dot_home_object_string(), VariableMode.Const,
            VariableKind.NORMAL_VARIABLE, InitializationFlag.kCreatedInitialized, MaybeAssignedFlag.kNotAssigned, out _);
        home_object_variable.set_is_used();
        home_object_variable.ForceContextAllocation();
        return home_object_variable;
    }

    public Variable DeclareStaticHomeObjectVariable(AstValueFactory ast_value_factory)
    {
        Variable static_home_object_variable = Declare(ast_value_factory.dot_static_home_object_string(),
            VariableMode.Const, VariableKind.NORMAL_VARIABLE, InitializationFlag.kCreatedInitialized,
            MaybeAssignedFlag.kNotAssigned, out _);
        static_home_object_variable.set_is_used();
        static_home_object_variable.ForceContextAllocation();
        return static_home_object_variable;
    }

    // Declarations list.
    public ThreadedList<Declaration> declarations() => decls_;

    public ThreadedList<Variable> locals() => locals_;

    // Create a new unresolved variable.
    public VariableProxy NewUnresolved(AstNodeFactory factory, AstRawString name, int start_pos,
                                       VariableKind kind = VariableKind.NORMAL_VARIABLE)
    {
        VariableProxy proxy = factory.NewVariableProxy(name, kind, start_pos);
        AddUnresolved(proxy);
        return proxy;
    }

    public void AddUnresolved(VariableProxy proxy)
    {
        // The scope is only allowed to already be resolved if we're reparsing a class
        // initializer. Class initializers will manually resolve these references
        // separate from regular variable resolution.
        unresolved_list_.Add(proxy);
    }

    // Deletes an unresolved variable. The variable proxy cannot be reused for
    // another list later. During parsing, an unresolved variable may have been
    // added optimistically, but then only the variable name was used (typically
    // for labels and arrow function parameters). If the variable was not
    // declared, the addition introduced a new unresolved variable which may end
    // up being allocated globally as a "ghost" variable. DeleteUnresolved removes
    // such a variable again if it was added; otherwise this is a no-op.
    public void DeleteUnresolved(VariableProxy var) => var.mark_removed_from_unresolved();

    // Creates a new temporary variable in this scope's TemporaryScope.  The
    // name is only used for printing and cannot be used to find the variable.
    // In particular, the only way to get hold of the temporary is by keeping the
    // Variable* around.  The name should not clash with a legitimate variable
    // names.
    public Variable NewTemporary(AstRawString name) => NewTemporary(name, MaybeAssignedFlag.kMaybeAssigned);

    private protected Variable NewTemporary(AstRawString name, MaybeAssignedFlag maybe_assigned)
    {
        DeclarationScope scope = GetClosureScope();
        var var = new Variable(scope, name, VariableMode.Temporary, VariableKind.NORMAL_VARIABLE,
                               InitializationFlag.kCreatedInitialized);
        scope.AddLocal(var);
        if (maybe_assigned == MaybeAssignedFlag.kMaybeAssigned) var.SetMaybeAssigned();
        return var;
    }

    // Find variable with (variable->mode() <= |mode_limit|) that was declared in
    // |scope|. This is used to catch patterns like `try{}catch(e){let e;}` and
    // function([e]) { let e }, which are errors even though the two 'e's are each
    // time declared in different scopes. Returns the first duplicate variable
    // name if there is one, null otherwise.
    public AstRawString? FindVariableDeclaredIn(Scope scope, VariableMode mode_limit)
    {
        foreach (var p in scope.variables_)
        {
            AstRawString name = p.Key;
            Variable? var = LookupLocal(name);
            if (var != null && var.mode() <= mode_limit) return name;
        }
        return null;
    }

    // ---------------------------------------------------------------------------
    // Scope-specific info.

    // Inform the scope and outer scopes that the corresponding code contains an
    // eval call.
    public void RecordEvalCall()
    {
        set_calls_eval(true);
        if (is_sloppy(language_mode()))
        {
            GetDeclarationScope().RecordDeclarationScopeEvalCall();
        }
        RecordInnerScopeEvalCall();
        // The eval contents might access "super" (if it's inside a function that
        // binds super).
        DeclarationScope receiver_scope = GetReceiverScope();
        FunctionKind function_kind = receiver_scope.function_kind();
        if (BindsSuper(function_kind))
        {
            receiver_scope.RecordSuperPropertyUsage();
        }
    }

    public void RecordInnerScopeEvalCall()
    {
        set_inner_scope_calls_eval(true);
        for (Scope? scope = outer_scope(); scope != null; scope = scope.outer_scope())
        {
            if (scope.inner_scope_calls_eval()) return;
            scope.set_inner_scope_calls_eval(true);
        }
    }

    // Set the language mode flag (unless disabled by a global flag).
    public void SetLanguageMode(LanguageMode language_mode) => set_language_mode(language_mode);

    // Inform the scope that the scope may execute declarations nonlinearly.
    // Currently, the only nonlinear scope is a switch statement.
    public void SetNonlinear() => set_scope_nonlinear(true);

    // Position in the source where this scope begins and ends.
    //
    // * For the scope of a with statement
    //     with (obj) stmt
    //   start position: start position of first token of 'stmt'
    //   end position: end position of last token of 'stmt'
    // * For the scope of a block
    //     { stmts }
    //   start position: start position of '{'
    //   end position: end position of '}'
    // * For the scope of a function literal or decalaration
    //     function fun(a,b) { stmts }
    //   start position: start position of '('
    //   end position: end position of '}'
    // * For the scope of a catch block
    //     try { stms } catch(e) { stmts }
    //   start position: start position of '('
    //   end position: end position of ')'
    // * For the scope of a for-statement
    //     for (let x ...) stmt
    //   start position: start position of '('
    //   end position: end position of last token of 'stmt'
    // * For the scope of a switch statement
    //     switch (tag) { cases }
    //   start position: start position of '{'
    //   end position: end position of '}'
    // * For the scope of a class literal or declaration
    //     class A extends B { body }
    //   start position: start position of 'class'
    //   end position: end position of '}'
    // * For the scope of a class member initializer functions:
    //     class A extends B { body }
    //   start position: start position of '{'
    //   end position: end position of '}'
    public int start_position() => _startPosition;
    public void set_start_position(int statement_pos) => _startPosition = statement_pos;
    public int end_position() => _endPosition;
    public void set_end_position(int statement_pos) => _endPosition = statement_pos;

    // Scopes created for desugaring are hidden. I.e. not visible to the debugger.
    public bool is_hidden() => _isHidden;
    public void set_is_hidden() => _isHidden = true;

    public void ForceContextAllocationForParameters() => set_force_context_allocation_for_parameters(true);
    public bool has_forced_context_allocation_for_parameters() => _forceContextAllocationForParameters;

    // ---------------------------------------------------------------------------
    // Predicates.

    // Specific scope types.
    public bool is_eval_scope() => _scopeType == ScopeType.EVAL_SCOPE;
    public bool is_function_scope() => _scopeType == ScopeType.FUNCTION_SCOPE;
    public bool is_module_scope() => _scopeType == ScopeType.MODULE_SCOPE;
    public bool is_script_scope() => _scopeType == ScopeType.SCRIPT_SCOPE || _scopeType == ScopeType.REPL_MODE_SCOPE;
    public bool is_toplevel_scope() => _scopeType <= ScopeType.MODULE_SCOPE;
    public bool is_catch_scope() => _scopeType == ScopeType.CATCH_SCOPE;
    public bool is_block_scope() => _scopeType == ScopeType.BLOCK_SCOPE || _scopeType == ScopeType.CLASS_SCOPE;
    public bool is_with_scope() => _scopeType == ScopeType.WITH_SCOPE;
    public bool is_declaration_scope() => _isDeclarationScope;
    public bool is_closure_scope() => is_declaration_scope() && !is_block_scope();
    public bool is_class_scope() => _scopeType == ScopeType.CLASS_SCOPE;
    public bool is_home_object_scope() => is_class_scope() || (is_block_scope() && is_block_scope_for_object_literal());
    public bool is_block_scope_for_object_literal() => is_block_scope() && _isBlockScopeForObjectLiteralOrWrappedFunction;
    public void set_is_block_scope_for_object_literal() => _isBlockScopeForObjectLiteralOrWrappedFunction = true;

    public bool inner_scope_calls_eval() => _innerScopeCallsEval;
    public bool private_name_lookup_skips_outer_class() => _privateNameLookupSkipsOuterClass;

    public bool has_using_declaration() => _hasUsingDeclaration;
    public bool has_await_using_declaration() => _hasAwaitUsingDeclaration;

    public bool has_context_cells() => _hasContextCells;

    public bool is_wrapped_function() => is_function_scope() && _isBlockScopeForObjectLiteralOrWrappedFunction;
    public void set_is_wrapped_function() => _isBlockScopeForObjectLiteralOrWrappedFunction = true;

    public bool is_hoisted_in_context() => _isHoistedInContext;
    public void set_is_hoisted_in_context(bool value) => _isHoistedInContext = value;

    // Does this scope have the potential to execute declarations non-linearly?
    public bool is_nonlinear() => _scopeNonlinear;

    // Returns if we need to force a context because the current scope is stricter
    // than the outerscope. We need this to properly track the language mode using
    // the context. This is required in ICs where we lookup the language mode
    // from the context.
    public bool ForceContextForLanguageMode()
    {
        // For function scopes we need not force a context since the language mode
        // can be obtained from the closure. Script scopes always have a context.
        if (_scopeType == ScopeType.FUNCTION_SCOPE || is_script_scope())
        {
            return false;
        }
        return language_mode() > outer_scope_!.language_mode();
    }

    // Whether this needs to be represented by a runtime context.
    public bool NeedsContext() => num_heap_slots() > 0;

    public enum Iteration
    {
        // Continue the iteration on the same level, do not recurse/descent into
        // inner scopes.
        kContinue,
        // Recurse/descend into inner scopes.
        kDescend,
    }

    // Use Scope::ForEach for depth first traversal of scopes.
    public void ForEach<TCallback>(ref TCallback callback) where TCallback : struct, IScopeCallback
    {
        Scope scope = this;
        while (true)
        {
            Iteration iteration = callback.Visit(scope);
            // Try to descend into inner scopes first.
            if (iteration == Iteration.kDescend && scope.inner_scope_ != null)
            {
                scope = scope.inner_scope_;
            }
            else
            {
                // Find the next outer scope with a sibling.
                while (scope.sibling_ == null)
                {
                    if (scope == this) return;
                    scope = scope.outer_scope_!;
                }
                if (scope == this) return;
                scope = scope.sibling_;
            }
        }
    }

    public bool IsConstructorScope() => is_declaration_scope() && IsClassConstructor(AsDeclarationScope().function_kind());

    // Check is this scope is an outer scope of the given scope.
    public bool IsOuterScopeOf(Scope other)
    {
        Scope? scope = other;
        while (scope != null)
        {
            if (scope == this) return true;
            scope = scope.outer_scope();
        }
        return false;
    }

    // ---------------------------------------------------------------------------
    // Accessors.

    // The type of this scope.
    public ScopeType scope_type() => _scopeType;

    // The language mode of this scope.
    public LanguageMode language_mode() => _isStrict ? LanguageMode.Strict : LanguageMode.Sloppy;

    // inner_scope() and sibling() together implement the inner scope list of a
    // scope. Inner scope points to the an inner scope of the function, and
    // "sibling" points to a next inner scope of the outer scope of this scope.
    public Scope? inner_scope() => inner_scope_;
    public Scope? sibling() => sibling_;

    // The scope immediately surrounding this scope, or null.
    public Scope? outer_scope() => outer_scope_;

    public Variable catch_variable() => variables_.First()!;

    public bool ShouldBanArguments() => GetReceiverScope().should_ban_arguments();

    // ---------------------------------------------------------------------------
    // Variable allocation.

    // Result of variable allocation.
    public int num_stack_slots() => num_stack_slots_;
    public int num_heap_slots() => num_heap_slots_;

    public bool HasContextExtensionSlot()
    {
        switch (_scopeType)
        {
            case ScopeType.MODULE_SCOPE:
            case ScopeType.WITH_SCOPE: // DebugEvaluateContext as well
                return true;
            default:
                return sloppy_eval_can_extend_vars();
        }
    }

    public int ContextHeaderLength() =>
        HasContextExtensionSlot() ? ContextSlots.MIN_CONTEXT_EXTENDED_SLOTS : ContextSlots.MIN_CONTEXT_SLOTS;

    public int ContextLocalCount()
    {
        if (num_heap_slots() == 0) return 0;
        Variable? function = is_function_scope() ? AsDeclarationScope().function_var() : null;
        bool is_function_var_in_context = function != null && function.IsContextSlot();
        return num_heap_slots() - ContextHeaderLength() - (is_function_var_in_context ? 1 : 0);
    }

    // Determine if we can parse a function literal in this scope lazily without
    // caring about the unresolved variables within.
    public bool AllowsLazyParsingWithoutUnresolvedVariables(Scope? outer)
    {
        // If none of the outer scopes need to decide whether to context allocate
        // specific variables, we can preparse inner functions without unresolved
        // variables. Otherwise we need to find unresolved variables to force context
        // allocation of the matching declarations. We can stop at the outer scope for
        // the parse, since context allocation of those variables is already
        // guaranteed to be correct.
        for (Scope? s = this; s != outer; s = s.outer_scope_)
        {
            // Eval forces context allocation on all outer scopes, so we don't need to
            // look at those scopes. Sloppy eval makes top-level non-lexical variables
            // dynamic, whereas strict-mode requires context allocation.
            if (s!.is_eval_scope()) return is_sloppy(s.language_mode());
            // Catch scopes force context allocation of all variables.
            if (s.is_catch_scope()) continue;
            // With scopes do not introduce variables that need allocation.
            if (s.is_with_scope()) continue;
            return false;
        }
        return true;
    }

    // The number of contexts between this and scope; zero if this == scope.
    public int ContextChainLength(Scope? scope)
    {
        int n = 0;
        for (Scope? s = this; s != scope; s = s.outer_scope_)
        {
            if (s!.NeedsContext()) n++;
        }
        return n;
    }

    // The number of contexts between this and the outermost context that has a
    // sloppy eval call. One if this->sloppy_eval_can_extend_vars().
    public int ContextChainLengthUntilOutermostSloppyEval()
    {
        int result = 0;
        int length = 0;

        for (Scope? s = this; s != null; s = s.outer_scope())
        {
            if (!s.NeedsContext()) continue;
            length++;
            if (s.is_declaration_scope() && s.AsDeclarationScope().sloppy_eval_can_extend_vars())
            {
                result = length;
            }
        }

        return result;
    }

    // Find the first function, script, eval or (declaration) block scope. This is
    // the scope where var declarations will be hoisted to in the implementation.
    public DeclarationScope GetDeclarationScope()
    {
        Scope scope = this;
        while (!scope.is_declaration_scope())
        {
            scope = scope.outer_scope()!;
        }
        return (DeclarationScope)scope;
    }

    // Find the first function, script, or (declaration) block scope.
    // This is the scope where var declarations will be hoisted to in the
    // implementation, including vars in direct sloppy eval calls.
    public DeclarationScope GetNonEvalDeclarationScope()
    {
        Scope scope = this;
        while (!scope.is_declaration_scope() || scope.is_eval_scope())
        {
            scope = scope.outer_scope()!;
        }
        return (DeclarationScope)scope;
    }

    // Find the first non-block declaration scope. This should be either a script,
    // function, or eval scope. Same as DeclarationScope(), but skips declaration
    // "block" scopes. Used for differentiating associated function objects (i.e.,
    // the scope for which a function prologue allocates a context) or declaring
    // temporaries.
    public DeclarationScope GetClosureScope()
    {
        Scope scope = this;
        while (!scope.is_closure_scope())
        {
            scope = scope.outer_scope()!;
        }
        return (DeclarationScope)scope;
    }

    // Returns true if this scope is an outer scope of the given scope, up to
    // and including the closure scope of the given scope.
    public bool IsOuterScopeUpToClosureScopeOf(Scope scope)
    {
        for (Scope s = scope; ; s = s.outer_scope()!)
        {
            if (s == this)
            {
                return true;
            }
            if (s.is_declaration_scope() && s.AsDeclarationScope().is_closure_scope())
            {
                return false;
            }
        }
    }

    // Find the first (non-arrow) function or script scope.  This is where
    // 'this' is bound, and what determines the function kind.
    public DeclarationScope GetReceiverScope()
    {
        Scope scope = this;
        while (!scope.is_declaration_scope() ||
               (!scope.is_script_scope() && !((DeclarationScope)scope).has_this_declaration()))
        {
            scope = scope.outer_scope()!;
        }
        return (DeclarationScope)scope;
    }

    // Find the first constructor scope. Its outer scope is where the instance
    // members that should be initialized right after super() is called
    // are declared.
    public DeclarationScope? GetConstructorScope()
    {
        Scope? scope = this;
        while (scope != null && !scope.IsConstructorScope())
        {
            scope = scope.outer_scope();
        }
        return scope == null ? null : scope.AsDeclarationScope();
    }

    // Find the first class scope or object literal block scope. This is where
    // 'super' is bound.
    public Scope? GetHomeObjectScope()
    {
        Scope scope = GetReceiverScope();
        FunctionKind kind = scope.AsDeclarationScope().function_kind();
        // "super" in arrow functions binds outside the arrow function. Arrow
        // functions are also never receiver scopes since they close over the
        // receiver.
        // If we find a function which doesn't bind "super" (is not a method etc.), we
        // know "super" here doesn't bind anywhere and we can return nullptr.
        if (!BindsSuper(kind)) return null;
        // Functions that bind "super" can only syntactically occur nested inside home
        // object scopes (i.e. class scopes and object literal scopes), so directly
        // return the outer scope.
        Scope outer_scope = scope.outer_scope()!;
        if (!outer_scope.is_home_object_scope()) throw new InvalidOperationException("CHECK(outer_scope->is_home_object_scope())");
        return outer_scope;
    }

    public DeclarationScope GetScriptScope()
    {
        Scope scope = this;
        while (!scope.is_script_scope())
        {
            scope = scope.outer_scope()!;
        }
        return (DeclarationScope)scope;
    }

    // Find the innermost outer scope that needs a context.
    public Scope? GetOuterScopeWithContext()
    {
        Scope? scope = outer_scope_;
        while (scope != null && !scope.NeedsContext())
        {
            scope = scope.outer_scope();
        }
        return scope;
    }

    public bool HasReceiverToDeserialize() => scope_info_ != null && scope_info_.HasAllocatedReceiver();

    public bool HasThisReference()
    {
        if (is_declaration_scope() && AsDeclarationScope().has_this_reference())
        {
            return true;
        }

        for (Scope? scope = inner_scope_; scope != null; scope = scope.sibling_)
        {
            if (!scope.is_declaration_scope() || !scope.AsDeclarationScope().has_this_declaration())
            {
                if (scope.HasThisReference()) return true;
            }
        }

        return false;
    }

    // Analyze() must have been called once to create the ScopeInfo.
    public IScopeInfo? scope_info() => scope_info_;

    public int num_var() => variables_.occupancy();

    public UnresolvedList unresolved_list() => unresolved_list_;

    public void MarkReparsingForClassInitializer() => reparsing_for_class_initializer_ = true;

    // Retrieve `IsSimpleParameterList` of current or outer function.
    public bool HasSimpleParameters()
    {
        DeclarationScope scope = GetClosureScope();
        return !scope.is_function_scope() || scope.has_simple_parameters();
    }

    public void set_is_dynamic_scope(bool value = true) => _isDynamicScope = value;
    public bool is_dynamic_scope() => _isDynamicScope;
    public bool sloppy_eval_can_extend_vars() => _sloppyEvalCanExtendVars;

    public bool is_debug_evaluate_scope() => _isDynamicScope && scope_info_ != null && scope_info_.IsDebugEvaluateScope();

    public bool IsSkippableFunctionScope()
    {
        // Lazy non-arrow function scopes are skippable. Lazy functions are exactly
        // those Scopes which have their own PreparseDataBuilder object. This
        // logic ensures that the scope allocation data is consistent with the
        // skippable function data (both agree on where the lazy function boundaries
        // are).
        if (!is_function_scope()) return false;
        DeclarationScope declaration_scope = AsDeclarationScope();
        return !declaration_scope.is_arrow_scope() && declaration_scope.preparse_data_builder() != null;
    }

    public bool is_repl_mode_scope() => _scopeType == ScopeType.REPL_MODE_SCOPE;

    public bool needs_home_object() => _needsHomeObject;
    public void set_needs_home_object() => _needsHomeObject = true;

    public bool RemoveInnerScope(Scope inner_scope)
    {
        if (inner_scope == inner_scope_)
        {
            inner_scope_ = inner_scope_.sibling_;
            return true;
        }
        for (Scope? scope = inner_scope_; scope != null; scope = scope.sibling_)
        {
            if (scope.sibling_ == inner_scope)
            {
                scope.sibling_ = scope.sibling_.sibling_;
                return true;
            }
        }
        return false;
    }

    public Variable? LookupInScopeOrScopeInfo(AstRawString name, Scope cache)
    {
        Variable? var = variables_.Lookup(name);
        if (var != null || scope_info_ == null) return var;
        return LookupInScopeInfo(name, cache);
    }

    public Variable? LookupForTesting(AstRawString name)
    {
        for (Scope? scope = this; scope != null; scope = scope.outer_scope())
        {
            Variable? var = scope.LookupInScopeOrScopeInfo(name, scope);
            if (var != null) return var;
        }
        return null;
    }

    public void ForceDynamicLookup(VariableProxy proxy)
    {
        // At the moment this is only used for looking up private names dynamically
        // in debug-evaluate from top-level scope.
        Variable dynamic = NonLocal(proxy.raw_name(), VariableMode.Dynamic);
        proxy.BindTo(dynamic);
    }

    public void RemoveDynamic() => variables_.RemoveDynamic();

    protected void set_scope_type(ScopeType type)
    {
        // The only case when a scope type is allowed to change is
        // SCRIPT_SCOPE->REPL_MODE_SCOPE update.
        _scopeType = type;
    }

    protected internal void set_language_mode(LanguageMode language_mode) => _isStrict = is_strict(language_mode);

    protected internal bool calls_eval() => _callsEval;
    protected internal void set_calls_eval(bool value) => _callsEval = value;
    protected internal void set_sloppy_eval_can_extend_vars(bool value) => _sloppyEvalCanExtendVars = value;
    protected internal void set_inner_scope_calls_eval(bool value) => _innerScopeCallsEval = value;
    protected void set_is_declaration_scope(bool value) => _isDeclarationScope = value;
    protected internal void set_private_name_lookup_skips_outer_class(bool value) => _privateNameLookupSkipsOuterClass = value;
    protected void set_is_block_scope_for_object_literal(bool value) => _isBlockScopeForObjectLiteralOrWrappedFunction = value;
    protected void set_has_using_declaration(bool value) => _hasUsingDeclaration = value;
    protected void set_has_await_using_declaration(bool value) => _hasAwaitUsingDeclaration = value;
    protected internal void set_has_context_cells(bool value) => _hasContextCells = value;
    protected void set_must_use_preparsed_scope_data(bool value) => _mustUsePreparsedScopeData = value;
    protected void set_scope_nonlinear(bool value) => _scopeNonlinear = value;
    protected void set_force_context_allocation_for_parameters(bool value) => _forceContextAllocationForParameters = value;
    protected void set_is_wrapped_function(bool value) => _isBlockScopeForObjectLiteralOrWrappedFunction = value;

    private protected Variable Declare(AstRawString name, VariableMode mode, VariableKind kind,
                                       InitializationFlag initialization_flag, MaybeAssignedFlag maybe_assigned_flag,
                                       out bool was_added)
    {
        // Static variables can only be declared using ClassScope methods.
        Variable result = variables_.Declare(this, name, mode, kind, initialization_flag, maybe_assigned_flag,
                                             IsStaticFlag.NotStatic, out was_added);
        if (mode == VariableMode.Using)
        {
            set_has_using_declaration(true);
        }
        if (mode == VariableMode.AwaitUsing)
        {
            set_has_await_using_declaration(true);
        }
        if (was_added) locals_.Add(result);
        return result;
    }

    // This method should only be invoked on scopes created during parsing (i.e.,
    // not deserialized from a context). Also, since NeedsContext() is only
    // returning a valid result after variables are resolved, NeedsScopeInfo()
    // should also be invoked after resolution.
    public bool NeedsScopeInfo()
    {
        // The debugger expects all functions to have scope infos.
        // TODO(yangguo): Remove this requirement.
        if (is_function_scope()) return true;
        return NeedsContext();
    }

    private struct SavePreparseDataCallback(Parser parser) : IScopeCallback
    {
        public readonly Iteration Visit(Scope scope)
        {
            // Save preparse data for every skippable scope, unless it was already
            // previously saved (this can happen with functions inside arrowheads).
            if (scope.IsSkippableFunctionScope() && !scope.AsDeclarationScope().was_lazily_parsed())
            {
                scope.AsDeclarationScope().SavePreparseDataForDeclarationScope(parser);
            }
            return Iteration.kDescend;
        }
    }

    // Walk the scope chain to find DeclarationScopes; call
    // SavePreparseDataForDeclarationScope for each.
    internal void SavePreparseData(Parser parser)
    {
        var callback = new SavePreparseDataCallback(parser);
        ForEach(ref callback);
    }

    // Create a non-local variable with a given name.
    // These variables are looked up dynamically at runtime.
    internal Variable NonLocal(AstRawString name, VariableMode mode)
    {
        // Declare a new non-local.
        Variable var = variables_.Declare(this, name, mode, VariableKind.NORMAL_VARIABLE,
                                          InitializationFlag.kCreatedInitialized, MaybeAssignedFlag.kNotAssigned,
                                          IsStaticFlag.NotStatic, out _);
        // Allocate it by giving it a dynamic lookup.
        var.AllocateTo(VariableLocation.LOOKUP, -1);
        return var;
    }

    private enum ScopeLookupMode
    {
        kParsedScope,
        kDeserializedScope,
    }

    // Variable resolution.
    // Lookup a variable reference given by name starting with this scope, and
    // stopping when reaching the outer_scope_end scope. If the code is executed
    // because of a call to 'eval', the context parameter should be set to the
    // calling context of 'eval'.
    private static Variable? Lookup(ScopeLookupMode mode, VariableProxy proxy, Scope scope, Scope? outer_scope_end,
                                    ref int access_position, Scope? cache_scope = null,
                                    bool force_context_allocation = false)
    {
        // If we have already passed the cache scope in earlier recursions, we should
        // first quickly check if the current scope uses the cache scope before
        // continuing.
        if (mode == ScopeLookupMode.kDeserializedScope)
        {
            Variable? cached = cache_scope!.variables_.Lookup(proxy.raw_name());
            if (cached != null) return cached;
        }

        while (true)
        {
            // Try to find the variable in this scope.
            Variable? var;
            if (mode == ScopeLookupMode.kParsedScope)
            {
                var = scope.LookupLocal(proxy.raw_name());
            }
            else
            {
                var = scope.LookupInScopeInfo(proxy.raw_name(), cache_scope!);
            }

            // We found a variable and we are done. (Even if there is an 'eval' in this
            // scope which introduces the same variable again, the resulting variable
            // remains the same.)
            if (var != null)
            {
                if (mode == ScopeLookupMode.kParsedScope)
                {
                    if (force_context_allocation && !var.is_dynamic())
                    {
                        var.ForceContextAllocation();
                    }
                }
                return var;
            }

            if (scope.outer_scope_ == outer_scope_end) break;
            if (scope.is_hoisted_in_context())
            {
                access_position = scope.outer_scope()!.start_position();
            }
            else if (scope.is_eval_scope())
            {
                access_position = scope.AsDeclarationScope().eval_position();
            }

            if (scope.is_dynamic_scope())
            {
                if (scope.is_declaration_scope() && scope.AsDeclarationScope().sloppy_eval_can_extend_vars())
                {
                    return LookupSloppyEval(proxy, scope, outer_scope_end, ref access_position, cache_scope,
                                            force_context_allocation);
                }
                if (scope.is_with_scope())
                {
                    return LookupWith(proxy, scope, outer_scope_end, ref access_position, cache_scope,
                                      force_context_allocation);
                }
                if (mode != ScopeLookupMode.kDeserializedScope || !scope.is_debug_evaluate_scope())
                {
                    throw new InvalidOperationException("CHECK failed: debug evaluate scope");
                }
                return cache_scope!.NonLocal(proxy.raw_name(), VariableMode.Dynamic);
            }
            force_context_allocation |= scope.is_function_scope();
            scope = scope.outer_scope_!;

            // TODO(verwaest): Separate through AnalyzePartially.
            if (mode == ScopeLookupMode.kParsedScope && scope.scope_info_ != null)
            {
                return Lookup(ScopeLookupMode.kDeserializedScope, proxy, scope, outer_scope_end, ref access_position,
                              scope, false);
            }
        }

        // We may just be trying to find all free variables. In that case, don't
        // declare them in the outer scope.
        // TODO(marja): Separate Lookup for preparsed scopes better.
        if (mode == ScopeLookupMode.kParsedScope && !scope.is_script_scope())
        {
            return null;
        }

        // No binding has been found. Declare a variable on the global object.
        return scope.AsDeclarationScope().DeclareDynamicGlobal(
            proxy.raw_name(), VariableKind.NORMAL_VARIABLE,
            mode == ScopeLookupMode.kDeserializedScope ? cache_scope! : scope);
    }

    private static Variable? LookupWith(VariableProxy proxy, Scope scope, Scope? outer_scope_end,
                                        ref int access_position, Scope? cache_scope, bool force_context_allocation)
    {
        Variable? var = scope.outer_scope_!.scope_info_ == null
            ? Lookup(ScopeLookupMode.kParsedScope, proxy, scope.outer_scope_, outer_scope_end, ref access_position,
                     null, force_context_allocation)
            : Lookup(ScopeLookupMode.kDeserializedScope, proxy, scope.outer_scope_, outer_scope_end,
                     ref access_position, cache_scope, false);

        if (var == null) return var;

        // The current scope is a with scope, so the variable binding can not be
        // statically resolved. However, note that it was necessary to do a lookup
        // in the outer scope anyway, because if a binding exists in an outer
        // scope, the associated variable has to be marked as potentially being
        // accessed from inside of an inner with scope (the property may not be in
        // the 'with' object).
        if (!var.is_dynamic() && var.IsUnallocated())
        {
            var.set_is_used();
            var.ForceContextAllocation();
            if (proxy.is_assigned()) var.SetMaybeAssigned();
        }
        cache_scope?.variables_.Remove(var);
        Scope target = cache_scope ?? scope;
        Variable dynamic = target.NonLocal(proxy.raw_name(), VariableMode.Dynamic);
        dynamic.set_local_if_not_shadowed(var);
        return dynamic;
    }

    private static Variable? LookupSloppyEval(VariableProxy proxy, Scope scope, Scope? outer_scope_end,
                                              ref int access_position, Scope? cache_scope,
                                              bool force_context_allocation)
    {
        // If we're compiling eval, it's possible that the outer scope is the first
        // ScopeInfo-backed scope. We use the next declaration scope as the cache for
        // this case, to avoid complexity around sloppy block function hoisting and
        // conflict detection through catch scopes in the eval.
        Scope entry_cache = cache_scope ?? scope.outer_scope()!;
        Variable? var = scope.outer_scope_!.scope_info_ == null
            ? Lookup(ScopeLookupMode.kParsedScope, proxy, scope.outer_scope_, outer_scope_end, ref access_position,
                     null, force_context_allocation)
            : Lookup(ScopeLookupMode.kDeserializedScope, proxy, scope.outer_scope_, outer_scope_end,
                     ref access_position, entry_cache, false);
        if (var == null) return var;

        // A variable binding may have been found in an outer scope, but the current
        // scope makes a sloppy 'eval' call, so the found variable may not be the
        // correct one (the 'eval' may introduce a binding with the same name). In
        // that case, change the lookup result to reflect this situation. Only
        // scopes that can host var bindings (declaration scopes) need be considered
        // here (this excludes block and catch scopes), and variable lookups at
        // script scope are always dynamic.
        if (var.IsGlobalObjectProperty())
        {
            Scope target1 = cache_scope ?? scope;
            var = target1.NonLocal(proxy.raw_name(), VariableMode.DynamicGlobal);
        }

        if (var.is_dynamic()) return var;

        Variable invalidated = var;
        cache_scope?.variables_.Remove(invalidated);

        Scope target = cache_scope ?? scope;
        var = target.NonLocal(proxy.raw_name(), VariableMode.DynamicLocal);
        var.set_local_if_not_shadowed(invalidated);

        return var;
    }

    private static void ResolvePreparsedVariable(VariableProxy proxy, Scope scope, Scope? end)
    {
        // Resolve the variable in all parsed scopes to force context allocation.
        for (Scope? s = scope.outer_scope_; s != end; s = s.outer_scope_)
        {
            Variable? var = s!.LookupLocal(proxy.raw_name());
            if (var != null)
            {
                var.set_is_used();
                if (!var.is_dynamic())
                {
                    var.ForceContextAllocation();
                    if (proxy.is_assigned()) var.SetMaybeAssigned();
                    return;
                }
            }
        }
    }

    internal void ResolveTo(VariableProxy proxy, Variable var, int access_position)
    {
        UpdateNeedsHoleCheck(var, proxy, this, access_position);
        proxy.BindTo(var);
    }

    internal void ResolveVariable(VariableProxy proxy)
    {
        int access_position = proxy.position();
        Variable? var;
        if (proxy.is_home_object())
        {
            // VariableProxies of the home object cannot be resolved like a normal
            // variable. Consider the case of a super.property usage in heritage
            // position:
            //
            //   class C extends super.foo { m() { super.bar(); } }
            //
            // The super.foo property access is logically nested under C's class scope,
            // which also has a home object due to its own method m's usage of
            // super.bar(). However, super.foo must resolve super in C's outer scope.
            //
            // Because of the above, start resolving home objects directly at the home
            // object scope instead of the current scope.
            Scope scope = GetHomeObjectScope()!;
            if (scope.scope_info_ == null)
            {
                var = Lookup(ScopeLookupMode.kParsedScope, proxy, scope, null, ref access_position);
            }
            else
            {
                var = Lookup(ScopeLookupMode.kDeserializedScope, proxy, scope, null, ref access_position, scope);
            }
        }
        else
        {
            var = Lookup(ScopeLookupMode.kParsedScope, proxy, this, null, ref access_position);
        }
        ResolveTo(proxy, var!, access_position);
    }

    private static void SetNeedsHoleCheck(Variable var, VariableProxy proxy, Variable.ForceHoleInitializationFlag flag)
    {
        proxy.set_needs_hole_check();
        if (var.scope()!.from_scope_info())
        {
            var.set_hole_check_state(Variable.HoleCheckState.kForce);
        }
        var.ForceHoleInitialization(flag);
    }

    // ScopeInfo::kMaxVariablePositionDistance.
    public const int kMaxVariablePositionDistance = 0xFFFF;

    private static bool UpdateNeedsHoleCheck(Variable var, VariableProxy proxy, Scope scope, int access_position)
    {
        switch (var.hole_check_state())
        {
            case Variable.HoleCheckState.kSkip:
                return false;
            case Variable.HoleCheckState.kForce:
            {
                Variable target = var;
                if (var.mode() == VariableMode.DynamicLocal)
                {
                    target = var.local_if_not_shadowed();
                }
                SetNeedsHoleCheck(target, proxy, Variable.ForceHoleInitializationFlag.kHasHoleCheckUseInDifferentClosureScope);
                return true;
            }
            case Variable.HoleCheckState.kUncached:
                break;
        }

        if (var.mode() == VariableMode.DynamicLocal)
        {
            // Dynamically introduced variables never need a hole check (since they're
            // VariableMode::kVar bindings, either from var or function declarations),
            // but the variable they shadow might need a hole check, which we want to do
            // if we decide that no shadowing variable was dynamically introduced.
            // The access_position passed to the recursive call is the closest access
            // position to the underlying variable (since it was updated during the
            // lookup traversal).
            bool needs_check = UpdateNeedsHoleCheck(var.local_if_not_shadowed(), proxy, scope, access_position);
            var.set_hole_check_state(needs_check ? Variable.HoleCheckState.kForce : Variable.HoleCheckState.kSkip);
            return needs_check;
        }

        if (var.mode() == VariableMode.Dynamic)
        {
            if (var.has_local_if_not_shadowed())
            {
                bool needs_check = UpdateNeedsHoleCheck(var.local_if_not_shadowed(), proxy, scope, access_position);
                if (needs_check)
                {
                    // Dynamic variables are fully handled in the runtime so don't need a
                    // hole check.
                    proxy.clear_needs_hole_check(var);
                }
            }
            return false;
        }

        if (var.initialization_flag() == InitializationFlag.kCreatedInitialized) return false;

        // It's impossible to eliminate module import hole checks here, because it's
        // unknown at compilation time whether the binding referred to in the
        // exporting module itself requires hole checks.
        if (var.location() == VariableLocation.MODULE && !var.IsExport())
        {
            SetNeedsHoleCheck(var, proxy, Variable.ForceHoleInitializationFlag.kHasHoleCheckUseInUnknownScope);
            return true;
        }

        // We should always have valid source positions.
        bool same_closure_scope = var.scope()!.IsOuterScopeUpToClosureScopeOf(scope);
        if (var.initializer_position() >= access_position)
        {
            SetNeedsHoleCheck(var, proxy, same_closure_scope
                ? Variable.ForceHoleInitializationFlag.kHasHoleCheckUseInSameClosureScope
                : Variable.ForceHoleInitializationFlag.kHasHoleCheckUseInDifferentClosureScope);
            return true;
        }

        if (!same_closure_scope)
        {
            int start_pos = var.scope()!.start_position();
            if (var.initializer_position() - start_pos >= kMaxVariablePositionDistance)
            {
                SetNeedsHoleCheck(var, proxy, Variable.ForceHoleInitializationFlag.kHasHoleCheckUseInDifferentClosureScope);
                return true;
            }
        }

        if (var.scope()!.from_scope_info())
        {
            var.set_hole_check_state(Variable.HoleCheckState.kSkip);
        }
        return false;
    }

    private static bool WasLazilyParsed(Scope scope) =>
        scope.is_declaration_scope() && ((DeclarationScope)scope).was_lazily_parsed();

    internal bool ResolveVariablesRecursively(Scope end)
    {
        // Lazy parsed declaration scopes are already partially analyzed. If there are
        // unresolved references remaining, they just need to be resolved in outer
        // scopes.
        if (WasLazilyParsed(this))
        {
            // Resolve in all parsed scopes except for the script scope.
            Scope? end_scope = end;
            if (!end.is_script_scope()) end_scope = end.outer_scope();

            foreach (VariableProxy proxy in unresolved_list_)
            {
                ResolvePreparsedVariable(proxy, this, end_scope);
            }
        }
        else
        {
            // Resolve unresolved variables for this scope.
            foreach (VariableProxy proxy in unresolved_list_)
            {
                ResolveVariable(proxy);
            }

            // Resolve unresolved variables for inner scopes.
            for (Scope? scope = inner_scope_; scope != null; scope = scope.sibling_)
            {
                if (!scope.ResolveVariablesRecursively(end)) return false;
            }
        }
        return true;
    }

    private struct AnalyzePartiallyCallback(DeclarationScope max_outer_scope, AstNodeFactory ast_node_factory,
                                            UnresolvedList new_unresolved_list, bool maybe_in_arrowhead) : IScopeCallback
    {
        public readonly Iteration Visit(Scope scope)
        {
            // Skip already lazily parsed scopes. This can only happen to functions
            // inside arrowheads.
            if (WasLazilyParsed(scope))
            {
                return Iteration.kContinue;
            }

            for (VariableProxy? proxy = scope.unresolved_list_.first(); proxy != null; proxy = proxy.next_unresolved())
            {
                if (proxy.is_removed_from_unresolved()) continue;
                int access_position = proxy.position();
                Variable? var = Lookup(ScopeLookupMode.kParsedScope, proxy, scope, max_outer_scope.outer_scope(),
                                       ref access_position);
                if (var == null)
                {
                    // Don't copy unresolved references to the script scope, unless it's a
                    // reference to a private name or method. In that case keep it so we
                    // can fail later.
                    if (!max_outer_scope.outer_scope()!.is_script_scope() || maybe_in_arrowhead)
                    {
                        VariableProxy copy = ast_node_factory.CopyVariableProxy(proxy);
                        new_unresolved_list.Add(copy);
                    }
                }
                else
                {
                    var.set_is_used();
                    if (proxy.is_assigned()) var.SetMaybeAssigned();
                }
            }

            // Clear unresolved_list_ as it's in an inconsistent state.
            scope.unresolved_list_.Clear();
            return Iteration.kDescend;
        }
    }

    // Finds free variables of this scope. This mutates the unresolved variables
    // list along the way, so full resolution cannot be done afterwards.
    internal void AnalyzePartially(DeclarationScope max_outer_scope, AstNodeFactory ast_node_factory,
                                   UnresolvedList new_unresolved_list, bool maybe_in_arrowhead)
    {
        var callback = new AnalyzePartiallyCallback(max_outer_scope, ast_node_factory, new_unresolved_list, maybe_in_arrowhead);
        ForEach(ref callback);
    }

    // Mark a variable as used and maybe-assigned if it might be dynamically
    // accessed by name (e.g. via eval(), in a catch scope, or script scope).
    internal void MarkMaybeAssignedIfEval(Variable var)
    {
        // Give var a read/write use if there is a chance it might be accessed
        // via an eval() call.  This is only possible if the variable has a
        // visible name.
        if (!var.raw_name().IsEmpty() && inner_scope_calls_eval())
        {
            var.set_is_used();
            if (!var.is_this()) var.SetMaybeAssigned();
        }
    }

    // Predicates.
    internal bool MustAllocate(Variable var)
    {
        if (var.has_forced_context_allocation() && !var.is_used())
        {
            throw new InvalidOperationException("CHECK(!var->has_forced_context_allocation() || var->is_used())");
        }
        // Global variables do not need to be allocated.
        return !var.IsGlobalObjectProperty() && var.is_used();
    }

    internal bool MustAllocateInContext(Variable var)
    {
        // If var is accessed from an inner scope, or if there is a possibility
        // that it might be accessed from the current or an inner scope (through
        // an eval() call or a runtime with lookup), it must be allocated in the
        // context.
        //
        // Temporary variables are always stack-allocated.  Catch-bound variables are
        // always context-allocated.
        VariableMode mode = var.mode();
        if (mode == VariableMode.Temporary) return false;
        if (is_catch_scope()) return true;
        if (is_script_scope() || is_eval_scope())
        {
            if (IsLexicalVariableMode(mode))
            {
                return true;
            }
        }
        return var.has_forced_context_allocation() || inner_scope_calls_eval();
    }

    // Variable allocation.
    internal void AllocateStackSlot(Variable var)
    {
        if (is_block_scope())
        {
            outer_scope()!.GetDeclarationScope().AllocateStackSlot(var);
        }
        else
        {
            var.AllocateTo(VariableLocation.LOCAL, num_stack_slots_++);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void AllocateHeapSlot(Variable var) =>
        var.AllocateTo(VariableLocation.CONTEXT, ContextHeaderLength() + num_heap_slots_++);

    internal void AllocateNonParameterLocal(Variable var)
    {
        if (!var.IsUnallocated()) return;
        MarkMaybeAssignedIfEval(var);
        if (MustAllocate(var))
        {
            if (MustAllocateInContext(var))
            {
                AllocateHeapSlot(var);
            }
            else
            {
                AllocateStackSlot(var);
            }
        }
    }

    internal void AllocateNonParameterLocalsAndDeclaredGlobals()
    {
        if (is_declaration_scope() && AsDeclarationScope().is_arrow_scope())
        {
            // In arrow functions, allocate non-temporaries first and then all the
            // temporaries to make the local variable ordering stable when reparsing to
            // collect source positions.
            foreach (Variable local in locals_)
            {
                if (local.mode() != VariableMode.Temporary)
                {
                    AllocateNonParameterLocal(local);
                }
            }

            foreach (Variable local in locals_)
            {
                if (local.mode() == VariableMode.Temporary)
                {
                    AllocateNonParameterLocal(local);
                }
            }
        }
        else
        {
            foreach (Variable local in locals_)
            {
                AllocateNonParameterLocal(local);
            }
        }

        if (is_declaration_scope())
        {
            AsDeclarationScope().AllocateLocals();
        }
    }

    private struct AllocateVariablesCallback : IScopeCallback
    {
        public readonly Iteration Visit(Scope scope)
        {
            if (WasLazilyParsed(scope)) return Iteration.kContinue;

            // Allocate variables for this scope.
            // Parameters must be allocated first, if any.
            if (scope.is_declaration_scope())
            {
                scope.AsDeclarationScope().AllocateReceiver();
                if (scope.is_function_scope())
                {
                    scope.AsDeclarationScope().AllocateParameterLocals();
                }
            }
            scope.AllocateNonParameterLocalsAndDeclaredGlobals();

            // If we need a context, ensure num_heap_slots_ includes space for the
            // context header.
            if (scope.num_heap_slots_ > 0 || scope.HasContextExtensionSlot() || scope.ForceContextForLanguageMode())
            {
                scope.num_heap_slots_ += scope.ContextHeaderLength();
            }

            // If the number of context slots are over the function context threshold,
            // do not allocate context cells.
            if (scope.is_function_scope() && scope.ContextLocalCount() > scope.flags().function_context_cells_max_size)
            {
                scope.set_has_context_cells(false);
            }

            return Iteration.kDescend;
        }
    }

    internal void AllocateVariablesRecursively()
    {
        var callback = new AllocateVariablesCallback();
        ForEach(ref callback);
    }

    internal void AllocateScopeInfosRecursively(IScopeInfoProvider provider, IScopeInfo? outer_scope,
                                                Dictionary<int, IScopeInfo> scope_infos_to_reuse)
    {
        IScopeInfo? next_outer_scope = outer_scope;

        if (scope_infos_to_reuse.TryGetValue(UniqueIdInScript(), out IScopeInfo? reused))
        {
            scope_info_ = reused;
        }
        else if (NeedsScopeInfo())
        {
            scope_info_ = provider.Create(this, outer_scope);
        }

        // The ScopeInfo chain mirrors the context chain, so we only link to the
        // next outer scope that needs a context.
        if (NeedsContext()) next_outer_scope = scope_info_;

        // Allocate ScopeInfos for inner scopes.
        for (Scope? scope = inner_scope_; scope != null; scope = scope.sibling_)
        {
            if (!NeedsContext())
            {
                scope.set_is_hoisted_in_context(is_hoisted_in_context());
            }
            if (!scope.is_function_scope() || scope.AsDeclarationScope().ShouldEagerCompile())
            {
                scope.AllocateScopeInfosRecursively(provider, next_outer_scope, scope_infos_to_reuse);
            }
            else
            {
                if (scope_infos_to_reuse.TryGetValue(scope.UniqueIdInScript(), out IScopeInfo? inner))
                {
                    scope.scope_info_ = inner;
                }
            }
        }
    }

    internal void AddInnerScope(Scope inner_scope)
    {
        inner_scope.sibling_ = inner_scope_;
        inner_scope_ = inner_scope;
        inner_scope.outer_scope_ = this;
    }

    // Debug printing (Scope::Print).
    public string Print()
    {
        var sb = new System.Text.StringBuilder();
        Print(sb, 0);
        return sb.ToString();
    }

    internal void Print(System.Text.StringBuilder sb, int n) => ScopePrinter.Print(this, sb, n);

    internal AstRawString? scope_name() => _scopeName;

    public override string ToString() => $"{_scopeType} ({_startPosition}, {_endPosition})";
}

// The callback of Scope::ForEach.
public interface IScopeCallback
{
    Scope.Iteration Visit(Scope scope);
}

public class DeclarationScope : Scope
{
    // If the scope is a function scope, this is the function kind.
    private FunctionKind _functionKind;

    private int _numParameters;
    private int _evalPosition = kNoSourcePosition;

    // Parameter list in source order.
    private readonly List<Variable> _params;
    // Map of function names to lists of functions defined in sloppy blocks
    private readonly ThreadedList<SloppyBlockFunctionStatement> _sloppyBlockFunctions = new();
    // Convenience variable.
    private Variable? _receiver;
    // Function variable, if any; function scopes only.
    private Variable? _function;
    // new.target variable, function scopes only.
    private Variable? _newTarget;
    // Convenience variable; function scopes only.
    private Variable? _arguments;

    // For producing the scope allocation data during preparsing.
    private PreparseDataBuilder? _preparseDataBuilder;

    // RareData
    private Variable? _thisFunction;
    private Variable? _generatorObject;

    // flags_ (DeclarationScope fields)
    private bool _hasSimpleParameters;
    private bool _forceEagerCompilation;
    private bool _hasRest;
    private bool _hasArgumentsParameter;
    private bool _usesSuperProperty;
    private bool _shouldEagerCompile;
    private bool _wasLazilyParsed;
    private bool _isSkippedFunction;
    private bool _hasInferredFunctionName;
    private bool _hasCheckedSyntax;
    private bool _hasThisReference;
    private bool _hasThisDeclaration;
    private bool _needsPrivateNameContextChainRecalc;
    private bool _classScopeHasPrivateBrand;
    private bool _isBeingLazilyParsed;

    // Creates a script scope.
    public DeclarationScope(AstValueFactory ast_value_factory, REPLMode repl_mode = REPLMode.No, ParsingFlags? flags = null)
        : base(repl_mode == REPLMode.Yes ? ScopeType.REPL_MODE_SCOPE : ScopeType.SCRIPT_SCOPE, flags ?? ParsingFlags.Default)
    {
        _functionKind = repl_mode == REPLMode.Yes ? FunctionKind.AsyncFunction : FunctionKind.NormalFunction;
        _params = new List<Variable>(4);
        SetDefaults();
        _receiver = DeclareDynamicGlobal(ast_value_factory.this_string(), VariableKind.THIS_VARIABLE, this);
    }

    public DeclarationScope(Scope outer_scope, ScopeType scope_type, FunctionKind function_kind = FunctionKind.NormalFunction)
        : base(outer_scope, scope_type)
    {
        _functionKind = function_kind;
        _params = new List<Variable>(4);
        SetDefaults();
    }

    internal DeclarationScope(ScopeType scope_type, AstValueFactory ast_value_factory, IScopeInfo scope_info, ParsingFlags flags)
        : base(scope_type, ast_value_factory, scope_info, flags)
    {
        _functionKind = scope_info.function_kind();
        _params = new List<Variable>(0);
        SetDefaults();
        if (scope_info.SloppyEvalCanExtendVars())
        {
            set_sloppy_eval_can_extend_vars(true);
            set_is_dynamic_scope(true);
        }
        if (scope_info.ClassScopeHasPrivateBrand())
        {
            set_class_scope_has_private_brand(true);
        }
    }

    private void SetDefaults()
    {
        set_is_declaration_scope(true);
        set_has_simple_parameters(true);
        set_force_eager_compilation(false);
        set_has_rest(false);
        set_has_arguments_parameter(false);
        set_uses_super_property(false);
        set_should_eager_compile(false);
        set_was_lazily_parsed(false);
        set_is_skipped_function(false);
        set_has_checked_syntax(false);
        set_has_this_reference(false);
        set_has_this_declaration((is_function_scope() && !is_arrow_scope()) || is_module_scope());
        set_needs_private_name_context_chain_recalc(false);
        set_class_scope_has_private_brand(false);
        _receiver = null;
        _newTarget = null;
        _function = null;
        _arguments = null;
        _thisFunction = null;
        _generatorObject = null;
        _preparseDataBuilder = null;
        DeclarationScope? outer_declaration_scope = outer_scope_?.GetDeclarationScope();
        _isBeingLazilyParsed = outer_declaration_scope != null && outer_declaration_scope._isBeingLazilyParsed;
    }

    public void SwitchScriptScopeToREPLMode()
    {
        if (scope_type() == ScopeType.REPL_MODE_SCOPE) return;
        set_scope_type(ScopeType.REPL_MODE_SCOPE);
        _functionKind = FunctionKind.AsyncFunction;
    }

    public FunctionKind function_kind() => _functionKind;

    // Inform the scope that the corresponding code uses "super".
    public void RecordSuperPropertyUsage()
    {
        set_uses_super_property(true);
        Scope home_object_scope = GetHomeObjectScope()!;
        home_object_scope.set_needs_home_object();
    }

    public bool uses_super_property() => _usesSuperProperty;
    public void set_uses_super_property(bool value) => _usesSuperProperty = value;

    public void TakeUnresolvedReferencesFromParent()
    {
        unresolved_list_.MoveTail(outer_scope_!.unresolved_list_, outer_scope_.unresolved_list_.begin());
    }

    public bool is_arrow_scope() => is_function_scope() && IsArrowFunction(_functionKind);

    // Inform the scope and outer scopes that the corresponding code contains an
    // eval call.
    public void RecordDeclarationScopeEvalCall()
    {
        set_calls_eval(true);

        // The caller already checked whether we're in sloppy mode.
        if (!is_sloppy(language_mode())) throw new InvalidOperationException("CHECK(is_sloppy(language_mode()))");

        // Sloppy eval in script scopes can only introduce global variables anyway,
        // so we don't care that it calls sloppy eval.
        if (is_script_scope()) return;

        // Sloppy eval in an eval scope can only introduce variables into the outer
        // (non-eval) declaration scope, not into this eval scope.
        if (is_eval_scope())
        {
            return;
        }

        set_is_dynamic_scope(true);
        set_sloppy_eval_can_extend_vars(true);
    }

    public bool was_lazily_parsed() => _wasLazilyParsed;
    public void set_was_lazily_parsed(bool value) => _wasLazilyParsed = value;

    public Variable LookupInModule(AstRawString name) => variables_.Lookup(name)!;

    public void DeserializeReceiver(AstValueFactory ast_value_factory)
    {
        if (is_script_scope())
        {
            return;
        }
        DeclareThis(ast_value_factory);
        if (is_debug_evaluate_scope())
        {
            _receiver!.AllocateTo(VariableLocation.LOOKUP, -1);
        }
        else
        {
            _receiver!.AllocateTo(VariableLocation.CONTEXT, scope_info_!.ReceiverContextSlotIndex());
        }
    }

    public void set_is_being_lazily_parsed(bool is_being_lazily_parsed) => _isBeingLazilyParsed = is_being_lazily_parsed;
    public bool is_being_lazily_parsed() => _isBeingLazilyParsed;

    // Migrate variables_' backing store to new zone.
    public void set_zone() => variables_ = new VariableMap(variables_);

    // ---------------------------------------------------------------------------
    // Illegal redeclaration support.

    // Check if the scope has conflicting var
    // declarations, i.e. a var declaration that has been hoisted from a nested
    // scope over a let binding of the same name.
    public Declaration? CheckConflictingVarDeclarations(ref bool allowed_catch_binding_var_redeclaration)
    {
        if (has_checked_syntax()) return null;
        foreach (Declaration decl in decls_)
        {
            // Lexical vs lexical conflicts within the same scope have already been
            // captured in Parser::Declare. The only conflicts we still need to check
            // are lexical vs nested var.
            if (decl.IsVariableDeclaration() && ((VariableDeclaration)decl).AsNested() != null)
            {
                Scope current = ((VariableDeclaration)decl).AsNested()!.scope();
                if (decl.var()!.mode() != VariableMode.Var && decl.var()!.mode() != VariableMode.Dynamic)
                {
                    continue;
                }
                // Iterate through all scopes until the declaration scope.
                do
                {
                    // There is a conflict if there exists a non-VAR binding.
                    Variable? other_var = current.LookupLocal(decl.var()!.raw_name());
                    if (current.is_catch_scope())
                    {
                        allowed_catch_binding_var_redeclaration |= other_var != null;
                        current = current.outer_scope()!;
                        continue;
                    }
                    if (other_var != null)
                    {
                        return decl;
                    }
                    current = current.outer_scope()!;
                } while (current != this);
            }
        }

        if (!is_eval_scope()) return null;
        if (!is_sloppy(language_mode())) return null;

        // Var declarations in sloppy eval are hoisted to the first non-eval
        // declaration scope. Check for conflicts between the eval scope that
        // declaration scope.
        Scope? end = outer_scope()!.GetNonEvalDeclarationScope().outer_scope();

        foreach (Declaration decl in decls_)
        {
            if (IsLexicalVariableMode(decl.var()!.mode())) continue;
            Scope current = outer_scope_!;
            // Iterate through all scopes until and including the declaration scope.
            do
            {
                // There is a conflict if there exists a non-VAR binding up to the
                // declaration scope in which this sloppy-eval runs.
                //
                // Use the current scope as the cache. We can't use the regular cache
                // since catch scope vars don't result in conflicts, but they will mask
                // variables for regular scope resolution. We have to make sure to not put
                // masked variables in the cache used for regular lookup.
                Variable? other_var = current.LookupInScopeOrScopeInfo(decl.var()!.raw_name(), current);
                if (other_var != null && !current.is_catch_scope())
                {
                    // If this is a VAR, then we know that it doesn't conflict with
                    // anything, so we can't conflict with anything either. The one
                    // exception is the binding variable in catch scopes, which is handled
                    // by the if above.
                    if (!IsLexicalVariableMode(other_var.mode())) break;
                    return decl;
                }
                current = current.outer_scope()!;
            } while (current != end);
        }
        return null;
    }

    public void set_has_checked_syntax(bool value) => _hasCheckedSyntax = value;
    public bool has_checked_syntax() => _hasCheckedSyntax;

    public bool ShouldEagerCompile() => force_eager_compilation() || should_eager_compile();

    public void set_should_eager_compile() => set_should_eager_compile(!was_lazily_parsed());

    public bool force_eager_compilation() => _forceEagerCompilation;
    public void set_force_eager_compilation(bool value) => _forceEagerCompilation = value;

    public bool should_eager_compile() => _shouldEagerCompile;
    public void set_should_eager_compile(bool value) => _shouldEagerCompile = value;

    public bool has_rest() => _hasRest;
    public void set_has_rest(bool value) => _hasRest = value;

    public bool has_arguments_parameter() => _hasArgumentsParameter;
    public void set_has_arguments_parameter(bool value) => _hasArgumentsParameter = value;

    public void SetScriptScopeInfo(IScopeInfo scope_info) => scope_info_ = scope_info;

    public bool should_ban_arguments() => IsClassInitializerFunction(function_kind());

    public void set_module_has_toplevel_await() => _functionKind = FunctionKind.ModuleWithTopLevelAwait;

    public void DeclareThis(AstValueFactory ast_value_factory)
    {
        bool derived_constructor = IsDerivedConstructor(_functionKind);

        _receiver = new Variable(this, ast_value_factory.this_string(),
                                 derived_constructor ? VariableMode.Const : VariableMode.Var,
                                 VariableKind.THIS_VARIABLE,
                                 derived_constructor ? InitializationFlag.kNeedsInitialization : InitializationFlag.kCreatedInitialized,
                                 MaybeAssignedFlag.kNotAssigned);
        // Derived constructors have hole checks when calling super. Mark the 'this'
        // variable as having hole initialization forced so that TDZ elision analysis
        // applies and numbers the variable.
        if (derived_constructor)
        {
            _receiver.ForceHoleInitialization(Variable.ForceHoleInitializationFlag.kHasHoleCheckUseInUnknownScope);
        }
        locals_.Add(_receiver);
    }

    public void DeclareArguments(AstValueFactory ast_value_factory)
    {
        // Because when arguments_ is not nullptr, we already declared
        // "arguments exotic object" to add it into parameters before
        // impl()->InsertShadowingVarBindingInitializers, so here
        // only declare "arguments exotic object" when arguments_
        // is nullptr
        if (_arguments != null)
        {
            return;
        }

        // Declare 'arguments' variable which exists in all non arrow functions.  Note
        // that it might never be accessed, in which case it won't be allocated during
        // variable allocation.
        _arguments = Declare(ast_value_factory.arguments_string(), VariableMode.Var, VariableKind.NORMAL_VARIABLE,
                             InitializationFlag.kCreatedInitialized, MaybeAssignedFlag.kNotAssigned, out bool was_added);
        // According to https://tc39.es/ecma262/#sec-functiondeclarationinstantiation
        // step 18 we should set argumentsObjectNeeded to false if has lexical
        // declared arguments only when hasParameterExpressions is false
        if (!was_added && IsLexicalVariableMode(_arguments.mode()) && has_simple_parameters())
        {
            // Check if there's lexically declared variable named arguments to avoid
            // redeclaration. See
            // https://tc39.es/ecma262/#sec-functiondeclarationinstantiation, step 20.
            _arguments = null;
        }
    }

    public void DeclareDefaultFunctionVariables(AstValueFactory ast_value_factory)
    {
        DeclareThis(ast_value_factory);
        _newTarget = Declare(ast_value_factory.dot_new_target_string(), VariableMode.Const, VariableKind.NORMAL_VARIABLE,
                             InitializationFlag.kCreatedInitialized, MaybeAssignedFlag.kNotAssigned, out _);

        if (IsConciseMethod(_functionKind) || IsClassConstructor(_functionKind) || IsAccessorFunction(_functionKind))
        {
            _thisFunction = Declare(ast_value_factory.dot_this_function_string(), VariableMode.Const,
                                    VariableKind.NORMAL_VARIABLE, InitializationFlag.kCreatedInitialized,
                                    MaybeAssignedFlag.kNotAssigned, out _);
        }
    }

    // Declare the function variable for a function literal. This variable
    // is in an intermediate scope between this function scope and the
    // outer scope. Only possible for function scopes; at most one variable.
    //
    // This function needs to be called after all other variables have been
    // declared in the scope. It will add a variable for {name} to {variables_};
    // either the function variable itself, or a non-local in case the function
    // calls sloppy eval.
    public Variable DeclareFunctionVar(AstRawString name, Scope? cache = null)
    {
        cache ??= this;
        if (_function == null)
        {
            VariableKind kind = is_sloppy(language_mode()) ? VariableKind.SLOPPY_FUNCTION_NAME_VARIABLE : VariableKind.NORMAL_VARIABLE;
            _function = new Variable(this, name, VariableMode.Const, kind, InitializationFlag.kCreatedInitialized);
        }
        if (sloppy_eval_can_extend_vars())
        {
            cache.NonLocal(name, VariableMode.Dynamic);
        }
        else
        {
            cache.variables_.Add(_function);
        }
        return _function;
    }

    // Declare some special internal variables which must be accessible to
    // Ignition without ScopeInfo.
    public Variable DeclareGeneratorObjectVar(AstRawString name)
    {
        Variable result = _generatorObject = NewTemporary(name, MaybeAssignedFlag.kNotAssigned);
        result.set_is_used();
        return result;
    }

    // Declare a parameter in this scope.  When there are duplicated
    // parameters the rightmost one 'wins'.  However, the implementation
    // expects all parameters to be declared and from left to right.
    public Variable DeclareParameter(AstRawString name, VariableMode mode, bool is_optional, bool is_rest,
                                     AstValueFactory ast_value_factory, int position)
    {
        Variable var;
        if (mode == VariableMode.Temporary)
        {
            var = NewTemporary(name);
        }
        else
        {
            var = LookupLocal(name)!;
        }
        set_has_rest(is_rest);
        var.set_initializer_position(position);
        _params.Add(var);
        if (!is_rest) ++_numParameters;
        if (name == ast_value_factory.arguments_string())
        {
            set_has_arguments_parameter(true);
        }
        return var;
    }

    // Makes sure that num_parameters_ and has_rest is correct for the preparser.
    public void RecordParameter(bool is_rest)
    {
        set_has_rest(is_rest);
        if (!is_rest) ++_numParameters;
    }

    // Declare an implicit global variable in this scope which must be a
    // script scope.  The variable was introduced (possibly from an inner
    // scope) by a reference to an unresolved variable with no intervening
    // with statements or eval calls.
    public Variable DeclareDynamicGlobal(AstRawString name, VariableKind variable_kind, Scope cache)
    {
        return cache.variables_.Declare(this, name, VariableMode.DynamicGlobal, variable_kind,
                                        InitializationFlag.kCreatedInitialized, MaybeAssignedFlag.kNotAssigned,
                                        IsStaticFlag.NotStatic, out _);
        // TODO(neis): Mark variable as maybe-assigned?
    }

    // The variable corresponding to the 'this' value.
    public Variable receiver() => _receiver!;

    public bool has_this_declaration() => _hasThisDeclaration;
    public void set_has_this_declaration(bool value) => _hasThisDeclaration = value;

    // The variable corresponding to the 'new.target' value.
    public Variable? new_target_var() => _newTarget;

    // The variable holding the function literal for named function
    // literals, or null.  Only valid for function scopes.
    public Variable? function_var() => _function;

    // The variable holding the JSGeneratorObject for generator, async
    // and async generator functions, and modules. Only valid for
    // function, module and REPL mode script scopes.
    public Variable? generator_object_var() => _generatorObject;

    // Parameters. The left-most parameter has index 0.
    // Only valid for function and module scopes.
    public Variable parameter(int index) => _params[index];

    // Returns the number of formal parameters, excluding a possible rest
    // parameter.  Examples:
    //   function foo(a, b) {}         ==> 2
    //   function foo(a, b, ...c) {}   ==> 2
    //   function foo(a, b, c = 1) {}  ==> 3
    public int num_parameters() => _numParameters;

    public int eval_position() => _evalPosition;
    public void set_eval_position(int eval_position) => _evalPosition = eval_position;

    // The function's rest parameter (null if there is none).
    public Variable? rest_parameter() => has_rest() ? _params[^1] : null;

    public bool has_simple_parameters() => _hasSimpleParameters;
    public void set_has_simple_parameters(bool value) => _hasSimpleParameters = value;

    // TODO(caitp): manage this state in a better way. PreParser must be able to
    // communicate that the scope is non-simple, without allocating any parameters
    // as the Parser does. This is necessary to ensure that TC39's proposed early
    // error can be reported consistently regardless of whether lazily parsed or
    // not.
    public void SetHasNonSimpleParameters() => set_has_simple_parameters(false);

    public void MakeParametersNonSimple()
    {
        SetHasNonSimpleParameters();
        foreach (var p in variables_)
        {
            Variable var = p.Value;
            if (var.is_parameter()) var.MakeParameterNonSimple();
        }
    }

    // Returns whether the arguments object aliases formal parameters.
    public CreateArgumentsType GetArgumentsType() =>
        is_sloppy(language_mode()) && has_simple_parameters()
            ? CreateArgumentsType.kMappedArguments
            : CreateArgumentsType.kUnmappedArguments;

    // The local variable 'arguments' if we need to allocate it; null
    // otherwise.
    public Variable? arguments() => _arguments;

    public Variable? this_function_var() => _thisFunction;

    // Adds a local variable in this scope's locals list. This is for adjusting
    // the scope of temporaries and do-expression vars when desugaring parameter
    // initializers.
    public void AddLocal(Variable var) => locals_.Add(var);

    public void DeclareSloppyBlockFunction(SloppyBlockFunctionStatement sloppy_block_function) =>
        _sloppyBlockFunctions.Add(sloppy_block_function);

    // Go through sloppy_block_functions_ and hoist those (into this scope)
    // which should be hoisted.
    public void HoistSloppyBlockFunctions(AstNodeFactory? factory)
    {
        if (_sloppyBlockFunctions.is_empty()) return;

        // In case of complex parameters the current scope is the body scope and the
        // parameters are stored in the outer scope.
        Scope parameter_scope = HasSimpleParameters() ? this : outer_scope_!;

        DeclarationScope decl_scope = GetNonEvalDeclarationScope();
        Scope? outer_scope = decl_scope.outer_scope();

        // For each variable which is used as a function declaration in a sloppy
        // block,
        foreach (SloppyBlockFunctionStatement sloppy_block_function in _sloppyBlockFunctions)
        {
            AstRawString name = sloppy_block_function.name();

            // If the variable wouldn't conflict with a lexical declaration
            // or parameter,

            // Check if there's a conflict with a parameter.
            Variable? maybe_parameter = parameter_scope.LookupLocal(name);
            if (maybe_parameter != null && maybe_parameter.is_parameter())
            {
                continue;
            }

            // Check if there's a conflict with a lexical declaration
            Scope query_scope = sloppy_block_function.scope().outer_scope()!;
            bool should_hoist = true;

            // It is not sufficient to just do a Lookup on query_scope: for
            // example, that does not prevent hoisting of the function in
            // `{ let e; try {} catch (e) { function e(){} } }`
            //
            // Don't use a generic cache scope, as the cache scope would be the outer
            // scope and we terminate the iteration there anyway.
            do
            {
                Variable? var = query_scope.LookupInScopeOrScopeInfo(name, query_scope);
                if (var != null && IsLexicalVariableMode(var.mode()) && !var.is_sloppy_block_function())
                {
                    should_hoist = false;
                    break;
                }
                query_scope = query_scope.outer_scope()!;
            } while (query_scope != outer_scope);

            if (!should_hoist) continue;

            if (factory != null)
            {
                int pos = sloppy_block_function.position();
                bool ok = true;
                bool redefinition = false;
                var declaration = factory.NewVariableDeclaration(pos);
                // Based on the preceding checks, it doesn't matter what we pass as
                // sloppy_mode_block_scope_function_redefinition.
                //
                // This synthesized var for Annex B functions-in-block (FiB) may be
                // declared multiple times for the same var scope, such as in the case of
                // shadowed functions-in-block like the following:
                //
                // {
                //    function f() {}
                //    { function f() {} }
                // }
                //
                // Redeclarations for vars do not create new bindings, but the
                // redeclarations' initializers are still run. That is, shadowed FiB will
                // result in multiple assignments to the same synthesized var.
                Variable var = DeclareVariable(declaration, name, pos, VariableMode.Var, VariableKind.NORMAL_VARIABLE,
                    Variable.DefaultInitializationFlag(VariableMode.Var), out _, ref redefinition, ref ok);
                VariableProxy source = factory.NewVariableProxy(sloppy_block_function.var());
                VariableProxy target = factory.NewVariableProxy(var);
                Assignment assignment = factory.NewAssignment(sloppy_block_function.init(), target, source, pos);
                assignment.set_lookup_hoisting_mode(LookupHoistingMode.kLegacySloppy);
                Statement statement = factory.NewExpressionStatement(assignment, pos);
                sloppy_block_function.set_statement(statement);
            }
            else
            {
                Variable var = DeclareVariableName(name, VariableMode.Var, out _)!;
                if (sloppy_block_function.init() == Token.Assign)
                {
                    var.SetMaybeAssigned();
                }
            }
        }
    }

    // Compute top scope and allocate variables. For lazy compilation the top
    // scope only contains the single lazily compiled function, so this
    // doesn't re-allocate variables repeatedly.
    //
    // Returns false if private names can not be resolved and
    // ParseInfo's pending_error_handler will be populated with an
    // error. Otherwise, returns true.
    public static bool Analyze(ParseInfo info)
    {
        DeclarationScope scope = info.literal()!.scope();

        if (scope.is_eval_scope() && is_sloppy(scope.language_mode()))
        {
            var factory = new AstNodeFactory(info.ast_value_factory());
            scope.HoistSloppyBlockFunctions(factory);
            scope.RemoveDynamic();
        }

        // We are compiling one of four cases:
        // 1) top-level code,
        // 2) a function/eval/module on the top-level
        // 4) a class member initializer function scope
        // 3) 4 function/eval in a scope that was already resolved.

        // The outer scope is never lazy.
        scope.set_should_eager_compile();

        if (scope.must_use_preparsed_scope_data())
        {
            info.consumed_preparse_data()!.RestoreScopeAllocationData(scope, info.ast_value_factory());
        }

        if (!scope.AllocateVariables(info)) return false;

        scope.RewriteReplGlobalVariables();

        return true;
    }

    // To be called during parsing. Do just enough scope analysis that we can
    // discard the Scope contents for lazily compiled functions. In particular,
    // this records variables which cannot be resolved inside the Scope (we don't
    // yet know what they will resolve to since the outer Scopes are incomplete)
    // and recreates them with the correct Zone with ast_node_factory.
    public void AnalyzePartially(Parser parser, AstNodeFactory ast_node_factory, bool maybe_in_arrowhead)
    {
        var new_unresolved_list = new UnresolvedList();

        // We don't need to do partial analysis for top level functions, since they
        // can only access values in the global scope, and we can't track assignments
        // for these since they're accessible across scripts.
        //
        // If the top level function has inner functions though, we do still want to
        // analyze those to save their preparse data.
        //
        // Additionally, functions in potential arrowheads _need_ to be analyzed, in
        // case they do end up being in an arrowhead and the arrow function needs to
        // know about context acceses. For example, in
        //
        //     (a, b=function foo(){ a = 1 }) => { b(); return a}
        //
        // `function foo(){ a = 1 }` is "maybe_in_arrowhead" but still top-level when
        // parsed, and is re-scoped to the arrow function when that one is parsed. The
        // arrow function needs to know that `a` needs to be context allocated, so
        // that the call to `b()` correctly updates the `a` parameter.
        bool is_top_level_function = outer_scope_!.is_script_scope();
        bool has_inner_functions = _preparseDataBuilder != null && _preparseDataBuilder.HasInnerFunctions();
        if (maybe_in_arrowhead || !is_top_level_function || has_inner_functions)
        {
            // Try to resolve unresolved variables for this Scope and migrate those
            // which cannot be resolved inside. It doesn't make sense to try to resolve
            // them in the outer Scopes here, because they are incomplete.
            AnalyzePartially(this, ast_node_factory, new_unresolved_list, maybe_in_arrowhead);

            // Migrate function_ to the right Zone.
            if (_function != null)
            {
                _function = ast_node_factory.CopyVariable(_function);
            }

            SavePreparseData(parser);
        }

        ResetAfterPreparsing(ast_node_factory.ast_value_factory(), false);

        unresolved_list_ = new_unresolved_list;
    }

    // Allocate ScopeInfos for top scope and any inner scopes that need them.
    // Does nothing if ScopeInfo is already allocated. `scope_infos_to_reuse`
    // holds the ScopeInfos of the script's existing SharedFunctionInfos by
    // UniqueIdInScript (collected by the engine from Script::infos, as
    // DeclarationScope::AllocateScopeInfos does).
    public static void AllocateScopeInfos(ParseInfo parse_info, IScopeInfoProvider provider,
                                          Dictionary<int, IScopeInfo>? scope_infos_to_reuse = null)
    {
        DeclarationScope scope = parse_info.literal()!.scope();

        IScopeInfo? outer_scope = null;
        if (scope.outer_scope_ != null)
        {
            Scope? outer = scope.GetOuterScopeWithContext();
            if (outer != null)
            {
                outer_scope = outer.scope_info_;
            }
        }

        if (scope.needs_private_name_context_chain_recalc())
        {
            scope.RecalcPrivateNameContextChain();
        }

        scope.AllocateScopeInfosRecursively(provider, outer_scope, scope_infos_to_reuse ?? []);

        // The debugger expects all shared function infos to contain a scope info.
        // Since the top-most scope will end up in a shared function info, make sure
        // it has one, even if it doesn't need a scope info.
        // TODO(yangguo): Remove this requirement.
        scope.scope_info_ ??= provider.Create(scope, outer_scope);
    }

    // Determine if we can use lazy compilation for this scope.
    public bool AllowsLazyCompilation() =>
        // Functions which force eager compilation and class member initializer
        // functions are not lazily compilable.
        !force_eager_compilation() && !IsClassInitializerFunction(function_kind());

    // Make sure this closure and all outer closures are eagerly compiled.
    public void ForceEagerCompilation()
    {
        DeclarationScope s;
        for (s = this; !s.is_script_scope(); s = s.outer_scope()!.GetClosureScope())
        {
            s.set_force_eager_compilation(true);
        }
        s.set_force_eager_compilation(true);
    }

    internal void AllocateLocals()
    {
        // For now, function_ must be allocated at the very end.  If it gets
        // allocated in the context, it must be the last slot in the context,
        // because of the current ScopeInfo implementation (see
        // ScopeInfo::ScopeInfo(FunctionScope* scope) constructor).
        if (_function != null)
        {
            MarkMaybeAssignedIfEval(_function);
            if (MustAllocate(_function))
            {
                AllocateNonParameterLocal(_function);
            }
            else
            {
                _function = null;
            }
        }

        if (_newTarget != null)
        {
            MarkMaybeAssignedIfEval(_newTarget);
            if (!MustAllocate(_newTarget))
            {
                _newTarget = null;
            }
        }

        if (_thisFunction != null)
        {
            MarkMaybeAssignedIfEval(_thisFunction);
            if (!MustAllocate(_thisFunction)) _thisFunction = null;
        }
    }

    internal void AllocateParameterLocals()
    {
        bool has_mapped_arguments = false;
        if (_arguments != null)
        {
            MarkMaybeAssignedIfEval(_arguments);
            if (MustAllocate(_arguments) && !has_arguments_parameter())
            {
                // 'arguments' is used and does not refer to a function
                // parameter of the same name. If the arguments object
                // aliases formal parameters, we conservatively allocate
                // them specially in the loop below.
                has_mapped_arguments = GetArgumentsType() == CreateArgumentsType.kMappedArguments;
            }
            else
            {
                // 'arguments' is unused. Tell the code generator that it does not need to
                // allocate the arguments object by nulling out arguments_.
                _arguments = null;
            }
        }

        // The same parameter may occur multiple times in the parameters_ list.
        // If it does, and if it is not copied into the context object, it must
        // receive the highest parameter index for that parameter; thus iteration
        // order is relevant!
        for (int i = num_parameters() - 1; i >= 0; --i)
        {
            Variable var = _params[i];
            // Parameter is reachable through arguments.
            if (_arguments != null)
            {
                var.set_is_used();
                if (has_mapped_arguments)
                {
                    var.SetMaybeAssigned();
                    var.ForceContextAllocation();
                }
            }
            AllocateParameter(var, i);
        }

        // If we have mapped arguments, do not use context cells.
        if (has_mapped_arguments)
        {
            set_has_context_cells(false);
        }
    }

    internal void AllocateReceiver()
    {
        if (!has_this_declaration()) return;
        AllocateParameter(receiver(), -1);
    }

    private void AllocateParameter(Variable var, int index)
    {
        MarkMaybeAssignedIfEval(var);
        if (has_forced_context_allocation_for_parameters() || MustAllocateInContext(var))
        {
            if (var.IsUnallocated()) AllocateHeapSlot(var);
        }
        else
        {
            if (var.IsUnallocated())
            {
                var.AllocateTo(VariableLocation.PARAMETER, index);
            }
        }
    }

    public void ResetAfterPreparsing(AstValueFactory ast_value_factory, bool aborted)
    {
        // Reset all non-trivial members.
        _params.Clear();
        _numParameters = 0;
        decls_.Clear();
        locals_.Clear();
        inner_scope_ = null;
        unresolved_list_.Clear();
        _sloppyBlockFunctions.Clear();
        _thisFunction = null;
        _generatorObject = null;
        set_has_rest(false);
        _function = null;

        // Make sure this scope and zone aren't used for allocation anymore.
        variables_ = new VariableMap();

        if (aborted)
        {
            // Prepare scope for use in the outer zone.
            if (!IsArrowFunction(_functionKind))
            {
                set_has_simple_parameters(true);
                DeclareDefaultFunctionVariables(ast_value_factory);
            }
        }

        _isBeingLazilyParsed = false;

        set_was_lazily_parsed(!aborted);
    }

    public bool is_skipped_function() => _isSkippedFunction;
    public void set_is_skipped_function(bool value) => _isSkippedFunction = value;

    public bool has_inferred_function_name() => _hasInferredFunctionName;
    public void set_has_inferred_function_name(bool value) => _hasInferredFunctionName = value;

    // Save data describing the context allocation of the variables in this scope
    // and its subscopes (except scopes at the laziness boundary). The data is
    // saved in produced_preparse_data_.
    public void SavePreparseDataForDeclarationScope(Parser parser)
    {
        if (_preparseDataBuilder == null) return;
        _preparseDataBuilder.SaveScopeAllocationData(this, parser);
    }

    public void set_preparse_data_builder(PreparseDataBuilder? preparse_data_builder) => _preparseDataBuilder = preparse_data_builder;
    public PreparseDataBuilder? preparse_data_builder() => _preparseDataBuilder;

    public void set_has_this_reference(bool value) => _hasThisReference = value;
    public bool has_this_reference() => _hasThisReference;

    public void UsesThis()
    {
        set_has_this_reference(true);
        GetReceiverScope().receiver().ForceContextAllocation();
    }

    public bool needs_private_name_context_chain_recalc() => _needsPrivateNameContextChainRecalc;
    public void set_needs_private_name_context_chain_recalc(bool value) => _needsPrivateNameContextChainRecalc = value;

    public void RecordNeedsPrivateNameContextChainRecalc()
    {
        DeclarationScope? scope;
        for (scope = this; scope != null; scope = scope.outer_scope() != null ? scope.outer_scope()!.GetClosureScope() : null)
        {
            if (scope.needs_private_name_context_chain_recalc()) return;
            scope.set_needs_private_name_context_chain_recalc(true);
        }
    }

    public void set_class_scope_has_private_brand(bool value) => _classScopeHasPrivateBrand = value;
    public bool class_scope_has_private_brand() => _classScopeHasPrivateBrand;

    // Resolve and fill in the allocation information for all variables
    // in this scopes. Must be called *after* all scopes have been
    // processed (parsed) to ensure that unresolved variables can be
    // resolved properly.
    //
    // In the case of code compiled and run using 'eval', the context
    // parameter is the context in which eval was called.  In all other
    // cases the context parameter is an empty handle.
    //
    // Returns false if private names can not be resolved.
    private bool AllocateVariables(ParseInfo info)
    {
        // Module variables must be allocated before variable resolution
        // to ensure that UpdateNeedsHoleCheck() can detect import variables.
        if (is_module_scope()) AsModuleScope().AllocateModuleVariables();

        var private_name_scope_iter = new PrivateNameScopeIterator(this);
        if (!private_name_scope_iter.Done() && !private_name_scope_iter.GetScope().ResolvePrivateNames(info))
        {
            return false;
        }

        if (!ResolveVariablesRecursively(info.scope()))
        {
            return false;
        }

        // Don't allocate variables of preparsed scopes.
        if (!was_lazily_parsed()) AllocateVariablesRecursively();

        return true;
    }

    private struct RecalcPrivateNameContextChainCallback : IScopeCallback
    {
        public readonly Iteration Visit(Scope scope)
        {
            Scope? outer = scope.outer_scope();
            if (outer == null) return Iteration.kDescend;
            if (!outer.NeedsContext())
            {
                scope.set_private_name_lookup_skips_outer_class(outer.private_name_lookup_skips_outer_class());
            }
            if (!scope.is_function_scope() || scope.AsDeclarationScope().ShouldEagerCompile())
            {
                return Iteration.kDescend;
            }
            return Iteration.kContinue;
        }
    }

    // Recalculate the private name context chain from the existing skip bit in
    // preparation for AllocateScopeInfos. Because the private name scope is
    // implemented with a skip bit for scopes in heritage position, that bit may
    // need to be recomputed due scopes that do not need contexts.
    private void RecalcPrivateNameContextChain()
    {
        var callback = new RecalcPrivateNameContextChainCallback();
        ForEach(ref callback);
    }

    internal List<Variable> params_for_printing() => _params;
}

public sealed class ModuleScope : DeclarationScope
{
    private readonly SourceTextModuleDescriptor? _moduleDescriptor;

    public ModuleScope(DeclarationScope script_scope, AstValueFactory avfactory)
        : base(script_scope, ScopeType.MODULE_SCOPE, FunctionKind.Module)
    {
        _moduleDescriptor = new SourceTextModuleDescriptor();
        set_language_mode(LanguageMode.Strict);
        DeclareThis(avfactory);
    }

    // Deserialization. Does not restore the module descriptor.
    internal ModuleScope(IScopeInfo scope_info, AstValueFactory avfactory, ParsingFlags flags)
        : base(ScopeType.MODULE_SCOPE, avfactory, scope_info, flags)
    {
        _moduleDescriptor = null;
        set_language_mode(LanguageMode.Strict);
    }

    // Returns null in a deserialized scope.
    public SourceTextModuleDescriptor? module() => _moduleDescriptor;

    // Set MODULE as VariableLocation for all variables that will live in a
    // module's export table.
    public void AllocateModuleVariables()
    {
        foreach (var it in module()!.regular_imports())
        {
            Variable var = LookupLocal(it.Key)!;
            var.AllocateTo(VariableLocation.MODULE, it.Value.cell_index);
        }

        foreach (var it in module()!.regular_exports())
        {
            Variable var = LookupLocal(it.Key)!;
            var.AllocateTo(VariableLocation.MODULE, it.Value.cell_index);
        }
    }
}

public sealed class ClassScope : Scope
{
    private sealed class RareData
    {
        public readonly UnresolvedList unresolved_private_names = new();
        public readonly VariableMap private_name_map = new();
        public Variable? brand;
    }

    private RareData? _rareData;
    private bool _isParsingHeritage;
    private Variable? _classVariable;
    // These are only maintained when the scope is parsed, not when the
    // scope is deserialized.
    private bool _hasStaticPrivateMethods;
    private bool _hasExplicitStaticPrivateMethodsAccess;
    private bool _isAnonymousClass;
    // This is only maintained during reparsing, restored from the
    // preparsed data.
    private bool _shouldSaveClassVariable;

    public ClassScope(Scope outer_scope, bool is_anonymous) : base(outer_scope, ScopeType.CLASS_SCOPE)
    {
        set_language_mode(LanguageMode.Strict);
        set_is_anonymous_class(is_anonymous);
    }

    // Deserialization.
    internal ClassScope(AstValueFactory ast_value_factory, IScopeInfo scope_info, ParsingFlags flags)
        : base(ScopeType.CLASS_SCOPE, ast_value_factory, scope_info, flags)
    {
        set_language_mode(LanguageMode.Strict);
        if (scope_info.ClassScopeHasPrivateBrand())
        {
            Variable? brand = LookupInScopeInfo(ast_value_factory.dot_brand_string(), this);
            EnsureRareData().brand = brand;
        }

        // If the class variable is context-allocated and its index is
        // saved for deserialization, deserialize it.
        if (scope_info.HasSavedClassVariable())
        {
            var (name, index) = scope_info.SavedClassVariable();
            Variable var = DeclareClassVariable(ast_value_factory, ast_value_factory.GetString(name), scope_info.EndPosition());
            var.set_maybe_assigned();
            var.AllocateTo(VariableLocation.CONTEXT, ContextSlots.MIN_CONTEXT_SLOTS + index);
        }

        set_start_position(scope_info.StartPosition());
        set_end_position(scope_info.EndPosition());
    }

    public readonly struct HeritageParsingScope : IDisposable
    {
        private readonly ClassScope _classScope;

        public HeritageParsingScope(ClassScope class_scope)
        {
            _classScope = class_scope;
            _classScope.SetIsParsingHeritage(true);
        }

        public void Dispose() => _classScope.SetIsParsingHeritage(false);
    }

    private static bool IsComplementaryAccessorPair(VariableMode a, VariableMode b) => a switch
    {
        VariableMode.PrivateGetterOnly => b == VariableMode.PrivateSetterOnly,
        VariableMode.PrivateSetterOnly => b == VariableMode.PrivateGetterOnly,
        _ => false,
    };

    // Declare a private name in the private name map and add it to the
    // local variables of this scope.
    public Variable DeclarePrivateName(AstRawString name, VariableMode mode, IsStaticFlag is_static_flag, out bool was_added)
    {
        Variable result = EnsureRareData().private_name_map.Declare(this, name, mode, VariableKind.NORMAL_VARIABLE,
            InitializationFlag.kNeedsInitialization, MaybeAssignedFlag.kNotAssigned, is_static_flag, out was_added);
        if (was_added)
        {
            locals_.Add(result);
            if (result.is_static() && IsPrivateMethodOrAccessorVariableMode(result.mode()))
            {
                set_has_static_private_methods(true);
            }
        }
        else if (IsComplementaryAccessorPair(result.mode(), mode) && result.is_static_flag() == is_static_flag)
        {
            was_added = true;
            result.set_mode(VariableMode.PrivateGetterAndSetter);
        }
        result.ForceContextAllocation();
        return result;
    }

    private Variable? LookupLocalPrivateName(AstRawString name)
    {
        RareData? rare_data = GetRareData();
        if (rare_data == null)
        {
            return null;
        }
        return rare_data.private_name_map.Lookup(name);
    }

    // Get the current tail of unresolved private names to be used to
    // reset the tail.
    public ThreadedList<VariableProxy>.Iterator? GetUnresolvedPrivateNameTail()
    {
        RareData? rare_data = GetRareData();
        if (rare_data == null)
        {
            return null;
        }
        return rare_data.unresolved_private_names.end();
    }

    // Reset the tail of unresolved private names, discard everything
    // between the tail passed into this method and the current tail.
    public void ResetUnresolvedPrivateNameTail(ThreadedList<VariableProxy>.Iterator? tail)
    {
        RareData? rare_data = GetRareData();
        if (rare_data == null || (tail.HasValue && rare_data.unresolved_private_names.end() == tail.Value))
        {
            return;
        }

        bool tail_is_empty = !tail.HasValue;
        if (tail_is_empty)
        {
            // If the saved tail is empty, the list used to be empty, so clear it.
            rare_data.unresolved_private_names.Clear();
        }
        else
        {
            rare_data.unresolved_private_names.Rewind(tail!.Value);
        }
    }

    // Migrate private names added between the tail passed into this method
    // and the current tail.
    public void MigrateUnresolvedPrivateNameTail(AstNodeFactory ast_node_factory, ThreadedList<VariableProxy>.Iterator? tail)
    {
        RareData? rare_data = GetRareData();
        if (rare_data == null || (tail.HasValue && rare_data.unresolved_private_names.end() == tail.Value))
        {
            return;
        }
        var migrated_names = new UnresolvedList();

        // If the saved tail is empty, the list used to be empty, so we should
        // migrate everything after the head.
        bool tail_is_empty = !tail.HasValue;
        var it = tail_is_empty ? rare_data.unresolved_private_names.begin() : tail!.Value;

        for (; it != rare_data.unresolved_private_names.end(); it.MoveNext())
        {
            VariableProxy proxy = it.Current!;
            VariableProxy copy = ast_node_factory.CopyVariableProxy(proxy);
            migrated_names.Add(copy);
        }

        // Replace with the migrated copies.
        if (tail_is_empty)
        {
            rare_data.unresolved_private_names.Clear();
        }
        else
        {
            rare_data.unresolved_private_names.Rewind(tail!.Value);
        }
        rare_data.unresolved_private_names.Append(migrated_names);
    }

    private Variable? LookupPrivateNameInScopeInfo(AstRawString name)
    {
        var lookup_result = new VariableLookupResult();
        int index = scope_info_!.ContextSlotIndex(name.Value, ref lookup_result);
        if (index < 0)
        {
            return null;
        }

        // Add the found private name to the map to speed up subsequent
        // lookups for the same name.
        Variable var = DeclarePrivateName(name, lookup_result.mode, lookup_result.is_static_flag, out _);
        var.AllocateTo(VariableLocation.CONTEXT, index);
        return var;
    }

    // Find the private name declared in the private name map first,
    // if it cannot be found there, try scope info if there is any.
    // Returns null if it cannot be found.
    private Variable? LookupPrivateName(VariableProxy proxy)
    {
        for (var scope_iter = new PrivateNameScopeIterator(this); !scope_iter.Done(); scope_iter.Next())
        {
            ClassScope scope = scope_iter.GetScope();
            // Try finding it in the private name map first, if it can't be found,
            // try the deserialized scope info.
            Variable? var = scope.LookupLocalPrivateName(proxy.raw_name());
            if (var == null && scope.scope_info_ != null)
            {
                var = scope.LookupPrivateNameInScopeInfo(proxy.raw_name());
            }
            if (var != null)
            {
                return var;
            }
        }
        return null;
    }

    // Try resolving all unresolved private names found in the current scope.
    // Called from DeclarationScope::AllocateVariables() when reparsing a
    // method to generate code or when eval() is called to access private names.
    // If there are any private names that cannot be resolved, returns false.
    public bool ResolvePrivateNames(ParseInfo info)
    {
        RareData? rare_data = GetRareData();
        if (rare_data == null || rare_data.unresolved_private_names.is_empty())
        {
            return true;
        }

        UnresolvedList list = rare_data.unresolved_private_names;
        foreach (VariableProxy proxy in list)
        {
            Variable? var = LookupPrivateName(proxy);
            if (var == null)
            {
                // It's only possible to fail to resolve private names here if
                // this is at the top level or the private name is accessed through eval.
                Scanner.Location loc = proxy.location();
                info.pending_error_handler().ReportMessageAt(loc.beg_pos, loc.end_pos,
                    MessageTemplate.InvalidPrivateFieldResolution, proxy.raw_name());
                return false;
            }
            else
            {
                proxy.BindTo(var);
            }
        }

        // By now all unresolved private names should be resolved so
        // clear the list.
        list.Clear();
        return true;
    }

    // Called after the entire class literal is parsed.
    // - If we are certain a private name cannot be resolve, return that
    //   variable proxy.
    // - If we find the private name in the scope chain, return null.
    //   If the name is found in the current class scope, resolve it
    //   immediately.
    // - If we are not sure if the private name can be resolved or not yet,
    //   return null.
    public VariableProxy? ResolvePrivateNamesPartially()
    {
        RareData? rare_data = GetRareData();
        if (rare_data == null || rare_data.unresolved_private_names.is_empty())
        {
            return null;
        }

        var private_name_scope_iter = new PrivateNameScopeIterator(this);
        private_name_scope_iter.Next();
        UnresolvedList unresolved = rare_data.unresolved_private_names;
        // V8: rare_data->private_name_map.capacity() > 0. A ZoneHashMap is
        // allocated with capacity 8 when RareData is created, so this is always
        // true here.
        bool has_private_names = true;

        // If the class itself does not have private names, nor does it have
        // an outer private name scope, then we are certain any private name access
        // inside cannot be resolved.
        if (!has_private_names && private_name_scope_iter.Done() && !unresolved.is_empty())
        {
            return unresolved.first();
        }

        for (VariableProxy? proxy = unresolved.first(); proxy != null;)
        {
            VariableProxy? next = proxy.next_unresolved();
            unresolved.Remove(proxy);
            Variable? var = null;

            // If we can find private name in the current class scope, we can bind
            // them immediately because it's going to shadow any outer private names.
            if (has_private_names)
            {
                var = LookupLocalPrivateName(proxy.raw_name());
                if (var != null)
                {
                    var.set_is_used();
                    proxy.BindTo(var);
                    // If the variable being accessed is a static private method, we need to
                    // save the class variable in the context to check that the receiver is
                    // the class during runtime.
                    if (var.is_static() && IsPrivateMethodOrAccessorVariableMode(var.mode()))
                    {
                        set_has_explicit_static_private_methods_access(true);
                    }
                }
            }

            // If the current scope does not have declared private names,
            // try looking from the outer class scope later.
            if (var == null)
            {
                // There's no outer private name scope so we are certain that the variable
                // cannot be resolved later.
                if (private_name_scope_iter.Done())
                {
                    return proxy;
                }

                // The private name may be found later in the outer private name scope, so
                // push it to the outer scope.
                private_name_scope_iter.AddUnresolvedPrivateName(proxy);
            }

            proxy = next;
        }

        return null;
    }

    public Variable DeclareBrandVariable(AstValueFactory ast_value_factory, IsStaticFlag is_static_flag, int class_token_pos)
    {
        Variable brand = Declare(ast_value_factory.dot_brand_string(), VariableMode.Const, VariableKind.NORMAL_VARIABLE,
                                 InitializationFlag.kNeedsInitialization, MaybeAssignedFlag.kNotAssigned, out _);
        brand.set_is_static_flag(is_static_flag);
        brand.ForceContextAllocation();
        brand.set_is_used();
        EnsureRareData().brand = brand;
        brand.set_initializer_position(class_token_pos);
        return brand;
    }

    public Variable DeclareClassVariable(AstValueFactory ast_value_factory, AstRawString name, int class_token_pos)
    {
        _classVariable = Declare(name.IsEmpty() ? ast_value_factory.dot_string() : name, VariableMode.Const,
                                 VariableKind.NORMAL_VARIABLE, InitializationFlag.kNeedsInitialization,
                                 MaybeAssignedFlag.kNotAssigned, out _);
        _classVariable.set_initializer_position(class_token_pos);
        return _classVariable;
    }

    public Variable? brand() => GetRareData()?.brand;

    public Variable? class_variable() => _classVariable;

    public bool IsParsingHeritage() => _isParsingHeritage;

    // Only maintained when the scope is parsed, not when the scope is
    // deserialized.
    public bool has_static_private_methods() => _hasStaticPrivateMethods;
    public void set_has_static_private_methods(bool value) => _hasStaticPrivateMethods = value;

    // Returns whether the index of class variable of this class scope should be
    // recorded in the ScopeInfo.
    // If any inner scope accesses static private names directly, the class
    // variable will be forced to be context-allocated.
    // The inner scope may also calls eval which may results in access to
    // static private names.
    // Only maintained when the scope is parsed.
    public bool should_save_class_variable() =>
        _shouldSaveClassVariable || _hasExplicitStaticPrivateMethodsAccess ||
        (has_static_private_methods() && inner_scope_calls_eval());

    // Only maintained when the scope is parsed.
    public bool is_anonymous_class() => _isAnonymousClass;

    // Overriden during reparsing
    public void set_should_save_class_variable() => _shouldSaveClassVariable = true;

    public void set_has_explicit_static_private_methods_access(bool value) => _hasExplicitStaticPrivateMethodsAccess = value;

    public void set_is_anonymous_class(bool value) => _isAnonymousClass = value;

    // The private names declared in this class scope (for printing / tests).
    public VariableMap? private_name_map() => GetRareData()?.private_name_map;

    internal UnresolvedList EnsureUnresolvedPrivateNames() => EnsureRareData().unresolved_private_names;

    private RareData? GetRareData() => _rareData;
    internal VariableMap? private_name_map_for_printing() => _rareData?.private_name_map;
    private RareData EnsureRareData() => _rareData ??= new RareData();
    private void SetIsParsingHeritage(bool v) => _isParsingHeritage = v;
}

// Iterate over the private name scope chain. The iteration proceeds from the
// innermost private name scope outwards.
public struct PrivateNameScopeIterator
{
    private bool _skippedAnyScopes;
    private readonly Scope _startScope;
    private Scope? _currentScope;

    public PrivateNameScopeIterator(Scope start)
    {
        _skippedAnyScopes = false;
        _startScope = start;
        _currentScope = start;
        if (!start.is_class_scope() || start.AsClassScope().IsParsingHeritage())
        {
            Next();
        }
    }

    public readonly bool Done() => _currentScope == null;

    public void Next()
    {
        Scope inner = _currentScope!;
        Scope? scope = inner.outer_scope();
        while (scope != null)
        {
            if (scope.is_class_scope())
            {
                if (!inner.private_name_lookup_skips_outer_class())
                {
                    _currentScope = scope;
                    return;
                }
                _skippedAnyScopes = true;
            }
            inner = scope;
            scope = scope.outer_scope();
        }
        _currentScope = null;
    }

    // Add an unresolved private name to the current scope.
    public readonly void AddUnresolvedPrivateName(VariableProxy proxy)
    {
        // During a reparse, current_scope_->already_resolved_ may be true here,
        // because the class scope is deserialized while the function scope inside may
        // be new.

        // Use dynamic lookup for top-level scopes in debug-evaluate.
        if (Done())
        {
            _startScope.ForceDynamicLookup(proxy);
            return;
        }

        GetScope().EnsureUnresolvedPrivateNames().Add(proxy);
        // Any closure scope that contain uses of private names that skips over a
        // class scope due to heritage expressions need private name context chain
        // recalculation, since not all scopes require a Context or ScopeInfo. See
        // comment in DeclarationScope::RecalcPrivateNameContextChain.
        if (_skippedAnyScopes)
        {
            _startScope.GetClosureScope().RecordNeedsPrivateNameContextChainRecalc();
        }
    }

    public readonly ClassScope GetScope() => _currentScope!.AsClassScope();
}
