// Copyright 2012 the V8 project authors. All rights reserved.
// Use of this source code is governed by a BSD-style license that can be
// found in the LICENSE file.

// Port of the parser-related declarations of src/common/globals.h,
// src/objects/function-kind.h and src/objects/function-syntax-kind.h.
// These live in V8Sharp.Parsing because the parser needs them and the engine
// (which references this assembly) reuses them. Use them with
// `using static V8Sharp.Common.Globals;`.

using System.Runtime.CompilerServices;

namespace V8Sharp.Common;

public enum LanguageMode : byte { Sloppy, Strict }

public enum REPLMode { Yes, No }

public enum ParsingWhileDebugging { Yes, No }

public enum NativesFlag { NOT_NATIVES_CODE, EXTENSION_CODE, INSPECTOR_CODE }

// ParseRestriction is used to restrict the set of valid statements in a
// unit of compilation.  Restriction violations cause a syntax error.
public enum ParseRestriction : byte
{
    NO_PARSE_RESTRICTION,         // All expressions are allowed.
    ONLY_SINGLE_FUNCTION_LITERAL, // Only a single FunctionLiteral expression.
}

public enum ScriptType { Classic, Module }

// The first 4 scopes are top-level scopes. The order is used for range checks.
public enum ScopeType : byte
{
    // Start of top-level scopes.
    SCRIPT_SCOPE,     // The top-level scope for a script or a top-level eval.
    REPL_MODE_SCOPE,  // The top-level scope for a repl-mode script.
    EVAL_SCOPE,       // The top-level scope for an eval source.
    MODULE_SCOPE,     // The scope introduced by a module literal
    // End of top-level scopes.
    CLASS_SCOPE,         // The scope introduced by a class.
    FUNCTION_SCOPE,      // The top-level scope for a function.
    CATCH_SCOPE,         // The scope introduced by catch.
    BLOCK_SCOPE,         // The scope introduced by a new block.
    WITH_SCOPE,          // The scope introduced by with.
    SHADOW_REALM_SCOPE,  // Synthetic scope for ShadowRealm NativeContexts.
}

// The order of this enum has to be kept in sync with the predicates below.
public enum VariableMode : byte
{
    // User declared variables:
    Let,        // declared via 'let' declarations (first lexical)
    Const,      // declared via 'const' declarations
    Using,      // declared via 'using' declaration for explicit resource management
    AwaitUsing, // declared via 'await using' declaration (last lexical)
    Var,        // declared via 'var', and 'function' declarations

    // Variables introduced by the compiler:
    Temporary,     // temporary variables (not user-visible), stack-allocated
                   // unless the scope as a whole has forced context allocation
    Dynamic,       // always require dynamic lookup (we don't know the declaration)
    DynamicGlobal, // requires dynamic lookup, but we know that the variable is
                   // global unless it has been shadowed by an eval-introduced variable
    DynamicLocal,  // requires dynamic lookup, but we know that the variable is
                   // local and where it is unless it has been shadowed by an
                   // eval-introduced variable

    // Variables for private methods or accessors whose access require
    // brand check. Declared only in class scopes by the compiler
    // and allocated only in class contexts:
    PrivateMethod,
    PrivateSetterOnly,
    PrivateGetterOnly,
    PrivateGetterAndSetter,

    FirstImmutableLexicalVariableMode = Const,
    LastLexicalVariableMode = AwaitUsing,
}

public enum VariableKind : byte
{
    NORMAL_VARIABLE,
    PARAMETER_VARIABLE,
    THIS_VARIABLE,
    SLOPPY_BLOCK_FUNCTION_VARIABLE,
    SLOPPY_FUNCTION_NAME_VARIABLE,
}

public enum VariableLocation : byte
{
    // Before and during variable allocation, a variable whose location is
    // not yet determined.  After allocation, a variable looked up as a
    // property on the global object (and possibly absent).
    UNALLOCATED,
    // A slot in the parameter section on the stack. The receiver is index -1;
    // the first parameter is index 0.
    PARAMETER,
    // A slot in the local section on the stack.
    LOCAL,
    // An indexed slot in a heap context.
    CONTEXT,
    // A named slot in a heap context, with lookup starting at the current context.
    LOOKUP,
    // A named slot in a module's export table.
    MODULE,
    // An indexed slot in a script context, accessed like a global (REPL mode).
    REPL_GLOBAL,

    kLastVariableLocation = REPL_GLOBAL,
}

public enum InitializationFlag : byte { kNeedsInitialization, kCreatedInitialized }

public enum IsStaticFlag : byte { NotStatic, Static }

public enum MaybeAssignedFlag : byte { kNotAssigned, kMaybeAssigned }

public enum VariableAllocationInfo { NONE, STACK, CONTEXT, UNUSED }

public enum FunctionKind : byte
{
    // BEGIN constructable functions
    NormalFunction,
    Module,
    ModuleWithTopLevelAwait,
    // BEGIN class constructors
    // BEGIN base constructors
    BaseConstructor,
    // BEGIN default constructors
    DefaultBaseConstructor,
    // END base constructors
    // BEGIN derived constructors
    DefaultDerivedConstructor,
    // END default constructors
    DerivedConstructor,
    // END derived constructors
    // END class constructors
    // END constructable functions.
    // BEGIN accessors
    GetterFunction,
    StaticGetterFunction,
    SetterFunction,
    StaticSetterFunction,
    // END accessors
    // BEGIN arrow functions
    ArrowFunction,
    // BEGIN async functions
    AsyncArrowFunction,
    // END arrow functions
    AsyncFunction,
    // BEGIN concise methods 1
    AsyncConciseMethod,
    StaticAsyncConciseMethod,
    // BEGIN generators
    AsyncConciseGeneratorMethod,
    StaticAsyncConciseGeneratorMethod,
    // END concise methods 1
    AsyncGeneratorFunction,
    // END async functions
    GeneratorFunction,
    // BEGIN concise methods 2
    ConciseGeneratorMethod,
    StaticConciseGeneratorMethod,
    // END generators
    ConciseMethod,
    StaticConciseMethod,
    ClassMembersInitializerFunction,
    ClassMembersInitializerFunctionPrecededByStatic,
    ClassStaticInitializerFunction,
    ClassStaticInitializerFunctionPrecededByMember,
    // END concise methods 2
    Invalid,

    LastFunctionKind = ClassStaticInitializerFunctionPrecededByMember,
}

public enum FunctionSyntaxKind : byte
{
    AnonymousExpression,
    NamedExpression,
    Declaration,
    AccessorOrMethod,
    Wrapped,

    LastFunctionSyntaxKind = Wrapped,
}

public static class Globals
{
    public const int kNoSourcePosition = -1;
    public const int kInvalidInfoId = -1;
    public const int kFunctionLiteralIdTopLevel = 0;
    public const uint kMaxUInt32 = 0xFFFF_FFFFu;
    public const int kMaxInt = int.MaxValue;
    public const int kMinInt = int.MinValue;
    public const int kFunctionKindBitSize = 5;
    // Smi::kMaxValue with 31-bit Smis (pointer compression, V8's default).
    public const int kSmiMaxValue = (1 << 30) - 1;
    public const int kSmiMinValue = -(1 << 30);
    // String::kMaxLength with 31-bit Smis and pointer compression on 64-bit.
    public const int kStringMaxLength = (1 << 29) - 24;
    public const int kMaxCodePoint = 0x10FFFF;
    // Script::kTemporaryScriptId.
    public const int kTemporaryScriptId = -2;

    // --- LanguageMode -------------------------------------------------------

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool is_sloppy(LanguageMode mode) => mode == LanguageMode.Sloppy;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool is_strict(LanguageMode mode) => mode != LanguageMode.Sloppy;

    public static bool is_valid_language_mode(int mode) => mode == (int)LanguageMode.Sloppy || mode == (int)LanguageMode.Strict;

    public static LanguageMode construct_language_mode(bool strict_bit) => strict_bit ? LanguageMode.Strict : LanguageMode.Sloppy;

    // Return kStrict if either of the language modes is kStrict, or kSloppy
    // otherwise.
    public static LanguageMode stricter_language_mode(LanguageMode mode1, LanguageMode mode2)
        => (LanguageMode)((int)mode1 | (int)mode2);

    public static string LanguageMode2String(LanguageMode mode) => mode == LanguageMode.Sloppy ? "sloppy" : "strict";

    public static REPLMode construct_repl_mode(bool is_repl_mode) => is_repl_mode ? REPLMode.Yes : REPLMode.No;

    // --- VariableMode -------------------------------------------------------

    public static string VariableMode2String(VariableMode mode) => mode switch
    {
        VariableMode.Var => "VAR",
        VariableMode.Let => "LET",
        VariableMode.PrivateGetterOnly => "PRIVATE_GETTER_ONLY",
        VariableMode.PrivateSetterOnly => "PRIVATE_SETTER_ONLY",
        VariableMode.PrivateMethod => "PRIVATE_METHOD",
        VariableMode.PrivateGetterAndSetter => "PRIVATE_GETTER_AND_SETTER",
        VariableMode.Const => "CONST",
        VariableMode.Dynamic => "DYNAMIC",
        VariableMode.DynamicGlobal => "DYNAMIC_GLOBAL",
        VariableMode.DynamicLocal => "DYNAMIC_LOCAL",
        VariableMode.Temporary => "TEMPORARY",
        VariableMode.Using => "USING",
        VariableMode.AwaitUsing => "AWAIT_USING",
        _ => throw new InvalidOperationException("UNREACHABLE"),
    };

    public static string ImmutableLexicalVariableModeToString(VariableMode mode) => mode switch
    {
        VariableMode.Const => "const",
        VariableMode.Using => "using",
        VariableMode.AwaitUsing => "await using",
        _ => throw new InvalidOperationException("UNREACHABLE"),
    };

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsDynamicVariableMode(VariableMode mode) => mode >= VariableMode.Dynamic && mode <= VariableMode.DynamicLocal;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsDeclaredVariableMode(VariableMode mode) => mode <= VariableMode.Var;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsPrivateAccessorVariableMode(VariableMode mode) => mode >= VariableMode.PrivateSetterOnly && mode <= VariableMode.PrivateGetterAndSetter;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsPrivateMethodVariableMode(VariableMode mode) => mode == VariableMode.PrivateMethod;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsPrivateMethodOrAccessorVariableMode(VariableMode mode) => IsPrivateMethodVariableMode(mode) || IsPrivateAccessorVariableMode(mode);

    public static bool IsSerializableVariableMode(VariableMode mode) => IsDeclaredVariableMode(mode) || IsPrivateMethodOrAccessorVariableMode(mode);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsImmutableLexicalVariableMode(VariableMode mode) => mode >= VariableMode.FirstImmutableLexicalVariableMode && mode <= VariableMode.LastLexicalVariableMode;

    public static bool IsImmutableLexicalOrPrivateVariableMode(VariableMode mode) => IsImmutableLexicalVariableMode(mode) || IsPrivateMethodOrAccessorVariableMode(mode);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsLexicalVariableMode(VariableMode mode) => mode <= VariableMode.LastLexicalVariableMode;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsResourceManagedVariableMode(VariableMode mode) => mode == VariableMode.Using || mode == VariableMode.AwaitUsing;

    // --- FunctionKind -------------------------------------------------------

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool InRange(FunctionKind kind, FunctionKind lo, FunctionKind hi) => (uint)(kind - lo) <= (uint)(hi - lo);

    public static bool IsArrowFunction(FunctionKind kind) => InRange(kind, FunctionKind.ArrowFunction, FunctionKind.AsyncArrowFunction);
    public static bool IsModule(FunctionKind kind) => InRange(kind, FunctionKind.Module, FunctionKind.ModuleWithTopLevelAwait);
    public static bool IsModuleWithTopLevelAwait(FunctionKind kind) => kind == FunctionKind.ModuleWithTopLevelAwait;
    public static bool IsAsyncGeneratorFunction(FunctionKind kind) => InRange(kind, FunctionKind.AsyncConciseGeneratorMethod, FunctionKind.AsyncGeneratorFunction);
    public static bool IsGeneratorFunction(FunctionKind kind) => InRange(kind, FunctionKind.AsyncConciseGeneratorMethod, FunctionKind.StaticConciseGeneratorMethod);
    public static bool IsAsyncFunction(FunctionKind kind) => InRange(kind, FunctionKind.AsyncArrowFunction, FunctionKind.AsyncGeneratorFunction);
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsResumableFunction(FunctionKind kind) => IsGeneratorFunction(kind) || IsAsyncFunction(kind) || IsModule(kind);

    public static bool IsConciseMethod(FunctionKind kind)
        => InRange(kind, FunctionKind.AsyncConciseMethod, FunctionKind.StaticAsyncConciseGeneratorMethod)
        || InRange(kind, FunctionKind.ConciseGeneratorMethod, FunctionKind.ClassStaticInitializerFunctionPrecededByMember);

    public static bool IsStrictFunctionWithoutPrototype(FunctionKind kind)
        => InRange(kind, FunctionKind.GetterFunction, FunctionKind.AsyncArrowFunction)
        || InRange(kind, FunctionKind.AsyncConciseMethod, FunctionKind.StaticAsyncConciseGeneratorMethod)
        || InRange(kind, FunctionKind.ConciseGeneratorMethod, FunctionKind.ClassStaticInitializerFunctionPrecededByMember);

    public static bool IsGetterFunction(FunctionKind kind) => InRange(kind, FunctionKind.GetterFunction, FunctionKind.StaticGetterFunction);
    public static bool IsSetterFunction(FunctionKind kind) => InRange(kind, FunctionKind.SetterFunction, FunctionKind.StaticSetterFunction);
    public static bool IsAccessorFunction(FunctionKind kind) => InRange(kind, FunctionKind.GetterFunction, FunctionKind.StaticSetterFunction);
    public static bool IsDefaultConstructor(FunctionKind kind) => InRange(kind, FunctionKind.DefaultBaseConstructor, FunctionKind.DefaultDerivedConstructor);
    public static bool IsBaseConstructor(FunctionKind kind) => InRange(kind, FunctionKind.BaseConstructor, FunctionKind.DefaultBaseConstructor);
    public static bool IsDerivedConstructor(FunctionKind kind) => InRange(kind, FunctionKind.DefaultDerivedConstructor, FunctionKind.DerivedConstructor);
    public static bool IsClassConstructor(FunctionKind kind) => InRange(kind, FunctionKind.BaseConstructor, FunctionKind.DerivedConstructor);
    public static bool IsClassInitializerFunction(FunctionKind kind) => InRange(kind, FunctionKind.ClassMembersInitializerFunction, FunctionKind.ClassStaticInitializerFunctionPrecededByMember);
    public static bool IsClassInstanceInitializerFunction(FunctionKind kind) => InRange(kind, FunctionKind.ClassMembersInitializerFunction, FunctionKind.ClassMembersInitializerFunctionPrecededByStatic);
    public static bool IsClassStaticInitializerFunction(FunctionKind kind) => InRange(kind, FunctionKind.ClassStaticInitializerFunction, FunctionKind.ClassStaticInitializerFunctionPrecededByMember);
    public static bool IsConstructable(FunctionKind kind) => InRange(kind, FunctionKind.NormalFunction, FunctionKind.DerivedConstructor);

    public static bool IsStatic(FunctionKind kind) => kind switch
    {
        FunctionKind.StaticGetterFunction or FunctionKind.StaticSetterFunction or FunctionKind.StaticConciseMethod
            or FunctionKind.StaticConciseGeneratorMethod or FunctionKind.StaticAsyncConciseMethod
            or FunctionKind.StaticAsyncConciseGeneratorMethod or FunctionKind.ClassStaticInitializerFunction
            or FunctionKind.ClassStaticInitializerFunctionPrecededByMember => true,
        _ => false,
    };

    public static bool BindsSuper(FunctionKind kind) => IsConciseMethod(kind) || IsAccessorFunction(kind) || IsClassConstructor(kind);

    public static string FunctionKind2String(FunctionKind kind) => kind switch
    {
        FunctionKind.NormalFunction => "NormalFunction",
        FunctionKind.ArrowFunction => "ArrowFunction",
        FunctionKind.GeneratorFunction => "GeneratorFunction",
        FunctionKind.ConciseMethod => "ConciseMethod",
        FunctionKind.StaticConciseMethod => "StaticConciseMethod",
        FunctionKind.DerivedConstructor => "DerivedConstructor",
        FunctionKind.BaseConstructor => "BaseConstructor",
        FunctionKind.GetterFunction => "GetterFunction",
        FunctionKind.StaticGetterFunction => "StaticGetterFunction",
        FunctionKind.SetterFunction => "SetterFunction",
        FunctionKind.StaticSetterFunction => "StaticSetterFunction",
        FunctionKind.AsyncFunction => "AsyncFunction",
        FunctionKind.Module => "Module",
        FunctionKind.ModuleWithTopLevelAwait => "AsyncModule",
        FunctionKind.ClassMembersInitializerFunction => "ClassMembersInitializerFunction",
        FunctionKind.ClassStaticInitializerFunction => "ClassStaticInitializerFunction",
        FunctionKind.ClassMembersInitializerFunctionPrecededByStatic => "ClassMembersInitializerFunctionPrecededByStatic",
        FunctionKind.ClassStaticInitializerFunctionPrecededByMember => "ClassStaticInitializerFunctionPrecededByMember",
        FunctionKind.DefaultBaseConstructor => "DefaultBaseConstructor",
        FunctionKind.DefaultDerivedConstructor => "DefaultDerivedConstructor",
        FunctionKind.AsyncArrowFunction => "AsyncArrowFunction",
        FunctionKind.AsyncConciseMethod => "AsyncConciseMethod",
        FunctionKind.StaticAsyncConciseMethod => "StaticAsyncConciseMethod",
        FunctionKind.ConciseGeneratorMethod => "ConciseGeneratorMethod",
        FunctionKind.StaticConciseGeneratorMethod => "StaticConciseGeneratorMethod",
        FunctionKind.AsyncConciseGeneratorMethod => "AsyncConciseGeneratorMethod",
        FunctionKind.StaticAsyncConciseGeneratorMethod => "StaticAsyncConciseGeneratorMethod",
        FunctionKind.AsyncGeneratorFunction => "AsyncGeneratorFunction",
        FunctionKind.Invalid => "Invalid",
        _ => throw new InvalidOperationException("UNREACHABLE"),
    };

    public static string FunctionSyntaxKind2String(FunctionSyntaxKind kind) => kind switch
    {
        FunctionSyntaxKind.AnonymousExpression => "AnonymousExpression",
        FunctionSyntaxKind.NamedExpression => "NamedExpression",
        FunctionSyntaxKind.Declaration => "Declaration",
        FunctionSyntaxKind.AccessorOrMethod => "AccessorOrMethod",
        FunctionSyntaxKind.Wrapped => "Wrapped",
        _ => throw new InvalidOperationException("UNREACHABLE"),
    };

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsInRange(int value, int lower, int upper) => (uint)(value - lower) <= (uint)(upper - lower);
}
