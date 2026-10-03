// Port of src/builtins/builtins-dataview.cc (DataViewConstructor) and
// src/builtins/data-view.tq (the accessors and the get/set methods), with
// JSFunction::GetDerivedRabGsabDataViewMap from src/objects/js-function.cc.
using System.Buffers.Binary;
using V8Sharp.Base.Numbers;
using V8Sharp.Objects;

namespace V8Sharp.Builtins;

public static partial class BuiltinRegistry
{
    static partial void RegisterDataView()
    {
        Register(Builtin.DataViewConstructor, BuiltinsDataView.DataViewConstructor);
        Register(Builtin.DataViewPrototypeGetBuffer, BuiltinsDataView.DataViewPrototypeGetBuffer);
        Register(Builtin.DataViewPrototypeGetByteLength, BuiltinsDataView.DataViewPrototypeGetByteLength);
        Register(Builtin.DataViewPrototypeGetByteOffset, BuiltinsDataView.DataViewPrototypeGetByteOffset);
        Register(Builtin.DataViewPrototypeGetUint8, static (Isolate i, in BuiltinArguments a) => BuiltinsDataView.DataViewGet(i, a, ElementsKind.UINT8_ELEMENTS));
        Register(Builtin.DataViewPrototypeGetInt8, static (Isolate i, in BuiltinArguments a) => BuiltinsDataView.DataViewGet(i, a, ElementsKind.INT8_ELEMENTS));
        Register(Builtin.DataViewPrototypeGetUint16, static (Isolate i, in BuiltinArguments a) => BuiltinsDataView.DataViewGet(i, a, ElementsKind.UINT16_ELEMENTS));
        Register(Builtin.DataViewPrototypeGetInt16, static (Isolate i, in BuiltinArguments a) => BuiltinsDataView.DataViewGet(i, a, ElementsKind.INT16_ELEMENTS));
        Register(Builtin.DataViewPrototypeGetUint32, static (Isolate i, in BuiltinArguments a) => BuiltinsDataView.DataViewGet(i, a, ElementsKind.UINT32_ELEMENTS));
        Register(Builtin.DataViewPrototypeGetInt32, static (Isolate i, in BuiltinArguments a) => BuiltinsDataView.DataViewGet(i, a, ElementsKind.INT32_ELEMENTS));
        Register(Builtin.DataViewPrototypeGetFloat16, static (Isolate i, in BuiltinArguments a) => BuiltinsDataView.DataViewGet(i, a, ElementsKind.FLOAT16_ELEMENTS));
        Register(Builtin.DataViewPrototypeGetFloat32, static (Isolate i, in BuiltinArguments a) => BuiltinsDataView.DataViewGet(i, a, ElementsKind.FLOAT32_ELEMENTS));
        Register(Builtin.DataViewPrototypeGetFloat64, static (Isolate i, in BuiltinArguments a) => BuiltinsDataView.DataViewGet(i, a, ElementsKind.FLOAT64_ELEMENTS));
        Register(Builtin.DataViewPrototypeGetBigUint64, static (Isolate i, in BuiltinArguments a) => BuiltinsDataView.DataViewGet(i, a, ElementsKind.BIGUINT64_ELEMENTS));
        Register(Builtin.DataViewPrototypeGetBigInt64, static (Isolate i, in BuiltinArguments a) => BuiltinsDataView.DataViewGet(i, a, ElementsKind.BIGINT64_ELEMENTS));
        Register(Builtin.DataViewPrototypeSetUint8, static (Isolate i, in BuiltinArguments a) => BuiltinsDataView.DataViewSet(i, a, ElementsKind.UINT8_ELEMENTS));
        Register(Builtin.DataViewPrototypeSetInt8, static (Isolate i, in BuiltinArguments a) => BuiltinsDataView.DataViewSet(i, a, ElementsKind.INT8_ELEMENTS));
        Register(Builtin.DataViewPrototypeSetUint16, static (Isolate i, in BuiltinArguments a) => BuiltinsDataView.DataViewSet(i, a, ElementsKind.UINT16_ELEMENTS));
        Register(Builtin.DataViewPrototypeSetInt16, static (Isolate i, in BuiltinArguments a) => BuiltinsDataView.DataViewSet(i, a, ElementsKind.INT16_ELEMENTS));
        Register(Builtin.DataViewPrototypeSetUint32, static (Isolate i, in BuiltinArguments a) => BuiltinsDataView.DataViewSet(i, a, ElementsKind.UINT32_ELEMENTS));
        Register(Builtin.DataViewPrototypeSetInt32, static (Isolate i, in BuiltinArguments a) => BuiltinsDataView.DataViewSet(i, a, ElementsKind.INT32_ELEMENTS));
        Register(Builtin.DataViewPrototypeSetFloat16, static (Isolate i, in BuiltinArguments a) => BuiltinsDataView.DataViewSet(i, a, ElementsKind.FLOAT16_ELEMENTS));
        Register(Builtin.DataViewPrototypeSetFloat32, static (Isolate i, in BuiltinArguments a) => BuiltinsDataView.DataViewSet(i, a, ElementsKind.FLOAT32_ELEMENTS));
        Register(Builtin.DataViewPrototypeSetFloat64, static (Isolate i, in BuiltinArguments a) => BuiltinsDataView.DataViewSet(i, a, ElementsKind.FLOAT64_ELEMENTS));
        Register(Builtin.DataViewPrototypeSetBigUint64, static (Isolate i, in BuiltinArguments a) => BuiltinsDataView.DataViewSet(i, a, ElementsKind.BIGUINT64_ELEMENTS));
        Register(Builtin.DataViewPrototypeSetBigInt64, static (Isolate i, in BuiltinArguments a) => BuiltinsDataView.DataViewSet(i, a, ElementsKind.BIGINT64_ELEMENTS));
    }
}

/// <summary>The DataView builtins.</summary>
public static class BuiltinsDataView
{
    const string kBuiltinNameByteLength = "get DataView.prototype.byteLength";
    const string kBuiltinNameByteOffset = "get DataView.prototype.byteOffset";

    static JSString Str(Isolate isolate, string s) => isolate.Factory.NewStringFromAsciiChecked(s);

    /// <summary>ES #sec-dataview-constructor (BUILTIN(DataViewConstructor)).</summary>
    public static JSValue DataViewConstructor(Isolate isolate, in BuiltinArguments args)
    {
        const string kMethodName = "DataView constructor";
        // 1. If NewTarget is undefined, throw a TypeError exception.
        if (args.NewTarget.IsUndefined)
        {
            return isolate.ThrowTypeError(MessageTemplate.ConstructorNotFunction, Str(isolate, "DataView"));
        }
        // [[Construct]]
        JSFunction target = args.Target;
        var newTarget = args.NewTarget.As<JSReceiver>();
        JSValue buffer = args.AtOrUndefined(1);
        JSValue byteOffset = args.AtOrUndefined(2);
        JSValue byteLength = args.AtOrUndefined(3);

        // 2. Perform ? RequireInternalSlot(buffer, [[ArrayBufferData]]).
        if (buffer.HeapObjectOrNull is not JSArrayBuffer arrayBuffer)
        {
            return isolate.ThrowTypeError(MessageTemplate.DataViewNotArrayBuffer);
        }

        // 3. Let offset be ? ToIndex(byteOffset).
        byteOffset = ObjectOps.ToIndex(isolate, byteOffset, MessageTemplate.InvalidOffset);
        ulong viewByteOffset = (ulong)byteOffset.Number;

        // 4. If IsDetachedBuffer(buffer) is true, throw a TypeError exception.
        if (arrayBuffer.WasDetached)
        {
            return isolate.ThrowTypeError(MessageTemplate.TypedArrayDetachedErrorOperation, Str(isolate, kMethodName));
        }

        // 5. Let bufferByteLength be ArrayBufferByteLength(buffer, SeqCst).
        ulong bufferByteLength = arrayBuffer.GetByteLength();

        // 6. If offset > bufferByteLength, throw a RangeError exception.
        if (viewByteOffset > bufferByteLength)
        {
            return isolate.ThrowRangeError(MessageTemplate.InvalidOffset, byteOffset);
        }

        // 7-11.
        ulong viewByteLength;
        bool lengthTracking = false;
        if (byteLength.IsUndefined)
        {
            viewByteLength = bufferByteLength - viewByteOffset;
            lengthTracking = arrayBuffer.IsResizableByJs;
        }
        else
        {
            byteLength = ObjectOps.ToIndex(isolate, byteLength, MessageTemplate.InvalidDataViewLength);
            if (viewByteOffset + byteLength.Number > bufferByteLength)
            {
                return isolate.ThrowRangeError(MessageTemplate.InvalidDataViewLength, byteLength);
            }
            viewByteLength = (ulong)byteLength.Number;
        }

        bool isBackedByRab = arrayBuffer.IsResizableByJs && !arrayBuffer.IsShared;

        // 12. Let O be ? OrdinaryCreateFromConstructor(NewTarget,
        //     "%DataViewPrototype%", «[[DataView]], [[ViewedArrayBuffer]],
        //     [[ByteLength]], [[ByteOffset]]»).
        JSDataView dataView;
        if (isBackedByRab || lengthTracking)
        {
            // Create a JSRabGsabDataView.
            Map initialMap = GetDerivedRabGsabDataViewMap(isolate, newTarget);
            dataView = (JSDataView)isolate.Factory.NewJSObjectFromMap(initialMap);
        }
        else
        {
            dataView = (JSDataView)JSObject.New(isolate, target, newTarget, null);
        }
        dataView.Elements = ReadOnlyRoots.empty_fixed_array;
        dataView.IsBackedByRab = isBackedByRab;
        dataView.IsLengthTracking = lengthTracking;
        dataView.RawByteLength = 0;
        dataView.ByteOffset = 0;
        dataView.Buffer = arrayBuffer;

        // 13. If IsDetachedBuffer(buffer) is true, throw a TypeError exception.
        if (arrayBuffer.WasDetached)
        {
            return isolate.ThrowTypeError(MessageTemplate.TypedArrayDetachedErrorOperation, Str(isolate, kMethodName));
        }

        // 14-15.
        bufferByteLength = arrayBuffer.GetByteLength();

        // 16. If offset > bufferByteLength, throw a RangeError exception.
        if (viewByteOffset > bufferByteLength)
        {
            return isolate.ThrowRangeError(MessageTemplate.InvalidOffset, byteOffset);
        }

        // 17. If byteLengthChecked is not empty, then
        //       a. If offset + viewByteLength > bufferByteLength, throw a RangeError
        //       exception.
        if (!lengthTracking && viewByteOffset + viewByteLength > bufferByteLength)
        {
            return isolate.ThrowRangeError(MessageTemplate.InvalidDataViewLength);
        }

        // 19. Set O.[[ByteLength]] to viewByteLength.
        dataView.RawByteLength = lengthTracking ? 0 : viewByteLength;
        // 20. Set O.[[ByteOffset]] to offset.
        dataView.ByteOffset = viewByteOffset;
        // 21. Return O.
        return dataView;
    }

    /// <summary>JSFunction::GetDerivedRabGsabDataViewMap.</summary>
    static Map GetDerivedRabGsabDataViewMap(Isolate isolate, JSReceiver newTarget)
    {
        NativeContext context = isolate.NativeContext;
        JSFunction constructor = context.DataViewFun;
        Map map = JSFunction.GetDerivedMap(isolate, constructor, newTarget);
        if (ReferenceEquals(map, constructor.InitialMap)) return context.JSRabGsabDataViewMap;
        // This is a subclass - create a new map.
        Map rabGsabMap = Map.Copy(isolate, map, "RAB / GSAB");
        rabGsabMap.InstanceType = InstanceType.JSRabGsabDataViewType;
        return rabGsabMap;
    }

    /// <summary>ValidateDataView.</summary>
    static JSDataView ValidateDataView(Isolate isolate, JSValue o, string method)
    {
        if (o.HeapObjectOrNull is JSDataView dataView) return dataView;
        isolate.ThrowTypeError(MessageTemplate.IncompatibleMethodReceiver, Str(isolate, method), o);
        return null!;
    }

    /// <summary>ES6 section 24.2.4.1 get DataView.prototype.buffer.</summary>
    public static JSValue DataViewPrototypeGetBuffer(Isolate isolate, in BuiltinArguments args) =>
        ValidateDataView(isolate, args.Receiver, "get DataView.prototype.buffer").Buffer;

    /// <summary>ES6 section 24.2.4.2 get DataView.prototype.byteLength.</summary>
    public static JSValue DataViewPrototypeGetByteLength(Isolate isolate, in BuiltinArguments args)
    {
        JSDataView dataView = ValidateDataView(isolate, args.Receiver, kBuiltinNameByteLength);
        if (dataView.IsVariableLength)
        {
            if (dataView.WasDetached || dataView.IsOutOfBounds)
            {
                return isolate.ThrowTypeError(MessageTemplate.TypedArrayValidateErrorOperation, Str(isolate, kBuiltinNameByteLength));
            }
            return JSValue.FromNumber(dataView.GetByteLength());
        }
        if (dataView.WasDetached)
        {
            return isolate.ThrowTypeError(MessageTemplate.TypedArrayValidateErrorOperation, Str(isolate, kBuiltinNameByteLength));
        }
        return JSValue.FromNumber(dataView.RawByteLength);
    }

    /// <summary>ES6 section 24.2.4.3 get DataView.prototype.byteOffset.</summary>
    public static JSValue DataViewPrototypeGetByteOffset(Isolate isolate, in BuiltinArguments args)
    {
        JSDataView dataView = ValidateDataView(isolate, args.Receiver, kBuiltinNameByteOffset);
        if (IsDetachedOrOutOfBounds(dataView))
        {
            return isolate.ThrowTypeError(MessageTemplate.TypedArrayValidateErrorOperation, Str(isolate, kBuiltinNameByteOffset));
        }
        return JSValue.FromNumber(dataView.ByteOffset);
    }

    /// <summary>typed_array::IsJSArrayBufferViewDetachedOrOutOfBounds for data views.</summary>
    static bool IsDetachedOrOutOfBounds(JSDataView dataView) =>
        dataView.WasDetached || (dataView.IsVariableLength && dataView.IsOutOfBounds);

    static string GetterName(ElementsKind kind) => kind switch
    {
        ElementsKind.UINT8_ELEMENTS => "DataView.prototype.getUint8",
        ElementsKind.INT8_ELEMENTS => "DataView.prototype.getInt8",
        ElementsKind.UINT16_ELEMENTS => "DataView.prototype.getUint16",
        ElementsKind.INT16_ELEMENTS => "DataView.prototype.getInt16",
        ElementsKind.UINT32_ELEMENTS => "DataView.prototype.getUint32",
        ElementsKind.INT32_ELEMENTS => "DataView.prototype.getInt32",
        ElementsKind.FLOAT16_ELEMENTS => "DataView.prototype.getFloat16",
        ElementsKind.FLOAT32_ELEMENTS => "DataView.prototype.getFloat32",
        ElementsKind.FLOAT64_ELEMENTS => "DataView.prototype.getFloat64",
        ElementsKind.BIGINT64_ELEMENTS => "DataView.prototype.getBigInt64",
        _ => "DataView.prototype.getBigUint64",
    };

    static string SetterName(ElementsKind kind) => kind switch
    {
        ElementsKind.UINT8_ELEMENTS => "DataView.prototype.setUint8",
        ElementsKind.INT8_ELEMENTS => "DataView.prototype.setInt8",
        ElementsKind.UINT16_ELEMENTS => "DataView.prototype.setUint16",
        ElementsKind.INT16_ELEMENTS => "DataView.prototype.setInt16",
        ElementsKind.UINT32_ELEMENTS => "DataView.prototype.setUint32",
        ElementsKind.INT32_ELEMENTS => "DataView.prototype.setInt32",
        ElementsKind.FLOAT16_ELEMENTS => "DataView.prototype.setFloat16",
        ElementsKind.FLOAT32_ELEMENTS => "DataView.prototype.setFloat32",
        ElementsKind.FLOAT64_ELEMENTS => "DataView.prototype.setFloat64",
        ElementsKind.BIGINT64_ELEMENTS => "DataView.prototype.setBigInt64",
        _ => "DataView.prototype.setBigUint64",
    };

    /// <summary>DataViewElementSize.</summary>
    static int DataViewElementSize(ElementsKind kind) => kind switch
    {
        ElementsKind.UINT8_ELEMENTS or ElementsKind.INT8_ELEMENTS => 1,
        ElementsKind.UINT16_ELEMENTS or ElementsKind.INT16_ELEMENTS or ElementsKind.FLOAT16_ELEMENTS => 2,
        ElementsKind.UINT32_ELEMENTS or ElementsKind.INT32_ELEMENTS or ElementsKind.FLOAT32_ELEMENTS => 4,
        _ => 8,
    };

    /// <summary>
    /// The bounds-checked bytes for an access of <paramref name="elementSize"/> at
    /// <paramref name="getIndex"/> (steps 7-12 of GetViewValue / 10-15 of SetViewValue).
    /// </summary>
    static Span<byte> ViewBytes(Isolate isolate, JSDataView dataView, ulong getIndex, int elementSize, string methodName)
    {
        // If IsViewOutOfBounds(view, getBufferByteLength) is true, throw a TypeError exception.
        if (IsDetachedOrOutOfBounds(dataView))
        {
            isolate.ThrowTypeError(MessageTemplate.TypedArrayValidateErrorOperation, Str(isolate, methodName));
        }
        ulong viewOffset = dataView.ByteOffset;
        ulong viewSize = dataView.IsLengthTracking ? dataView.GetByteLength() : dataView.RawByteLength;
        // If getIndex + elementSize > viewSize, throw a RangeError exception.
        if (getIndex + (ulong)elementSize > viewSize)
        {
            isolate.ThrowRangeError(MessageTemplate.InvalidDataViewAccessorOffset);
        }
        ulong bufferIndex = getIndex + viewOffset;
        return dataView.Buffer.BackingStoreBuffer.AsSpan((int)bufferIndex, elementSize);
    }

    /// <summary>ToIndex(requestIndex) otherwise RangeError(kInvalidDataViewAccessorOffset).</summary>
    static ulong ToAccessIndex(Isolate isolate, JSValue requestIndex)
    {
        if (!BuiltinsTypedArray.ToIndex(isolate, requestIndex, out ulong index))
        {
            isolate.ThrowRangeError(MessageTemplate.InvalidDataViewAccessorOffset);
        }
        return index;
    }

    /// <summary>DataViewGet: GetViewValue ( view, requestIndex, isLittleEndian, type ).</summary>
    internal static JSValue DataViewGet(Isolate isolate, in BuiltinArguments args, ElementsKind kind)
    {
        string methodName = GetterName(kind);
        // 1-2.
        JSDataView dataView = ValidateDataView(isolate, args.Receiver, methodName);
        // 3. Let getIndex be ? ToIndex(requestIndex).
        ulong getIndex = ToAccessIndex(isolate, args.AtOrUndefined(1));
        // 4. Set isLittleEndian to ! ToBoolean(isLittleEndian).
        bool littleEndian = kind is not (ElementsKind.UINT8_ELEMENTS or ElementsKind.INT8_ELEMENTS) &&
                            ObjectOps.BooleanValue(args.AtOrUndefined(2));
        ReadOnlySpan<byte> bytes = ViewBytes(isolate, dataView, getIndex, DataViewElementSize(kind), methodName);

        switch (kind)
        {
            case ElementsKind.UINT8_ELEMENTS:
                return JSValue.FromInt(bytes[0]);
            case ElementsKind.INT8_ELEMENTS:
                return JSValue.FromInt((sbyte)bytes[0]);
            case ElementsKind.UINT16_ELEMENTS:
                return JSValue.FromInt(littleEndian ? BinaryPrimitives.ReadUInt16LittleEndian(bytes) : BinaryPrimitives.ReadUInt16BigEndian(bytes));
            case ElementsKind.INT16_ELEMENTS:
                return JSValue.FromInt(littleEndian ? BinaryPrimitives.ReadInt16LittleEndian(bytes) : BinaryPrimitives.ReadInt16BigEndian(bytes));
            case ElementsKind.UINT32_ELEMENTS:
                return JSValue.FromNumber(littleEndian ? BinaryPrimitives.ReadUInt32LittleEndian(bytes) : BinaryPrimitives.ReadUInt32BigEndian(bytes));
            case ElementsKind.INT32_ELEMENTS:
                return JSValue.FromInt(littleEndian ? BinaryPrimitives.ReadInt32LittleEndian(bytes) : BinaryPrimitives.ReadInt32BigEndian(bytes));
            case ElementsKind.FLOAT16_ELEMENTS:
            {
                ushort bits = littleEndian ? BinaryPrimitives.ReadUInt16LittleEndian(bytes) : BinaryPrimitives.ReadUInt16BigEndian(bytes);
                return JSValue.FromNumber(TypedArrayScalars.Float16ToDouble(bits));
            }
            case ElementsKind.FLOAT32_ELEMENTS:
            {
                uint bits = littleEndian ? BinaryPrimitives.ReadUInt32LittleEndian(bytes) : BinaryPrimitives.ReadUInt32BigEndian(bytes);
                return JSValue.FromNumber(BitConverter.UInt32BitsToSingle(bits));
            }
            case ElementsKind.FLOAT64_ELEMENTS:
            {
                ulong bits = littleEndian ? BinaryPrimitives.ReadUInt64LittleEndian(bytes) : BinaryPrimitives.ReadUInt64BigEndian(bytes);
                return JSValue.FromNumber(BitConverter.UInt64BitsToDouble(bits));
            }
            case ElementsKind.BIGUINT64_ELEMENTS:
            {
                ulong bits = littleEndian ? BinaryPrimitives.ReadUInt64LittleEndian(bytes) : BinaryPrimitives.ReadUInt64BigEndian(bytes);
                return BigInt.FromUint64(isolate, bits);
            }
            default:
            {
                ulong bits = littleEndian ? BinaryPrimitives.ReadUInt64LittleEndian(bytes) : BinaryPrimitives.ReadUInt64BigEndian(bytes);
                return BigInt.FromInt64(isolate, (long)bits);
            }
        }
    }

    /// <summary>DataViewSet: SetViewValue ( view, requestIndex, isLittleEndian, type, value ).</summary>
    internal static JSValue DataViewSet(Isolate isolate, in BuiltinArguments args, ElementsKind kind)
    {
        string methodName = SetterName(kind);
        // 1-2.
        JSDataView dataView = ValidateDataView(isolate, args.Receiver, methodName);
        if (dataView.Buffer.IsImmutable)
        {
            return isolate.ThrowTypeError(MessageTemplate.TypedArrayImmutableBufferErrorOperation, Str(isolate, methodName));
        }
        // 3. Let getIndex be ? ToIndex(requestIndex).
        ulong getIndex = ToAccessIndex(isolate, args.AtOrUndefined(1));
        JSValue value = args.AtOrUndefined(2);
        bool isBigInt = kind is ElementsKind.BIGUINT64_ELEMENTS or ElementsKind.BIGINT64_ELEMENTS;
        // 4-5. ToBigInt / ToNumber.
        JSValue numberValue = isBigInt ? BigInt.FromObject(isolate, value) : ObjectOps.ToNumber(isolate, value);
        // 6. Set isLittleEndian to !ToBoolean(isLittleEndian).
        bool littleEndian = kind is not (ElementsKind.UINT8_ELEMENTS or ElementsKind.INT8_ELEMENTS) &&
                            ObjectOps.BooleanValue(args.AtOrUndefined(3));
        Span<byte> bytes = ViewBytes(isolate, dataView, getIndex, DataViewElementSize(kind), methodName);

        if (isBigInt)
        {
            // Only the 64 lowest bits are stored (two's complement).
            ulong bits = BigInt.AsUint64(numberValue.As<BigInt>(), out _);
            if (littleEndian) BinaryPrimitives.WriteUInt64LittleEndian(bytes, bits);
            else BinaryPrimitives.WriteUInt64BigEndian(bytes, bits);
            return JSValue.Undefined;
        }

        double doubleValue = numberValue.Number;
        switch (kind)
        {
            case ElementsKind.UINT8_ELEMENTS:
            case ElementsKind.INT8_ELEMENTS:
                bytes[0] = (byte)Conversions.DoubleToInt32(doubleValue);
                break;
            case ElementsKind.UINT16_ELEMENTS:
            case ElementsKind.INT16_ELEMENTS:
            {
                ushort v = (ushort)Conversions.DoubleToInt32(doubleValue);
                if (littleEndian) BinaryPrimitives.WriteUInt16LittleEndian(bytes, v);
                else BinaryPrimitives.WriteUInt16BigEndian(bytes, v);
                break;
            }
            case ElementsKind.FLOAT16_ELEMENTS:
            {
                ushort v = TypedArrayScalars.DoubleToFloat16(doubleValue);
                if (littleEndian) BinaryPrimitives.WriteUInt16LittleEndian(bytes, v);
                else BinaryPrimitives.WriteUInt16BigEndian(bytes, v);
                break;
            }
            case ElementsKind.UINT32_ELEMENTS:
            case ElementsKind.INT32_ELEMENTS:
            {
                uint v = (uint)Conversions.DoubleToInt32(doubleValue);
                if (littleEndian) BinaryPrimitives.WriteUInt32LittleEndian(bytes, v);
                else BinaryPrimitives.WriteUInt32BigEndian(bytes, v);
                break;
            }
            case ElementsKind.FLOAT32_ELEMENTS:
            {
                uint v = BitConverter.SingleToUInt32Bits(TypedArrayScalars.DoubleToFloat32(doubleValue));
                if (littleEndian) BinaryPrimitives.WriteUInt32LittleEndian(bytes, v);
                else BinaryPrimitives.WriteUInt32BigEndian(bytes, v);
                break;
            }
            default:
            {
                ulong v = BitConverter.DoubleToUInt64Bits(doubleValue);
                if (littleEndian) BinaryPrimitives.WriteUInt64LittleEndian(bytes, v);
                else BinaryPrimitives.WriteUInt64BigEndian(bytes, v);
                break;
            }
        }
        return JSValue.Undefined;
    }
}
