// The executable memory the native Maglev backend's code lives in: V8's
// CodeRange (src/heap/code-range.cc) reduced to what V8Sharp needs, reserved
// through the platform layer's OS::Allocate (src/base/platform/platform-posix.cc,
// mmap with PROT_EXEC). From Ryu's ExecutableMemory (MIT, THIRD_PARTY_NOTICES.md).
//
// Code is bump-allocated in chunks of read/write/execute memory and never
// freed: V8 collects unreachable Code objects with the heap, V8Sharp keeps
// native code for the life of the process and stops generating native code
// when the budget (kMaxBytes) is spent (deviations.md, "Native Maglev
// backend"). Linux on x64 only; elsewhere Supported is false and the IL
// backend is used.
using System.Runtime.InteropServices;

namespace V8Sharp.Codegen;

public static unsafe class ExecutableMemory
{
    const int PROT_READ = 1, PROT_WRITE = 2, PROT_EXEC = 4;
    const int MAP_PRIVATE = 0x02, MAP_ANONYMOUS = 0x20;
    const long kChunkBytes = 16L << 20;
    /// <summary>The most native code a process generates.</summary>
    public const long kMaxBytes = 512L << 20;

    [DllImport("libc", EntryPoint = "mmap", SetLastError = true)]
    static extern nint Mmap(nint addr, nuint length, int prot, int flags, int fd, nint offset);

    /// <summary>True if this host can run native code (Linux on x64).</summary>
    public static bool Supported { get; } = OperatingSystem.IsLinux() && RuntimeInformation.ProcessArchitecture == Architecture.X64;

    static readonly object s_lock = new();
    static byte* s_chunk;
    static long s_chunkUsed = kChunkBytes;
    static long s_total;

    public static long BytesUsed => Volatile.Read(ref s_total);

    /// <summary>
    /// Copies <paramref name="code"/> into executable memory (16-byte aligned)
    /// and returns its address, or 0 when the budget is spent or mmap fails.
    /// Safe to call from several threads.
    /// </summary>
    public static nint Add(ReadOnlySpan<byte> code)
    {
        if (!Supported || code.Length > kChunkBytes) return 0;
        lock (s_lock)
        {
            long at = (s_chunkUsed + 15) & ~15L;
            if (at + code.Length > kChunkBytes)
            {
                if (s_total + kChunkBytes > kMaxBytes) return 0;
                nint p = Mmap(0, (nuint)kChunkBytes, PROT_READ | PROT_WRITE | PROT_EXEC, MAP_PRIVATE | MAP_ANONYMOUS, -1, 0);
                if (p == -1) return 0;
                s_chunk = (byte*)p;
                s_total += kChunkBytes;
                at = 0;
            }
            byte* dst = s_chunk + at;
            code.CopyTo(new Span<byte>(dst, code.Length));
            s_chunkUsed = at + code.Length;
            return (nint)dst;
        }
    }
}
