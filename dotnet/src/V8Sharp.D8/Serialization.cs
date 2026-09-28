// Port of d8's structured-clone glue (src/d8/d8.h SerializationData, and the
// Serializer / Deserializer delegates, Shell::SerializeValue /
// DeserializeValue and SerializationDataQueue of src/d8/d8.cc): what Worker
// messages and d8.serializer carry between isolates.
using V8Sharp.Common;
using V8Sharp.Objects;

namespace V8Sharp.D8;

/// <summary>
/// d8's SerializationData: the wire bytes plus the backing stores that travel
/// out of band (transferred ArrayBuffers, SharedArrayBuffers, shared immutable
/// buffers). Backing stores are managed objects, so the receiving isolate
/// uses the same memory.
/// </summary>
public sealed class SerializationData
{
    public byte[] Data { get; internal set; } = [];
    public List<BackingStore> BackingStores { get; } = [];
    public List<BackingStore> SabBackingStores { get; } = [];
    public List<BackingStore> SharedImmutableBackingStores { get; internal set; } = [];
}

/// <summary>SerializationDataQueue: a thread-safe FIFO of messages.</summary>
public sealed class SerializationDataQueue
{
    readonly Queue<SerializationData?> _data = new();

    public void Enqueue(SerializationData? data)
    {
        lock (_data) _data.Enqueue(data);
    }

    public bool Dequeue(out SerializationData? data)
    {
        lock (_data)
        {
            return _data.TryDequeue(out data);
        }
    }

    public bool IsEmpty
    {
        get
        {
            lock (_data) return _data.Count == 0;
        }
    }

    public void Clear()
    {
        lock (_data) _data.Clear();
    }
}

/// <summary>d8's Serializer (a ValueSerializer::Delegate).</summary>
public sealed class Serializer : ValueSerializerDelegate
{
    readonly Isolate _isolate;
    readonly ValueSerializer _serializer;
    SerializationData? _data;
    readonly List<JSArrayBuffer> _arrayBuffers = [];
    readonly List<JSArrayBuffer> _sharedArrayBuffers = [];

    public Serializer(Isolate isolate)
    {
        _isolate = isolate;
        _serializer = new ValueSerializer(isolate, this, shareImmutableArrayBuffers: true);
    }

    /// <summary>Serializer::WriteValue: throws (a JS exception) on failure.</summary>
    public void WriteValue(JSValue value, JSValue transfer)
    {
        _data = new SerializationData();
        PrepareTransfer(transfer);
        _serializer.WriteHeader();
        _serializer.WriteObject(value);
        FinalizeTransfer();
        _data.Data = _serializer.Release();
        _data.SharedImmutableBackingStores = _serializer.ReleaseSharedImmutableBackingStores();
    }

    public SerializationData Release()
    {
        SerializationData data = _data!;
        _data = null;
        return data;
    }

    // Implements ValueSerializer::Delegate.
    public override void ThrowDataCloneError(Isolate isolate, JSString message) =>
        isolate.Throw(isolate.Factory.NewError(isolate.NativeContext.ErrorFunction, message));

    public override uint GetSharedArrayBufferId(Isolate isolate, JSArrayBuffer sharedArrayBuffer)
    {
        for (int index = 0; index < _sharedArrayBuffers.Count; ++index)
        {
            if (ReferenceEquals(_sharedArrayBuffers[index], sharedArrayBuffer)) return (uint)index;
        }
        int newIndex = _sharedArrayBuffers.Count;
        _sharedArrayBuffers.Add(sharedArrayBuffer);
        _data!.SabBackingStores.Add(sharedArrayBuffer.GetBackingStore()!);
        return (uint)newIndex;
    }

    void ThrowError(string message) =>
        _isolate.Throw(_isolate.Factory.NewError(_isolate.NativeContext.ErrorFunction, _isolate.Factory.NewStringFromUtf16(message)));

    void PrepareTransfer(JSValue transfer)
    {
        if (transfer.HeapObjectOrNull is JSArray transferArray)
        {
            ObjectOps.ToArrayLength(transferArray.Length, out uint length);
            for (uint i = 0; i < length; ++i)
            {
                JSValue element = ObjectOps.GetElement(_isolate, transferArray, i);
                if (element.HeapObjectOrNull is not JSArrayBuffer arrayBuffer || arrayBuffer.IsShared)
                {
                    ThrowError("Transfer array elements must be an ArrayBuffer");
                    return;
                }
                if (_arrayBuffers.Contains(arrayBuffer))
                {
                    ThrowError("ArrayBuffer occurs in the transfer array more than once");
                    return;
                }
                _serializer.TransferArrayBuffer((uint)_arrayBuffers.Count, arrayBuffer);
                _arrayBuffers.Add(arrayBuffer);
            }
        }
        else if (!transfer.IsUndefined)
        {
            ThrowError("Transfer list must be an Array or undefined");
        }
    }

    void FinalizeTransfer()
    {
        foreach (JSArrayBuffer arrayBuffer in _arrayBuffers)
        {
            if (!arrayBuffer.IsDetachable)
            {
                ThrowError("ArrayBuffer is not detachable and could not be transferred");
                return;
            }
            BackingStore backingStore = arrayBuffer.GetBackingStore() ?? BackingStore.WrapAllocation([], false);
            _data!.BackingStores.Add(backingStore);
            JSArrayBuffer.Detach(_isolate, arrayBuffer);
        }
    }
}

/// <summary>d8's Deserializer (a ValueDeserializer::Delegate).</summary>
public sealed class Deserializer : ValueDeserializerDelegate
{
    readonly Isolate _isolate;
    readonly ValueDeserializer _deserializer;
    readonly SerializationData _data;

    public Deserializer(Isolate isolate, SerializationData data)
    {
        _isolate = isolate;
        _data = data;
        _deserializer = new ValueDeserializer(isolate, data.Data, this);
        _deserializer.SetSupportsLegacyWireFormat(true);
        _deserializer.SetSharedImmutableBackingStores(data.SharedImmutableBackingStores);
    }

    /// <summary>Deserializer::ReadValue: throws (a JS exception) on failure.</summary>
    public JSValue ReadValue()
    {
        _deserializer.ReadHeader();
        uint index = 0;
        foreach (BackingStore backingStore in _data.BackingStores)
        {
            JSArrayBuffer arrayBuffer = _isolate.Factory.NewJSArrayBuffer(backingStore);
            _deserializer.TransferArrayBuffer(index++, arrayBuffer);
        }
        return _deserializer.ReadValue();
    }

    public override JSArrayBuffer? GetSharedArrayBufferFromId(Isolate isolate, uint cloneId)
    {
        if (cloneId < _data.SabBackingStores.Count)
        {
            return _isolate.Factory.NewJSSharedArrayBuffer(_data.SabBackingStores[(int)cloneId]);
        }
        return null;
    }
}

/// <summary>Shell::SerializeValue / DeserializeValue and d8.serializer.</summary>
public static class D8Serialization
{
    /// <summary>Shell::SerializeValue: throws a JS exception when the value cannot be cloned.</summary>
    public static SerializationData SerializeValue(Isolate isolate, JSValue value, JSValue transfer)
    {
        var serializer = new Serializer(isolate);
        serializer.WriteValue(value, transfer);
        return serializer.Release();
    }

    /// <summary>Shell::DeserializeValue.</summary>
    public static JSValue DeserializeValue(Isolate isolate, SerializationData data) => new Deserializer(isolate, data).ReadValue();

    /// <summary>Shell::SerializerSerialize (d8.serializer.serialize): the wire bytes as an ArrayBuffer.</summary>
    public static JSValue SerializerSerialize(Isolate isolate, ReadOnlySpan<JSValue> values)
    {
        var serializer = new ValueSerializer(isolate);
        serializer.WriteHeader();
        foreach (JSValue value in values) serializer.WriteObject(value);
        byte[] bytes = serializer.Release();
        JSArrayBuffer? buffer = isolate.Factory.NewJSArrayBufferAndBackingStore((ulong)bytes.Length);
        if (buffer is null) return isolate.ThrowRangeError(MessageTemplate.ArrayBufferAllocationFailed);
        bytes.CopyTo(buffer.BackingStoreBuffer, 0);
        return buffer;
    }

    /// <summary>Shell::SerializerDeserialize (d8.serializer.deserialize).</summary>
    public static JSValue SerializerDeserialize(Isolate isolate, JSValue value)
    {
        if (value.HeapObjectOrNull is not JSArrayBuffer buffer)
        {
            return isolate.Throw(isolate.Factory.NewError(isolate.NativeContext.ErrorFunction,
                isolate.Factory.NewStringFromUtf16("Can only deserialize from an ArrayBuffer")));
        }
        ulong length = buffer.GetByteLength();
        if (length > int.MaxValue)
        {
            return isolate.Throw(isolate.Factory.NewError(isolate.NativeContext.ErrorFunction,
                isolate.Factory.NewStringFromUtf16("Serialized data too large")));
        }
        byte[] bytes = buffer.BackingStoreBuffer.AsSpan(0, (int)length).ToArray();
        var deserializer = new ValueDeserializer(isolate, bytes);
        deserializer.ReadHeader();
        return deserializer.ReadValue();
    }
}
