// The entry into native code: V8's JSEntry / CallFunction trampolines
// (src/builtins/x64/builtins-x64.cc) reduced to one call.
//
// Native code follows Ryu's rule: it never calls managed code. It runs as a
// System V function `long Code(long* context)` called through an unmanaged
// function pointer with SuppressGCTransition, so the thread stays in
// cooperative mode and no garbage collection can start while it runs: the
// managed objects it reads (through arrays on the pinned object heap, whose
// addresses the context holds, and the references they hold) cannot move
// under it. Everything it cannot do
// inline is an exit: it returns an exit number and the managed side
// (MaglevNativeCode) does the rest, then re-enters it if needed. Native code
// writes no object references (it would need the GC's write barrier); it
// writes numbers only, into the context and into double arrays.
//
// Native code must return regularly so that other threads waiting for a GC
// are not blocked: every loop of generated code counts down a poll budget
// and exits when it runs out (MaglevNativeCodeGenerator).
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace V8Sharp.Codegen;

public static unsafe class NativeCode
{
    // The context words the entry writes (the rest of the context belongs to the generated code).
    public const int kFrameSlot = 0;
    public const int kConstantsSlot = 1;
    public const int kSpillSlot = 2;
    public const int kInterruptFlagsSlot = 3;

    /// <summary>A context: pinned (on the pinned object heap), so its address never changes.</summary>
    public static long[] NewContext(int words) => GC.AllocateArray<long>(words, pinned: true);

    /// <summary>
    /// Calls the code at <paramref name="entry"/> with the context at
    /// <paramref name="context"/>: every array it reads is on the pinned
    /// object heap, so nothing is pinned per call and the context already
    /// holds their addresses.
    /// </summary>
    /// <remarks>Unsafe: a call through an unmanaged function pointer to generated code.</remarks>
    public static long CallPinned(nint entry, nint context) =>
        ((delegate* unmanaged[SuppressGCTransition]<long*, long>)entry)((long*)context);

    /// <summary>The address of an element of an array on the pinned object heap (GC.AllocateArray pinned).</summary>
    public static nint AddressOf<T>(T[] pinnedArray, int index) =>
        System.Runtime.InteropServices.Marshal.UnsafeAddrOfPinnedArrayElement(pinnedArray, index);

    /// <summary>Calls code that takes only its context (tests of the assembler).</summary>
    public static long CallRaw(nint entry, long[] context)
    {
        fixed (long* ctx = context)
        {
            return ((delegate* unmanaged[SuppressGCTransition]<long*, long>)entry)(ctx);
        }
    }
}
