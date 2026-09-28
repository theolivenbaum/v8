// Port of src/interpreter/bytecode-register-allocator.h.
namespace V8Sharp.Interpreter;

/// <summary>A class that allows the allocation of contiguous temporary registers.</summary>
public sealed class BytecodeRegisterAllocator(int startIndex)
{
    /// <summary>Enables observation of register allocation and free events.</summary>
    public interface IObserver
    {
        void RegisterAllocateEvent(Register reg);
        void RegisterListAllocateEvent(RegisterList regList);
        void RegisterListFreeEvent(RegisterList regList);
        void RegisterFreeEvent(Register reg);
    }

    int _nextRegisterIndex = startIndex;
    int _maxRegisterCount = startIndex;
    IObserver? _observer;

    /// <summary>Returns a new register.</summary>
    public Register NewRegister()
    {
        var reg = new Register(_nextRegisterIndex++);
        _maxRegisterCount = Math.Max(_nextRegisterIndex, _maxRegisterCount);
        _observer?.RegisterAllocateEvent(reg);
        return reg;
    }

    /// <summary>Returns a consecutive list of |count| new registers.</summary>
    public RegisterList NewRegisterList(int count)
    {
        var regList = new RegisterList(_nextRegisterIndex, count);
        _nextRegisterIndex += count;
        _maxRegisterCount = Math.Max(_nextRegisterIndex, _maxRegisterCount);
        _observer?.RegisterListAllocateEvent(regList);
        return regList;
    }

    /// <summary>Returns a growable register list.</summary>
    public RegisterList NewGrowableRegisterList() => new(_nextRegisterIndex, 0);

    /// <summary>
    /// Appends a new register to |regList| increasing its count by one and
    /// returning the register added. No other new registers must be currently
    /// allocated since the register list was originally allocated.
    /// </summary>
    public Register GrowRegisterList(ref RegisterList regList)
    {
        Register reg = NewRegister();
        regList = regList.IncrementRegisterCount();
        // If the following check fails then a register was allocated (and not
        // freed) between the creation of the RegisterList and this call to add a
        // Register.
        if (reg.Index != regList.LastRegister().Index)
            throw new InvalidOperationException("Check failed: reg.index() == reg_list->last_register().index()");
        return reg;
    }

    /// <summary>Release all registers above |registerIndex|.</summary>
    public void ReleaseRegisters(int registerIndex)
    {
        int count = _nextRegisterIndex - registerIndex;
        _nextRegisterIndex = registerIndex;
        _observer?.RegisterListFreeEvent(new RegisterList(registerIndex, count));
    }

    /// <summary>Release last allocated register.</summary>
    public void ReleaseRegister(Register reg)
    {
        Debug.Assert(_nextRegisterIndex - 1 == reg.Index);
        _observer?.RegisterFreeEvent(reg);
        _nextRegisterIndex--;
    }

    /// <summary>Returns true if the register |reg| is a live register.</summary>
    public bool RegisterIsLive(Register reg) => reg.Index < _nextRegisterIndex;

    /// <summary>Returns a register list for all currently live registers.</summary>
    public RegisterList AllLiveRegisters() => new(0, NextRegisterIndex());

    public void SetObserver(IObserver? observer) => _observer = observer;

    public int NextRegisterIndex() => _nextRegisterIndex;
    public int MaximumRegisterCount() => _maxRegisterCount;
}
