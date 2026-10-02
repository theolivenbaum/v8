// Port of src/execution/stack-guard.{h,cc}.
//
// V8 compares the machine stack pointer against a limit that interrupts lower
// to force a slow path. V8Sharp has two stacks: the native (.NET) stack, which
// is checked with RuntimeHelpers.TryEnsureSufficientExecutionStack, and the
// isolate's register stack (architecture.md section 7), which the interpreter
// checks itself. Interrupt requests are a flag word, as in V8, set from any
// thread and handled at the next StackCheck (function entry, loop back edge).
using System.Runtime.CompilerServices;

namespace V8Sharp;

/// <summary>V8's StackGuard: stack overflow checks and interrupt requests.</summary>
public sealed class StackGuard(Isolate isolate)
{
    /// <summary>StackGuard::InterruptFlag (INTERRUPT_LIST).</summary>
    [Flags]
    public enum InterruptFlag : uint
    {
        TERMINATE_EXECUTION = 1 << 0,
        GC_REQUEST = 1 << 1,
        INSTALL_CODE = 1 << 2,
        INSTALL_BASELINE_CODE = 1 << 3,
        API_INTERRUPT = 1 << 4,
        DEOPT_MARKED_ALLOCATION_SITES = 1 << 5,
        GROW_SHARED_MEMORY = 1 << 6,
        LOG_WASM_CODE = 1 << 7,
        WASM_CODE_GC = 1 << 8,
        INSTALL_MAGLEV_CODE = 1 << 9,
        GLOBAL_SAFEPOINT = 1 << 10,
        START_INCREMENTAL_MARKING = 1 << 11,
        ALL_INTERRUPTS = (1 << 12) - 1,
    }

    readonly Isolate _isolate = isolate;
    int _interruptFlags;
    readonly Queue<(Action<Isolate, object?> Callback, object? Data)> _apiInterrupts = new();

    /// <summary>True if any interrupt is requested (V8: the JS stack limit was lowered).</summary>
    public bool HasPendingInterrupts
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => Volatile.Read(ref _interruptFlags) != 0;
    }

    public bool CheckInterrupt(InterruptFlag flag) => (Volatile.Read(ref _interruptFlags) & (int)flag) != 0;

    public void RequestInterrupt(InterruptFlag flag)
    {
        int current, updated;
        do
        {
            current = Volatile.Read(ref _interruptFlags);
            updated = current | (int)flag;
        } while (Interlocked.CompareExchange(ref _interruptFlags, updated, current) != current);
    }

    public void ClearInterrupt(InterruptFlag flag)
    {
        int current, updated;
        do
        {
            current = Volatile.Read(ref _interruptFlags);
            updated = current & ~(int)flag;
        } while (Interlocked.CompareExchange(ref _interruptFlags, updated, current) != current);
    }

    public bool CheckTerminateExecution() => CheckInterrupt(InterruptFlag.TERMINATE_EXECUTION);
    public void RequestTerminateExecution() => RequestInterrupt(InterruptFlag.TERMINATE_EXECUTION);
    public void ClearTerminateExecution() => ClearInterrupt(InterruptFlag.TERMINATE_EXECUTION);

    /// <summary>Isolate::RequestInterrupt: runs <paramref name="callback"/> at the next interrupt check.</summary>
    public void RequestApiInterrupt(Action<Isolate, object?> callback, object? data)
    {
        lock (_apiInterrupts) _apiInterrupts.Enqueue((callback, data));
        RequestInterrupt(InterruptFlag.API_INTERRUPT);
    }

    /// <summary>
    /// StackGuard::HandleInterrupts: processes (and clears) pending interrupts.
    /// A termination request throws the uncatchable termination exception.
    /// </summary>
    public JSValue HandleInterrupts()
    {
        int flags = Interlocked.Exchange(ref _interruptFlags, 0);
        if ((flags & (int)InterruptFlag.TERMINATE_EXECUTION) != 0)
        {
            return _isolate.TerminateExecution();
        }
        if ((flags & (int)InterruptFlag.INSTALL_BASELINE_CODE) != 0)
        {
            _isolate.BaselineBatchCompiler.InstallBatch();
        }
        if ((flags & (int)InterruptFlag.INSTALL_MAGLEV_CODE) != 0)
        {
            Maglev.MaglevCompiler.InstallConcurrentCode(_isolate);
        }
        if ((flags & (int)InterruptFlag.API_INTERRUPT) != 0)
        {
            InvokeApiInterruptCallbacks();
        }
        return JSValue.Undefined;
    }

    void InvokeApiInterruptCallbacks()
    {
        while (true)
        {
            (Action<Isolate, object?> Callback, object? Data) entry;
            lock (_apiInterrupts)
            {
                if (_apiInterrupts.Count == 0) return;
                entry = _apiInterrupts.Dequeue();
            }
            entry.Callback(_isolate, entry.Data);
        }
    }

    /// <summary>
    /// StackLimitCheck::HasOverflowed for the native stack. V8 compares against
    /// the real C limit; .NET tells us whether enough stack remains.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool HasOverflowed() => !RuntimeHelpers.TryEnsureSufficientExecutionStack();

    /// <summary>
    /// STACK_CHECK: throws V8's RangeError on stack overflow and handles
    /// pending interrupts. Runtime functions that recurse call this.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void StackCheck(Isolate isolate)
    {
        if (HasOverflowed()) isolate.StackOverflow();
        if (HasPendingInterrupts) HandleInterrupts();
    }
}
