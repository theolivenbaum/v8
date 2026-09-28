// Port of src/objects/js-array-buffer.{h,cc} (JSArrayBuffer, JSArrayBufferView)
// and src/objects/backing-store.{h,cc} (BackingStore): the ArrayBuffer data
// model.
//
// The backing store is a managed byte[] (architecture.md section 2). A
// BackingStore object is what V8's std::shared_ptr<BackingStore> is: the
// transfer and SharedArrayBuffer sharing unit. SharedArrayBuffers share the
// BackingStore object (and therefore the byte[]) across isolates/agents.
using System.Runtime.CompilerServices;

namespace V8Sharp.Objects;

/// <summary>V8's SharedFlag / ResizableFlag / InitializedFlag, as booleans.</summary>
public enum ArrayBufferKind { ARRAY_BUFFER, SHARED_ARRAY_BUFFER }

/// <summary>
/// V8's BackingStore: the memory of one or more JSArrayBuffers. Growable
/// SharedArrayBuffers read their length from here (with sequentially
/// consistent ordering), because other agents may grow it concurrently.
/// </summary>
public sealed class BackingStore
{
    /// <summary>BackingStore::ResizeOrGrowResult.</summary>
    public enum ResizeOrGrowResult { kSuccess, kFailure, kRace }

    // V8 allocates or reserves native memory; V8Sharp allocates a managed
    // array, whose length is bounded by Array.MaxLength (deviations.md).
    static readonly ulong s_maxManagedLength = (ulong)Array.MaxLength;

    byte[] _buffer;
    long _byteLength;

    BackingStore(byte[] buffer, ulong byteLength, ulong maxByteLength, bool shared, bool resizable)
    {
        _buffer = buffer;
        _byteLength = (long)byteLength;
        MaxByteLength = maxByteLength;
        IsShared = shared;
        IsResizableByJs = resizable;
    }

    /// <summary>The memory. For resizable non-shared buffers the array may be replaced on growth.</summary>
    public byte[] Buffer
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _buffer;
    }

    public ulong MaxByteLength { get; }
    public bool IsShared { get; }
    public bool IsResizableByJs { get; }
    public bool IsImmutable { get; set; }

    /// <summary>BackingStore::byte_length (seq_cst for shared stores).</summary>
    public ulong ByteLength => IsShared ? (ulong)Volatile.Read(ref _byteLength) : (ulong)_byteLength;

    public bool IsEmpty => _buffer.Length == 0;

    /// <summary>
    /// BackingStore::Allocate: a fixed-length store, zeroed. Returns null when
    /// the allocation fails (V8: the allocator returns nullptr).
    /// </summary>
    public static BackingStore? Allocate(Isolate isolate, ulong byteLength, bool shared, bool initialized)
    {
        // initialized == false is only an optimisation in V8 (uninitialized
        // memory); managed arrays are always zeroed.
        byte[]? buffer = TryAllocate(byteLength);
        if (buffer is null) return null;
        return new BackingStore(buffer, byteLength, byteLength, shared, resizable: false);
    }

    /// <summary>
    /// BackingStore::TryAllocateAndPartiallyCommitMemory for resizable buffers.
    /// </summary>
    public static BackingStore? TryAllocateAndPartiallyCommitMemory(Isolate isolate, ulong byteLength, ulong maxByteLength,
        bool shared)
    {
        // V8 reserves max_byte_length of address space and commits
        // byte_length. V8Sharp allocates the current length for resizable
        // ArrayBuffers (a resize replaces the array) and the maximum length up
        // front for growable SharedArrayBuffers, whose memory other agents may
        // be using concurrently and therefore cannot move (deviations.md).
        byte[]? buffer = TryAllocate(shared ? maxByteLength : byteLength);
        if (buffer is null) return null;
        return new BackingStore(buffer, byteLength, maxByteLength, shared, resizable: true);
    }

    static byte[]? TryAllocate(ulong length)
    {
        if (length == 0) return [];
        if (length > s_maxManagedLength) return null;
        try
        {
            return new byte[(int)length];
        }
        catch (OutOfMemoryException)
        {
            return null;
        }
    }

    /// <summary>A store over existing memory (the empty store of a zero-length transfer, API wrapping).</summary>
    public static BackingStore WrapAllocation(byte[] buffer, bool shared) =>
        new(buffer, (ulong)buffer.Length, (ulong)buffer.Length, shared, resizable: false);

    /// <summary>BackingStore::ResizeInPlace: resizable, non-shared buffers.</summary>
    public ResizeOrGrowResult ResizeInPlace(Isolate isolate, ulong newByteLength)
    {
        Debug.Assert(!IsShared);
        Debug.Assert(newByteLength <= MaxByteLength);
        ulong byteLength = (ulong)_byteLength;
        if (newByteLength < byteLength)
        {
            // Zero the memory so that in case the buffer is grown later, we have
            // zeroed the contents already.
            _buffer.AsSpan((int)newByteLength, (int)(byteLength - newByteLength)).Clear();
            _byteLength = (long)newByteLength;
            return ResizeOrGrowResult.kSuccess;
        }
        if (newByteLength == byteLength) return ResizeOrGrowResult.kSuccess;
        if (newByteLength > (ulong)_buffer.Length)
        {
            // Grow the managed array geometrically (bounded by the maximum), so
            // that a sequence of small resizes is amortized.
            ulong capacity = Math.Max(newByteLength, Math.Min(MaxByteLength, (ulong)_buffer.Length * 2));
            byte[]? grown = TryAllocate(capacity) ?? TryAllocate(newByteLength);
            if (grown is null) return ResizeOrGrowResult.kFailure;
            _buffer.AsSpan(0, (int)byteLength).CopyTo(grown);
            _buffer = grown;
        }
        _byteLength = (long)newByteLength;
        return ResizeOrGrowResult.kSuccess;
    }

    /// <summary>BackingStore::GrowInPlace: growable SharedArrayBuffers (may race with other agents).</summary>
    public ResizeOrGrowResult GrowInPlace(Isolate isolate, ulong newByteLength)
    {
        Debug.Assert(IsShared);
        Debug.Assert(newByteLength <= MaxByteLength);
        // GrowableSharedArrayBuffer.prototype.grow can be called from several
        // threads. If two threads try to grow() in a racy way, the spec allows the
        // larger grow to throw also if the smaller grow succeeds first. The
        // implementation below doesn't throw in that case - instead, it retries and
        // succeeds. If the larger grow finishes first though, the smaller grow must
        // throw.
        long oldByteLength = Volatile.Read(ref _byteLength);
        while (true)
        {
            if ((long)newByteLength < oldByteLength) return ResizeOrGrowResult.kRace;
            if ((long)newByteLength == oldByteLength) return ResizeOrGrowResult.kSuccess;
            long seen = Interlocked.CompareExchange(ref _byteLength, (long)newByteLength, oldByteLength);
            if (seen == oldByteLength) break;
            oldByteLength = seen;
        }
        return ResizeOrGrowResult.kSuccess;
    }
}

/// <summary>V8's JSArrayBuffer.</summary>
public sealed partial class JSArrayBuffer(Map map) : JSObject(map)
{
    /// <summary>
    /// ArrayBuffer::kMaxByteLength: V8's default 64-bit configuration (with the
    /// sandbox) limits buffers to kMaxSafeBufferSizeForSandbox = 32 GB - 1.
    /// Larger lengths are a RangeError "Invalid array buffer length"; lengths
    /// below this limit that a managed array cannot hold fail the allocation
    /// ("Array buffer allocation failed").
    /// </summary>
    public const ulong kMaxByteLength = 32UL * 1024 * 1024 * 1024 - 1;

    BackingStore? _backingStore;
    ulong _byteLength;
    ulong _maxByteLength;
    JSValue _detachKey;
    bool _hasDetachKey;

    public bool IsShared;
    public bool IsResizableByJs;
    public bool IsDetachable = true;
    public bool WasDetached;
    public bool IsImmutable;
    public bool IsExternal;

    /// <summary>The bytes of the buffer (V8's backing_store()); empty when detached.</summary>
    public byte[] BackingStoreBuffer
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _backingStore?.Buffer ?? [];
    }

    /// <summary>JSArrayBuffer::byte_length (0 for growable SharedArrayBuffers: use <see cref="GetByteLength"/>).</summary>
    public ulong ByteLength
    {
        get => _byteLength;
        set => _byteLength = value;
    }

    /// <summary>JSArrayBuffer::max_byte_length.</summary>
    public ulong MaxByteLength
    {
        get => _maxByteLength;
        set => _maxByteLength = value;
    }

    /// <summary>JSArrayBuffer::GetBackingStore.</summary>
    public BackingStore? GetBackingStore() => _backingStore;

    /// <summary>JSArrayBuffer::GetByteLength: reads the BackingStore for growable SharedArrayBuffers.</summary>
    public ulong GetByteLength()
    {
        if (IsShared && IsResizableByJs)
        {
            // Invariant: byte_length for GSAB is 0 (it needs to be read from the
            // BackingStore).
            return _backingStore?.ByteLength ?? 0;
        }
        return _byteLength;
    }

    /// <summary>JSArrayBuffer::IsEmpty.</summary>
    public bool IsEmpty => _backingStore is null || _backingStore.IsEmpty;

    /// <summary>JSArrayBuffer::DetachKey.</summary>
    public JSValue DetachKey => _hasDetachKey ? _detachKey : JSValue.Undefined;

    /// <summary>JSArrayBuffer::Setup.</summary>
    public void Setup(bool shared, bool resizable, BackingStore? backingStore, Isolate isolate)
    {
        _detachKey = JSValue.Undefined;
        _hasDetachKey = false;
        WasDetached = false;
        IsImmutable = false;
        IsExternal = false;
        IsShared = shared;
        IsResizableByJs = resizable;
        IsDetachable = !shared;
        if (backingStore is null)
        {
            _backingStore = null;
            _byteLength = 0;
            _maxByteLength = 0;
            return;
        }
        // Rest of the code here deals with attaching the BackingStore.
        Debug.Assert(IsShared == backingStore.IsShared);
        Debug.Assert(IsResizableByJs == backingStore.IsResizableByJs);
        _backingStore = backingStore;

        // GSABs need to read their byte_length from the BackingStore. Maintain the
        // invariant that their byte_length field is always 0.
        _byteLength = IsShared && IsResizableByJs ? 0 : backingStore.ByteLength;
        _maxByteLength = IsResizableByJs ? backingStore.MaxByteLength : backingStore.ByteLength;
        if (backingStore.IsImmutable) IsImmutable = true;
    }

    /// <summary>
    /// JSArrayBuffer::Detach: throws TypeError when <paramref name="maybeKey"/>
    /// does not match the detach key or the buffer is immutable. Passing
    /// <paramref name="hasKey"/> false is V8's null MaybeHandle (no key given).
    /// </summary>
    public static void Detach(Isolate isolate, JSArrayBuffer buffer, bool forceForWasmMemory = false, bool hasKey = false,
        JSValue maybeKey = default)
    {
        bool keyMismatch;
        JSValue key = buffer.DetachKey;
        if (!key.IsUndefined)
        {
            keyMismatch = !hasKey || !ObjectOps.StrictEquals(maybeKey, key);
        }
        else
        {
            // Detach key is undefined; allow not passing maybe_key but disallow passing
            // something else than undefined.
            keyMismatch = hasKey && !maybeKey.IsUndefined;
        }
        if (keyMismatch)
        {
            isolate.ThrowTypeError(MessageTemplate.ArrayBufferDetachKeyDoesntMatch);
        }

        if (buffer.IsImmutable)
        {
            isolate.ThrowTypeError(MessageTemplate.TypedArrayImmutableBufferErrorOperation,
                isolate.Factory.NewStringFromAsciiChecked("DetachArrayBuffer"));
        }

        if (buffer.WasDetached) return;

        if (forceForWasmMemory)
        {
            // Skip the is_detachable() check.
        }
        else if (!buffer.IsDetachable)
        {
            // Not detachable, do nothing.
            return;
        }

        DetachInternal(isolate, buffer);
    }

    /// <summary>JSArrayBuffer::DetachInternal.</summary>
    static void DetachInternal(Isolate isolate, JSArrayBuffer buffer)
    {
        Debug.Assert(!buffer.IsShared);
        buffer._backingStore = null;
        buffer.WasDetached = true;

        // V8 marks the single tracked view detached when it can
        // (TryDetachViews, --track-array-buffer-views) and invalidates the
        // protector otherwise. V8Sharp does not track views.
        if (Protectors.IsArrayBufferDetachingIntact(isolate)) Protectors.InvalidateArrayBufferDetaching(isolate);

        buffer._byteLength = 0;
    }

    /// <summary>JSArrayBuffer::SetDetachKey.</summary>
    public static void SetDetachKey(JSArrayBuffer arrayBuffer, JSValue key, Isolate isolate)
    {
        if (key.IsUndefined && !arrayBuffer._hasDetachKey) return;
        arrayBuffer._detachKey = key;
        arrayBuffer._hasDetachKey = true;
    }

    /// <summary>JSArrayBuffer::MakeImmutable.</summary>
    public void MakeImmutable(Isolate isolate)
    {
        if (IsImmutable) return;
        Debug.Assert(!WasDetached);
        IsImmutable = true;
        if (_backingStore is not null) _backingStore.IsImmutable = true;
        if (Protectors.IsArrayBufferMutableIntact(isolate)) Protectors.InvalidateArrayBufferMutable(isolate);
    }

    /// <summary>Test/debug helper equivalent of V8's %ArrayBufferDetach without a key.</summary>
    public void Detach()
    {
        Isolate? isolate = Isolate.Current;
        if (isolate is not null)
        {
            Detach(isolate, this);
            return;
        }
        if (WasDetached || !IsDetachable) return;
        _backingStore = null;
        _byteLength = 0;
        WasDetached = true;
    }
}

/// <summary>V8's JSArrayBufferView: the common part of typed arrays and DataViews.</summary>
public abstract class JSArrayBufferView(Map map) : JSObject(map)
{
    public JSArrayBuffer Buffer = null!;
    public ulong ByteOffset;

    /// <summary>JSArrayBufferView::byte_length (0 for length-tracking views).</summary>
    public ulong RawByteLength;

    public bool IsLengthTracking;
    public bool IsBackedByRab;

    /// <summary>JSArrayBufferView::WasDetached.</summary>
    public bool WasDetached => Buffer.WasDetached;

    /// <summary>JSArrayBufferView::IsVariableLength.</summary>
    public bool IsVariableLength => IsLengthTracking || IsBackedByRab;

    /// <summary>JSArrayBufferView::IsDetachedOrOutOfBounds.</summary>
    public bool IsViewDetachedOrOutOfBounds
    {
        get
        {
            JSArrayBuffer backingBuffer = Buffer;
            if (backingBuffer.WasDetached) return true;
            // TypedArrays backed by GSABs or regular AB/SABs are never out of bounds.
            if (!IsBackedByRab) return false;
            ulong bufferByteLength = backingBuffer.GetByteLength();
            // Length-tracking ArrayBufferViews have byte_length() == 0, so this math
            // works.
            return ByteOffset + RawByteLength > bufferByteLength;
        }
    }
}

/// <summary>V8's JSDataView and JSRabGsabDataView (instance type JS_RAB_GSAB_DATA_VIEW_TYPE).</summary>
public sealed class JSDataView(Map map) : JSArrayBufferView(map)
{
    public bool IsRabGsab => Map.InstanceType == InstanceType.JSRabGsabDataViewType;

    /// <summary>JSRabGsabDataView::GetByteLength (and byte_length() for plain DataViews).</summary>
    public ulong GetByteLength()
    {
        if (!IsRabGsab) return RawByteLength;
        if (IsOutOfBounds) return 0;
        if (IsLengthTracking)
        {
            // Invariant: byte_length of length tracking DataViews is 0.
            return Buffer.GetByteLength() - ByteOffset;
        }
        return RawByteLength;
    }

    /// <summary>JSRabGsabDataView::IsOutOfBounds.</summary>
    public bool IsOutOfBounds
    {
        get
        {
            if (!IsBackedByRab) return false;
            if (IsLengthTracking) return ByteOffset > Buffer.GetByteLength();
            return ByteOffset + RawByteLength > Buffer.GetByteLength();
        }
    }

    /// <summary>The bytes of the view's buffer, from the view's offset.</summary>
    public Span<byte> DataSpan() => Buffer.BackingStoreBuffer.AsSpan((int)ByteOffset);
}
