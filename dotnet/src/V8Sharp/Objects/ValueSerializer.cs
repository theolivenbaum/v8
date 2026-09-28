// Port of src/objects/value-serializer.{h,cc}: the structured-clone wire format
// (ValueSerializer / ValueDeserializer), and the parts of the public
// v8::ValueSerializer / v8::ValueDeserializer API (src/api/api.cc) the
// embedder uses: the delegates, ReadHeader with the legacy-format check, and
// ReadValue.
//
// Deviations (deviations.md, V8Sharp engine):
// - The buffer is a managed byte array grown by the serializer; the
//   delegate's ReallocateBufferMemory / FreeBufferMemory hooks are not ported.
// - Strings are written as one-byte (Latin-1) when every code unit is at most
//   0xFF, since V8Sharp strings have no one-byte representation.
// - ReadJSObjectProperties does not take V8's map-transition fast path; the
//   properties are defined one by one, which follows the same transitions.
// - No WebAssembly, shared structs/arrays or shared-object conveyors.
using System.Buffers.Binary;
using V8Sharp.RegExp;

namespace V8Sharp.Objects;

/// <summary>SerializationTag (value-serializer.cc).</summary>
public enum SerializationTag : byte
{
    kVersion = 0xFF,
    kPadding = (byte)'\0',
    kVerifyObjectCount = (byte)'?',
    kTheHole = (byte)'-',
    kUndefined = (byte)'_',
    kNull = (byte)'0',
    kTrue = (byte)'T',
    kFalse = (byte)'F',
    kInt32 = (byte)'I',
    kUint32 = (byte)'U',
    kDouble = (byte)'N',
    kBigInt = (byte)'Z',
    kUtf8String = (byte)'S',
    kOneByteString = (byte)'"',
    kTwoByteString = (byte)'c',
    kObjectReference = (byte)'^',
    kBeginJSObject = (byte)'o',
    kEndJSObject = (byte)'{',
    kBeginSparseJSArray = (byte)'a',
    kEndSparseJSArray = (byte)'@',
    kBeginDenseJSArray = (byte)'A',
    kEndDenseJSArray = (byte)'$',
    kDate = (byte)'D',
    kTrueObject = (byte)'y',
    kFalseObject = (byte)'x',
    kNumberObject = (byte)'n',
    kBigIntObject = (byte)'z',
    kStringObject = (byte)'s',
    kRegExp = (byte)'R',
    kBeginJSMap = (byte)';',
    kEndJSMap = (byte)':',
    kBeginJSSet = (byte)'\'',
    kEndJSSet = (byte)',',
    kArrayBuffer = (byte)'B',
    kImmutableArrayBuffer = (byte)'C',
    kSharedImmutableArrayBuffer = (byte)'E',
    kResizableArrayBuffer = (byte)'~',
    kArrayBufferTransfer = (byte)'t',
    kArrayBufferView = (byte)'V',
    kSharedArrayBuffer = (byte)'u',
    kSharedObject = (byte)'p',
    kWasmModuleTransfer = (byte)'w',
    kHostObject = (byte)'\\',
    kWasmMemoryTransfer = (byte)'m',
    kError = (byte)'r',
}

/// <summary>
/// v8::ValueSerializer::Delegate: the embedder's hooks. The defaults are the
/// API's: errors are thrown as Error, shared array buffers and host objects
/// cannot be cloned.
/// </summary>
public class ValueSerializerDelegate
{
    /// <summary>Handles the DataCloneError message by throwing an exception.</summary>
    public virtual void ThrowDataCloneError(Isolate isolate, JSString message) =>
        isolate.Throw(isolate.Factory.NewError(isolate.NativeContext.ErrorFunction, message));

    public virtual bool HasCustomHostObject(Isolate isolate) => false;

    public virtual bool IsHostObject(Isolate isolate, JSObject obj) => false;

    /// <summary>Writes a host object (the default throws the DataCloneError, as the API's does).</summary>
    public virtual void WriteHostObject(Isolate isolate, JSObject obj, ValueSerializer serializer) =>
        isolate.Throw(isolate.Factory.NewError(isolate.NativeContext.ErrorFunction, MessageTemplate.DataCloneError, [obj]));

    /// <summary>The id of a SharedArrayBuffer (the default throws the DataCloneError).</summary>
    public virtual uint GetSharedArrayBufferId(Isolate isolate, JSArrayBuffer sharedArrayBuffer)
    {
        isolate.Throw(isolate.Factory.NewError(isolate.NativeContext.ErrorFunction, MessageTemplate.DataCloneError,
            [sharedArrayBuffer]));
        return 0;
    }
}

/// <summary>v8::ValueDeserializer::Delegate.</summary>
public class ValueDeserializerDelegate
{
    /// <summary>Reads a host object; null (without an exception) fails the read.</summary>
    public virtual JSObject? ReadHostObject(Isolate isolate, ValueDeserializer deserializer)
    {
        isolate.Throw(isolate.Factory.NewError(isolate.NativeContext.ErrorFunction, MessageTemplate.DataCloneDeserializationError,
            ReadOnlySpan<JSValue>.Empty));
        return null;
    }

    /// <summary>The SharedArrayBuffer for an id from GetSharedArrayBufferId; null fails the read.</summary>
    public virtual JSArrayBuffer? GetSharedArrayBufferFromId(Isolate isolate, uint cloneId) => null;
}

/// <summary>
/// ValueSerializer: writes values in a binary format that allows the objects
/// to be cloned according to the HTML structured clone algorithm.
/// </summary>
public sealed class ValueSerializer
{
    /// <summary>kLatestVersion (v8::CurrentValueSerializerFormatVersion).</summary>
    public const uint kLatestVersion = 16;

    readonly Isolate _isolate;
    readonly ValueSerializerDelegate? _delegate;
    byte[] _buffer = [];
    int _bufferSize;
    readonly bool _hasCustomHostObjects;
    bool _treatArrayBufferViewsAsHostObjects;
    readonly bool _shareImmutableArrayBuffers;

    readonly Dictionary<JSReceiver, uint> _idMap = new(ReferenceEqualityComparer.Instance);
    uint _nextId;
    readonly Dictionary<JSArrayBuffer, uint> _arrayBufferTransferMap = new(ReferenceEqualityComparer.Instance);
    readonly List<BackingStore> _sharedImmutableBackingStores = [];

    public ValueSerializer(Isolate isolate, ValueSerializerDelegate? @delegate = null, bool shareImmutableArrayBuffers = false)
    {
        _isolate = isolate;
        _delegate = @delegate;
        _shareImmutableArrayBuffers = shareImmutableArrayBuffers;
        if (_delegate is not null) _hasCustomHostObjects = _delegate.HasCustomHostObject(isolate);
    }

    /// <summary>Writes out a header, which includes the format version.</summary>
    public void WriteHeader()
    {
        WriteTag(SerializationTag.kVersion);
        WriteVarint(kLatestVersion);
    }

    public void SetTreatArrayBufferViewsAsHostObjects(bool mode) => _treatArrayBufferViewsAsHostObjects = mode;

    /// <summary>Returns the buffer and forgets it.</summary>
    public byte[] Release()
    {
        byte[] result = _buffer.AsSpan(0, _bufferSize).ToArray();
        _buffer = [];
        _bufferSize = 0;
        return result;
    }

    /// <summary>ReleaseSharedImmutableBackingStores.</summary>
    public List<BackingStore> ReleaseSharedImmutableBackingStores() => _sharedImmutableBackingStores;

    /// <summary>
    /// Marks an ArrayBuffer as having its contents transferred out of band.
    /// Pass the corresponding JSArrayBuffer in the deserializing context to
    /// ValueDeserializer.TransferArrayBuffer.
    /// </summary>
    public void TransferArrayBuffer(uint transferId, JSArrayBuffer arrayBuffer)
    {
        Debug.Assert(!_arrayBufferTransferMap.ContainsKey(arrayBuffer));
        Debug.Assert(!arrayBuffer.IsShared);
        _arrayBufferTransferMap[arrayBuffer] = transferId;
    }

    // ---- Raw writing -------------------------------------------------------------------

    void WriteTag(SerializationTag tag) => WriteByte((byte)tag);

    /// <summary>Writes an unsigned integer as a base-128 varint.</summary>
    void WriteVarint(ulong value)
    {
        Span<byte> stackBuffer = stackalloc byte[10];
        int next = 0;
        do
        {
            stackBuffer[next++] = (byte)((value & 0x7F) | 0x80);
            value >>= 7;
        } while (value != 0);
        stackBuffer[next - 1] &= 0x7F;
        WriteRawBytes(stackBuffer[..next]);
    }

    /// <summary>Writes a signed integer as a varint using ZigZag encoding.</summary>
    void WriteZigZag(int value) => WriteVarint((uint)((value << 1) ^ (value >> 31)));

    public void WriteDouble(double value)
    {
        // Warning: this uses host endianness (little-endian, as V8 on x64).
        Span<byte> bytes = stackalloc byte[8];
        BinaryPrimitives.WriteDoubleLittleEndian(bytes, value);
        WriteRawBytes(bytes);
    }

    void WriteOneByteString(ReadOnlySpan<char> chars)
    {
        WriteVarint((uint)chars.Length);
        Span<byte> dest = ReserveRawBytes(chars.Length);
        for (int i = 0; i < chars.Length; i++) dest[i] = (byte)chars[i];
    }

    void WriteTwoByteString(ReadOnlySpan<char> chars)
    {
        // Warning: this uses host endianness.
        WriteVarint((uint)(chars.Length * 2));
        Span<byte> dest = ReserveRawBytes(chars.Length * 2);
        for (int i = 0; i < chars.Length; i++) BinaryPrimitives.WriteUInt16LittleEndian(dest[(i * 2)..], chars[i]);
    }

    void WriteBigIntContents(BigInt bigint)
    {
        // BigInt::GetBitfieldForSerialization: sign bit, then the length in bytes.
        uint bytelength = (uint)(bigint.Length * BigInt.kDigitSize);
        uint bitfield = (bigint.Sign ? 1u : 0u) | (bytelength << 1);
        WriteVarint(bitfield);
        // BigInt::SerializeDigits (little-endian).
        Span<byte> dest = ReserveRawBytes((int)bytelength);
        ReadOnlySpan<ulong> digits = bigint.Digits;
        for (int i = 0; i < digits.Length; i++) BinaryPrimitives.WriteUInt64LittleEndian(dest[(i * 8)..], digits[i]);
    }

    public void WriteRawBytes(ReadOnlySpan<byte> source) => source.CopyTo(ReserveRawBytes(source.Length));

    Span<byte> ReserveRawBytes(int bytes)
    {
        int oldSize = _bufferSize;
        int newSize = oldSize + bytes;
        if (newSize > _buffer.Length) ExpandBuffer(newSize);
        _bufferSize = newSize;
        return _buffer.AsSpan(oldSize, bytes);
    }

    void ExpandBuffer(int requiredCapacity)
    {
        int requestedCapacity = Math.Max(requiredCapacity, _buffer.Length * 2) + 64;
        Array.Resize(ref _buffer, requestedCapacity);
    }

    public void WriteByte(byte value) => ReserveRawBytes(1)[0] = value;

    public void WriteUint32(uint value) => WriteVarint(value);

    public void WriteUint64(ulong value) => WriteVarint(value);

    // ---- Objects -----------------------------------------------------------------------

    /// <summary>Serializes a V8 object into the buffer. Throws on failure (DataCloneError).</summary>
    public void WriteObject(JSValue obj)
    {
        if (obj.IsSmi)
        {
            WriteSmi((int)obj.Number);
            return;
        }
        if (obj.IsNumber)
        {
            WriteHeapNumber(obj.Number);
            return;
        }
        switch (obj.HeapObjectOrNull)
        {
            case null:
                WriteTag(SerializationTag.kUndefined);
                return;
            case Oddball:
                WriteOddball(obj);
                return;
            case BigInt bigint:
                WriteTag(SerializationTag.kBigInt);
                WriteBigIntContents(bigint);
                return;
            case JSArrayBufferView view:
            {
                // Despite being JSReceivers, these have their wrapped buffer serialized
                // first. That makes this logic a little quirky, because it needs to
                // happen before we assign object IDs.
                if (!_idMap.ContainsKey(view) && !_treatArrayBufferViewsAsHostObjects)
                {
                    WriteJSReceiver(view.Buffer);
                }
                WriteJSReceiver(view);
                return;
            }
            case JSString s:
                WriteString(s);
                return;
            case JSReceiver receiver:
                WriteJSReceiver(receiver);
                return;
            default:
                ThrowDataCloneError(MessageTemplate.DataCloneError, obj);
                return;
        }
    }

    void WriteOddball(JSValue oddball)
    {
        SerializationTag tag;
        if (oddball.IsFalse) tag = SerializationTag.kFalse;
        else if (oddball.IsTrue) tag = SerializationTag.kTrue;
        else if (oddball.IsNull) tag = SerializationTag.kNull;
        else if (oddball.IsUndefined) tag = SerializationTag.kUndefined;
        else throw new InvalidOperationException("unreachable: oddball");
        WriteTag(tag);
    }

    void WriteSmi(int value)
    {
        WriteTag(SerializationTag.kInt32);
        WriteZigZag(value);
    }

    void WriteHeapNumber(double value)
    {
        WriteTag(SerializationTag.kDouble);
        WriteDouble(value);
    }

    void WriteString(JSString str)
    {
        ReadOnlySpan<char> chars = str.FlatSpan();
        bool oneByte = true;
        foreach (char c in chars)
        {
            if (c > 0xFF)
            {
                oneByte = false;
                break;
            }
        }
        if (oneByte)
        {
            WriteTag(SerializationTag.kOneByteString);
            WriteOneByteString(chars);
        }
        else
        {
            uint byteLength = (uint)(chars.Length * 2);
            // The existing reading code expects 16-byte strings to be aligned.
            if (((_bufferSize + 1 + BytesNeededForVarint(byteLength)) & 1) != 0)
            {
                WriteTag(SerializationTag.kPadding);
            }
            WriteTag(SerializationTag.kTwoByteString);
            WriteTwoByteString(chars);
        }
    }

    static int BytesNeededForVarint(ulong value)
    {
        int result = 0;
        do
        {
            result++;
            value >>= 7;
        } while (value != 0);
        return result;
    }

    void WriteJSReceiver(JSReceiver receiver)
    {
        // If the object has already been serialized, just write its ID.
        if (_idMap.TryGetValue(receiver, out uint existing))
        {
            WriteTag(SerializationTag.kObjectReference);
            WriteVarint(existing);
            return;
        }

        // Otherwise, allocate an ID for it.
        uint id = _nextId++;
        _idMap[receiver] = id;

        // Eliminate callable and exotic objects, which should not be serialized.
        InstanceType instanceType = receiver.Map.InstanceType;
        if (receiver.Map.IsCallable || receiver is JSProxy || instanceType is InstanceType.JSGlobalObjectType or
                InstanceType.JSGlobalProxyType or InstanceType.JSModuleNamespaceType)
        {
            ThrowDataCloneError(MessageTemplate.DataCloneError, receiver);
            return;
        }

        // If we are at the end of the stack, abort. This function may recurse.
        _isolate.StackGuard.StackCheck(_isolate);

        switch (instanceType)
        {
            case InstanceType.JSArrayType:
                WriteJSArray((JSArray)receiver);
                return;
            case InstanceType.JSArrayIteratorPrototypeType:
            case InstanceType.JSIteratorPrototypeType:
            case InstanceType.JSMapIteratorPrototypeType:
            case InstanceType.JSObjectPrototypeType:
            case InstanceType.JSObjectType:
            case InstanceType.JSPromisePrototypeType:
            case InstanceType.JSRegExpPrototypeType:
            case InstanceType.JSSetIteratorPrototypeType:
            case InstanceType.JSSetPrototypeType:
            case InstanceType.JSStringIteratorPrototypeType:
            case InstanceType.JSTypedArrayPrototypeType:
            case InstanceType.JSApiObjectType:
            {
                var jsObject = (JSObject)receiver;
                if (IsHostObject(jsObject)) WriteHostObject(jsObject);
                else WriteJSObject(jsObject);
                return;
            }
            case InstanceType.JSSpecialApiObjectType:
                WriteHostObject((JSObject)receiver);
                return;
            case InstanceType.JSDateType:
                WriteTag(SerializationTag.kDate);
                WriteDouble(((JSDate)receiver).Value);
                return;
            case InstanceType.JSPrimitiveWrapperType:
                WriteJSPrimitiveWrapper((JSPrimitiveWrapper)receiver);
                return;
            case InstanceType.JSRegExpType:
                WriteJSRegExp((JSRegExp)receiver);
                return;
            case InstanceType.JSMapType:
                WriteJSMap((JSMap)receiver);
                return;
            case InstanceType.JSSetType:
                WriteJSSet((JSSet)receiver);
                return;
            case InstanceType.JSArrayBufferType:
                WriteJSArrayBuffer((JSArrayBuffer)receiver);
                return;
            case InstanceType.JSTypedArrayType:
            case InstanceType.JSDataViewType:
            case InstanceType.JSRabGsabDataViewType:
                WriteJSArrayBufferView((JSArrayBufferView)receiver);
                return;
            case InstanceType.JSErrorType:
            {
                var jsError = (JSObject)receiver;
                if (IsHostObject(jsError)) WriteHostObject(jsError);
                else WriteJSError(jsError);
                return;
            }
        }
        ThrowDataCloneError(MessageTemplate.DataCloneError, receiver);
    }

    void WriteJSObject(JSObject obj)
    {
        bool canSerializeFast = obj.HasFastProperties && obj.Elements.Length == 0;
        if (!canSerializeFast)
        {
            WriteJSObjectSlow(obj);
            return;
        }

        Map map = obj.Map;
        WriteTag(SerializationTag.kBeginJSObject);

        // Write out fast properties as long as they are only data properties and the
        // map doesn't change.
        uint propertiesWritten = 0;
        bool mapChanged = false;
        DescriptorArray descriptors = map.InstanceDescriptors;
        int count = map.NumberOfOwnDescriptors;
        for (int i = 0; i < count; i++)
        {
            var index = new InternalIndex(i);
            Name key = descriptors.GetKey(index);
            if (key is not JSString keyString) continue;
            PropertyDetails details = descriptors.GetDetails(index);
            if (details.IsDontEnum) continue;

            JSValue value;
            if (!mapChanged) mapChanged = !ReferenceEquals(map, obj.Map);
            if (!mapChanged && details.Location == PropertyLocation.Field)
            {
                Debug.Assert(details.Kind == PropertyKind.Data);
                FieldIndex fieldIndex = FieldIndex.ForDetails(map, details);
                value = obj.RawFastPropertyAt(fieldIndex);
            }
            else
            {
                // This logic should essentially match WriteJSObjectPropertiesSlow.
                // If the property is no longer found, do not serialize it.
                // This could happen if a getter deleted the property.
                var it = new LookupIterator(_isolate, obj, key, LookupIterator.Configuration.OWN);
                if (!it.IsFound) continue;
                value = ObjectOps.GetProperty(ref it);
            }

            WriteObject(keyString);
            WriteObject(value);
            propertiesWritten++;
        }

        WriteTag(SerializationTag.kEndJSObject);
        WriteVarint(propertiesWritten);
    }

    void WriteJSObjectSlow(JSObject obj)
    {
        WriteTag(SerializationTag.kBeginJSObject);
        FixedArray keys = KeyAccumulator.GetKeys(_isolate, obj, KeyCollectionMode.OwnOnly, PropertyFilter.ENUMERABLE_STRINGS);
        uint propertiesWritten = WriteJSObjectPropertiesSlow(obj, keys);
        WriteTag(SerializationTag.kEndJSObject);
        WriteVarint(propertiesWritten);
    }

    void WriteJSArray(JSArray array)
    {
        ObjectOps.ToArrayLength(array.Length, out uint length);

        // To keep things simple, for now we decide between dense and sparse
        // serialization based on elements kind.
        ElementsKind kind = array.GetElementsKind();
        bool shouldSerializeDensely = ElementsKinds.IsFastElementsKind(kind) && !ElementsKinds.IsHoleyElementsKind(kind);

        if (shouldSerializeDensely)
        {
            WriteTag(SerializationTag.kBeginDenseJSArray);
            WriteVarint(length);
            uint i = 0;

            // Fast paths. Note that PACKED_ELEMENTS in particular can bail due to the
            // structure of the elements changing.
            switch (kind)
            {
                case ElementsKind.PACKED_SMI_ELEMENTS:
                {
                    var elements = (FixedArray)array.Elements;
                    for (i = 0; i < length; i++) WriteSmi((int)elements.Get((int)i).Number);
                    break;
                }
                case ElementsKind.PACKED_DOUBLE_ELEMENTS:
                {
                    // Elements are empty_fixed_array, not a FixedDoubleArray, if the array
                    // is empty. No elements to encode in this case anyhow.
                    if (length == 0) break;
                    var elements = (FixedDoubleArray)array.Elements;
                    for (i = 0; i < length; i++)
                    {
                        WriteTag(SerializationTag.kDouble);
                        WriteDouble(elements.GetScalar((int)i));
                    }
                    break;
                }
                case ElementsKind.PACKED_ELEMENTS:
                {
                    JSValue oldLength = array.Length;
                    for (; i < length; i++)
                    {
                        if (!array.Length.IsIdenticalTo(oldLength) || array.GetElementsKind() != ElementsKind.PACKED_ELEMENTS)
                        {
                            // Fall back to slow path.
                            break;
                        }
                        WriteObject(((FixedArray)array.Elements).Get((int)i));
                    }
                    break;
                }
            }

            // If there are elements remaining, serialize them slowly.
            for (; i < length; i++)
            {
                // Serializing the array's elements can have arbitrary side effects, so we
                // cannot rely on still having fast elements, even if it did to begin
                // with.
                var it = new LookupIterator(_isolate, array, i, array, LookupIterator.Configuration.OWN);
                if (!it.IsFound)
                {
                    // This can happen in the case where an array that was originally dense
                    // became sparse during serialization. It's too late to switch to the
                    // sparse format, but we can mark the elements as absent.
                    WriteTag(SerializationTag.kTheHole);
                    continue;
                }
                WriteObject(ObjectOps.GetProperty(ref it));
            }

            FixedArray keys = KeyAccumulator.GetKeys(_isolate, array, KeyCollectionMode.OwnOnly, PropertyFilter.ENUMERABLE_STRINGS,
                GetKeysConversion.KeepNumbers, false, true);
            uint propertiesWritten = WriteJSObjectPropertiesSlow(array, keys);
            WriteTag(SerializationTag.kEndDenseJSArray);
            WriteVarint(propertiesWritten);
            WriteVarint(length);
        }
        else
        {
            WriteTag(SerializationTag.kBeginSparseJSArray);
            WriteVarint(length);
            FixedArray keys = KeyAccumulator.GetKeys(_isolate, array, KeyCollectionMode.OwnOnly, PropertyFilter.ENUMERABLE_STRINGS);
            uint propertiesWritten = WriteJSObjectPropertiesSlow(array, keys);
            WriteTag(SerializationTag.kEndSparseJSArray);
            WriteVarint(propertiesWritten);
            WriteVarint(length);
        }
    }

    void WriteJSPrimitiveWrapper(JSPrimitiveWrapper value)
    {
        JSValue innerValue = value.Value;
        if (innerValue.IsTrue)
        {
            WriteTag(SerializationTag.kTrueObject);
        }
        else if (innerValue.IsFalse)
        {
            WriteTag(SerializationTag.kFalseObject);
        }
        else if (innerValue.IsNumber)
        {
            WriteTag(SerializationTag.kNumberObject);
            WriteDouble(innerValue.Number);
        }
        else if (innerValue.HeapObjectOrNull is BigInt bigint)
        {
            WriteTag(SerializationTag.kBigIntObject);
            WriteBigIntContents(bigint);
        }
        else if (innerValue.HeapObjectOrNull is JSString s)
        {
            WriteTag(SerializationTag.kStringObject);
            WriteString(s);
        }
        else
        {
            Debug.Assert(innerValue.IsSymbol);
            ThrowDataCloneError(MessageTemplate.DataCloneError, value);
        }
    }

    void WriteJSRegExp(JSRegExp regexp)
    {
        WriteTag(SerializationTag.kRegExp);
        WriteString(regexp.Source);
        WriteVarint((uint)regexp.Flags);
    }

    void WriteJSMap(JSMap jsMap)
    {
        // First copy the key-value pairs, since getters could mutate them.
        OrderedHashMap table = jsMap.Table;
        int length = table.NumberOfElements * 2;
        var entries = new JSValue[length];
        int resultIndex = 0;
        int used = table.UsedCapacity;
        for (int entry = 0; entry < used; entry++)
        {
            if (table.IsDeletedEntry(entry)) continue;
            entries[resultIndex++] = table.KeyAtRaw(entry);
            entries[resultIndex++] = table.ValueAtRaw(entry);
        }
        Debug.Assert(resultIndex == length);

        // Then write it out.
        WriteTag(SerializationTag.kBeginJSMap);
        for (int i = 0; i < length; i++) WriteObject(entries[i]);
        WriteTag(SerializationTag.kEndJSMap);
        WriteVarint((uint)length);
    }

    void WriteJSSet(JSSet jsSet)
    {
        // First copy the element pointers, since getters could mutate them.
        OrderedHashSet table = jsSet.Table;
        int length = table.NumberOfElements;
        var entries = new JSValue[length];
        int resultIndex = 0;
        int used = table.UsedCapacity;
        for (int entry = 0; entry < used; entry++)
        {
            if (table.IsDeletedEntry(entry)) continue;
            entries[resultIndex++] = table.KeyAtRaw(entry);
        }
        Debug.Assert(resultIndex == length);

        // Then write it out.
        WriteTag(SerializationTag.kBeginJSSet);
        for (int i = 0; i < length; i++) WriteObject(entries[i]);
        WriteTag(SerializationTag.kEndJSSet);
        WriteVarint((uint)length);
    }

    void WriteJSArrayBuffer(JSArrayBuffer arrayBuffer)
    {
        if (arrayBuffer.IsShared)
        {
            if (_delegate is null)
            {
                ThrowDataCloneError(MessageTemplate.DataCloneError, arrayBuffer);
                return;
            }
            uint index = _delegate.GetSharedArrayBufferId(_isolate, arrayBuffer);
            WriteTag(SerializationTag.kSharedArrayBuffer);
            WriteVarint(index);
            return;
        }

        if (_arrayBufferTransferMap.TryGetValue(arrayBuffer, out uint transferEntry))
        {
            WriteTag(SerializationTag.kArrayBufferTransfer);
            WriteVarint(transferEntry);
            return;
        }
        if (arrayBuffer.WasDetached)
        {
            ThrowDataCloneError(MessageTemplate.DataCloneErrorDetachedArrayBuffer);
            return;
        }
        ulong byteLength = arrayBuffer.ByteLength;
        if (byteLength > uint.MaxValue)
        {
            ThrowDataCloneError(MessageTemplate.DataCloneError, arrayBuffer);
            return;
        }

        if (arrayBuffer.IsResizableByJs)
        {
            WriteTag(SerializationTag.kResizableArrayBuffer);
            WriteVarint(byteLength);
            WriteVarint(arrayBuffer.MaxByteLength);
            WriteRawBytes(arrayBuffer.BackingStoreBuffer.AsSpan(0, (int)byteLength));
            return;
        }

        if (arrayBuffer.IsImmutable)
        {
            if (_shareImmutableArrayBuffers && _isolate.Flags.js_postmessage_share_immutable_arraybuffer)
            {
                BackingStore? backingStore = arrayBuffer.GetBackingStore();
                Debug.Assert(backingStore is not null || byteLength == 0);
                if (backingStore is not null)
                {
                    int id = _sharedImmutableBackingStores.IndexOf(backingStore);
                    if (id < 0)
                    {
                        id = _sharedImmutableBackingStores.Count;
                        _sharedImmutableBackingStores.Add(backingStore);
                    }
                    WriteTag(SerializationTag.kSharedImmutableArrayBuffer);
                    WriteVarint((uint)id);
                    return;
                }
            }
            WriteTag(SerializationTag.kImmutableArrayBuffer);
        }
        else
        {
            WriteTag(SerializationTag.kArrayBuffer);
        }
        WriteVarint(byteLength);
        WriteRawBytes(arrayBuffer.BackingStoreBuffer.AsSpan(0, (int)byteLength));
    }

    void WriteJSArrayBufferView(JSArrayBufferView view)
    {
        if (_treatArrayBufferViewsAsHostObjects)
        {
            WriteHostObject(view);
            return;
        }
        WriteTag(SerializationTag.kArrayBufferView);
        byte tag;
        if (view is JSTypedArray typedArray)
        {
            if (typedArray.IsOutOfBounds)
            {
                ThrowDataCloneError(MessageTemplate.DataCloneError, view);
                return;
            }
            tag = typedArray.Type switch
            {
                ExternalArrayType.kExternalInt8Array => (byte)'b',
                ExternalArrayType.kExternalUint8Array => (byte)'B',
                ExternalArrayType.kExternalUint8ClampedArray => (byte)'C',
                ExternalArrayType.kExternalInt16Array => (byte)'w',
                ExternalArrayType.kExternalUint16Array => (byte)'W',
                ExternalArrayType.kExternalInt32Array => (byte)'d',
                ExternalArrayType.kExternalUint32Array => (byte)'D',
                ExternalArrayType.kExternalFloat16Array => (byte)'h',
                ExternalArrayType.kExternalFloat32Array => (byte)'f',
                ExternalArrayType.kExternalFloat64Array => (byte)'F',
                ExternalArrayType.kExternalBigInt64Array => (byte)'q',
                ExternalArrayType.kExternalBigUint64Array => (byte)'Q',
                _ => throw new InvalidOperationException("unreachable: typed array type"),
            };
        }
        else
        {
            var dataView = (JSDataView)view;
            if (dataView.IsRabGsab && dataView.IsOutOfBounds)
            {
                ThrowDataCloneError(MessageTemplate.DataCloneError, view);
                return;
            }
            tag = (byte)'?';
        }
        WriteVarint(tag);
        WriteVarint(view.ByteOffset);
        WriteVarint(view.RawByteLength);
        // JSArrayBufferViewIsLengthTracking (bit 0), JSArrayBufferViewIsBackedByRab (bit 1).
        uint flags = (view.IsLengthTracking ? 1u : 0u) | (view.IsBackedByRab ? 2u : 0u);
        WriteVarint(flags);
    }

    void WriteJSError(JSObject error)
    {
        var messageDesc = new PropertyDescriptor();
        bool messageFound = JSReceiver.GetOwnPropertyDescriptor(_isolate, error, ReadOnlyRoots.message_string, ref messageDesc);
        var causeDesc = new PropertyDescriptor();
        bool causeFound = JSReceiver.GetOwnPropertyDescriptor(_isolate, error, ReadOnlyRoots.cause_string, ref causeDesc);

        WriteTag(SerializationTag.kError);

        JSValue nameObject = ObjectOps.GetProperty(_isolate, error, _isolate.Factory.InternalizeString("name"));
        string name = ObjectOps.ToString(_isolate, nameObject).ToString();

        switch (name)
        {
            case "EvalError": WriteVarint((byte)'E'); break;
            case "RangeError": WriteVarint((byte)'R'); break;
            case "ReferenceError": WriteVarint((byte)'F'); break;
            case "SyntaxError": WriteVarint((byte)'S'); break;
            case "TypeError": WriteVarint((byte)'T'); break;
            case "URIError": WriteVarint((byte)'U'); break;
            default:
                // The default prototype in the deserialization side is Error.prototype, so
                // we don't have to do anything here.
                break;
        }

        if (messageFound && PropertyDescriptor.IsDataDescriptor(messageDesc))
        {
            JSString message = ObjectOps.ToString(_isolate, messageDesc.Value);
            WriteVarint((byte)'m');
            WriteString(message);
        }

        JSValue stack = ObjectOps.GetProperty(_isolate, error, ReadOnlyRoots.stack_string);
        if (stack.HeapObjectOrNull is JSString stackString)
        {
            WriteVarint((byte)'s');
            WriteString(stackString);
        }

        // The {cause} can self-reference the error. We add at the end, so that we can
        // create the Error first when deserializing.
        if (causeFound && PropertyDescriptor.IsDataDescriptor(causeDesc))
        {
            WriteVarint((byte)'c');
            WriteObject(causeDesc.Value);
        }

        WriteVarint((byte)'.');
    }

    void WriteHostObject(JSObject obj)
    {
        WriteTag(SerializationTag.kHostObject);
        if (_delegate is null)
        {
            _isolate.Throw(_isolate.Factory.NewError(_isolate.NativeContext.ErrorFunction, MessageTemplate.DataCloneError, [obj]));
            return;
        }
        _delegate.WriteHostObject(_isolate, obj, this);
    }

    /// <summary>
    /// Reads the specified keys from the object and writes key-value pairs to the
    /// buffer. Returns the number of keys actually written, which may be smaller
    /// if some keys are not own properties when accessed.
    /// </summary>
    uint WriteJSObjectPropertiesSlow(JSObject obj, FixedArray keys)
    {
        uint propertiesWritten = 0;
        int length = keys.Length;
        for (int i = 0; i < length; i++)
        {
            JSValue key = keys.Get(i);
            var lookupKey = new PropertyKey(_isolate, key);
            var it = new LookupIterator(_isolate, obj, lookupKey, LookupIterator.Configuration.OWN);
            JSValue value = ObjectOps.GetProperty(ref it);

            // If the property is no longer found, do not serialize it.
            // This could happen if a getter deleted the property.
            if (!it.IsFound) continue;

            WriteObject(key);
            WriteObject(value);
            propertiesWritten++;
        }
        return propertiesWritten;
    }

    /// <summary>IsHostObject: objects with embedder fields, or what the delegate says.</summary>
    bool IsHostObject(JSObject jsObject)
    {
        // V8Sharp objects have no embedder fields (no embedder API).
        if (!_hasCustomHostObjects) return false;
        return _delegate!.IsHostObject(_isolate, jsObject);
    }

    /// <summary>ThrowDataCloneError: the message goes to the delegate (or an Error is thrown).</summary>
    void ThrowDataCloneError(MessageTemplate index) => ThrowDataCloneError(index, ReadOnlyRoots.empty_string);

    void ThrowDataCloneError(MessageTemplate index, JSValue arg0)
    {
        JSString message = MessageFormatter.Format(_isolate, index, [arg0]);
        if (_delegate is not null)
        {
            _delegate.ThrowDataCloneError(_isolate, message);
        }
        else
        {
            _isolate.Throw(_isolate.Factory.NewError(_isolate.NativeContext.ErrorFunction, message));
        }
        // The delegate must throw.
        throw new InvalidOperationException("ValueSerializerDelegate.ThrowDataCloneError returned");
    }
}

/// <summary>
/// ValueDeserializer: deserializes values from data written with
/// ValueSerializer, or a compatible implementation. Read methods return null
/// for invalid data; <see cref="ReadValue"/> turns that into V8's
/// DataCloneDeserializationError.
/// </summary>
public sealed class ValueDeserializer
{
    readonly Isolate _isolate;
    readonly ValueDeserializerDelegate? _delegate;
    readonly byte[] _data;
    int _position;
    readonly int _end;
    uint _version;
    uint _nextId;
    bool _version13BrokenDataMode;
    bool _suppressDeserializationErrors;
    bool _supportsLegacyWireFormat;

    JSReceiver?[] _idMap = [];
    Dictionary<uint, JSArrayBuffer>? _arrayBufferTransferMap;
    List<BackingStore> _sharedImmutableBackingStores = [];

    public ValueDeserializer(Isolate isolate, byte[] data, ValueDeserializerDelegate? @delegate = null)
    {
        _isolate = isolate;
        _delegate = @delegate;
        _data = data;
        _position = 0;
        _end = data.Length;
    }

    /// <summary>v8::ValueDeserializer::SetSupportsLegacyWireFormat.</summary>
    public void SetSupportsLegacyWireFormat(bool supportsLegacyWireFormat) => _supportsLegacyWireFormat = supportsLegacyWireFormat;

    public void SetSharedImmutableBackingStores(List<BackingStore> stores) => _sharedImmutableBackingStores = stores;

    /// <summary>The wire format version (after ReadHeader).</summary>
    public uint GetWireFormatVersion() => _version;

    /// <summary>
    /// v8::ValueDeserializer::ReadHeader: runs version detection logic, and
    /// rejects the legacy format unless it is supported.
    /// </summary>
    public void ReadHeader()
    {
        ReadHeaderInternal();
        bool readHeader = true;
        // Detect the legacy wire format (version 0) and reject it unless supported.
        if (!_supportsLegacyWireFormat && GetWireFormatVersion() == 0) readHeader = false;
        if (!readHeader)
        {
            _isolate.Throw(_isolate.Factory.NewError(_isolate.NativeContext.ErrorFunction,
                MessageTemplate.DataCloneDeserializationVersionError, ReadOnlySpan<JSValue>.Empty));
        }
    }

    /// <summary>ValueDeserializer::ReadHeader.</summary>
    void ReadHeaderInternal()
    {
        if (_position < _end && _data[_position] == (byte)SerializationTag.kVersion)
        {
            ReadTag();
            ulong? version = ReadVarintLoop(32);
            if (version is null || version.Value > ValueSerializer.kLatestVersion)
            {
                _isolate.Throw(_isolate.Factory.NewError(_isolate.NativeContext.ErrorFunction,
                    MessageTemplate.DataCloneDeserializationVersionError, ReadOnlySpan<JSValue>.Empty));
            }
            _version = (uint)version!.Value;
        }
    }

    /// <summary>v8::ValueDeserializer::ReadValue.</summary>
    public JSValue ReadValue()
    {
        if (GetWireFormatVersion() > 0) return ReadObjectWrapper();
        return ReadObjectUsingEntireBufferForLegacyFormat();
    }

    /// <summary>Accepts the array buffer corresponding to the one passed previously to ValueSerializer.TransferArrayBuffer.</summary>
    public void TransferArrayBuffer(uint transferId, JSArrayBuffer arrayBuffer)
    {
        _arrayBufferTransferMap ??= [];
        _arrayBufferTransferMap[transferId] = arrayBuffer;
    }

    // ---- Raw reading -------------------------------------------------------------------

    SerializationTag? PeekTag()
    {
        int peekPosition = _position;
        SerializationTag tag;
        do
        {
            if (peekPosition >= _end) return null;
            tag = (SerializationTag)_data[peekPosition];
            peekPosition++;
        } while (tag == SerializationTag.kPadding);
        return tag;
    }

    void ConsumeTag(SerializationTag peekedTag)
    {
        SerializationTag? actualTag = ReadTag();
        Debug.Assert(actualTag == peekedTag);
    }

    SerializationTag? ReadTag()
    {
        SerializationTag tag;
        do
        {
            if (_position >= _end) return null;
            tag = (SerializationTag)_data[_position];
            _position++;
        } while (tag == SerializationTag.kPadding);
        return tag;
    }

    /// <summary>ReadVarintLoop&lt;T&gt; for a T of <paramref name="bits"/> bits.</summary>
    ulong? ReadVarintLoop(int bits)
    {
        ulong value = 0;
        int shift = 0;
        bool hasAnotherByte;
        do
        {
            if (_position >= _end) return null;
            byte b = _data[_position];
            hasAnotherByte = (b & 0x80) != 0;
            if (shift < bits)
            {
                value |= (ulong)(b & 0x7F) << shift;
                shift += 7;
            }
            else
            {
                // For consistency with the fast unrolled loop in ReadVarint we return
                // after we have read size(T) + 1 bytes.
                return bits == 64 ? value : value & ((1UL << bits) - 1);
            }
            _position++;
        } while (hasAnotherByte);
        return bits == 64 ? value : value & ((1UL << bits) - 1);
    }

    uint? ReadVarint32() => ReadVarintLoop(32) is { } v ? (uint)v : null;

    byte? ReadVarint8() => ReadVarintLoop(8) is { } v ? (byte)v : null;

    int? ReadZigZag32()
    {
        if (ReadVarint32() is not { } unsignedValue) return null;
        return (int)(unsignedValue >> 1) ^ -(int)(unsignedValue & 1);
    }

    double? ReadDoubleInternal()
    {
        // Warning: this uses host endianness.
        if (8 > _end - _position) return null;
        double value = BinaryPrimitives.ReadDoubleLittleEndian(_data.AsSpan(_position, 8));
        _position += 8;
        if (double.IsNaN(value)) value = JSValue.QuietNaN;
        return value;
    }

    bool ReadRawBytesInternal(ulong size, out ReadOnlySpan<byte> bytes)
    {
        if (size > (ulong)(_end - _position))
        {
            bytes = default;
            return false;
        }
        bytes = _data.AsSpan(_position, (int)size);
        _position += (int)size;
        return true;
    }

    public bool ReadUint32(out uint value)
    {
        uint? v = ReadVarint32();
        value = v ?? 0;
        return v.HasValue;
    }

    public bool ReadUint64(out ulong value)
    {
        ulong? v = ReadVarintLoop(64);
        value = v ?? 0;
        return v.HasValue;
    }

    public bool ReadSizeT(out ulong value) => ReadUint64(out value);

    public bool ReadDouble(out double value)
    {
        double? v = ReadDoubleInternal();
        value = v ?? 0;
        return v.HasValue;
    }

    public bool ReadByte(out byte value)
    {
        if (_end - _position < 1)
        {
            value = 0;
            return false;
        }
        value = _data[_position++];
        return true;
    }

    public bool ReadRawBytes(int length, out ReadOnlySpan<byte> data) => ReadRawBytesInternal((ulong)length, out data);

    // ---- Objects -----------------------------------------------------------------------

    /// <summary>ReadObjectWrapper: reads an object; throws DataCloneDeserializationError for invalid data.</summary>
    JSValue ReadObjectWrapper()
    {
        // We had a bug which produced invalid version 13 data (see
        // crbug.com/1284506). This compatibility mode tries to first read the data
        // normally, and if it fails, and the version is 13, tries to read the broken
        // format.
        int originalPosition = _position;
        _suppressDeserializationErrors = true;
        JSValue? result = ReadObject();

        if (result is null && _version == 13)
        {
            _version13BrokenDataMode = true;
            _position = originalPosition;
            result = ReadObject();
        }

        if (result is null) ThrowDeserializationError();
        return result!.Value;
    }

    void ThrowDeserializationError() =>
        _isolate.Throw(_isolate.Factory.NewError(_isolate.NativeContext.ErrorFunction, MessageTemplate.DataCloneDeserializationError,
            ReadOnlySpan<JSValue>.Empty));

    JSValue? ReadObject()
    {
        // If we are at the end of the stack, abort. This function may recurse.
        _isolate.StackGuard.StackCheck(_isolate);

        JSValue? result = ReadObjectInternal();

        // ArrayBufferView is special in that it consumes the value before it, even
        // after format version 0.
        if (result is { } obj && obj.HeapObjectOrNull is JSArrayBuffer buffer &&
            PeekTag() == SerializationTag.kArrayBufferView)
        {
            ConsumeTag(SerializationTag.kArrayBufferView);
            result = ReadJSArrayBufferView(buffer);
        }

        if (result is null && !_suppressDeserializationErrors) ThrowDeserializationError();
        return result;
    }

    JSValue? ReadObjectInternal()
    {
        if (ReadTag() is not { } tag) return null;
        switch (tag)
        {
            case SerializationTag.kVerifyObjectCount:
                // Read the count and ignore it.
                if (ReadVarint32() is null) return null;
                return ReadObject();
            case SerializationTag.kUndefined:
                return JSValue.Undefined;
            case SerializationTag.kNull:
                return JSValue.Null;
            case SerializationTag.kTrue:
                return JSValue.True;
            case SerializationTag.kFalse:
                return JSValue.False;
            case SerializationTag.kInt32:
                return ReadZigZag32() is { } i ? JSValue.FromInt(i) : (JSValue?)null;
            case SerializationTag.kUint32:
                return ReadVarint32() is { } u ? JSValue.FromNumber(u) : (JSValue?)null;
            case SerializationTag.kDouble:
                return ReadDoubleInternal() is { } d ? JSValue.FromNumber(d) : (JSValue?)null;
            case SerializationTag.kBigInt:
                return ReadBigInt();
            case SerializationTag.kUtf8String:
                return ReadUtf8String();
            case SerializationTag.kOneByteString:
                return ReadOneByteString();
            case SerializationTag.kTwoByteString:
                return ReadTwoByteString();
            case SerializationTag.kObjectReference:
            {
                if (ReadVarint32() is not { } id) return null;
                return GetObjectWithID(id);
            }
            case SerializationTag.kBeginJSObject:
                return ReadJSObject();
            case SerializationTag.kBeginSparseJSArray:
                return ReadSparseJSArray();
            case SerializationTag.kBeginDenseJSArray:
                return ReadDenseJSArray();
            case SerializationTag.kDate:
                return ReadJSDate();
            case SerializationTag.kTrueObject:
            case SerializationTag.kFalseObject:
            case SerializationTag.kNumberObject:
            case SerializationTag.kBigIntObject:
            case SerializationTag.kStringObject:
                return ReadJSPrimitiveWrapper(tag);
            case SerializationTag.kRegExp:
                return ReadJSRegExp();
            case SerializationTag.kBeginJSMap:
                return ReadJSMap();
            case SerializationTag.kBeginJSSet:
                return ReadJSSet();
            case SerializationTag.kArrayBuffer:
                return ReadJSArrayBuffer(false, false, false);
            case SerializationTag.kResizableArrayBuffer:
                return ReadJSArrayBuffer(false, true, false);
            case SerializationTag.kImmutableArrayBuffer:
                return ReadJSArrayBuffer(false, false, true);
            case SerializationTag.kSharedImmutableArrayBuffer:
                return ReadSharedImmutableJSArrayBuffer();
            case SerializationTag.kArrayBufferTransfer:
                return ReadTransferredJSArrayBuffer();
            case SerializationTag.kSharedArrayBuffer:
                return ReadJSArrayBuffer(true, false, false);
            case SerializationTag.kError:
                return ReadJSError();
            case SerializationTag.kHostObject:
                return ReadHostObject();
            default:
                // Before there was an explicit tag for host objects, all unknown tags
                // were delegated to the host.
                if (_version < 13)
                {
                    _position--;
                    return ReadHostObject();
                }
                return null;
        }
    }

    JSValue? ReadString()
    {
        if (_version < 12) return ReadUtf8String();
        if (ReadObject() is not { } obj || !obj.IsString) return null;
        return obj;
    }

    JSValue? ReadBigInt()
    {
        if (ReadVarint32() is not { } bitfield) return null;
        // BigInt::DigitsByteLengthForBitfield.
        uint bytelength = bitfield >> 1;
        if (!ReadRawBytesInternal(bytelength, out ReadOnlySpan<byte> digitsStorage)) return null;
        // BigInt::FromSerializedDigits.
        bool sign = (bitfield & 1) != 0;
        int length = (int)((bytelength + BigInt.kDigitSize - 1) / BigInt.kDigitSize);  // Round up.
        // There is no -0n. Reject corrupted serialized data.
        if (length == 0 && sign) return null;
        if (length > BigInt.kMaxLength) return null;
        var digits = new ulong[length];
        Span<byte> padded = new byte[length * BigInt.kDigitSize];
        digitsStorage.CopyTo(padded);
        for (int i = 0; i < length; i++) digits[i] = BinaryPrimitives.ReadUInt64LittleEndian(padded[(i * 8)..]);
        // MutableBigInt::MakeImmutable: trim leading zero digits.
        int used = length;
        while (used > 0 && digits[used - 1] == 0) used--;
        if (used != length) Array.Resize(ref digits, used);
        return new BigInt(sign, digits);
    }

    JSValue? ReadUtf8String()
    {
        if (ReadVarint32() is not { } utf8Length) return null;
        if (!ReadRawBytesInternal(utf8Length, out ReadOnlySpan<byte> utf8Bytes)) return null;
        return _isolate.Factory.NewStringFromUtf16(System.Text.Encoding.UTF8.GetString(utf8Bytes));
    }

    JSValue? ReadOneByteString()
    {
        if (ReadVarint32() is not { } byteLength) return null;
        if (!ReadRawBytesInternal(byteLength, out ReadOnlySpan<byte> bytes)) return null;
        return _isolate.Factory.NewStringFromUtf16(System.Text.Encoding.Latin1.GetString(bytes));
    }

    JSValue? ReadTwoByteString()
    {
        if (ReadVarint32() is not { } byteLength) return null;
        if (byteLength % 2 != 0 || !ReadRawBytesInternal(byteLength, out ReadOnlySpan<byte> bytes)) return null;
        if (byteLength == 0) return ReadOnlyRoots.empty_string;
        var chars = new char[byteLength / 2];
        for (int i = 0; i < chars.Length; i++) chars[i] = (char)BinaryPrimitives.ReadUInt16LittleEndian(bytes[(i * 2)..]);
        return _isolate.Factory.NewStringFromUtf16(chars);
    }

    JSValue? ReadJSObject()
    {
        // If we are at the end of the stack, abort. This function may recurse.
        _isolate.StackGuard.StackCheck(_isolate);

        uint id = _nextId++;
        JSObject obj = _isolate.Factory.NewJSObject(_isolate.NativeContext.ObjectFunction);
        AddObjectWithID(id, obj);

        if (ReadJSObjectProperties(obj, SerializationTag.kEndJSObject) is not { } numProperties ||
            ReadVarint32() is not { } expectedNumProperties || numProperties != expectedNumProperties)
        {
            return null;
        }
        return obj;
    }

    JSValue? ReadSparseJSArray()
    {
        // If we are at the end of the stack, abort. This function may recurse.
        _isolate.StackGuard.StackCheck(_isolate);

        if (ReadVarint32() is not { } length) return null;

        uint id = _nextId++;
        JSArray array = _isolate.Factory.NewJSArray(ElementsKind.TERMINAL_FAST_ELEMENTS_KIND, 0, 0);
        JSArray.SetLength(_isolate, array, length);
        AddObjectWithID(id, array);

        if (ReadJSObjectProperties(array, SerializationTag.kEndSparseJSArray) is not { } numProperties ||
            ReadVarint32() is not { } expectedNumProperties || ReadVarint32() is not { } expectedLength ||
            numProperties != expectedNumProperties || length != expectedLength)
        {
            return null;
        }
        return array;
    }

    JSValue? ReadDenseJSArray()
    {
        // If we are at the end of the stack, abort. This function may recurse.
        _isolate.StackGuard.StackCheck(_isolate);

        // We shouldn't permit an array larger than the biggest we can request from
        // V8. As an additional sanity check, since each entry will take at least one
        // byte to encode, if there are fewer bytes than that we can also fail fast.
        if (ReadVarint32() is not { } length || length > FixedArrayBase.kMaxLength || length > (uint)(_end - _position))
        {
            return null;
        }

        uint id = _nextId++;
        JSArray array = _isolate.Factory.NewJSArray(ElementsKind.HOLEY_ELEMENTS, (int)length, (int)length,
            Factory.ArrayStorageAllocationMode.INITIALIZE_ARRAY_ELEMENTS_WITH_HOLE);
        AddObjectWithID(id, array);

        var elements = (FixedArray)array.Elements;
        int elementsLength = elements.Length;
        for (uint i = 0; i < length; i++)
        {
            if (PeekTag() == SerializationTag.kTheHole)
            {
                ConsumeTag(SerializationTag.kTheHole);
                continue;
            }

            if (ReadObject() is not { } element) return null;

            // Serialization versions less than 11 encode the hole the same as
            // undefined. For consistency with previous behavior, store these as the
            // hole. Past version 11, undefined means undefined.
            if (_version < 11 && element.IsUndefined) continue;

            // Safety check.
            if (i >= elementsLength) return null;

            elements.Set((int)i, element);
        }

        if (ReadJSObjectProperties(array, SerializationTag.kEndDenseJSArray) is not { } numProperties ||
            ReadVarint32() is not { } expectedNumProperties || ReadVarint32() is not { } expectedLength ||
            numProperties != expectedNumProperties || length != expectedLength)
        {
            return null;
        }
        return array;
    }

    JSValue? ReadJSDate()
    {
        if (ReadDoubleInternal() is not { } value) return null;
        uint id = _nextId++;
        JSFunction dateFunction = _isolate.NativeContext.DateFunction;
        JSDate date = JSDate.New(_isolate, dateFunction, dateFunction, value);
        AddObjectWithID(id, date);
        return date;
    }

    JSValue? ReadJSPrimitiveWrapper(SerializationTag tag)
    {
        uint id = _nextId++;
        NativeContext nativeContext = _isolate.NativeContext;
        JSPrimitiveWrapper value;
        switch (tag)
        {
            case SerializationTag.kTrueObject:
                value = (JSPrimitiveWrapper)_isolate.Factory.NewJSObject(nativeContext.BooleanFunction);
                value.Value = JSValue.True;
                break;
            case SerializationTag.kFalseObject:
                value = (JSPrimitiveWrapper)_isolate.Factory.NewJSObject(nativeContext.BooleanFunction);
                value.Value = JSValue.False;
                break;
            case SerializationTag.kNumberObject:
            {
                if (ReadDoubleInternal() is not { } number) return null;
                value = (JSPrimitiveWrapper)_isolate.Factory.NewJSObject(nativeContext.NumberFunction);
                value.Value = JSValue.FromNumber(number);
                break;
            }
            case SerializationTag.kBigIntObject:
            {
                if (ReadBigInt() is not { } bigint) return null;
                value = (JSPrimitiveWrapper)_isolate.Factory.NewJSObject(nativeContext.BigIntFunction);
                value.Value = bigint;
                break;
            }
            case SerializationTag.kStringObject:
            {
                if (ReadString() is not { } str) return null;
                value = (JSPrimitiveWrapper)ObjectOps.ToObject(_isolate, str);
                break;
            }
            default:
                throw new InvalidOperationException("unreachable: primitive wrapper tag");
        }
        AddObjectWithID(id, value);
        return value;
    }

    JSValue? ReadJSRegExp()
    {
        uint id = _nextId++;
        if (ReadString() is not { } pattern || ReadVarint32() is not { } rawFlags) return null;

        // Ensure the deserialized flags are valid.
        uint badFlagsMask = uint.MaxValue << JSRegExp.kFlagCount;
        // kLinear is accepted only with the appropriate flag.
        if (!_isolate.Flags.enable_experimental_regexp_engine) badFlagsMask |= (uint)RegExpFlags.Linear;
        if ((rawFlags & badFlagsMask) != 0 || !RegExpEngine.VerifyFlags((RegExpFlags)rawFlags)) return null;
        JSRegExp regexp;
        try
        {
            regexp = JSRegExp.New(_isolate, pattern.As<JSString>(), (RegExpFlags)rawFlags);
        }
        catch (JavaScriptException)
        {
            return null;
        }
        AddObjectWithID(id, regexp);
        return regexp;
    }

    JSValue? ReadJSMap()
    {
        // If we are at the end of the stack, abort. This function may recurse.
        _isolate.StackGuard.StackCheck(_isolate);

        uint id = _nextId++;
        NativeContext nativeContext = _isolate.NativeContext;
        JSObject map = _isolate.Factory.NewJSObject(nativeContext.JSMapFun);
        AddObjectWithID(id, map);

        JSFunction mapSet = nativeContext.MapSet;
        uint length = 0;
        while (true)
        {
            if (PeekTag() is not { } tag) return null;
            if (tag == SerializationTag.kEndJSMap)
            {
                ConsumeTag(SerializationTag.kEndJSMap);
                break;
            }

            if (ReadObject() is not { } key || ReadObject() is not { } value) return null;
            Execution.Call(_isolate, mapSet, map, [key, value]);
            length += 2;
        }

        if (ReadVarint32() is not { } expectedLength || length != expectedLength) return null;
        return map;
    }

    JSValue? ReadJSSet()
    {
        // If we are at the end of the stack, abort. This function may recurse.
        _isolate.StackGuard.StackCheck(_isolate);

        uint id = _nextId++;
        NativeContext nativeContext = _isolate.NativeContext;
        JSObject set = _isolate.Factory.NewJSObject(nativeContext.JSSetFun);
        AddObjectWithID(id, set);
        JSFunction setAdd = nativeContext.SetAdd;
        uint length = 0;
        while (true)
        {
            if (PeekTag() is not { } tag) return null;
            if (tag == SerializationTag.kEndJSSet)
            {
                ConsumeTag(SerializationTag.kEndJSSet);
                break;
            }

            if (ReadObject() is not { } value) return null;
            Execution.Call(_isolate, setAdd, set, [value]);
            length++;
        }

        if (ReadVarint32() is not { } expectedLength || length != expectedLength) return null;
        return set;
    }

    JSValue? ReadJSArrayBuffer(bool isShared, bool isResizable, bool isImmutable)
    {
        uint id = _nextId++;
        if (isShared)
        {
            if (ReadVarint32() is not { } cloneId || _delegate is null) return null;
            JSArrayBuffer? sab = _delegate.GetSharedArrayBufferFromId(_isolate, cloneId);
            if (sab is null) return null;
            Debug.Assert(sab.IsShared);
            AddObjectWithID(id, sab);
            return sab;
        }
        if (!ReadSizeT(out ulong byteLength)) return null;
        ulong maxByteLength = byteLength;
        if (isResizable)
        {
            if (!ReadSizeT(out maxByteLength)) return null;
            if (byteLength > maxByteLength) return null;
        }
        if (byteLength > (ulong)(_end - _position)) return null;
        JSArrayBuffer? arrayBuffer = _isolate.Factory.NewJSArrayBufferAndBackingStore(byteLength, maxByteLength, false, isResizable);
        if (arrayBuffer is null)
        {
            _isolate.ThrowRangeError(MessageTemplate.ArrayBufferAllocationFailed);
            return null;
        }

        if (isImmutable) arrayBuffer.MakeImmutable(_isolate);

        if (byteLength > 0) _data.AsSpan(_position, (int)byteLength).CopyTo(arrayBuffer.BackingStoreBuffer);
        _position += (int)byteLength;
        AddObjectWithID(id, arrayBuffer);
        return arrayBuffer;
    }

    JSValue? ReadTransferredJSArrayBuffer()
    {
        uint id = _nextId++;
        if (ReadVarint32() is not { } transferId || _arrayBufferTransferMap is null) return null;
        if (!_arrayBufferTransferMap.TryGetValue(transferId, out JSArrayBuffer? arrayBuffer)) return null;
        AddObjectWithID(id, arrayBuffer);
        return arrayBuffer;
    }

    JSValue? ReadSharedImmutableJSArrayBuffer()
    {
        uint id = _nextId++;
        if (ReadVarint32() is not { } backingStoreId) return null;
        if (backingStoreId >= _sharedImmutableBackingStores.Count) return null;
        BackingStore backingStore = _sharedImmutableBackingStores[(int)backingStoreId];
        if (backingStore.IsResizableByJs) throw new InvalidOperationException("CHECK failed: shared immutable backing store is resizable");
        JSArrayBuffer arrayBuffer = _isolate.Factory.NewJSArrayBuffer(backingStore);
        arrayBuffer.MakeImmutable(_isolate);
        AddObjectWithID(id, arrayBuffer);
        return arrayBuffer;
    }

    JSValue? ReadJSArrayBufferView(JSArrayBuffer buffer)
    {
        ulong bufferByteLength = buffer.GetByteLength();
        uint flags = 0;
        if (ReadVarint8() is not { } tag || !ReadSizeT(out ulong byteOffset) || !ReadSizeT(out ulong byteLength) ||
            byteOffset > bufferByteLength || byteLength > bufferByteLength - byteOffset)
        {
            return null;
        }
        bool shouldReadFlags = _version >= 14 || _version13BrokenDataMode;
        if (shouldReadFlags)
        {
            if (ReadVarint32() is not { } f) return null;
            flags = f;
        }
        uint id = _nextId++;
        ElementsKind kind;
        switch ((char)tag)
        {
            case '?':
            {
                if (!ValidateJSArrayBufferViewFlags(buffer, flags, out bool lengthTracking, out bool backedByRab)) return null;
                JSDataView dataView = _isolate.Factory.NewJSDataViewOrRabGsabDataView(buffer, byteOffset, byteLength, lengthTracking);
                if (backedByRab != dataView.IsBackedByRab || lengthTracking != dataView.IsLengthTracking)
                {
                    throw new InvalidOperationException("CHECK failed: data view flags");
                }
                AddObjectWithID(id, dataView);
                return dataView;
            }
            case 'b': kind = ElementsKind.INT8_ELEMENTS; break;
            case 'B': kind = ElementsKind.UINT8_ELEMENTS; break;
            case 'C': kind = ElementsKind.UINT8_CLAMPED_ELEMENTS; break;
            case 'w': kind = ElementsKind.INT16_ELEMENTS; break;
            case 'W': kind = ElementsKind.UINT16_ELEMENTS; break;
            case 'd': kind = ElementsKind.INT32_ELEMENTS; break;
            case 'D': kind = ElementsKind.UINT32_ELEMENTS; break;
            case 'h': kind = ElementsKind.FLOAT16_ELEMENTS; break;
            case 'f': kind = ElementsKind.FLOAT32_ELEMENTS; break;
            case 'F': kind = ElementsKind.FLOAT64_ELEMENTS; break;
            case 'q': kind = ElementsKind.BIGINT64_ELEMENTS; break;
            case 'Q': kind = ElementsKind.BIGUINT64_ELEMENTS; break;
            default: return null;
        }
        uint elementSize = (uint)ElementsKinds.ElementsKindToByteSize(kind);
        if (byteOffset % elementSize != 0 || byteLength % elementSize != 0) return null;
        if (!ValidateJSArrayBufferViewFlags(buffer, flags, out bool isLengthTracking, out bool isBackedByRab)) return null;
        JSTypedArray typedArray = _isolate.Factory.NewJSTypedArray(kind, buffer, byteOffset, byteLength / elementSize, isLengthTracking);
        if (isLengthTracking != typedArray.IsLengthTracking || isBackedByRab != typedArray.IsBackedByRab)
        {
            throw new InvalidOperationException("CHECK failed: typed array flags");
        }
        AddObjectWithID(id, typedArray);
        return typedArray;
    }

    static bool ValidateJSArrayBufferViewFlags(JSArrayBuffer buffer, uint serializedFlags, out bool isLengthTracking,
        out bool isBackedByRab)
    {
        isLengthTracking = (serializedFlags & 1) != 0;
        isBackedByRab = (serializedFlags & 2) != 0;

        if (isBackedByRab || isLengthTracking)
        {
            if (!buffer.IsResizableByJs) return false;
            if (isBackedByRab && buffer.IsShared) return false;
        }
        // The RAB-ness of the buffer and the TA's "is_backed_by_rab" need to be in
        // sync.
        if (buffer.IsResizableByJs && !buffer.IsShared && !isBackedByRab) return false;
        return true;
    }

    JSValue? ReadJSError()
    {
        uint id = _nextId++;

        if (ReadVarint8() is not { } tag) return null;

        // Read error type constructor.
        NativeContext nativeContext = _isolate.NativeContext;
        JSFunction constructor;
        switch ((char)tag)
        {
            case 'E': constructor = nativeContext.EvalErrorFunction; break;
            case 'R': constructor = nativeContext.RangeErrorFunction; break;
            case 'F': constructor = nativeContext.ReferenceErrorFunction; break;
            case 'S': constructor = nativeContext.SyntaxErrorFunction; break;
            case 'T': constructor = nativeContext.TypeErrorFunction; break;
            case 'U': constructor = nativeContext.UriErrorFunction; break;
            default:
                // The default prototype in the deserialization side is Error.prototype,
                // so we don't have to do anything here.
                constructor = nativeContext.ErrorFunction;
                goto afterPrototype;
        }
        if (ReadVarint8() is not { } next) return null;
        tag = next;
    afterPrototype:

        // Check for message property.
        JSValue message = JSValue.Undefined;
        if (tag == (byte)'m')
        {
            if (ReadString() is not { } messageString) return null;
            message = messageString;
            if (ReadVarint8() is not { } n) return null;
            tag = n;
        }

        // Check for stack property.
        JSValue stack = JSValue.Undefined;
        if (tag == (byte)'s')
        {
            if (ReadString() is not { } stackString) return null;
            stack = stackString;
            if (ReadVarint8() is not { } n) return null;
            tag = n;
        }

        // Create error object before adding the cause property.
        JSObject error = ErrorUtils.Construct(_isolate, constructor, constructor, message, JSValue.Undefined, FrameSkipMode.SKIP_NONE,
            JSValue.Undefined, ErrorUtils.StackTraceCollection.Disabled);
        ErrorUtils.SetFormattedStack(_isolate, error, stack);
        AddObjectWithID(id, error);

        // Add cause property if needed.
        if (tag == (byte)'c')
        {
            if (ReadObject() is not { } cause) return null;
            JSObject.SetOwnPropertyIgnoreAttributes(_isolate, error, ReadOnlyRoots.cause_string, cause, PropertyAttributes.DONT_ENUM);
            if (ReadVarint8() is not { } n) return null;
            tag = n;
        }

        if (tag != (byte)'.') return null;
        return error;
    }

    JSValue? ReadHostObject()
    {
        if (_delegate is null) return null;
        _isolate.StackGuard.StackCheck(_isolate);
        uint id = _nextId++;
        JSObject? obj = _delegate.ReadHostObject(_isolate, this);
        if (obj is null) return null;
        AddObjectWithID(id, obj);
        return obj;
    }

    static bool IsValidObjectKey(JSValue value) =>
        value.IsSmi || value.IsNumber || value.HeapObjectOrNull is Name;

    /// <summary>
    /// Reads key-value pairs into the object until the specified end tag is
    /// encountered. If successful, returns the number of properties read.
    /// Deviation: V8's map-transition fast path is not taken (see the header).
    /// </summary>
    uint? ReadJSObjectProperties(JSObject obj, SerializationTag endTag)
    {
        for (uint numProperties = 0; ; numProperties++)
        {
            if (PeekTag() is not { } tag) return null;
            if (tag == endTag)
            {
                ConsumeTag(endTag);
                return numProperties;
            }

            if (ReadObject() is not { } key || !IsValidObjectKey(key)) return null;
            if (key.HeapObjectOrNull is JSString keyString) key = _isolate.Factory.InternalizeString(keyString);
            if (ReadObject() is not { } value) return null;

            // We checked earlier that IsValidObjectKey(key).
            var lookupKey = new PropertyKey(_isolate, key);
            var it = new LookupIterator(_isolate, obj, lookupKey, LookupIterator.Configuration.OWN);
            if (it.State != LookupIterator.StateKind.NOT_FOUND) return null;
            JSObject.DefineOwnPropertyIgnoreAttributes(ref it, value, PropertyAttributes.NONE, ShouldThrow.ThrowOnError);
        }
    }

    bool HasObjectWithID(uint id) => id < _idMap.Length && _idMap[id] is not null;

    JSValue? GetObjectWithID(uint id)
    {
        if (id >= _idMap.Length) return null;
        JSReceiver? value = _idMap[id];
        if (value is null) return null;
        return value;
    }

    void AddObjectWithID(uint id, JSReceiver obj)
    {
        Debug.Assert(!HasObjectWithID(id));
        if (id >= _idMap.Length) Array.Resize(ref _idMap, Math.Max((int)id + 1, _idMap.Length * 2 + 4));
        _idMap[id] = obj;
    }

    /// <summary>
    /// ReadObjectUsingEntireBufferForLegacyFormat: the legacy "version 0"
    /// format, which relied on a "stack" model for deserializing, with the
    /// contents of objects and arrays provided first.
    /// </summary>
    JSValue ReadObjectUsingEntireBufferForLegacyFormat()
    {
        Debug.Assert(_version == 0);
        var stack = new List<JSValue>();
        while (_position < _end)
        {
            if (PeekTag() is not { } tag) break;

            JSValue newObject;
            switch (tag)
            {
                case SerializationTag.kEndJSObject:
                {
                    ConsumeTag(SerializationTag.kEndJSObject);

                    // JS Object: Read the last 2*n values from the stack and use them as
                    // key-value pairs.
                    if (ReadVarint32() is not { } numProperties || stack.Count / 2 < numProperties)
                    {
                        ThrowDeserializationError();
                        return default;
                    }

                    int beginProperties = stack.Count - 2 * (int)numProperties;
                    JSObject jsObject = _isolate.Factory.NewJSObject(_isolate.NativeContext.ObjectFunction);
                    if (numProperties != 0 && !SetPropertiesFromKeyValuePairs(jsObject, stack, beginProperties, numProperties))
                    {
                        ThrowDeserializationError();
                        return default;
                    }
                    stack.RemoveRange(beginProperties, stack.Count - beginProperties);
                    newObject = jsObject;
                    break;
                }
                case SerializationTag.kEndSparseJSArray:
                {
                    ConsumeTag(SerializationTag.kEndSparseJSArray);

                    // Sparse JS Array: Read the last 2*|num_properties| from the stack.
                    if (ReadVarint32() is not { } numProperties || ReadVarint32() is not { } length ||
                        stack.Count / 2 < numProperties)
                    {
                        ThrowDeserializationError();
                        return default;
                    }

                    JSArray jsArray = _isolate.Factory.NewJSArray(ElementsKind.TERMINAL_FAST_ELEMENTS_KIND, 0, 0);
                    JSArray.SetLength(_isolate, jsArray, length);
                    int beginProperties = stack.Count - 2 * (int)numProperties;
                    if (numProperties != 0 && !SetPropertiesFromKeyValuePairs(jsArray, stack, beginProperties, numProperties))
                    {
                        ThrowDeserializationError();
                        return default;
                    }
                    stack.RemoveRange(beginProperties, stack.Count - beginProperties);
                    newObject = jsArray;
                    break;
                }
                case SerializationTag.kEndDenseJSArray:
                    // This was already broken in Chromium, and apparently wasn't missed.
                    ThrowDeserializationError();
                    return default;
                default:
                    if (ReadObject() is not { } o) return default;
                    newObject = o;
                    break;
            }
            stack.Add(newObject);
        }

        // Nothing remains but padding.
        _position = _end;

        if (stack.Count != 1)
        {
            ThrowDeserializationError();
            return default;
        }
        return stack[0];
    }

    bool SetPropertiesFromKeyValuePairs(JSObject obj, List<JSValue> data, int start, uint numProperties)
    {
        for (int i = 0; i < 2 * numProperties; i += 2)
        {
            JSValue key = data[start + i];
            if (!IsValidObjectKey(key)) return false;
            JSValue value = data[start + i + 1];
            var lookupKey = new PropertyKey(_isolate, key);
            var it = new LookupIterator(_isolate, obj, lookupKey, LookupIterator.Configuration.OWN);
            if (it.State != LookupIterator.StateKind.NOT_FOUND) return false;
            JSObject.DefineOwnPropertyIgnoreAttributes(ref it, value, PropertyAttributes.NONE, ShouldThrow.ThrowOnError);
        }
        return true;
    }
}
