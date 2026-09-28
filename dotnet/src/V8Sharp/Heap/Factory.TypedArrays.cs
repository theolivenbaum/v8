// Port of the ArrayBuffer, typed array, DataView and array iterator
// allocation of src/heap/factory.cc: NewJSArrayBuffer,
// NewJSArrayBufferAndBackingStore, NewJSSharedArrayBuffer,
// NewJSArrayBufferView, NewJSTypedArray, NewJSDataViewOrRabGsabDataView,
// NewJSArrayIterator and NewJSUint8ArraySetFromResult.
using V8Sharp.Objects;

namespace V8Sharp;

public sealed partial class Factory
{
    /// <summary>Factory::NewJSArrayBuffer(backing_store).</summary>
    public JSArrayBuffer NewJSArrayBuffer(BackingStore? backingStore)
    {
        Map map = _isolate.NativeContext.ArrayBufferFun.InitialMap;
        bool resizableByJs = backingStore is not null && backingStore.IsResizableByJs;
        var result = (JSArrayBuffer)NewJSObjectFromMap(map);
        result.Setup(shared: false, resizableByJs, backingStore, _isolate);
        return result;
    }

    /// <summary>
    /// Factory::NewJSArrayBufferAndBackingStore: null when the backing store
    /// cannot be allocated (V8: an empty MaybeHandle).
    /// </summary>
    public JSArrayBuffer? NewJSArrayBufferAndBackingStore(ulong byteLength, ulong maxByteLength, bool initialized,
        bool resizable)
    {
        Debug.Assert(byteLength <= maxByteLength);
        BackingStore? backingStore = null;
        if (resizable)
        {
            if (byteLength > JSArrayBuffer.kMaxByteLength || maxByteLength > JSArrayBuffer.kMaxByteLength) return null;
            backingStore = BackingStore.TryAllocateAndPartiallyCommitMemory(_isolate, byteLength, maxByteLength, shared: false);
            if (backingStore is null) return null;
        }
        else if (byteLength > 0)
        {
            backingStore = BackingStore.Allocate(_isolate, byteLength, shared: false, initialized);
            if (backingStore is null) return null;
        }
        Map map = _isolate.NativeContext.ArrayBufferFun.InitialMap;
        var arrayBuffer = (JSArrayBuffer)NewJSObjectFromMap(map);
        arrayBuffer.Setup(shared: false, resizable, backingStore, _isolate);
        return arrayBuffer;
    }

    /// <summary>Factory::NewJSArrayBufferAndBackingStore(byte_length, initialized).</summary>
    public JSArrayBuffer? NewJSArrayBufferAndBackingStore(ulong byteLength, bool initialized = true) =>
        NewJSArrayBufferAndBackingStore(byteLength, byteLength, initialized, resizable: false);

    /// <summary>Factory::NewJSSharedArrayBuffer.</summary>
    public JSArrayBuffer NewJSSharedArrayBuffer(BackingStore backingStore)
    {
        Map map = _isolate.NativeContext.SharedArrayBufferFun.InitialMap;
        var result = (JSArrayBuffer)NewJSObjectFromMap(map);
        result.Setup(shared: true, backingStore.IsResizableByJs, backingStore, _isolate);
        return result;
    }

    /// <summary>Factory::NewJSArrayBufferView.</summary>
    JSArrayBufferView NewJSArrayBufferView(Map map, FixedArrayBase elements, JSArrayBuffer buffer, ulong byteOffset,
        ulong byteLength)
    {
        if (!ElementsKinds.IsRabGsabTypedArrayElementsKind(map.ElementsKind) &&
            map.InstanceType != InstanceType.JSRabGsabDataViewType)
        {
            ulong bufferByteLength = buffer.GetByteLength();
            if (byteLength > bufferByteLength || byteOffset > bufferByteLength || byteOffset + byteLength > bufferByteLength)
            {
                throw new InvalidOperationException("NewJSArrayBufferView: view out of the buffer's bounds");
            }
        }
        var view = (JSArrayBufferView)NewJSObjectFromMap(map);
        view.Elements = elements;
        view.Buffer = buffer;
        view.ByteOffset = byteOffset;
        view.RawByteLength = byteLength;
        view.IsLengthTracking = false;
        view.IsBackedByRab = false;
        return view;
    }

    /// <summary>Factory::NewJSTypedArray(type, buffer, byte_offset, length, is_length_tracking).</summary>
    public JSTypedArray NewJSTypedArray(ElementsKind elementsKind, JSArrayBuffer buffer, ulong byteOffset, ulong length,
        bool isLengthTracking = false)
    {
        Debug.Assert(ElementsKinds.IsTypedArrayElementsKind(elementsKind));
        ulong elementSize = (ulong)ElementsKinds.ElementsKindToByteSize(elementsKind);
        bool isBackedByRab = buffer.IsResizableByJs && !buffer.IsShared;

        NativeContext nativeContext = _isolate.NativeContext;
        Map map = isBackedByRab || isLengthTracking
            ? JSTypedArray.TypedArrayElementsKindToRabGsabCtorMap(nativeContext, elementsKind)
            : JSTypedArray.TypedArrayElementsKindToCtorMap(nativeContext, elementsKind);

        if (isLengthTracking)
        {
            // Security: enforce the invariant that length-tracking TypedArrays have
            // their length and byte_length set to 0.
            length = 0;
        }

        Debug.Assert(length <= JSTypedArray.kMaxByteLength / elementSize);
        Debug.Assert(byteOffset % elementSize == 0);
        ulong byteLength = length * elementSize;

        var typedArray = (JSTypedArray)NewJSArrayBufferView(map, ByteArray.Empty, buffer, byteOffset, byteLength);
        typedArray.RawLength = length;
        typedArray.IsLengthTracking = isLengthTracking;
        typedArray.IsBackedByRab = isBackedByRab;
        return typedArray;
    }

    /// <summary>
    /// A typed array of <paramref name="elementsKind"/> over a fresh buffer of
    /// <paramref name="length"/> elements (Factory::NewJSTypedArray(kind, length)
    /// together with its buffer, as TypedArrayCreateByLength does).
    /// Throws RangeError when the buffer cannot be allocated.
    /// </summary>
    public JSTypedArray NewJSTypedArrayWithBuffer(ElementsKind elementsKind, ulong length)
    {
        ulong elementSize = (ulong)ElementsKinds.ElementsKindToByteSize(elementsKind);
        if (length > JSTypedArray.kMaxByteLength / elementSize)
        {
            _isolate.ThrowRangeError(MessageTemplate.InvalidTypedArrayLength, NewNumberFromSize(length));
        }
        JSArrayBuffer? buffer = NewJSArrayBufferAndBackingStore(length * elementSize, initialized: true);
        if (buffer is null) _isolate.ThrowRangeError(MessageTemplate.ArrayBufferAllocationFailed);
        return NewJSTypedArray(elementsKind, buffer!, 0, length);
    }

    /// <summary>Factory::NewJSDataViewOrRabGsabDataView.</summary>
    public JSDataView NewJSDataViewOrRabGsabDataView(JSArrayBuffer buffer, ulong byteOffset, ulong byteLength,
        bool isLengthTracking)
    {
        if (isLengthTracking)
        {
            // Security: enforce the invariant that length-tracking DataViews have their
            // byte_length set to 0.
            byteLength = 0;
        }
        bool isBackedByRab = !buffer.IsShared && buffer.IsResizableByJs;
        NativeContext nativeContext = _isolate.NativeContext;
        Map map = isBackedByRab || isLengthTracking
            ? nativeContext.JSRabGsabDataViewMap
            : nativeContext.DataViewFun.InitialMap;
        var obj = (JSDataView)NewJSArrayBufferView(map, FixedArray.Empty, buffer, byteOffset, byteLength);
        obj.IsLengthTracking = isLengthTracking;
        obj.IsBackedByRab = isBackedByRab;
        return obj;
    }

    /// <summary>Factory::NewJSArrayIterator (CreateArrayIterator in builtins-array-gen.cc).</summary>
    public JSArrayIterator NewJSArrayIterator(JSReceiver iteratedObject, IterationKind kind)
    {
        Map map = _isolate.NativeContext.InitialArrayIteratorMap;
        var iterator = (JSArrayIterator)NewJSObjectFromMap(map);
        iterator.IteratedObject = iteratedObject;
        iterator.NextIndex = JSValue.Zero;
        iterator.Kind = kind;
        return iterator;
    }

    /// <summary>Factory::NewJSUint8ArraySetFromResult: the {read, written} result object.</summary>
    public JSObject NewJSUint8ArraySetFromResult(JSValue read, JSValue written)
    {
        Map map = _isolate.NativeContext.SetUnit8ArrayResultMap;
        JSObject result = NewJSObjectFromMap(map);
        result.RawFields[0] = read;
        result.RawFields[1] = written;
        return result;
    }
}
