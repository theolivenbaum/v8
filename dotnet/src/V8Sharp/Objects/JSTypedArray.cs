// Port of the JSTypedArray parts of src/objects/js-array-buffer.{h,cc,-inl.h}:
// element type and size, length tracking and out-of-bounds computation for
// resizable/growable buffers, Validate and [[DefineOwnProperty]].
using V8Sharp.Common;
using V8Sharp.Roots;

namespace V8Sharp.Objects;

/// <summary>V8's TypedArrayAccessMode.</summary>
public enum TypedArrayAccessMode { kRead, kWrite }

/// <summary>V8's ExternalArrayType.</summary>
public enum ExternalArrayType
{
    kExternalInt8Array = 1,
    kExternalUint8Array,
    kExternalInt16Array,
    kExternalUint16Array,
    kExternalInt32Array,
    kExternalUint32Array,
    kExternalFloat16Array,
    kExternalFloat32Array,
    kExternalFloat64Array,
    kExternalUint8ClampedArray,
    kExternalBigInt64Array,
    kExternalBigUint64Array,
}

/// <summary>V8's JSTypedArray.</summary>
public sealed partial class JSTypedArray(Map map) : JSArrayBufferView(map)
{
    /// <summary>JSTypedArray::kMaxByteLength.</summary>
    public const ulong kMaxByteLength = JSArrayBuffer.kMaxByteLength;

    public ulong RawLength;

    public ElementsKind Kind => Map.ElementsKind;

    public int ElementSize => ElementsKinds.ElementsKindToByteSize(Kind);

    public bool IsBigIntArray => ElementsKinds.IsBigIntTypedArrayElementsKind(Kind);

    /// <summary>JSTypedArray::type.</summary>
    public ExternalArrayType Type
    {
        get
        {
            ElementsKind kind = ElementsKinds.IsRabGsabTypedArrayElementsKind(Kind)
                ? ElementsKinds.GetCorrespondingNonRabGsabElementsKind(Kind)
                : Kind;
            return kind switch
            {
                ElementsKind.INT8_ELEMENTS => ExternalArrayType.kExternalInt8Array,
                ElementsKind.UINT8_ELEMENTS => ExternalArrayType.kExternalUint8Array,
                ElementsKind.INT16_ELEMENTS => ExternalArrayType.kExternalInt16Array,
                ElementsKind.UINT16_ELEMENTS => ExternalArrayType.kExternalUint16Array,
                ElementsKind.INT32_ELEMENTS => ExternalArrayType.kExternalInt32Array,
                ElementsKind.UINT32_ELEMENTS => ExternalArrayType.kExternalUint32Array,
                ElementsKind.FLOAT16_ELEMENTS => ExternalArrayType.kExternalFloat16Array,
                ElementsKind.FLOAT32_ELEMENTS => ExternalArrayType.kExternalFloat32Array,
                ElementsKind.FLOAT64_ELEMENTS => ExternalArrayType.kExternalFloat64Array,
                ElementsKind.UINT8_CLAMPED_ELEMENTS => ExternalArrayType.kExternalUint8ClampedArray,
                ElementsKind.BIGINT64_ELEMENTS => ExternalArrayType.kExternalBigInt64Array,
                _ => ExternalArrayType.kExternalBigUint64Array,
            };
        }
    }

    /// <summary>The bytes of the view in its buffer (V8's DataPtr()).</summary>
    public Span<byte> DataSpan() => Buffer.BackingStoreBuffer.AsSpan((int)ByteOffset);

    /// <summary>The bytes of elements [start, start + count) (V8's DataPtr() + start * element_size).</summary>
    public Span<byte> DataSpan(ulong start, ulong count)
    {
        int elementSize = ElementSize;
        return Buffer.BackingStoreBuffer.AsSpan((int)(ByteOffset + start * (ulong)elementSize), (int)(count * (ulong)elementSize));
    }

    /// <summary>JSTypedArray::GetVariableByteLengthOrOutOfBounds.</summary>
    public ulong GetVariableByteLengthOrOutOfBounds(out bool outOfBounds)
    {
        Debug.Assert(!WasDetached);
        outOfBounds = false;
        ulong ownByteOffset = ByteOffset;
        if (IsLengthTracking)
        {
            ulong ownElementSize = (ulong)ElementSize;
            if (IsBackedByRab)
            {
                ulong bufferByteLength = Buffer.GetByteLength();
                if (ownByteOffset > bufferByteLength)
                {
                    outOfBounds = true;
                    return 0;
                }
                // Round down to the nearest multiple of element size.
                ulong available = bufferByteLength - ownByteOffset;
                return available - available % ownElementSize;
            }
            // GSAB-backed TypedArrays can't be out of bounds.
            ulong gsabByteLength = Buffer.GetByteLength();
            Debug.Assert(ownByteOffset <= gsabByteLength);
            ulong gsabAvailable = gsabByteLength - ownByteOffset;
            return gsabAvailable - gsabAvailable % ownElementSize;
        }
        Debug.Assert(IsBackedByRab);
        ulong ownByteLength = RawByteLength;
        ulong bufferLength = Buffer.GetByteLength();
        if (ownByteLength > bufferLength || ownByteOffset > bufferLength - ownByteLength)
        {
            outOfBounds = true;
            return 0;
        }
        return ownByteLength;
    }

    /// <summary>JSTypedArray::GetLengthOrOutOfBounds.</summary>
    public ulong GetLengthOrOutOfBounds(out bool outOfBounds)
    {
        outOfBounds = false;
        if (WasDetached) return 0;
        if (IsVariableLength) return GetVariableByteLengthOrOutOfBounds(out outOfBounds) / (ulong)ElementSize;
        return RawByteLength / (ulong)ElementSize;
    }

    /// <summary>JSTypedArray::GetLength.</summary>
    public ulong GetLength() => GetLengthOrOutOfBounds(out _);

    /// <summary>JSTypedArray::GetByteLength.</summary>
    public ulong GetByteLength()
    {
        if (WasDetached) return 0;
        if (IsVariableLength) return GetVariableByteLengthOrOutOfBounds(out _);
        return RawByteLength;
    }

    /// <summary>byte_length() as V8's JSArrayBufferView field.</summary>
    public ulong ByteLength => RawByteLength;

    public bool IsOutOfBounds
    {
        get
        {
            GetLengthOrOutOfBounds(out bool outOfBounds);
            return outOfBounds;
        }
    }

    public bool IsDetachedOrOutOfBounds => WasDetached || IsOutOfBounds;

    /// <summary>
    /// JSTypedArray::Validate (ES #sec-validatetypedarray): throws TypeError
    /// for non-typed-arrays, detached or out-of-bounds arrays and, for writes,
    /// immutable buffers.
    /// </summary>
    public static JSTypedArray Validate(Isolate isolate, JSValue receiver, string methodName,
        TypedArrayAccessMode accessMode = TypedArrayAccessMode.kRead)
    {
        if (receiver.HeapObjectOrNull is not JSTypedArray array)
        {
            isolate.ThrowTypeError(MessageTemplate.NotTypedArray);
            return null!;
        }

        // All errors throw the same message. In theory we could be more specific
        // here. However, many of the fast-paths do not distinguish and we'd rather be
        // consistent.
        MessageTemplate message = ValidateErrorMessage(isolate, accessMode);
        if (array.WasDetached)
        {
            isolate.ThrowTypeError(message, isolate.Factory.NewStringFromAsciiChecked(methodName));
        }
        if (accessMode == TypedArrayAccessMode.kWrite && array.Buffer.IsImmutable)
        {
            isolate.ThrowTypeError(message, isolate.Factory.NewStringFromAsciiChecked(methodName));
        }
        if (array.IsVariableLength && array.IsOutOfBounds)
        {
            isolate.ThrowTypeError(message, isolate.Factory.NewStringFromAsciiChecked(methodName));
        }
        return array;
    }

    /// <summary>
    /// kTypedArrayValidateWriteErrorOperation's text depends on
    /// --js-immutable-arraybuffer in V8 (message-template.h); without the flag
    /// it is the text of kTypedArrayValidateErrorOperation.
    /// </summary>
    public static MessageTemplate ValidateErrorMessage(Isolate isolate, TypedArrayAccessMode accessMode) =>
        accessMode == TypedArrayAccessMode.kWrite && isolate.Flags.js_immutable_arraybuffer
            ? MessageTemplate.TypedArrayValidateWriteErrorOperation
            : MessageTemplate.TypedArrayValidateErrorOperation;

    /// <summary>The Context slot of the constructor of a typed array kind (Context::TYPE##_ARRAY_FUN_INDEX).</summary>
    public static Context.Field ConstructorIndexForKind(ElementsKind kind)
    {
        if (ElementsKinds.IsRabGsabTypedArrayElementsKind(kind)) kind = ElementsKinds.GetCorrespondingNonRabGsabElementsKind(kind);
        Debug.Assert(ElementsKinds.IsTypedArrayElementsKind(kind));
        return Context.Field.UINT8_ARRAY_FUN_INDEX + (kind - ElementsKind.FIRST_FIXED_TYPED_ARRAY_ELEMENTS_KIND);
    }

    /// <summary>The constructor of a typed array kind in <paramref name="nativeContext"/> (GetDefaultConstructor).</summary>
    public static JSFunction ConstructorForKind(NativeContext nativeContext, ElementsKind kind) =>
        nativeContext.Slots[(int)ConstructorIndexForKind(kind)].As<JSFunction>();

    /// <summary>NativeContext::TypedArrayElementsKindToCtorMap.</summary>
    public static Map TypedArrayElementsKindToCtorMap(NativeContext nativeContext, ElementsKind kind) =>
        ConstructorForKind(nativeContext, kind).InitialMap;

    /// <summary>NativeContext::TypedArrayElementsKindToRabGsabCtorMap.</summary>
    public static Map TypedArrayElementsKindToRabGsabCtorMap(NativeContext nativeContext, ElementsKind kind)
    {
        if (ElementsKinds.IsRabGsabTypedArrayElementsKind(kind)) kind = ElementsKinds.GetCorrespondingNonRabGsabElementsKind(kind);
        var index = Context.Field.RAB_GSAB_UINT8_ARRAY_MAP_INDEX + (kind - ElementsKind.FIRST_FIXED_TYPED_ARRAY_ELEMENTS_KIND);
        return nativeContext.Slots[(int)index].As<Map>();
    }

    /// <summary>The non-RAB/GSAB kind of this array (the kind of its constructor).</summary>
    public ElementsKind BaseKind => ElementsKinds.IsRabGsabTypedArrayElementsKind(Kind)
        ? ElementsKinds.GetCorrespondingNonRabGsabElementsKind(Kind)
        : Kind;

    /// <summary>The class name ("Uint8Array" ...), for JSReceiver::class_name.</summary>
    public static JSString TypedArrayClassName(ElementsKind kind)
    {
        if (ElementsKinds.IsRabGsabTypedArrayElementsKind(kind)) kind = ElementsKinds.GetCorrespondingNonRabGsabElementsKind(kind);
        return kind switch
        {
            ElementsKind.INT8_ELEMENTS => ReadOnlyRoots.Int8Array_string,
            ElementsKind.UINT8_ELEMENTS => ReadOnlyRoots.Uint8Array_string,
            ElementsKind.INT16_ELEMENTS => ReadOnlyRoots.Int16Array_string,
            ElementsKind.UINT16_ELEMENTS => ReadOnlyRoots.Uint16Array_string,
            ElementsKind.INT32_ELEMENTS => ReadOnlyRoots.Int32Array_string,
            ElementsKind.UINT32_ELEMENTS => ReadOnlyRoots.Uint32Array_string,
            ElementsKind.FLOAT16_ELEMENTS => ReadOnlyRoots.Float16Array_string,
            ElementsKind.FLOAT32_ELEMENTS => ReadOnlyRoots.Float32Array_string,
            ElementsKind.FLOAT64_ELEMENTS => ReadOnlyRoots.Float64Array_string,
            ElementsKind.UINT8_CLAMPED_ELEMENTS => ReadOnlyRoots.Uint8ClampedArray_string,
            ElementsKind.BIGINT64_ELEMENTS => ReadOnlyRoots.BigInt64Array_string,
            _ => ReadOnlyRoots.BigUint64Array_string,
        };
    }

    // https://tc39.es/ecma262/#sec-canonicalnumericindexstring
    // Returns true if the lookup_key represents a valid index string.
    static bool CanonicalNumericIndexString(Isolate isolate, in PropertyKey lookupKey, out bool isMinusZero)
    {
        // 1. Assert: Type(argument) is String.
        isMinusZero = false;
        if (lookupKey.IsElement) return true;

        var key = (JSString)lookupKey.Name!;

        // 3. Let n be ! ToNumber(argument).
        double result = JSString.ToNumber(key);
        if (result == 0 && double.IsNegative(result))
        {
            // 2. If argument is "-0", return -0𝔽.
            // We are not performing SaveValue check for -0 because it'll be rejected
            // anyway.
            isMinusZero = true;
        }
        else
        {
            // 4. If SameValue(! ToString(n), argument) is false, return undefined.
            JSString str = isolate.Factory.NumberToString(result);
            // Avoid treating strings like "2E1" and "20" as the same key.
            if (!JSString.Equals(str, key)) return false;
        }
        return true;
    }

    /// <summary>JSTypedArray::DefineOwnProperty (ES #sec-typedarray-defineownproperty).</summary>
    public static bool DefineOwnProperty(Isolate isolate, JSTypedArray o, JSValue key, ref PropertyDescriptor desc,
        ShouldThrow? shouldThrow)
    {
        Debug.Assert(key.IsName || key.IsNumber);
        // 1. If Type(P) is String, then
        var lookupKey = new PropertyKey(isolate, key);
        if (lookupKey.IsElement || key.IsSmi || key.IsString)
        {
            // 1a. Let numericIndex be ! CanonicalNumericIndexString(P)
            // 1b. If numericIndex is not undefined, then
            bool isMinusZero = false;
            if (key.IsSmi ||  // Smi keys are definitely canonical
                CanonicalNumericIndexString(isolate, lookupKey, out isMinusZero))
            {
                // 1b i. If IsValidIntegerIndex(O, numericIndex) is false, return false.

                // IsValidIntegerIndex:
                ulong index = lookupKey.Index;
                ulong length = o.GetLengthOrOutOfBounds(out bool outOfBounds);
                if (o.WasDetached || outOfBounds || index >= length)
                {
                    return ObjectOps.ReturnFailure(isolate, ObjectOps.GetShouldThrow(isolate, shouldThrow),
                        MessageTemplate.InvalidTypedArrayIndex);
                }
                if (o.Buffer.IsImmutable)
                {
                    // 10.4.5.3 [[DefineOwnProperty]] ( P, Desc )
                    // step 1.b.ii. If IsImmutableBuffer(O.[[ViewedArrayBuffer]]) is true...
                    // We need to validate that the new descriptor is compatible with the
                    // existing immutable property (which is non-configurable,
                    // non-writable).
                    var it0 = new LookupIterator(isolate, o, index, LookupIterator.Configuration.OWN);
                    JSValue currentValue = ObjectOps.GetProperty(ref it0);

                    if (PropertyDescriptor.IsAccessorDescriptor(in desc) || (desc.HasConfigurable && desc.Configurable) ||
                        (desc.HasEnumerable && !desc.Enumerable) || (desc.HasWritable && desc.Writable) ||
                        (desc.HasValue && !ObjectOps.SameValue(desc.Value, currentValue)))
                    {
                        return ObjectOps.ReturnFailure(isolate, ObjectOps.GetShouldThrow(isolate, shouldThrow),
                            MessageTemplate.RedefineDisallowed, key);
                    }
                    return true;
                }
                if (!lookupKey.IsElement || isMinusZero)
                {
                    return ObjectOps.ReturnFailure(isolate, ObjectOps.GetShouldThrow(isolate, shouldThrow),
                        MessageTemplate.InvalidTypedArrayIndex);
                }

                // 1b ii. If Desc has a [[Configurable]] field and if
                //     Desc.[[Configurable]] is false, return false.
                // 1b iii. If Desc has an [[Enumerable]] field and if Desc.[[Enumerable]]
                //     is false, return false.
                // 1b iv. If IsAccessorDescriptor(Desc) is true, return false.
                // 1b v. If Desc has a [[Writable]] field and if Desc.[[Writable]] is
                //     false, return false.

                if (PropertyDescriptor.IsAccessorDescriptor(in desc))
                {
                    return ObjectOps.ReturnFailure(isolate, ObjectOps.GetShouldThrow(isolate, shouldThrow),
                        MessageTemplate.RedefineDisallowed, key);
                }

                if ((desc.HasConfigurable && !desc.Configurable) || (desc.HasEnumerable && !desc.Enumerable) ||
                    (desc.HasWritable && !desc.Writable))
                {
                    return ObjectOps.ReturnFailure(isolate, ObjectOps.GetShouldThrow(isolate, shouldThrow),
                        MessageTemplate.RedefineDisallowed, key);
                }

                // 1b vi. If Desc has a [[Value]] field, perform
                // ? IntegerIndexedElementSet(O, numericIndex, Desc.[[Value]]).
                if (desc.HasValue)
                {
                    if (!desc.HasConfigurable) desc.SetConfigurable(true);
                    if (!desc.HasEnumerable) desc.SetEnumerable(true);
                    if (!desc.HasWritable) desc.SetWritable(true);
                    JSValue value = desc.Value;
                    var it = new LookupIterator(isolate, o, index, LookupIterator.Configuration.OWN);
                    JSObject.DefineOwnPropertyIgnoreAttributes(ref it, value, desc.ToAttributes());
                }
                // 1b vii. Return true.
                return true;
            }
        }
        // 4. Return ! OrdinaryDefineOwnProperty(O, P, Desc).
        return JSReceiver.OrdinaryDefineOwnProperty(isolate, o, lookupKey, ref desc, shouldThrow);
    }
}
