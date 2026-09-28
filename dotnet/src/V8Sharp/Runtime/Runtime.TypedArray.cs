// Port of src/runtime/runtime-typedarray.cc and the typed-array queries of
// runtime-test.cc (%HasFixed<Type>Elements): the runtime functions the
// typed-array builtins and the test suites (test262's detachArrayBuffer.js,
// mjsunit) call.
using V8Sharp.Builtins;

namespace V8Sharp.Runtime;

public static class RuntimeTypedArray
{
    /// <summary>Runtime_ArrayBufferDetach (also exposed to fuzzers: any arguments).</summary>
    public static JSValue ArrayBufferDetach(Isolate isolate, ReadOnlySpan<JSValue> args)
    {
        if (args.Length < 1 || args[0].HeapObjectOrNull is not JSArrayBuffer arrayBuffer)
        {
            isolate.ThrowTypeError(MessageTemplate.NotTypedArray);
            return default;
        }
        JSArrayBuffer.Detach(isolate, arrayBuffer, forceForWasmMemory: false, hasKey: args.Length > 1,
            maybeKey: args.Length > 1 ? args[1] : JSValue.Undefined);
        return JSValue.Undefined;
    }

    /// <summary>Runtime_ArrayBufferSetDetachKey.</summary>
    public static JSValue ArrayBufferSetDetachKey(Isolate isolate, JSValue argument, JSValue key)
    {
        if (argument.HeapObjectOrNull is not JSArrayBuffer arrayBuffer)
        {
            isolate.ThrowTypeError(MessageTemplate.NotTypedArray);
            return default;
        }
        JSArrayBuffer.SetDetachKey(arrayBuffer, key, isolate);
        return JSValue.Undefined;
    }

    /// <summary>Runtime_TypedArrayCopyElements / Runtime_TypedArraySet.</summary>
    public static JSValue TypedArrayCopyElements(Isolate isolate, JSTypedArray target, JSValue source, double length, double offset)
    {
        TypedArrayElementsOps.CopyElementsHandle(isolate, source, target, (ulong)length, (ulong)offset);
        return JSValue.Undefined;
    }

    /// <summary>Runtime_TypedArrayGetBuffer.</summary>
    public static JSValue TypedArrayGetBuffer(JSTypedArray holder) => holder.Buffer;

    /// <summary>Runtime_GrowableSharedArrayBufferByteLength.</summary>
    public static JSValue GrowableSharedArrayBufferByteLength(JSArrayBuffer arrayBuffer) =>
        JSValue.FromNumber(arrayBuffer.GetBackingStore()?.ByteLength ?? 0);

    /// <summary>Runtime_TypedArraySortFast.</summary>
    public static JSValue TypedArraySortFast(JSTypedArray array) => BuiltinsTypedArray.TypedArraySortFast(array);

    /// <summary>Runtime_ArrayBufferMaxByteLength: the allocator's maximum allocation size.</summary>
    public static JSValue ArrayBufferMaxByteLength() => JSValue.FromNumber(Array.MaxLength);

    /// <summary>Runtime_HasFixed&lt;Type&gt;Elements.</summary>
    public static JSValue HasFixedElements(JSValue obj, ElementsKind kind)
    {
        if (obj.HeapObjectOrNull is not JSObject o) return JSValue.False;
        ElementsKind k = o.GetElementsKind();
        if (ElementsKinds.IsRabGsabTypedArrayElementsKind(k)) k = ElementsKinds.GetCorrespondingNonRabGsabElementsKind(k);
        return JSValue.FromBoolean(k == kind);
    }
}

public static partial class RuntimeTable
{
    static void RegisterTypedArray()
    {
        Register(FunctionId.ArrayBufferDetach, static (i, a) => RuntimeTypedArray.ArrayBufferDetach(i, a));
        Register(FunctionId.ArrayBufferSetDetachKey, static (i, a) => RuntimeTypedArray.ArrayBufferSetDetachKey(i, a[0], a[1]));
        Register(FunctionId.GrowableSharedArrayBufferByteLength,
            static (i, a) => RuntimeTypedArray.GrowableSharedArrayBufferByteLength(a[0].As<JSArrayBuffer>()));
        Register(FunctionId.TypedArrayCopyElements,
            static (i, a) => RuntimeTypedArray.TypedArrayCopyElements(i, a[0].As<JSTypedArray>(), a[1], a[2].Number, 0));
        Register(FunctionId.TypedArrayGetBuffer, static (i, a) => RuntimeTypedArray.TypedArrayGetBuffer(a[0].As<JSTypedArray>()));
        Register(FunctionId.TypedArraySet,
            static (i, a) => RuntimeTypedArray.TypedArrayCopyElements(i, a[0].As<JSTypedArray>(), a[1], a[2].Number, a[3].Number));
        Register(FunctionId.TypedArraySortFast, static (i, a) => RuntimeTypedArray.TypedArraySortFast(a[0].As<JSTypedArray>()));
        Register(FunctionId.ArrayBufferMaxByteLength, static (i, a) => RuntimeTypedArray.ArrayBufferMaxByteLength());
        Register(FunctionId.HasFixedInt8Elements, static (i, a) => RuntimeTypedArray.HasFixedElements(a[0], ElementsKind.INT8_ELEMENTS));
        Register(FunctionId.HasFixedUint8Elements, static (i, a) => RuntimeTypedArray.HasFixedElements(a[0], ElementsKind.UINT8_ELEMENTS));
        Register(FunctionId.HasFixedInt16Elements, static (i, a) => RuntimeTypedArray.HasFixedElements(a[0], ElementsKind.INT16_ELEMENTS));
        Register(FunctionId.HasFixedUint16Elements, static (i, a) => RuntimeTypedArray.HasFixedElements(a[0], ElementsKind.UINT16_ELEMENTS));
        Register(FunctionId.HasFixedInt32Elements, static (i, a) => RuntimeTypedArray.HasFixedElements(a[0], ElementsKind.INT32_ELEMENTS));
        Register(FunctionId.HasFixedUint32Elements, static (i, a) => RuntimeTypedArray.HasFixedElements(a[0], ElementsKind.UINT32_ELEMENTS));
        Register(FunctionId.HasFixedFloat16Elements, static (i, a) => RuntimeTypedArray.HasFixedElements(a[0], ElementsKind.FLOAT16_ELEMENTS));
        Register(FunctionId.HasFixedFloat32Elements, static (i, a) => RuntimeTypedArray.HasFixedElements(a[0], ElementsKind.FLOAT32_ELEMENTS));
        Register(FunctionId.HasFixedFloat64Elements, static (i, a) => RuntimeTypedArray.HasFixedElements(a[0], ElementsKind.FLOAT64_ELEMENTS));
        Register(FunctionId.HasFixedUint8ClampedElements,
            static (i, a) => RuntimeTypedArray.HasFixedElements(a[0], ElementsKind.UINT8_CLAMPED_ELEMENTS));
        Register(FunctionId.HasFixedBigInt64Elements,
            static (i, a) => RuntimeTypedArray.HasFixedElements(a[0], ElementsKind.BIGINT64_ELEMENTS));
        Register(FunctionId.HasFixedBigUint64Elements,
            static (i, a) => RuntimeTypedArray.HasFixedElements(a[0], ElementsKind.BIGUINT64_ELEMENTS));
    }
}
