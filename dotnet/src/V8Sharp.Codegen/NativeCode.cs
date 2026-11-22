// The entry into native code: V8's JSEntry / CallFunction trampolines
// (src/builtins/x64/builtins-x64.cc) reduced to one call.
//
// Native code follows Ryu's rule: it never calls managed code. It runs as a
// System V function `long Code(long* context)` called through an unmanaged
// function pointer with SuppressGCTransition, so the thread stays in
// cooperative mode and no garbage collection can start while it runs: the
// managed objects it reads (through the pinned arrays below and the
// references they hold) cannot move under it. Everything it cannot do
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
    /// Calls the code at <paramref name="entry"/> with <paramref name="context"/>,
    /// after storing in it the addresses of <paramref name="frame"/> (a slot of
    /// the register stack), of the first elements of <paramref name="constants"/>
    /// and <paramref name="spill"/>, and of <paramref name="interruptFlags"/>.
    /// The arrays are pinned for the duration of the call. Returns the exit number.
    /// </summary>
    /// <remarks>
    /// Unsafe: the call goes through an unmanaged function pointer to code
    /// generated at run time (no safe API calls code that is not IL), and the
    /// pinned addresses are what that code reads (no safe API hands a managed
    /// address to it).
    /// </remarks>
    public static long Call<TValue>(nint entry, long[] context, ref TValue frame, TValue[] constants, TValue[] spill, ref int interruptFlags)
    {
        fixed (long* ctx = context)
        fixed (byte* fp = &Unsafe.As<TValue, byte>(ref frame))
        fixed (byte* k = &Unsafe.As<TValue, byte>(ref MemoryMarshal.GetArrayDataReference(constants)))
        fixed (byte* s = &Unsafe.As<TValue, byte>(ref MemoryMarshal.GetArrayDataReference(spill)))
        fixed (int* flags = &interruptFlags)
        {
            ctx[kFrameSlot] = (long)fp;
            ctx[kConstantsSlot] = (long)k;
            ctx[kSpillSlot] = (long)s;
            ctx[kInterruptFlagsSlot] = (long)flags;
            return ((delegate* unmanaged[SuppressGCTransition]<long*, long>)entry)(ctx);
        }
    }

    /// <summary>Calls code that takes only its context (tests of the assembler).</summary>
    public static long CallRaw(nint entry, long[] context)
    {
        fixed (long* ctx = context)
        {
            return ((delegate* unmanaged[SuppressGCTransition]<long*, long>)entry)(ctx);
        }
    }
}
