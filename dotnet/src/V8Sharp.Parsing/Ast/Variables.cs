// Copyright 2011 the V8 project authors. All rights reserved.
// Use of this source code is governed by a BSD-style license that can be
// found in the LICENSE file.

// Port of src/ast/variables.h and variables.cc.

using V8Sharp.Common;
using static V8Sharp.Common.Globals;

namespace V8Sharp.Ast;

public sealed class Variable : IThreadedListNode<Variable>
{
    private Scope? _scope;
    private readonly AstRawString _name;

    // If this field is set, this variable references the stored locally bound
    // variable, but it might be shadowed by variable bindings introduced by with
    // blocks or sloppy 'eval' calls between the reference scope (inclusive) and
    // the binding scope (exclusive).
    private Variable? _localIfNotShadowed;
    private Variable? _next;
    private int _index;
    private int _initializerPosition;

    // bit_field_
    private VariableMode _mode;
    private VariableKind _kind;
    private VariableLocation _location;
    private bool _forceContextAllocation;
    private bool _isUsed;
    private InitializationFlag _initializationFlag;
    private MaybeAssignedFlag _maybeAssigned;
    private IsStaticFlag _isStaticFlag;

    // hole_check_analysis_bit_field_
    private byte _holeCheckBitmapIndex;
    private ForceHoleInitializationFlag _forceHoleInitializationFlag;
    private HoleCheckState _holeCheckState;

    Variable? IThreadedListNode<Variable>.NextNode { get => _next; set => _next = value; }

    public Variable(Scope? scope, AstRawString name, VariableMode mode, VariableKind kind,
                    InitializationFlag initialization_flag,
                    MaybeAssignedFlag maybe_assigned_flag = MaybeAssignedFlag.kNotAssigned,
                    IsStaticFlag is_static_flag = IsStaticFlag.NotStatic)
    {
        _scope = scope;
        _name = name;
        _index = -1;
        _initializerPosition = kNoSourcePosition;
        _maybeAssigned = maybe_assigned_flag;
        _initializationFlag = initialization_flag;
        _mode = mode;
        _isUsed = false;
        _forceContextAllocation = false;
        _location = VariableLocation.UNALLOCATED;
        _kind = kind;
        _isStaticFlag = is_static_flag;
        _holeCheckBitmapIndex = kUncacheableHoleCheckBitmapIndex;
        _forceHoleInitializationFlag = ForceHoleInitializationFlag.kHoleInitializationNotForced;
    }

    public Variable(Variable other)
    {
        _scope = other._scope;
        _name = other._name;
        _index = other._index;
        _initializerPosition = other._initializerPosition;
        _mode = other._mode;
        _kind = other._kind;
        _location = other._location;
        _forceContextAllocation = other._forceContextAllocation;
        _isUsed = other._isUsed;
        _initializationFlag = other._initializationFlag;
        _maybeAssigned = other._maybeAssigned;
        _isStaticFlag = other._isStaticFlag;
    }

    // The source code for an eval() call may refer to a variable that is
    // in an outer scope about which we don't know anything (it may not
    // be the script scope). scope() is nullptr in that case. Currently the
    // scope is only used to follow the context chain length.
    public Scope? scope() => _scope;

    // This is for adjusting the scope of temporaries used when desugaring
    // parameter initializers.
    public void set_scope(Scope scope) => _scope = scope;

    public AstRawString raw_name() => _name;
    public VariableMode mode() => _mode;
    public void set_mode(VariableMode mode) => _mode = mode;
    public void set_is_static_flag(IsStaticFlag is_static_flag) => _isStaticFlag = is_static_flag;
    public IsStaticFlag is_static_flag() => _isStaticFlag;
    public bool is_static() => _isStaticFlag == IsStaticFlag.Static;

    public bool has_forced_context_allocation() => _forceContextAllocation;
    public void ForceContextAllocation() => _forceContextAllocation = true;
    public bool is_used() => _isUsed;
    public void set_is_used() => _isUsed = true;
    public MaybeAssignedFlag maybe_assigned() => _maybeAssigned;
    public void clear_maybe_assigned() => _maybeAssigned = MaybeAssignedFlag.kNotAssigned;
    public void set_maybe_assigned() => _maybeAssigned = MaybeAssignedFlag.kMaybeAssigned;

    public void SetMaybeAssigned()
    {
        if (IsImmutableLexicalVariableMode(mode()))
        {
            return;
        }
        // Private names are only initialized once by us.
        if (_name.IsPrivateName())
        {
            return;
        }
        // If this variable is dynamically shadowing another variable, then that
        // variable could also be assigned (in the non-shadowing case).
        if (has_local_if_not_shadowed())
        {
            // Avoid repeatedly marking the same tree of variables by only recursing
            // when this variable's maybe_assigned status actually changes.
            if (maybe_assigned() == MaybeAssignedFlag.kNotAssigned)
            {
                local_if_not_shadowed().SetMaybeAssigned();
            }
        }
        set_maybe_assigned();
    }

    public bool requires_brand_check() => IsPrivateMethodOrAccessorVariableMode(mode());

    public int initializer_position() => _initializerPosition;
    public void set_initializer_position(int pos) => _initializerPosition = pos;

    public bool IsUnallocated() => _location == VariableLocation.UNALLOCATED;
    public bool IsParameter() => _location == VariableLocation.PARAMETER;
    public bool IsStackLocal() => _location == VariableLocation.LOCAL;
    public bool IsStackAllocated() => IsParameter() || IsStackLocal();
    public bool IsContextSlot() => _location == VariableLocation.CONTEXT;
    public bool IsLookupSlot() => _location == VariableLocation.LOOKUP;

    public bool IsGlobalObjectProperty()
    {
        // Temporaries are never global, they must always be allocated in the
        // activation frame.
        return (IsDynamicVariableMode(mode()) || mode() == VariableMode.Var) &&
               _scope != null && _scope.is_script_scope();
    }

    // True for 'let' and 'const' variables declared in the script scope of a REPL
    // script.
    public bool IsReplGlobal() =>
        scope()!.is_repl_mode_scope() &&
        (mode() == VariableMode.Let || mode() == VariableMode.Const ||
         mode() == VariableMode.Using || mode() == VariableMode.AwaitUsing);

    public bool is_dynamic() => IsDynamicVariableMode(mode());

    // Returns the InitializationFlag this Variable was created with.
    public InitializationFlag initialization_flag() => _initializationFlag;

    // Whether this variable needs to be initialized with the hole at
    // declaration time. Only returns valid results after scope analysis.
    public bool binding_needs_init()
    {
        // Always initialize if hole initialization was forced during
        // scope analysis.
        if (IsHoleInitializationForced()) return true;

        // If initialization was not forced, no need for initialization
        // for stack allocated variables, since UpdateNeedsHoleCheck()
        // in scopes.cc has proven that no VariableProxy refers to
        // this variable in such a way that a runtime hole check
        // would be generated.
        if (IsStackAllocated()) return false;

        // Otherwise, defer to the flag set when this Variable was constructed.
        return initialization_flag() == InitializationFlag.kNeedsInitialization;
    }

    public enum HoleCheckState : byte
    {
        kUncached = 0,
        kForce = 1,
        kSkip = 2,
    }

    public HoleCheckState hole_check_state() => _holeCheckState;
    public void set_hole_check_state(HoleCheckState state) => _holeCheckState = state;

    [Flags]
    public enum ForceHoleInitializationFlag
    {
        kHoleInitializationNotForced = 0,
        kHasHoleCheckUseInDifferentClosureScope = 1 << 0,
        kHasHoleCheckUseInSameClosureScope = 1 << 1,
        kHasHoleCheckUseInUnknownScope = kHasHoleCheckUseInDifferentClosureScope | kHasHoleCheckUseInSameClosureScope,
    }

    public ForceHoleInitializationFlag force_hole_initialization_flag_field() => _forceHoleInitializationFlag;

    public bool IsHoleInitializationForced() => _forceHoleInitializationFlag != ForceHoleInitializationFlag.kHoleInitializationNotForced;

    public bool HasHoleCheckUseInSameClosureScope() =>
        (_forceHoleInitializationFlag & ForceHoleInitializationFlag.kHasHoleCheckUseInSameClosureScope) != 0;

    // Called during scope analysis when a VariableProxy is found to
    // reference this Variable in such a way that a hole check will
    // be required at runtime.
    public void ForceHoleInitialization(ForceHoleInitializationFlag flag) => _forceHoleInitializationFlag |= flag;

    // The first N-1 lexical bindings that need hole checks in a compilation are
    // numbered, where N is the number of bits in HoleCheckBitmap. This number is
    // an index into a bitmap that the BytecodeGenerator uses to elide redundant
    // hole checks. (HoleCheckBitmap is a ulong.)

    // The 0th index is reserved for bindings for which the BytecodeGenerator
    // should not elide hole checks, such as for bindings beyond the first N-1.
    public const byte kUncacheableHoleCheckBitmapIndex = 0;
    public const byte kHoleCheckBitmapBits = 64;

    public void ResetHoleCheckBitmapIndex() => _holeCheckBitmapIndex = kUncacheableHoleCheckBitmapIndex;

    public void RememberHoleCheckInBitmap(ref ulong bitmap, List<Variable> list)
    {
        byte index = HoleCheckBitmapIndex();
        if (index == kUncacheableHoleCheckBitmapIndex)
        {
            index = (byte)(list.Count + 1);
            // The bitmap is full.
            if (index == kHoleCheckBitmapBits) return;
            AssignHoleCheckBitmapIndex(list, index);
        }
        bitmap |= 1UL << index;
    }

    public bool HasRememberedHoleCheck(ulong bitmap)
    {
        byte index = HoleCheckBitmapIndex();
        return (bitmap & (1UL << index)) != 0;
    }

    public bool throw_on_const_assignment(LanguageMode language_mode) =>
        kind() != VariableKind.SLOPPY_FUNCTION_NAME_VARIABLE || is_strict(language_mode);

    public bool is_this() => kind() == VariableKind.THIS_VARIABLE;
    public bool is_sloppy_function_name() => kind() == VariableKind.SLOPPY_FUNCTION_NAME_VARIABLE;
    public bool is_parameter() => kind() == VariableKind.PARAMETER_VARIABLE;
    public bool is_sloppy_block_function() => kind() == VariableKind.SLOPPY_BLOCK_FUNCTION_VARIABLE;

    public Variable local_if_not_shadowed() => _localIfNotShadowed!;
    public bool has_local_if_not_shadowed() => _localIfNotShadowed != null;
    public void set_local_if_not_shadowed(Variable? local) => _localIfNotShadowed = local;

    public VariableLocation location() => _location;
    public VariableKind kind() => _kind;
    public int index() => _index;

    public bool IsReceiver() => _index == -1;

    public bool IsExport() => index() > 0;

    public void AllocateTo(VariableLocation location, int index)
    {
        System.Diagnostics.Debug.Assert(IsUnallocated() || (_location == location && _index == index));
        _location = location;
        _index = index;
    }

    public void MakeParameterNonSimple()
    {
        _mode = VariableMode.Let;
        _initializationFlag = InitializationFlag.kNeedsInitialization;
        // It's possible a parameter hasn't been used but when we introduce
        // temporaries, it will be used in the initialization block.
        set_is_used();
    }

    public static InitializationFlag DefaultInitializationFlag(VariableMode mode) =>
        mode == VariableMode.Var ? InitializationFlag.kCreatedInitialized : InitializationFlag.kNeedsInitialization;

    // Rewrites the VariableLocation of repl script scope 'lets' to REPL_GLOBAL.
    public void RewriteLocationForRepl()
    {
        if (mode() == VariableMode.Let || mode() == VariableMode.Const ||
            mode() == VariableMode.Using || mode() == VariableMode.AwaitUsing)
        {
            _location = VariableLocation.REPL_GLOBAL;
        }
    }

    private byte HoleCheckBitmapIndex() => _holeCheckBitmapIndex;

    private void AssignHoleCheckBitmapIndex(List<Variable> list, byte next_index)
    {
        _holeCheckBitmapIndex = next_index;
        list.Add(this);
    }

    public override string ToString() => $"{_name} ({VariableMode2String(_mode)}, {_location} {_index})";
}
