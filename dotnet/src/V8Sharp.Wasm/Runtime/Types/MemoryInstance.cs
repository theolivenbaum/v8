// Copyright 2024 Kelvin Nishikawa
// 
// Licensed under the Apache License, Version 2.0 (the "License");
// you may not use this file except in compliance with the License.
// You may obtain a copy of the License at
// 
//     http://www.apache.org/licenses/LICENSE-2.0
// 
// Unless required by applicable law or agreed to in writing, software
// distributed under the License is distributed on an "AS IS" BASIS,
// WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
// See the License for the specific language governing permissions and
// limitations under the License.

using System;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Wacs.Core.Runtime.Exceptions;
using Wacs.Core.Types;
using Wacs.Core.Types.Defs;
using Wacs.Core.Utilities;

namespace Wacs.Core.Runtime.Types
{
    // V8Sharp: linear memory is always a managed byte[]. WACS also had a
    // NativePointer mode (NativeMemory.AllocZeroed, unsafe pointers) for
    // memories above 2 GiB; V8Sharp keeps the engine free of unsafe code
    // (dotnet/CLAUDE.md rule 5), so memories are bounded by Array.MaxLength,
    // as V8Sharp's ArrayBuffers are (deviations.md, "WebAssembly").
    // Shared memories reserve their maximum size up front (when it is small
    // enough) so that the byte[] never moves: SharedArrayBuffers handed to
    // JavaScript alias it across growth, as V8's do.
    public class MemoryInstance : IDisposable
    {
        /// <summary>The largest maximum a shared memory reserves up front.</summary>
        public const long SharedReservationLimit = 64L * 1024 * 1024;

        // The storage. For a shared memory with a reservation, longer than
        // the memory; _byteLength is the memory's size.
        public byte[] Data;

        private long _byteLength;

        /// <summary>
        /// V8Sharp: called after the memory grew (memory.grow or the JS API's
        /// Memory.prototype.grow), so that the JS API can refresh the
        /// ArrayBuffer that aliases the memory (WasmMemoryObject::Grow).
        /// </summary>
        public Action<MemoryInstance, long>? OnGrow;

        /// <summary>
        /// Backing storage selector; always <see cref="MemoryStorageMode.ManagedArray"/>.
        /// </summary>
        public MemoryStorageMode StorageMode => MemoryStorageMode.ManagedArray;

        // Lazy-allocated only when the host has opted into concurrent wasm
        // execution (see ConcurrencyPolicyMode.HostDefined) AND the memory
        // type is shared. Without both, atomic ops operate on an
        // uncontended Data array and Grow is only called from the one
        // executing thread — no lock needed.
        internal ReaderWriterLockSlim? _growLock;

        public MemoryInstance(MemoryType type)
            : this(type, MemoryStorageMode.ManagedArray) { }

        public MemoryInstance(MemoryType type, MemoryStorageMode storage)
        {
            Type = type;

            if (type.Limits.Minimum > MaxPages)
                throw new InstantiationException($"Cannot allocate memory of size {type.Limits.Minimum}");

            long initialSize = type.Limits.Minimum * Constants.PageSize;
            long reserved = initialSize;
            if (type.Limits.Shared && type.Limits.Maximum is long max)
            {
                long maxBytes = Math.Min(max, MaxPages) * Constants.PageSize;
                if (maxBytes <= SharedReservationLimit) reserved = maxBytes;
            }
            Data = new byte[reserved];
            _byteLength = initialSize;
        }

        public MemoryType Type { get; private set; }

        public long Size => Volatile.Read(ref _byteLength) / Constants.PageSize;

        /// <summary>
        /// Authoritative byte length. <c>nuint</c> (host-pointer-sized) so
        /// memory64 callers don't truncate.
        /// </summary>
        public nuint ByteLength
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => (nuint)Volatile.Read(ref _byteLength);
        }

        public Span<byte> this[Range range]
        {
            get
            {
                var (offset, length) = range.GetOffsetAndLength((int)_byteLength);
                return Data.AsSpan(offset, length);
            }
        }

        /// <summary>
        /// Returns a <see cref="Span{T}"/> over the storage. Callers must not
        /// retain the span past the next <see cref="Grow"/> on this instance —
        /// grow reallocates the backing storage and the span becomes stale.
        /// </summary>
        public Span<byte> AsSpan(int offset, int length) => Data.AsSpan(offset, length);

        /// <summary>
        /// High-address sibling of <see cref="AsSpan(int, int)"/>. The byte[]
        /// is bounded by Array.MaxLength, so the <c>(int)offset</c> cast is
        /// safe for in-bounds offsets.
        /// </summary>
        public Span<byte> AsSpan(nuint offset, int length) => Data.AsSpan((int)offset, length);

        /// <summary>
        /// @Spec 4.5.3.9. Growing memories
        /// </summary>
        public bool Grow(long numPages)
        {
            long oldNumPages = Size;
            long newNumPages = oldNumPages + numPages;

            if (numPages < 0 || newNumPages > MaxPages)
                return false;

            if (Type.Limits.Maximum is long maximum && newNumPages > maximum)
                return false;

            var newLimits = new Limits(Type.Limits)
            {
                Minimum = newNumPages
            };

            long len = newNumPages * Constants.PageSize;

            var gl = _growLock;
            if (gl != null)
            {
                gl.EnterWriteLock();
                try { GrowStorage(len, newLimits); }
                finally { gl.ExitWriteLock(); }
            }
            else
            {
                GrowStorage(len, newLimits);
            }

            OnGrow?.Invoke(this, oldNumPages);
            return true;
        }

        // A memory whose reservation holds the new size grows in place;
        // otherwise Array.Resize moves it (capped at ~2 GiB).
        private void GrowStorage(long newLenBytes, Limits newLimits)
        {
            if (newLenBytes > Data.Length)
            {
                var data = Data;
                Array.Resize(ref data, (int)newLenBytes);
                Data = data;
            }
            Volatile.Write(ref _byteLength, newLenBytes);
            Type = new MemoryType(newLimits);
        }

        // Page-count cap: what a byte[] can hold (Constants.HostMaxPages,
        // ~2 GiB), for 32-bit and 64-bit memories alike.
        private long MaxPages => Constants.HostMaxPages;

        public void Dispose()
        {
            _growLock?.Dispose();
            _growLock = null;
        }

        /// <summary>
        /// Opt-in: allocate the concurrent-grow lock. Called by the
        /// runtime when a shared memory is instantiated under
        /// <see cref="Concurrency.ConcurrencyPolicyMode.HostDefined"/>.
        /// Idempotent; safe to call more than once.
        /// </summary>
        public void EnableConcurrentGrow()
        {
            if (_growLock == null)
                Interlocked.CompareExchange(ref _growLock,
                    new ReaderWriterLockSlim(LockRecursionPolicy.NoRecursion), null);
        }

        // Atomic helpers. These are the entry points every
        // InstAtomic* class routes through for shared-memory access,
        // so the lock-vs-Interlocked discipline lives in one place.
        // When _growLock is null (single-thread / non-shared case) the
        // read-lock/release pair is skipped — zero overhead beyond the
        // Interlocked intrinsic itself.

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void EnterReadIfShared()
        {
            _growLock?.EnterReadLock();
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void ExitReadIfShared()
        {
            _growLock?.ExitReadLock();
        }

        /// <summary>
        /// <c>ref T</c> over the byte at offset <paramref name="ea"/>
        /// (<c>Unsafe.As</c> over the array element, no unsafe code). Used by
        /// atomic load/store/RMW. Caller guarantees <paramref name="ea"/> is
        /// in-bounds and aligned for <typeparamref name="T"/>.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public ref T RefAs<T>(int ea) where T : unmanaged
            => ref Unsafe.As<byte, T>(ref Data[ea]);

        /// <summary>High-address sibling of <see cref="RefAs{T}(int)"/>.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public ref T RefAs<T>(nuint ea) where T : unmanaged
            => ref Unsafe.As<byte, T>(ref Data[(int)ea]);

        /// <summary>Atomic 32-bit load at byte offset <paramref name="ea"/>.
        /// Caller guarantees <paramref name="ea"/> is in-bounds and 4-byte
        /// aligned.</summary>
        public int AtomicLoadInt32(int ea)
        {
            EnterReadIfShared();
            try
            {
                ref int cell = ref RefAs<int>(ea);
                return Volatile.Read(ref cell);
            }
            finally { ExitReadIfShared(); }
        }

        /// <summary>Atomic 64-bit load. Uses <see cref="Interlocked.Read(ref long)"/>
        /// because 64-bit reads on 32-bit ARM are not naturally atomic.</summary>
        public long AtomicLoadInt64(int ea)
        {
            EnterReadIfShared();
            try
            {
                ref long cell = ref RefAs<long>(ea);
                return Interlocked.Read(ref cell);
            }
            finally { ExitReadIfShared(); }
        }

        /// <summary>Seq-cst store. Uses <see cref="Interlocked.Exchange(ref int, int)"/>
        /// (ignoring the return) because <c>Volatile.Write</c> is release-only,
        /// insufficient for the threads-proposal requirement.</summary>
        public void AtomicStoreInt32(int ea, int value)
        {
            EnterReadIfShared();
            try
            {
                ref int cell = ref RefAs<int>(ea);
                Interlocked.Exchange(ref cell, value);
            }
            finally { ExitReadIfShared(); }
        }

        public void AtomicStoreInt64(int ea, long value)
        {
            EnterReadIfShared();
            try
            {
                ref long cell = ref RefAs<long>(ea);
                Interlocked.Exchange(ref cell, value);
            }
            finally { ExitReadIfShared(); }
        }

        /// <summary>Returns the original value at <paramref name="ea"/>.</summary>
        public int AtomicCompareExchangeInt32(int ea, int newValue, int expected)
        {
            EnterReadIfShared();
            try
            {
                ref int cell = ref RefAs<int>(ea);
                return Interlocked.CompareExchange(ref cell, newValue, expected);
            }
            finally { ExitReadIfShared(); }
        }

        public long AtomicCompareExchangeInt64(int ea, long newValue, long expected)
        {
            EnterReadIfShared();
            try
            {
                ref long cell = ref RefAs<long>(ea);
                return Interlocked.CompareExchange(ref cell, newValue, expected);
            }
            finally { ExitReadIfShared(); }
        }

        /// <summary>Fetch-and-add: returns the original value.</summary>
        public int AtomicAddInt32(int ea, int value)
        {
            EnterReadIfShared();
            try
            {
                ref int cell = ref RefAs<int>(ea);
                // Interlocked.Add returns the new value; subtract to get original.
                return Interlocked.Add(ref cell, value) - value;
            }
            finally { ExitReadIfShared(); }
        }

        public long AtomicAddInt64(int ea, long value)
        {
            EnterReadIfShared();
            try
            {
                ref long cell = ref RefAs<long>(ea);
                return Interlocked.Add(ref cell, value) - value;
            }
            finally { ExitReadIfShared(); }
        }

        /// <summary>Exchange: returns original.</summary>
        public int AtomicExchangeInt32(int ea, int value)
        {
            EnterReadIfShared();
            try
            {
                ref int cell = ref RefAs<int>(ea);
                return Interlocked.Exchange(ref cell, value);
            }
            finally { ExitReadIfShared(); }
        }

        public long AtomicExchangeInt64(int ea, long value)
        {
            EnterReadIfShared();
            try
            {
                ref long cell = ref RefAs<long>(ea);
                return Interlocked.Exchange(ref cell, value);
            }
            finally { ExitReadIfShared(); }
        }

#if NET8_0_OR_GREATER
        // Native bitwise-atomic paths (.NET 5+). These return the original value.
        public int AtomicAndInt32(int ea, int value)
        {
            EnterReadIfShared();
            try
            {
                ref int cell = ref RefAs<int>(ea);
                return Interlocked.And(ref cell, value);
            }
            finally { ExitReadIfShared(); }
        }

        public int AtomicOrInt32(int ea, int value)
        {
            EnterReadIfShared();
            try
            {
                ref int cell = ref RefAs<int>(ea);
                return Interlocked.Or(ref cell, value);
            }
            finally { ExitReadIfShared(); }
        }

        public long AtomicAndInt64(int ea, long value)
        {
            EnterReadIfShared();
            try
            {
                ref long cell = ref RefAs<long>(ea);
                return Interlocked.And(ref cell, value);
            }
            finally { ExitReadIfShared(); }
        }

        public long AtomicOrInt64(int ea, long value)
        {
            EnterReadIfShared();
            try
            {
                ref long cell = ref RefAs<long>(ea);
                return Interlocked.Or(ref cell, value);
            }
            finally { ExitReadIfShared(); }
        }
#else
        // netstandard2.1 fallbacks — CAS loops for And/Or. Xor is CAS on all targets.
        public int AtomicAndInt32(int ea, int value)
        {
            EnterReadIfShared();
            try
            {
                ref int cell = ref RefAs<int>(ea);
                int old;
                do { old = Volatile.Read(ref cell); }
                while (Interlocked.CompareExchange(ref cell, old & value, old) != old);
                return old;
            }
            finally { ExitReadIfShared(); }
        }

        public int AtomicOrInt32(int ea, int value)
        {
            EnterReadIfShared();
            try
            {
                ref int cell = ref RefAs<int>(ea);
                int old;
                do { old = Volatile.Read(ref cell); }
                while (Interlocked.CompareExchange(ref cell, old | value, old) != old);
                return old;
            }
            finally { ExitReadIfShared(); }
        }

        public long AtomicAndInt64(int ea, long value)
        {
            EnterReadIfShared();
            try
            {
                ref long cell = ref RefAs<long>(ea);
                long old;
                do { old = Interlocked.Read(ref cell); }
                while (Interlocked.CompareExchange(ref cell, old & value, old) != old);
                return old;
            }
            finally { ExitReadIfShared(); }
        }

        public long AtomicOrInt64(int ea, long value)
        {
            EnterReadIfShared();
            try
            {
                ref long cell = ref RefAs<long>(ea);
                long old;
                do { old = Interlocked.Read(ref cell); }
                while (Interlocked.CompareExchange(ref cell, old & value, old) != old);
                return old;
            }
            finally { ExitReadIfShared(); }
        }
#endif

        // Xor is CAS-loop on all targets — Interlocked.Xor is .NET 7+.
        public int AtomicXorInt32(int ea, int value)
        {
            EnterReadIfShared();
            try
            {
                ref int cell = ref RefAs<int>(ea);
                int old;
                do { old = Volatile.Read(ref cell); }
                while (Interlocked.CompareExchange(ref cell, old ^ value, old) != old);
                return old;
            }
            finally { ExitReadIfShared(); }
        }

        public long AtomicXorInt64(int ea, long value)
        {
            EnterReadIfShared();
            try
            {
                ref long cell = ref RefAs<long>(ea);
                long old;
                do { old = Interlocked.Read(ref cell); }
                while (Interlocked.CompareExchange(ref cell, old ^ value, old) != old);
                return old;
            }
            finally { ExitReadIfShared(); }
        }

        public bool Contains(int offset, int width) =>
            offset >= 0 && (offset + width) <= (long)ByteLength;

        public string ReadString(uint ptr, uint len)
        {
            var bytes = this[(int)ptr..(int)(ptr + len)];
            return Encoding.UTF8.GetString(bytes.ToArray());
        }

        public int WriteUtf8String(uint ptr, string str, bool nullTerminate = false)
        {
            var data = Encoding.UTF8.GetBytes(str);
            Buffer.BlockCopy(data, 0, Data, (int)ptr, data.Length);
            if (nullTerminate)
                Data[ptr + data.Length] = 0;
            return data.Length + (nullTerminate?1:0);
        }

        public int WriteStruct<T>(int ptr, ref T str) where T : struct => WriteStruct((uint)ptr, ref str);

        public int WriteStruct<T>(uint ptr, ref T str)
            where T: struct
        {
            int size = Marshal.SizeOf<T>();
            var buf = this[(int)ptr..(int)(ptr + size)];
#if NET8_0_OR_GREATER
            MemoryMarshal.Write(buf, in str);
#else
            MemoryMarshal.Write(buf, ref str);
#endif
            return size;
        }

        public void WriteInt32(int ptr, int x)
        {
            if (!Contains(ptr, sizeof(int)))
                throw new ArgumentOutOfRangeException(nameof(ptr), "Pointer is out of bounds.");
            
            var scratchBuffer = BitConverter.GetBytes(x);
            Buffer.BlockCopy(scratchBuffer, 0, Data, ptr, sizeof(int));
        }

        public void WriteInt32(uint ptr, int x)
        {
            if (!Contains((int)ptr, sizeof(uint)))
                throw new ArgumentOutOfRangeException(nameof(ptr), "Pointer is out of bounds.");
            
            var scratchBuffer = BitConverter.GetBytes(x);
            Buffer.BlockCopy(scratchBuffer, 0, Data, (int)ptr, sizeof(int));
        }

        public void WriteInt32(int ptr, uint x)
        {
            if (!Contains(ptr, sizeof(uint)))
                throw new ArgumentOutOfRangeException(nameof(ptr), "Pointer is out of bounds.");
            
            var scratchBuffer = BitConverter.GetBytes(x);
            Buffer.BlockCopy(scratchBuffer, 0, Data, ptr, sizeof(uint));
        }

        public void WriteInt32(uint ptr, uint x)
        {
            if (!Contains((int)ptr, sizeof(uint)))
                throw new ArgumentOutOfRangeException(nameof(ptr), "Pointer is out of bounds.");
            
            var scratchBuffer = BitConverter.GetBytes(x);
            Buffer.BlockCopy(scratchBuffer, 0, Data, (int)ptr, sizeof(uint));
        }

        public void WriteInt64(int ptr, long x)
        {
            if (!Contains(ptr, sizeof(long)))
                throw new ArgumentOutOfRangeException(nameof(ptr), "Pointer is out of bounds.");
            
            var scratchBuffer = BitConverter.GetBytes(x);
            Buffer.BlockCopy(scratchBuffer, 0, Data, ptr, sizeof(long));
        }

        public void WriteInt64(uint ptr, long x)
        {
            if (!Contains((int)ptr, sizeof(long)))
                throw new ArgumentOutOfRangeException(nameof(ptr), "Pointer is out of bounds.");
            
            var scratchBuffer = BitConverter.GetBytes(x);
            Buffer.BlockCopy(scratchBuffer, 0, Data, (int)ptr, sizeof(long));
        }

        public void WriteInt64(int ptr, ulong x)
        {
            if (!Contains(ptr, sizeof(ulong)))
                throw new ArgumentOutOfRangeException(nameof(ptr), "Pointer is out of bounds.");
            
            var scratchBuffer = BitConverter.GetBytes(x);
            Buffer.BlockCopy(scratchBuffer, 0, Data, ptr, sizeof(ulong));
        }

        public void WriteInt64(uint ptr, ulong x)
        {
            if (!Contains((int)ptr, sizeof(ulong)))
                throw new ArgumentOutOfRangeException(nameof(ptr), "Pointer is out of bounds.");
            
            var scratchBuffer = BitConverter.GetBytes(x);
            Buffer.BlockCopy(scratchBuffer, 0, Data, (int)ptr, sizeof(ulong));
        }

        public T[] ReadStructs<T>(uint iovsPtr, uint iovsLen)
            where T : struct
        {
            int size = Marshal.SizeOf<T>();
    
            long span = size * iovsLen;
            if (span > int.MaxValue)
            {
                throw new ArgumentOutOfRangeException(nameof(iovsLen), "Resulting array size exceeds maximum allowed size.");
            }

            int byteCount = (int)span;

            // Ensure access is within bounds here (for example, check if (iovsPtr + byteCount) is within allowed range)
            var bytes = this[(int)iovsPtr..(int)(iovsPtr + byteCount)];
    
            // Use MemoryMarshal to cast the byte array to an array of T
            var array = MemoryMarshal.Cast<byte, T>(bytes).ToArray();
    
            return array;
        }
    }
}