// memory.atomic.wait32/wait64/notify over V8's futex emulation
// (src/execution/futex-emulation.cc, as the wasm runtime functions
// Runtime_WasmI32AtomicWait, Runtime_WasmI64AtomicWait and
// Runtime_WasmAtomicNotify use it), so that wasm and Atomics.wait/notify on
// the memory's SharedArrayBuffer wait on the same list.
using System.Runtime.CompilerServices;
using V8Sharp.Builtins;
using Wacs.Core.Runtime;
using Wacs.Core.Runtime.Concurrency;
using Wacs.Core.Runtime.Types;

namespace V8Sharp.Wasm;

sealed class WasmFutexPolicy : IConcurrencyPolicy
{
    public static readonly WasmFutexPolicy Instance = new();

    static readonly ConditionalWeakTable<byte[], BackingStore> s_stores = new();

    public ConcurrencyPolicyMode Mode => ConcurrencyPolicyMode.HostDefined;

    static BackingStore StoreFor(MemoryInstance mem) =>
        s_stores.GetValue(mem.Data, data => BackingStore.WrapWasmMemory(data, (ulong)data.Length, shared: true));

    static double TimeoutMs(long timeoutNs) =>
        timeoutNs < 0 ? double.PositiveInfinity : timeoutNs / 1e6;

    static int Result(string result) => result switch
    {
        "ok" => 0,
        "not-equal" => 1,
        _ => 2,
    };

    // The wait results are V8's (WasmI32AtomicWait): 0 ok, 1 not-equal,
    // 2 timed-out.
    public int Wait32(MemoryInstance mem, long addr, int expected, long timeoutNs)
    {
        CheckWaitAllowed(mem);
        return Result(FutexEmulation.WaitSync(StoreFor(mem), addr, expected, is64: false, TimeoutMs(timeoutNs)));
    }

    public int Wait64(MemoryInstance mem, long addr, long expected, long timeoutNs)
    {
        CheckWaitAllowed(mem);
        return Result(FutexEmulation.WaitSync(StoreFor(mem), addr, expected, is64: true, TimeoutMs(timeoutNs)));
    }

    internal const string WaitNotAllowed = "Atomics.wait cannot be called in this context";

    // Runtime_WasmI32AtomicWait: trap if the memory is not shared, or wait is
    // not allowed on the isolate.
    static void CheckWaitAllowed(MemoryInstance mem)
    {
        if (!mem.Type.Limits.Shared || Isolate.Current is { AllowAtomicsWait: false })
            throw new TrapException(WaitNotAllowed);
    }

    public int Notify(MemoryInstance mem, long addr, int maxWaiters) =>
        FutexEmulation.Wake(StoreFor(mem), addr, (uint)maxWaiters);
}
