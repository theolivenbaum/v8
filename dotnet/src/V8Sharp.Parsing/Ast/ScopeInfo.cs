// Copyright 2016 the V8 project authors. All rights reserved.
// Use of this source code is governed by a BSD-style license that can be
// found in the LICENSE file.

// The parts of src/objects/scope-info.h and src/objects/script.h that scope
// analysis reads. ScopeInfo and Script are heap objects owned by the engine;
// scope analysis (src/ast/scopes.cc) only needs to query them, so the engine
// implements these interfaces on its ScopeInfo/Script and passes them in.

using V8Sharp.Common;

namespace V8Sharp.Ast;

// src/objects/scope-info.h VariableLookupResult.
public struct VariableLookupResult
{
    public uint context_index;
    public int slot_index;
    // repl_mode flag is needed to disable inlining of 'const' variables in REPL
    // mode.
    public bool is_repl_mode;
    public IsStaticFlag is_static_flag;
    public VariableMode mode;
    public InitializationFlag init_flag;
    public MaybeAssignedFlag maybe_assigned_flag;
    public int initializer_position;
}

public interface IScopeInfo
{
    ScopeType scope_type();
    LanguageMode language_mode();
    FunctionKind function_kind();
    bool is_declaration_scope();
    bool is_script_scope();
    bool IsEmpty();
    bool IsDebugEvaluateScope();
    bool PrivateNameLookupSkipsOuterClass();
    bool HasContextCells();
    bool is_hoisted_in_context();
    bool SloppyEvalCanExtendVars();
    bool ClassScopeHasPrivateBrand();
    bool HasSavedClassVariable();
    // The saved class variable's name and context local index.
    (string name, int index) SavedClassVariable();
    int StartPosition();
    int EndPosition();
    int ContextLength();
    bool HasContext();
    bool HasSimpleParameters();
    int ParameterCount();
    int UniqueIdInScript();
    bool HasOuterScopeInfo();
    IScopeInfo? OuterScopeInfo();
    int ContextLocalCount();
    bool HasInlinedLocalNames();
    string ContextInlinedLocalName(int var);
    VariableMode ContextLocalMode(int var);
    InitializationFlag ContextLocalInitFlag(int var);
    MaybeAssignedFlag ContextLocalMaybeAssignedFlag(int var);
    // Lookup support for deserialized scope info. Returns the index of the
    // context slot for the given name, or -1.
    int ContextSlotIndex(string name);
    int ContextSlotIndex(string name, ref VariableLookupResult lookup_result);
    // Lookup metadata of a MODULE-allocated variable. Return 0 if there is no
    // module variable with the given name (the index value of a MODULE variable
    // is never 0).
    int ModuleIndex(string name, out VariableMode mode, out InitializationFlag init_flag,
                    out MaybeAssignedFlag maybe_assigned_flag, out int initializer_position);
    // Lookup support for the function variable of a named function expression;
    // -1 if absent.
    int FunctionContextSlotIndex(string name);
    int ReceiverContextSlotIndex();
    bool HasAllocatedReceiver();
}

// What Scope::DeserializeScopeChain needs from a Script (eval origin chain).
public interface IScriptEvalOrigin
{
    int eval_from_position();
    bool has_eval_from_shared();
    // The Script of eval_from_shared(), if any.
    IScriptEvalOrigin? eval_from_shared_script();
    // eval_from_scope_info() if has_eval_from_scope_info(), otherwise null.
    IScopeInfo? eval_from_scope_info();
}

// Heap-side scope infos the engine supplies to scope analysis.
public interface IScopeInfoProvider
{
    // Factory::empty_scope_info().
    IScopeInfo empty_scope_info();
    // Factory::global_this_binding_scope_info().
    IScopeInfo global_this_binding_scope_info();
    // ScopeInfo::Create(isolate, zone, scope, outer_scope).
    IScopeInfo Create(Scope scope, IScopeInfo? outer_scope);
}
