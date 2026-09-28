// Port of src/interpreter/bytecode-register-optimizer.h/.cc.
//
// Variables are the bytecode generator's AST Variable objects, compared by
// identity (V8 compares Variable* pointers).
using System.Runtime.CompilerServices;

namespace V8Sharp.Interpreter;

/// <summary>
/// An optimization stage for eliminating unnecessary transfers between
/// registers. The bytecode generator uses temporary registers liberally for
/// correctness and convenience and this stage removes transfers that are not
/// required and preserves correctness.
/// </summary>
public sealed class BytecodeRegisterOptimizer : BytecodeRegisterAllocator.IObserver
{
    public interface IBytecodeWriter
    {
        // Called to emit a register transfer bytecode.
        void EmitLdar(Register input);
        void EmitStar(Register output);
        void EmitMov(Register input, Register output);
    }

    const uint kInvalidEquivalenceId = uint.MaxValue;

    // kDefinitelyHasVariable means that the variable is definitely in the register.
    // kMightHaveVariable means that the variable might be in the register.
    enum VariableHintMode { DefinitelyHasVariable, MightHaveVariable }

    readonly record struct VariableHint(object? Variable, VariableHintMode Mode);

    enum MaterializedInfo { NotMaterialized, Materialized }

    enum ResetVariableHint { DontReset, Reset }

    /// <summary>
    /// Tracks the state of a register: which equivalence set it is a member of
    /// and whether it is materialized in the bytecode stream.
    /// </summary>
    sealed class RegisterInfo
    {
        readonly Register _register;
        uint _equivalenceId;
        bool _materialized;
        bool _allocated;
        bool _needsFlush;
        TypeHint _typeHint = TypeHint.Any;
        VariableHint _variableHint = new(null, VariableHintMode.DefinitelyHasVariable);

        // Equivalence set pointers.
        RegisterInfo _next;
        RegisterInfo _prev;

        public RegisterInfo(Register reg, uint equivalenceId, bool materialized, bool allocated)
        {
            _register = reg;
            _equivalenceId = equivalenceId;
            _materialized = materialized;
            _allocated = allocated;
            _next = this;
            _prev = this;
        }

        public void AddToEquivalenceSetOf(RegisterInfo info)
        {
            Debug.Assert(info.EquivalenceId != kInvalidEquivalenceId);
            // Fix old list
            _next._prev = _prev;
            _prev._next = _next;
            // Add to new list.
            _next = info._next;
            _prev = info;
            _prev._next = this;
            _next._prev = this;
            _equivalenceId = info.EquivalenceId;
            _materialized = false;
            _variableHint = info._variableHint;
            _typeHint = info._typeHint;
        }

        public void MoveToNewEquivalenceSet(uint equivalenceId, MaterializedInfo materialized,
                                            ResetVariableHint reset = ResetVariableHint.Reset)
        {
            _next._prev = _prev;
            _prev._next = _next;
            _next = _prev = this;
            _equivalenceId = equivalenceId;
            _materialized = materialized == MaterializedInfo.Materialized;
            FlushVariableHint(reset == ResetVariableHint.Reset);
            _typeHint = TypeHint.Any;
        }

        public bool IsOnlyMemberOfEquivalenceSet() => _next == this;

        public bool IsInSameEquivalenceSet(RegisterInfo info) => EquivalenceId == info.EquivalenceId;

        // Get a member of the register's equivalence set that is allocated.
        // Returns itself if allocated, and null if there is no allocated
        // equivalent register.
        public RegisterInfo? GetAllocatedEquivalent()
        {
            RegisterInfo visitor = this;
            do
            {
                if (visitor.Allocated) return visitor;
                visitor = visitor._next;
            } while (visitor != this);
            return null;
        }

        // Get a member of this register's equivalence set that is materialized.
        // The materialized equivalent will be this register if it is materialized.
        // Returns null if no materialized equivalent exists.
        public RegisterInfo? GetMaterializedEquivalent()
        {
            RegisterInfo visitor = this;
            do
            {
                if (visitor.Materialized) return visitor;
                visitor = visitor._next;
            } while (visitor != this);
            return null;
        }

        // Get a member of this register's equivalence set that is materialized and
        // not register |reg|. Returns null if no materialized equivalent exists.
        public RegisterInfo? GetMaterializedEquivalentOtherThan(Register reg)
        {
            RegisterInfo visitor = this;
            do
            {
                if (visitor.Materialized && visitor.RegisterValue != reg) return visitor;
                visitor = visitor._next;
            } while (visitor != this);
            return null;
        }

        // Get a member of this register's equivalence set that is intended
        // to be materialized in place of this register (which is currently
        // materialized). The best candidate is deemed to be the register
        // with the lowest index as this permits temporary registers to be
        // removed from the bytecode stream. Returns null if no candidate exists.
        public RegisterInfo? GetEquivalentToMaterialize()
        {
            Debug.Assert(Materialized);
            RegisterInfo visitor = _next;
            RegisterInfo? best_info = null;
            while (visitor != this)
            {
                if (visitor.Materialized) return null;
                if (visitor.Allocated && (best_info is null || visitor.RegisterValue < best_info.RegisterValue))
                {
                    best_info = visitor;
                }
                visitor = visitor._next;
            }
            return best_info;
        }

        // Marks all temporary registers of the equivalence set as unmaterialized.
        public void MarkTemporariesAsUnmaterialized(Register temporaryBase)
        {
            Debug.Assert(RegisterValue < temporaryBase);
            Debug.Assert(Materialized);
            RegisterInfo visitor = _next;
            while (visitor != this)
            {
                if (visitor.RegisterValue >= temporaryBase) visitor.Materialized = false;
                visitor = visitor._next;
            }
        }

        // Get an equivalent register. Returns this if none exists.
        public RegisterInfo GetEquivalent() => _next;

        public Register RegisterValue => _register;
        public bool Materialized { get => _materialized; set => _materialized = value; }
        public bool Allocated { get => _allocated; set => _allocated = value; }
        public uint EquivalenceId { get => _equivalenceId; set => _equivalenceId = value; }
        // Indicates if a register should be processed when calling Flush().
        public bool NeedsFlush { get => _needsFlush; set => _needsFlush = value; }
        public TypeHint TypeHint { get => _typeHint; set => _typeHint = value; }
        public VariableHint VariableHint { get => _variableHint; set => _variableHint = value; }

        public void FlushVariableHint(bool resetVariableHint)
        {
            if (resetVariableHint)
            {
                _variableHint = new VariableHint(null, VariableHintMode.DefinitelyHasVariable);
            }
            else if (_variableHint.Variable is not null)
            {
                _variableHint = _variableHint with { Mode = VariableHintMode.MightHaveVariable };
            }
        }

        public RegisterInfo Next => _next;
    }

    readonly Register _accumulator;
    readonly RegisterInfo _accumulatorInfo;
    readonly Register _temporaryBase;
    int _maxRegisterIndex;

    // Direct mapping to register info.
    readonly List<RegisterInfo> _registerInfoTable = [];
    readonly int _registerInfoTableOffset;

    readonly List<RegisterInfo> _registersNeedingFlushed = [];

    // Counter for equivalence sets identifiers.
    uint _equivalenceId;

    readonly IBytecodeWriter _bytecodeWriter;
    bool _flushRequired;

    public BytecodeRegisterOptimizer(BytecodeRegisterAllocator registerAllocator, int fixedRegistersCount,
                                     int parameterCount, IBytecodeWriter bytecodeWriter)
    {
        _accumulator = Register.VirtualAccumulator();
        _temporaryBase = new Register(fixedRegistersCount);
        _maxRegisterIndex = fixedRegistersCount - 1;
        _bytecodeWriter = bytecodeWriter;
        registerAllocator.SetObserver(this);

        // Calculate offset so register index values can be mapped into
        // a vector of register metadata.
        // There is at least one parameter, which is the JS receiver.
        Debug.Assert(parameterCount != 0);
        int first_slot_index = parameterCount - 1;
        _registerInfoTableOffset = -Register.FromParameterIndex(first_slot_index).Index;

        // Initialize register map for parameters, locals, and the
        // accumulator.
        int size = _registerInfoTableOffset + _temporaryBase.Index;
        for (int i = 0; i < size; ++i)
        {
            _registerInfoTable.Add(new RegisterInfo(RegisterFromRegisterInfoTableIndex(i), NextEquivalenceId(), true, true));
            Debug.Assert(_registerInfoTable[i].RegisterValue.Index == RegisterFromRegisterInfoTableIndex(i).Index);
        }
        _accumulatorInfo = GetRegisterInfo(_accumulator);
        Debug.Assert(_accumulatorInfo.RegisterValue == _accumulator);
    }

    // Perform explicit register transfer operations.
    public void DoLdar(Register input)
    {
        // TODO(rmcilroy): Avoid treating accumulator loads as clobbering the
        // accumulator until the value is actually materialized in the accumulator.
        RegisterInfo input_info = GetRegisterInfo(input);
        RegisterTransfer(input_info, _accumulatorInfo);
    }

    public void DoStar(Register output)
    {
        RegisterInfo output_info = GetRegisterInfo(output);
        RegisterTransfer(_accumulatorInfo, output_info);
    }

    public void DoMov(Register input, Register output)
    {
        RegisterInfo input_info = GetRegisterInfo(input);
        RegisterInfo output_info = GetRegisterInfo(output);
        RegisterTransfer(input_info, output_info);
    }

    /// <summary>Prepares for |bytecode| (V8's PrepareForBytecode&lt;bytecode, implicit_register_use&gt;).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void PrepareForBytecode(Bytecode bytecode, ImplicitRegisterUse implicitRegisterUse)
    {
        if (Bytecodes.IsJump(bytecode) || Bytecodes.IsSwitch(bytecode) || bytecode == Bytecode.Debugger ||
            bytecode == Bytecode.SuspendGenerator || bytecode == Bytecode.ResumeGenerator ||
            bytecode == Bytecode.ForOfNext)
        {
            // All state must be flushed before emitting
            // - a jump bytecode (as the register equivalents at the jump target
            //   aren't known)
            // - a switch bytecode (as the register equivalents at the switch targets
            //   aren't known)
            // - a call to the debugger (as it can manipulate locals and parameters),
            // - a generator suspend (as this involves saving all registers).
            // - a generator register restore.
            // - forof optimization bytecode to make sure `done` register is
            // initialized in every loop
            Flush();
        }

        // Materialize the accumulator if it is read by the bytecode. The
        // accumulator is special and no other register can be materialized
        // in it's place.
        if (BytecodeOperands.ReadsAccumulator(implicitRegisterUse))
        {
            Materialize(_accumulatorInfo);
        }

        // Materialize an equivalent to the accumulator if it will be
        // clobbered when the bytecode is dispatched.
        if (BytecodeOperands.WritesOrClobbersAccumulator(implicitRegisterUse))
        {
            PrepareOutputRegister(_accumulator);
            Debug.Assert(GetTypeHint(_accumulator) == TypeHint.Any);
        }
    }

    /// <summary>Prepares for |bytecode|, using its own implicit register use.</summary>
    public void PrepareForBytecode(Bytecode bytecode) =>
        PrepareForBytecode(bytecode, Bytecodes.GetImplicitRegisterUse(bytecode));

    public void SetVariableInRegister(object variable, Register reg)
    {
        RegisterInfo info = GetRegisterInfo(reg);
        RegisterInfo it = info;
        do
        {
            PushToRegistersNeedingFlush(it);
            it.VariableHint = new VariableHint(variable, VariableHintMode.DefinitelyHasVariable);
            it = it.Next;
        } while (it != info);
    }

    /// <summary>The variable that might be in the reg. This is a variable value
    /// that is preserved across flushes.</summary>
    public object? GetPotentialVariableInRegister(Register reg) => GetRegisterInfo(reg).VariableHint.Variable;

    /// <summary>The variable that might be in the accumulator.</summary>
    public object? GetPotentialVariableInAccumulator() => GetPotentialVariableInRegister(_accumulator);

    /// <summary>Return true if the var is in the reg.</summary>
    public bool IsVariableInRegister(object variable, Register reg)
    {
        Debug.Assert(variable is not null);
        VariableHint hint = GetRegisterInfo(reg).VariableHint;
        return hint.Mode == VariableHintMode.DefinitelyHasVariable && ReferenceEquals(hint.Variable, variable);
    }

    public TypeHint GetTypeHint(Register reg) => GetRegisterInfo(reg).TypeHint;

    public void SetTypeHintForAccumulator(TypeHint hint)
    {
        Debug.Assert(TypeHints.IsSameOrSubTypeHint(_accumulatorInfo.TypeHint, hint));
        if (_accumulatorInfo.TypeHint != hint) _accumulatorInfo.TypeHint = hint;
    }

    public void ResetTypeHintForAccumulator() => _accumulatorInfo.TypeHint = TypeHint.Any;

    public bool IsAccumulatorReset() => _accumulatorInfo.TypeHint == TypeHint.Any;

    /// <summary>maxiumum_register_index() (sic, V8's spelling).</summary>
    public int MaximumRegisterIndex() => _maxRegisterIndex;

    void PushToRegistersNeedingFlush(RegisterInfo reg)
    {
        // Flushing is required in two cases:
        // 1) Two or more registers in the same equivalence set.
        // 2) Binding a variable to a register.
        _flushRequired = true;
        if (!reg.NeedsFlush)
        {
            reg.NeedsFlush = true;
            _registersNeedingFlushed.Add(reg);
        }
    }

    public bool EnsureAllRegistersAreFlushed()
    {
        foreach (RegisterInfo reg_info in _registerInfoTable)
        {
            if (reg_info.NeedsFlush) return false;
            if (!reg_info.IsOnlyMemberOfEquivalenceSet()) return false;
            if (reg_info.Allocated && !reg_info.Materialized) return false;
        }
        return true;
    }

    /// <summary>Materialize all live registers and flush equivalence sets.</summary>
    public void Flush()
    {
        if (!_flushRequired) return;

        // Materialize all live registers and break equivalences.
        foreach (RegisterInfo reg_info in _registersNeedingFlushed)
        {
            if (!reg_info.NeedsFlush) continue;
            reg_info.NeedsFlush = false;
            reg_info.FlushVariableHint(false);
            reg_info.TypeHint = TypeHint.Any;

            RegisterInfo? materialized = reg_info.Materialized ? reg_info : reg_info.GetMaterializedEquivalent();

            if (materialized is not null)
            {
                // Walk equivalents of materialized registers, materializing
                // each equivalent register as necessary and placing in their
                // own equivalence set.
                RegisterInfo equivalent;
                while ((equivalent = materialized.GetEquivalent()) != materialized)
                {
                    if (equivalent.Allocated && !equivalent.Materialized)
                    {
                        OutputRegisterTransfer(materialized, equivalent);
                    }
                    equivalent.MoveToNewEquivalenceSet(NextEquivalenceId(), MaterializedInfo.Materialized,
                                                       ResetVariableHint.DontReset);
                    equivalent.NeedsFlush = false;
                }
            }
            else
            {
                // Equivalence class containing only unallocated registers.
                Debug.Assert(reg_info.GetAllocatedEquivalent() is null);
                reg_info.MoveToNewEquivalenceSet(NextEquivalenceId(), MaterializedInfo.NotMaterialized,
                                                 ResetVariableHint.DontReset);
            }
        }

        _registersNeedingFlushed.Clear();
        Debug.Assert(EnsureAllRegistersAreFlushed());

        _flushRequired = false;
    }

    void OutputRegisterTransfer(RegisterInfo inputInfo, RegisterInfo outputInfo)
    {
        Register input = inputInfo.RegisterValue;
        Register output = outputInfo.RegisterValue;
        Debug.Assert(input.Index != output.Index);

        if (input == _accumulator)
        {
            _bytecodeWriter.EmitStar(output);
        }
        else if (output == _accumulator)
        {
            _bytecodeWriter.EmitLdar(input);
        }
        else
        {
            _bytecodeWriter.EmitMov(input, output);
        }
        if (output != _accumulator)
        {
            _maxRegisterIndex = Math.Max(_maxRegisterIndex, output.Index);
        }
        outputInfo.Materialized = true;
    }

    void CreateMaterializedEquivalent(RegisterInfo info)
    {
        Debug.Assert(info.Materialized);
        RegisterInfo? unmaterialized = info.GetEquivalentToMaterialize();
        if (unmaterialized is not null) OutputRegisterTransfer(info, unmaterialized);
    }

    RegisterInfo GetMaterializedEquivalentNotAccumulator(RegisterInfo info)
    {
        if (info.Materialized) return info;

        RegisterInfo? result = info.GetMaterializedEquivalentOtherThan(_accumulator);
        if (result is null)
        {
            Materialize(info);
            result = info;
        }
        Debug.Assert(result.RegisterValue != _accumulator);
        return result;
    }

    void Materialize(RegisterInfo info)
    {
        if (!info.Materialized)
        {
            RegisterInfo? materialized = info.GetMaterializedEquivalent();
            Debug.Assert(materialized is not null);
            OutputRegisterTransfer(materialized!, info);
        }
    }

    void AddToEquivalenceSet(RegisterInfo setMember, RegisterInfo nonSetMember)
    {
        // Equivalence class is now of size >= 2, so we make sure it will be flushed.
        PushToRegistersNeedingFlush(nonSetMember);
        nonSetMember.AddToEquivalenceSetOf(setMember);
    }

    void RegisterTransfer(RegisterInfo inputInfo, RegisterInfo outputInfo)
    {
        bool output_is_observable = RegisterIsObservable(outputInfo.RegisterValue);
        bool in_same_equivalence_set = outputInfo.IsInSameEquivalenceSet(inputInfo);
        if (in_same_equivalence_set && (!output_is_observable || outputInfo.Materialized))
        {
            return; // Nothing more to do.
        }

        // Materialize an alternate in the equivalence set that
        // |output_info| is leaving.
        if (outputInfo.Materialized) CreateMaterializedEquivalent(outputInfo);

        // Add |output_info| to new equivalence set.
        if (!in_same_equivalence_set) AddToEquivalenceSet(inputInfo, outputInfo);

        if (output_is_observable)
        {
            // Force store to be emitted when register is observable.
            outputInfo.Materialized = false;
            RegisterInfo? materialized_info = inputInfo.GetMaterializedEquivalent();
            OutputRegisterTransfer(materialized_info!, outputInfo);
        }

        bool input_is_observable = RegisterIsObservable(inputInfo.RegisterValue);
        if (input_is_observable)
        {
            // If input is observable by the debugger, mark all other temporaries
            // registers as unmaterialized so that this register is used in preference.
            inputInfo.MarkTemporariesAsUnmaterialized(_temporaryBase);
        }
    }

    /// <summary>Prepares |reg| for being used as an output operand.</summary>
    public void PrepareOutputRegister(Register reg)
    {
        RegisterInfo reg_info = GetRegisterInfo(reg);
        if (reg_info.Materialized) CreateMaterializedEquivalent(reg_info);
        reg_info.MoveToNewEquivalenceSet(NextEquivalenceId(), MaterializedInfo.Materialized);
        _maxRegisterIndex = Math.Max(_maxRegisterIndex, reg_info.RegisterValue.Index);
    }

    /// <summary>Prepares registers in |regList| for being used as an output operand.</summary>
    public void PrepareOutputRegisterList(RegisterList regList)
    {
        int start_index = regList.FirstRegister().Index;
        for (int i = 0; i < regList.RegisterCount; ++i)
        {
            PrepareOutputRegister(new Register(start_index + i));
        }
    }

    /// <summary>Returns an equivalent register to |reg| to be used as an input operand.</summary>
    public Register GetInputRegister(Register reg)
    {
        RegisterInfo reg_info = GetRegisterInfo(reg);
        if (reg_info.Materialized) return reg;
        RegisterInfo equivalent_info = GetMaterializedEquivalentNotAccumulator(reg_info);
        return equivalent_info.RegisterValue;
    }

    /// <summary>Returns an equivalent register list to |regList| to be used as an input operand.</summary>
    public RegisterList GetInputRegisterList(RegisterList regList)
    {
        if (regList.RegisterCount == 1)
        {
            // If there is only a single register, treat it as a normal input register.
            Register reg = GetInputRegister(regList.FirstRegister());
            return new RegisterList(reg);
        }
        int start_index = regList.FirstRegister().Index;
        for (int i = 0; i < regList.RegisterCount; ++i)
        {
            Materialize(GetRegisterInfo(new Register(start_index + i)));
        }
        return regList;
    }

    void GrowRegisterMap(Register reg)
    {
        Debug.Assert(RegisterIsTemporary(reg));
        int index = GetRegisterInfoTableIndex(reg);
        if (index >= _registerInfoTable.Count)
        {
            int new_size = index + 1;
            int old_size = _registerInfoTable.Count;
            for (int i = old_size; i < new_size; ++i)
            {
                _registerInfoTable.Add(new RegisterInfo(RegisterFromRegisterInfoTableIndex(i), NextEquivalenceId(),
                                                        true, false));
            }
        }
    }

    void AllocateRegister(RegisterInfo info)
    {
        info.Allocated = true;
        if (!info.Materialized)
        {
            info.MoveToNewEquivalenceSet(NextEquivalenceId(), MaterializedInfo.Materialized);
        }
    }

    // BytecodeRegisterAllocator::Observer interface.
    void BytecodeRegisterAllocator.IObserver.RegisterAllocateEvent(Register reg) =>
        AllocateRegister(GetOrCreateRegisterInfo(reg));

    void BytecodeRegisterAllocator.IObserver.RegisterListAllocateEvent(RegisterList regList)
    {
        if (regList.RegisterCount != 0)
        {
            int first_index = regList.FirstRegister().Index;
            GrowRegisterMap(new Register(first_index + regList.RegisterCount - 1));
            for (int i = 0; i < regList.RegisterCount; i++)
            {
                AllocateRegister(GetRegisterInfo(new Register(first_index + i)));
            }
        }
    }

    void BytecodeRegisterAllocator.IObserver.RegisterListFreeEvent(RegisterList regList)
    {
        int first_index = regList.FirstRegister().Index;
        for (int i = 0; i < regList.RegisterCount; i++)
        {
            GetRegisterInfo(new Register(first_index + i)).Allocated = false;
        }
    }

    void BytecodeRegisterAllocator.IObserver.RegisterFreeEvent(Register reg) =>
        GetRegisterInfo(reg).Allocated = false;

    // Methods for finding and creating metadata for each register.
    RegisterInfo GetRegisterInfo(Register reg)
    {
        int index = GetRegisterInfoTableIndex(reg);
        Debug.Assert(index < _registerInfoTable.Count);
        return _registerInfoTable[index];
    }

    RegisterInfo GetOrCreateRegisterInfo(Register reg)
    {
        int index = GetRegisterInfoTableIndex(reg);
        return index < _registerInfoTable.Count ? _registerInfoTable[index] : NewRegisterInfo(reg);
    }

    RegisterInfo NewRegisterInfo(Register reg)
    {
        int index = GetRegisterInfoTableIndex(reg);
        Debug.Assert(index >= _registerInfoTable.Count);
        GrowRegisterMap(reg);
        return _registerInfoTable[index];
    }

    bool RegisterIsTemporary(Register reg) => reg >= _temporaryBase;

    bool RegisterIsObservable(Register reg) => reg != _accumulator && !RegisterIsTemporary(reg);

    int GetRegisterInfoTableIndex(Register reg) => reg.Index + _registerInfoTableOffset;

    Register RegisterFromRegisterInfoTableIndex(int index) => new(index - _registerInfoTableOffset);

    uint NextEquivalenceId()
    {
        _equivalenceId++;
        if (_equivalenceId == kInvalidEquivalenceId) throw new InvalidOperationException("equivalence id overflow");
        return _equivalenceId;
    }
}
