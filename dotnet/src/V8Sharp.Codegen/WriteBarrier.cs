// The GC's write barrier for reference stores from native code.
//
// A reference stored into a heap object must go through the GC's write
// barrier, which marks the card of the destination so that a collection of
// the young generations finds a young object an older one points to. .NET
// does not expose the barrier: compiled .NET code calls a runtime helper
// (JIT_WriteBarrier, the JIT's CORINFO_HELP_ASSIGN_REF: the destination
// address in rdi, the reference in rsi on System V x64). Its address is found
// here in the machine code of a method that stores a reference into a field
// (the call or tail jump it makes), and then checked by a self-test before
// anything uses it: young objects stored with it into objects of the oldest
// generation must survive compacting collections of the young generations,
// with the fields updated to their new addresses. A helper that did not mark
// cards would leave those fields pointing at freed or moved memory, which the
// test sees. When the helper is not found or the test fails, Address is 0 and
// native code does not store references.
//
// The helper is a leaf routine that never triggers a GC, so native code may
// call it in cooperative mode, as compiled .NET code does; the runtime patches
// its body in place when the GC changes its tables, so its address is stable.
using System.Runtime.CompilerServices;

namespace V8Sharp.Codegen;

public static unsafe class WriteBarrier
{
    sealed class Box
    {
        public object? Field;
    }

    [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.AggressiveOptimization)]
    static void Store(Box box, object? value) => box.Field = value;

    static nint s_address = -1;
    static string? s_failure;

    /// <summary>The write barrier helper's address, or 0 when it is not available (Failure says why).</summary>
    public static nint Address
    {
        get
        {
            if (s_address < 0)
            {
                lock (typeof(WriteBarrier))
                {
                    if (s_address < 0) s_address = Initialize(out s_failure);
                }
            }
            return s_address;
        }
    }

    public static string? Failure
    {
        get
        {
            _ = Address;
            return s_failure;
        }
    }

    static nint Initialize(out string? failure)
    {
        failure = null;
        if (!ExecutableMemory.Supported || Environment.GetEnvironmentVariable("V8SHARP_NO_NATIVE_WRITE_BARRIER") == "1")
        {
            failure = "disabled";
            return 0;
        }
        nint helper = Find();
        if (helper == 0)
        {
            failure = "the helper call was not found";
            return 0;
        }
        if (!SelfTest(helper, out failure)) return 0;
        return helper;
    }

    /// <summary>The target of the call (or tail jump) Store makes, following the entry's jump stubs.</summary>
    static nint Find()
    {
        RuntimeMethodHandle handle = typeof(WriteBarrier).GetMethod(nameof(Store),
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!.MethodHandle;
        RuntimeHelpers.PrepareMethod(handle);
        // Make sure the method has its optimized code (the entry point may still be a stub).
        var box = new Box();
        for (int i = 0; i < 4; i++) Store(box, box);
        byte* p = (byte*)handle.GetFunctionPointer();
        for (int hop = 0; hop < 8; hop++)
        {
            // A precode stub: jmp [rip+disp32] or jmp rel32 to the code.
            if (p[0] == 0xFF && p[1] == 0x25)
            {
                p = *(byte**)(p + 6 + *(int*)(p + 2));
                continue;
            }
            if (p[0] == 0xE9)
            {
                p = p + 5 + *(int*)(p + 1);
                continue;
            }
            break;
        }
        // The body: the field's address into rdi (lea rdi, [rdi+8] or similar),
        // the value already in rsi, then call or jmp the helper.
        for (int i = 0; i < 48; i++)
        {
            byte b = p[i];
            if (b == 0xE8 || b == 0xE9) return (nint)(p + i + 5 + *(int*)(p + i + 1));
            // call/jmp [rip+disp32]: an indirect call through a helper cell.
            if (b == 0xFF && (p[i + 1] == 0x15 || p[i + 1] == 0x25)) return *(nint*)(p + i + 6 + *(int*)(p + i + 2));
            if (b == 0xC3) break;
        }
        return 0;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static object NewYoung(int k) => new string('w', 8 + (k & 7)) + k;

    [MethodImpl(MethodImplOptions.NoInlining)]
    static void Churn()
    {
        var keep = new object[32];
        for (int i = 0; i < 20_000; i++) keep[i & 31] = new byte[48];
    }

    /// <summary>
    /// Stores young objects with the helper into objects of the oldest
    /// generation, then runs compacting collections of the young generations.
    /// The young objects stay rooted (so a helper that marks no card cannot
    /// free them), and the fields are compared with them by identity (no
    /// dereference): a young collection updates a field of an old object only
    /// when the object's card is marked, so the fields must follow the objects
    /// the collection moved. At least one must have moved for the test to count.
    /// </summary>
    static bool SelfTest(nint helper, out string? failure)
    {
        failure = null;
        const int n = 64;
        var boxes = new Box[n];
        for (int i = 0; i < n; i++) boxes[i] = new Box();
        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
        if (GC.GetGeneration(boxes[0]) != GC.MaxGeneration)
        {
            failure = "the self-test could not age its objects";
            return false;
        }
        var call = (delegate* unmanaged[SuppressGCTransition]<nint, nint, void>)helper;
        var young = new object[n];
        var before = new nint[n];
        int moved = 0;
        for (int round = 0; round < 4; round++)
        {
            for (int i = 0; i < n; i++) young[i] = NewYoung(round * n + i);
            for (int i = 0; i < n; i++)
            {
                // (No GC between taking the addresses and the store: no allocation, a SuppressGCTransition call.)
                Box box = boxes[i];
                object value = young[i];
                nint field = (nint)Unsafe.AsPointer(ref box.Field);
                nint address = Unsafe.As<object, nint>(ref value);
                before[i] = address;
                call(field, address);
            }
            Churn();
            GC.Collect(0, GCCollectionMode.Forced, blocking: true, compacting: true);
            for (int i = 0; i < n; i++)
            {
                object current = young[i];
                if (Unsafe.As<object, nint>(ref current) != before[i]) moved++;
                if (!ReferenceEquals(boxes[i].Field, current))
                {
                    failure = "a field stored with the helper was not updated when its object moved";
                    return false;
                }
            }
        }
        for (int i = 0; i < n; i++) boxes[i].Field = null;
        if (moved == 0)
        {
            failure = "the self-test was inconclusive (no object moved)";
            return false;
        }
        return true;
    }
}
