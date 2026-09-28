// Port of the ArrayBuffer, SharedArrayBuffer, Atomics, TypedArray, typed
// array constructors and DataView sections of Genesis::InitializeGlobal
// (src/init/bootstrapper.cc, "-- A r r a y B u f f e r" to "-- D a t a V i e w"),
// Genesis::CreateArrayBuffer, Genesis::InstallTypedArray, and the typed-array
// related InitializeGlobal_* feature installers (sharedarraybuffer,
// js_immutable_arraybuffer).
namespace V8Sharp.Init;

sealed partial class Genesis
{
    /// <summary>
    /// bootstrapper.cc 4120-4451: runs in InitializeGlobal after Math (and
    /// Intl, which V8Sharp does not have) and before Map.
    /// </summary>
    void InitializeGlobalTypedArrays(JSObject global)
    {
        Isolate isolate = _isolate;
        NativeContext nativeContext = _nativeContext;

        {  // -- A r r a y B u f f e r
            JSString name = ReadOnlyRoots.ArrayBuffer_string;
            JSFunction arrayBufferFun = CreateArrayBuffer(name, ArrayBufferKind.ARRAY_BUFFER);
            JSObject.AddProperty(isolate, global, name, arrayBufferFun, PropertyAttributes.DONT_ENUM);
            Bootstrapper.InstallWithIntrinsicDefaultProto(isolate, arrayBufferFun, Context.Field.ARRAY_BUFFER_FUN_INDEX);
            Bootstrapper.InstallSpeciesGetter(isolate, arrayBufferFun);

            JSFunction arrayBufferNoinitFun = Bootstrapper.SimpleCreateFunction(isolate,
                _factory.InternalizeString("arrayBufferConstructor_DoNotInitialize"),
                Builtin.ArrayBufferConstructor_DoNotInitialize, 1, false);
            nativeContext.ArrayBufferNoinitFun = arrayBufferNoinitFun;

            var arrayBufferPrototype = (JSObject)arrayBufferFun.InstancePrototype;
            Bootstrapper.SimpleInstallGetter(isolate, arrayBufferPrototype, ReadOnlyRoots.max_byte_length_string,
                Builtin.ArrayBufferPrototypeGetMaxByteLength, true);
            Bootstrapper.SimpleInstallGetter(isolate, arrayBufferPrototype, ReadOnlyRoots.resizable_string,
                Builtin.ArrayBufferPrototypeGetResizable, true);
            Bootstrapper.SimpleInstallFunction(isolate, arrayBufferPrototype, "resize", Builtin.ArrayBufferPrototypeResize, 1, true);
            Bootstrapper.SimpleInstallFunction(isolate, arrayBufferPrototype, "transfer", Builtin.ArrayBufferPrototypeTransfer, 0,
                false);
            Bootstrapper.SimpleInstallFunction(isolate, arrayBufferPrototype, "transferToFixedLength",
                Builtin.ArrayBufferPrototypeTransferToFixedLength, 0, false);
            Bootstrapper.SimpleInstallGetter(isolate, arrayBufferPrototype, ReadOnlyRoots.detached_string,
                Builtin.ArrayBufferPrototypeGetDetached, true);

            // Genesis::ConfigureGlobalObject sets the array buffer map at the end
            // of genesis; it is not observable, and nothing reads it earlier.
            nativeContext.ArrayBufferMap = arrayBufferFun.InitialMap;
        }

        {  // -- S h a r e d A r r a y B u f f e r
            JSString name = ReadOnlyRoots.SharedArrayBuffer_string;
            JSFunction sharedArrayBufferFun = CreateArrayBuffer(name, ArrayBufferKind.SHARED_ARRAY_BUFFER);
            Bootstrapper.InstallWithIntrinsicDefaultProto(isolate, sharedArrayBufferFun,
                Context.Field.SHARED_ARRAY_BUFFER_FUN_INDEX);
            Bootstrapper.InstallSpeciesGetter(isolate, sharedArrayBufferFun);

            var sharedArrayBufferPrototype = (JSObject)sharedArrayBufferFun.InstancePrototype;
            Bootstrapper.SimpleInstallGetter(isolate, sharedArrayBufferPrototype, ReadOnlyRoots.max_byte_length_string,
                Builtin.SharedArrayBufferPrototypeGetMaxByteLength, true);
            Bootstrapper.SimpleInstallGetter(isolate, sharedArrayBufferPrototype, ReadOnlyRoots.growable_string,
                Builtin.SharedArrayBufferPrototypeGetGrowable, true);
            Bootstrapper.SimpleInstallFunction(isolate, sharedArrayBufferPrototype, "grow",
                Builtin.SharedArrayBufferPrototypeGrow, 1, true);
        }

        {  // -- A t o m i c s
            JSObject atomicsObject = _factory.NewJSObject(nativeContext.ObjectFunction);
            JSObject.AddProperty(isolate, global, _factory.InternalizeString("Atomics"), atomicsObject,
                PropertyAttributes.DONT_ENUM);
            Bootstrapper.InstallToStringTag(isolate, atomicsObject, "Atomics");

            Bootstrapper.SimpleInstallFunction(isolate, atomicsObject, "load", Builtin.AtomicsLoad, 2, true);
            Bootstrapper.SimpleInstallFunction(isolate, atomicsObject, "store", Builtin.AtomicsStore, 3, true);
            Bootstrapper.SimpleInstallFunction(isolate, atomicsObject, "add", Builtin.AtomicsAdd, 3, true);
            Bootstrapper.SimpleInstallFunction(isolate, atomicsObject, "sub", Builtin.AtomicsSub, 3, true);
            Bootstrapper.SimpleInstallFunction(isolate, atomicsObject, "and", Builtin.AtomicsAnd, 3, true);
            Bootstrapper.SimpleInstallFunction(isolate, atomicsObject, "or", Builtin.AtomicsOr, 3, true);
            Bootstrapper.SimpleInstallFunction(isolate, atomicsObject, "xor", Builtin.AtomicsXor, 3, true);
            Bootstrapper.SimpleInstallFunction(isolate, atomicsObject, "exchange", Builtin.AtomicsExchange, 3, true);
            Bootstrapper.SimpleInstallFunction(isolate, atomicsObject, "compareExchange", Builtin.AtomicsCompareExchange, 4, true);
            Bootstrapper.SimpleInstallFunction(isolate, atomicsObject, "isLockFree", Builtin.AtomicsIsLockFree, 1, true);
            Bootstrapper.SimpleInstallFunction(isolate, atomicsObject, "wait", Builtin.AtomicsWait, 4, true);
            Bootstrapper.SimpleInstallFunction(isolate, atomicsObject, "waitAsync", Builtin.AtomicsWaitAsync, 4, true);
            Bootstrapper.SimpleInstallFunction(isolate, atomicsObject, "notify", Builtin.AtomicsNotify, 3, true);
            Bootstrapper.SimpleInstallFunction(isolate, atomicsObject, "pause", Builtin.AtomicsPause, 0, false);
        }

        {  // -- T y p e d A r r a y
            JSFunction typedArrayFun = Bootstrapper.CreateFunction(isolate, _factory.InternalizeString("TypedArray"),
                InstanceType.JSTypedArrayType, JSObject.GetHeaderSize(InstanceType.JSTypedArrayType), 0, JSValue.TheHole,
                Builtin.TypedArrayBaseConstructor, 0, true);
            typedArrayFun.Shared.Native = false;
            Bootstrapper.InstallSpeciesGetter(isolate, typedArrayFun);
            nativeContext.TypedArrayFunction = typedArrayFun;

            Bootstrapper.SimpleInstallFunction(isolate, typedArrayFun, "of", Builtin.TypedArrayOf, 0, false);
            Bootstrapper.SimpleInstallFunction(isolate, typedArrayFun, "from", Builtin.TypedArrayFrom, 1, false);

            // Setup %TypedArrayPrototype%.
            var prototype = (JSObject)typedArrayFun.InstancePrototype;
            nativeContext.TypedArrayPrototype = prototype;

            // Install the "buffer", "byteOffset", "byteLength", "length"
            // and @@toStringTag getters on the {prototype}.
            Bootstrapper.SimpleInstallGetter(isolate, prototype, ReadOnlyRoots.buffer_string, Builtin.TypedArrayPrototypeBuffer, false);
            Bootstrapper.SimpleInstallGetter(isolate, prototype, ReadOnlyRoots.byte_length_string,
                Builtin.TypedArrayPrototypeByteLength, true);
            Bootstrapper.SimpleInstallGetter(isolate, prototype, ReadOnlyRoots.byte_offset_string,
                Builtin.TypedArrayPrototypeByteOffset, true);
            Bootstrapper.SimpleInstallGetter(isolate, prototype, ReadOnlyRoots.length_string, Builtin.TypedArrayPrototypeLength, true);
            Bootstrapper.SimpleInstallGetter(isolate, prototype, ReadOnlyRoots.to_string_tag_symbol,
                Builtin.TypedArrayPrototypeToStringTag, true);

            // Install "keys", "values" and "entries" methods on the {prototype}.
            Bootstrapper.InstallFunctionWithBuiltinId(isolate, prototype, "entries", Builtin.TypedArrayPrototypeEntries, 0, false);
            Bootstrapper.InstallFunctionWithBuiltinId(isolate, prototype, "keys", Builtin.TypedArrayPrototypeKeys, 0, false);
            JSFunction values = Bootstrapper.InstallFunctionWithBuiltinId(isolate, prototype, "values",
                Builtin.TypedArrayPrototypeValues, 0, false);
            JSObject.AddProperty(isolate, prototype, ReadOnlyRoots.iterator_symbol, values, PropertyAttributes.DONT_ENUM);

            // TODO(caitp): alphasort accessors/methods
            Bootstrapper.SimpleInstallFunction(isolate, prototype, "at", Builtin.TypedArrayPrototypeAt, 1, true);
            Bootstrapper.SimpleInstallFunction(isolate, prototype, "copyWithin", Builtin.TypedArrayPrototypeCopyWithin, 2, false);
            Bootstrapper.SimpleInstallFunction(isolate, prototype, "every", Builtin.TypedArrayPrototypeEvery, 1, false);
            Bootstrapper.SimpleInstallFunction(isolate, prototype, "fill", Builtin.TypedArrayPrototypeFill, 1, false);
            Bootstrapper.SimpleInstallFunction(isolate, prototype, "filter", Builtin.TypedArrayPrototypeFilter, 1, false);
            Bootstrapper.SimpleInstallFunction(isolate, prototype, "find", Builtin.TypedArrayPrototypeFind, 1, false);
            Bootstrapper.SimpleInstallFunction(isolate, prototype, "findIndex", Builtin.TypedArrayPrototypeFindIndex, 1, false);
            Bootstrapper.SimpleInstallFunction(isolate, prototype, "findLast", Builtin.TypedArrayPrototypeFindLast, 1, false);
            Bootstrapper.SimpleInstallFunction(isolate, prototype, "findLastIndex", Builtin.TypedArrayPrototypeFindLastIndex, 1, false);
            Bootstrapper.SimpleInstallFunction(isolate, prototype, "forEach", Builtin.TypedArrayPrototypeForEach, 1, false);
            Bootstrapper.SimpleInstallFunction(isolate, prototype, "includes", Builtin.TypedArrayPrototypeIncludes, 1, false);
            Bootstrapper.SimpleInstallFunction(isolate, prototype, "indexOf", Builtin.TypedArrayPrototypeIndexOf, 1, false);
            Bootstrapper.SimpleInstallFunction(isolate, prototype, "join", Builtin.TypedArrayPrototypeJoin, 1, false);
            Bootstrapper.SimpleInstallFunction(isolate, prototype, "lastIndexOf", Builtin.TypedArrayPrototypeLastIndexOf, 1, false);
            Bootstrapper.SimpleInstallFunction(isolate, prototype, "map", Builtin.TypedArrayPrototypeMap, 1, false);
            Bootstrapper.SimpleInstallFunction(isolate, prototype, "reverse", Builtin.TypedArrayPrototypeReverse, 0, false);
            Bootstrapper.SimpleInstallFunction(isolate, prototype, "reduce", Builtin.TypedArrayPrototypeReduce, 1, false);
            Bootstrapper.SimpleInstallFunction(isolate, prototype, "reduceRight", Builtin.TypedArrayPrototypeReduceRight, 1, false);
            Bootstrapper.SimpleInstallFunction(isolate, prototype, "set", Builtin.TypedArrayPrototypeSet, 1, false);
            Bootstrapper.SimpleInstallFunction(isolate, prototype, "slice", Builtin.TypedArrayPrototypeSlice, 2, false);
            Bootstrapper.SimpleInstallFunction(isolate, prototype, "some", Builtin.TypedArrayPrototypeSome, 1, false);
            Bootstrapper.SimpleInstallFunction(isolate, prototype, "sort", Builtin.TypedArrayPrototypeSort, 1, false);
            Bootstrapper.SimpleInstallFunction(isolate, prototype, "subarray", Builtin.TypedArrayPrototypeSubArray, 2, false);
            Bootstrapper.SimpleInstallFunction(isolate, prototype, "toReversed", Builtin.TypedArrayPrototypeToReversed, 0, false);
            Bootstrapper.SimpleInstallFunction(isolate, prototype, "toSorted", Builtin.TypedArrayPrototypeToSorted, 1, false);
            Bootstrapper.SimpleInstallFunction(isolate, prototype, "with", Builtin.TypedArrayPrototypeWith, 2, true);
            Bootstrapper.SimpleInstallFunction(isolate, prototype, "toLocaleString", Builtin.TypedArrayPrototypeToLocaleString, 0,
                false);
            // V8 keeps array_prototype_to_string_fun in a local of
            // InitializeGlobal; it is Array.prototype.toString.
            JSValue arrayPrototypeToStringFun = ObjectOps.GetProperty(isolate, nativeContext.InitialArrayPrototype,
                ReadOnlyRoots.toString_string);
            JSObject.AddProperty(isolate, prototype, ReadOnlyRoots.toString_string, arrayPrototypeToStringFun,
                PropertyAttributes.DONT_ENUM);
        }

        {  // -- T y p e d A r r a y s
            InstallTypedArrayWithDefaultProto("Uint8Array", ElementsKind.UINT8_ELEMENTS);
            InstallTypedArrayWithDefaultProto("Int8Array", ElementsKind.INT8_ELEMENTS);
            InstallTypedArrayWithDefaultProto("Uint16Array", ElementsKind.UINT16_ELEMENTS);
            InstallTypedArrayWithDefaultProto("Int16Array", ElementsKind.INT16_ELEMENTS);
            InstallTypedArrayWithDefaultProto("Uint32Array", ElementsKind.UINT32_ELEMENTS);
            InstallTypedArrayWithDefaultProto("Int32Array", ElementsKind.INT32_ELEMENTS);
            InstallTypedArrayWithDefaultProto("BigUint64Array", ElementsKind.BIGUINT64_ELEMENTS);
            InstallTypedArrayWithDefaultProto("BigInt64Array", ElementsKind.BIGINT64_ELEMENTS);
            InstallTypedArrayWithDefaultProto("Uint8ClampedArray", ElementsKind.UINT8_CLAMPED_ELEMENTS);
            InstallTypedArrayWithDefaultProto("Float32Array", ElementsKind.FLOAT32_ELEMENTS);
            InstallTypedArrayWithDefaultProto("Float64Array", ElementsKind.FLOAT64_ELEMENTS);
            InstallTypedArrayWithDefaultProto("Float16Array", ElementsKind.FLOAT16_ELEMENTS);

            Map map = Bootstrapper.CreateLiteralObjectMapFromCache(isolate, [ReadOnlyRoots.read_string, ReadOnlyRoots.written_string]);
            nativeContext.SetUnit8ArrayResultMap = map;

            var uint8ArrayFunction = ObjectOps.GetProperty(isolate, global, ReadOnlyRoots.Uint8Array_string).As<JSFunction>();
            Bootstrapper.SimpleInstallFunction(isolate, uint8ArrayFunction, "fromBase64", Builtin.Uint8ArrayFromBase64, 1, false);
            Bootstrapper.SimpleInstallFunction(isolate, uint8ArrayFunction, "fromHex", Builtin.Uint8ArrayFromHex, 1, false);

            var uint8ArrayPrototype = (JSObject)uint8ArrayFunction.InstancePrototype;
            Bootstrapper.SimpleInstallFunction(isolate, uint8ArrayPrototype, "toBase64", Builtin.Uint8ArrayPrototypeToBase64, 0, false);
            Bootstrapper.SimpleInstallFunction(isolate, uint8ArrayPrototype, "setFromBase64",
                Builtin.Uint8ArrayPrototypeSetFromBase64, 1, false);
            Bootstrapper.SimpleInstallFunction(isolate, uint8ArrayPrototype, "toHex", Builtin.Uint8ArrayPrototypeToHex, 0, false);
            Bootstrapper.SimpleInstallFunction(isolate, uint8ArrayPrototype, "setFromHex", Builtin.Uint8ArrayPrototypeSetFromHex, 1,
                false);
        }

        {  // -- D a t a V i e w
            JSFunction dataViewFun = Bootstrapper.InstallFunction(isolate, global, "DataView", InstanceType.JSDataViewType,
                JSObject.GetHeaderSize(InstanceType.JSDataViewType), 0, JSValue.TheHole, Builtin.DataViewConstructor, 1, false);
            Bootstrapper.InstallWithIntrinsicDefaultProto(isolate, dataViewFun, Context.Field.DATA_VIEW_FUN_INDEX);

            // Setup %DataViewPrototype%.
            var prototype = (JSObject)dataViewFun.InstancePrototype;

            Bootstrapper.InstallToStringTag(isolate, prototype, "DataView");

            // Setup objects needed for the JSRabGsabDataView.
            Map rabGsabDataViewMap = _factory.NewContextfulMapForCurrentContext(InstanceType.JSRabGsabDataViewType,
                JSObject.GetHeaderSize(InstanceType.JSRabGsabDataViewType), ElementsKind.TERMINAL_FAST_ELEMENTS_KIND);
            Map.SetPrototype(isolate, rabGsabDataViewMap, prototype);
            rabGsabDataViewMap.SetConstructor(dataViewFun);
            nativeContext.JSRabGsabDataViewMap = rabGsabDataViewMap;

            // Install the "buffer", "byteOffset" and "byteLength" getters
            // on the {prototype}.
            Bootstrapper.SimpleInstallGetter(isolate, prototype, ReadOnlyRoots.buffer_string, Builtin.DataViewPrototypeGetBuffer, false);
            Bootstrapper.SimpleInstallGetter(isolate, prototype, ReadOnlyRoots.byte_length_string,
                Builtin.DataViewPrototypeGetByteLength, false);
            Bootstrapper.SimpleInstallGetter(isolate, prototype, ReadOnlyRoots.byte_offset_string,
                Builtin.DataViewPrototypeGetByteOffset, false);

            Bootstrapper.SimpleInstallFunction(isolate, prototype, "getInt8", Builtin.DataViewPrototypeGetInt8, 1, false);
            Bootstrapper.SimpleInstallFunction(isolate, prototype, "setInt8", Builtin.DataViewPrototypeSetInt8, 2, false);
            Bootstrapper.SimpleInstallFunction(isolate, prototype, "getUint8", Builtin.DataViewPrototypeGetUint8, 1, false);
            Bootstrapper.SimpleInstallFunction(isolate, prototype, "setUint8", Builtin.DataViewPrototypeSetUint8, 2, false);
            Bootstrapper.SimpleInstallFunction(isolate, prototype, "getInt16", Builtin.DataViewPrototypeGetInt16, 1, false);
            Bootstrapper.SimpleInstallFunction(isolate, prototype, "setInt16", Builtin.DataViewPrototypeSetInt16, 2, false);
            Bootstrapper.SimpleInstallFunction(isolate, prototype, "getUint16", Builtin.DataViewPrototypeGetUint16, 1, false);
            Bootstrapper.SimpleInstallFunction(isolate, prototype, "setUint16", Builtin.DataViewPrototypeSetUint16, 2, false);
            Bootstrapper.SimpleInstallFunction(isolate, prototype, "getInt32", Builtin.DataViewPrototypeGetInt32, 1, false);
            Bootstrapper.SimpleInstallFunction(isolate, prototype, "setInt32", Builtin.DataViewPrototypeSetInt32, 2, false);
            Bootstrapper.SimpleInstallFunction(isolate, prototype, "getUint32", Builtin.DataViewPrototypeGetUint32, 1, false);
            Bootstrapper.SimpleInstallFunction(isolate, prototype, "setUint32", Builtin.DataViewPrototypeSetUint32, 2, false);
            Bootstrapper.SimpleInstallFunction(isolate, prototype, "getFloat16", Builtin.DataViewPrototypeGetFloat16, 1, false);
            Bootstrapper.SimpleInstallFunction(isolate, prototype, "setFloat16", Builtin.DataViewPrototypeSetFloat16, 2, false);
            Bootstrapper.SimpleInstallFunction(isolate, prototype, "getFloat32", Builtin.DataViewPrototypeGetFloat32, 1, false);
            Bootstrapper.SimpleInstallFunction(isolate, prototype, "setFloat32", Builtin.DataViewPrototypeSetFloat32, 2, false);
            Bootstrapper.SimpleInstallFunction(isolate, prototype, "getFloat64", Builtin.DataViewPrototypeGetFloat64, 1, false);
            Bootstrapper.SimpleInstallFunction(isolate, prototype, "setFloat64", Builtin.DataViewPrototypeSetFloat64, 2, false);
            Bootstrapper.SimpleInstallFunction(isolate, prototype, "getBigInt64", Builtin.DataViewPrototypeGetBigInt64, 1, false);
            Bootstrapper.SimpleInstallFunction(isolate, prototype, "setBigInt64", Builtin.DataViewPrototypeSetBigInt64, 2, false);
            Bootstrapper.SimpleInstallFunction(isolate, prototype, "getBigUint64", Builtin.DataViewPrototypeGetBigUint64, 1, false);
            Bootstrapper.SimpleInstallFunction(isolate, prototype, "setBigUint64", Builtin.DataViewPrototypeSetBigUint64, 2, false);
        }
    }

    void InstallTypedArrayWithDefaultProto(string name, ElementsKind kind)
    {
        JSFunction fun = InstallTypedArray(name, kind,
            Context.Field.RAB_GSAB_UINT8_ARRAY_MAP_INDEX + (kind - ElementsKind.FIRST_FIXED_TYPED_ARRAY_ELEMENTS_KIND));
        Bootstrapper.InstallWithIntrinsicDefaultProto(_isolate, fun, JSTypedArray.ConstructorIndexForKind(kind));
    }

    /// <summary>Genesis::CreateArrayBuffer.</summary>
    JSFunction CreateArrayBuffer(JSString name, ArrayBufferKind arrayBufferKind)
    {
        Isolate isolate = _isolate;
        // Create the %ArrayBufferPrototype%
        // Setup the {prototype} with the given {name} for @@toStringTag.
        JSObject prototype = _factory.NewJSObject(_nativeContext.ObjectFunction);
        Bootstrapper.InstallToStringTag(isolate, prototype, name);

        // Allocate the constructor with the given {prototype}.
        JSFunction arrayBufferFun = Bootstrapper.CreateFunction(isolate, name, InstanceType.JSArrayBufferType,
            JSObject.GetHeaderSize(InstanceType.JSArrayBufferType), 0, prototype, Builtin.ArrayBufferConstructor, 1, true);

        // Install the "constructor" property on the {prototype}.
        JSObject.AddProperty(isolate, prototype, ReadOnlyRoots.constructor_string, arrayBufferFun, PropertyAttributes.DONT_ENUM);

        switch (arrayBufferKind)
        {
            case ArrayBufferKind.ARRAY_BUFFER:
                Bootstrapper.InstallFunctionWithBuiltinId(isolate, arrayBufferFun, "isView", Builtin.ArrayBufferIsView, 1, true);

                // Install the "byteLength" getter on the {prototype}.
                Bootstrapper.SimpleInstallGetter(isolate, prototype, ReadOnlyRoots.byte_length_string,
                    Builtin.ArrayBufferPrototypeGetByteLength, true);
                Bootstrapper.SimpleInstallFunction(isolate, prototype, "slice", Builtin.ArrayBufferPrototypeSlice, 2, true);
                break;

            case ArrayBufferKind.SHARED_ARRAY_BUFFER:
                // Install the "byteLength" getter on the {prototype}.
                Bootstrapper.SimpleInstallGetter(isolate, prototype, ReadOnlyRoots.byte_length_string,
                    Builtin.SharedArrayBufferPrototypeGetByteLength, false);
                Bootstrapper.SimpleInstallFunction(isolate, prototype, "slice", Builtin.SharedArrayBufferPrototypeSlice, 2, true);
                break;
        }

        return arrayBufferFun;
    }

    /// <summary>Genesis::InstallTypedArray.</summary>
    JSFunction InstallTypedArray(string name, ElementsKind elementsKind, Context.Field rabGsabInitialMapIndex)
    {
        Isolate isolate = _isolate;
        JSObject global = _nativeContext.GlobalObject;

        JSObject typedArrayPrototype = _nativeContext.TypedArrayPrototype;
        JSFunction typedArrayFunction = _nativeContext.TypedArrayFunction;

        JSFunction result = Bootstrapper.InstallFunction(isolate, global, name, InstanceType.JSTypedArrayType,
            JSObject.GetHeaderSize(InstanceType.JSTypedArrayType), 0, JSValue.TheHole, Builtin.TypedArrayConstructor, 3, false);
        result.InitialMap.SetElementsKind(elementsKind);

        JSObject.SetPrototype(isolate, result, typedArrayFunction, false, ShouldThrow.DontThrow);

        JSValue bytesPerElement = JSValue.FromInt(1 << ElementsKinds.ElementsKindToShiftSize(elementsKind));

        Bootstrapper.InstallConstant(isolate, result, "BYTES_PER_ELEMENT", bytesPerElement);

        // V8 also sets a per-kind constructor instance type
        // (TYPE##_TYPED_ARRAY_CONSTRUCTOR_TYPE) for its protector checks;
        // V8Sharp's protector checks recognize the constructors by their
        // native context slots instead (IsolateProtectors.IsTypedArrayConstructor).

        // Setup prototype object.
        var prototype = result.Prototype.As<JSObject>();

        JSObject.SetPrototype(isolate, prototype, typedArrayPrototype, false, ShouldThrow.DontThrow);

        Debug.Assert(!ReferenceEquals(prototype.Map, _nativeContext.InitialObjectPrototype.Map));
        prototype.Map.InstanceType = InstanceType.JSTypedArrayPrototypeType;

        Bootstrapper.InstallConstant(isolate, prototype, "BYTES_PER_ELEMENT", bytesPerElement);

        // RAB / GSAB backed TypedArrays don't have separate constructors, but they
        // have their own maps. Create the corresponding map here.
        Map rabGsabInitialMap = _factory.NewContextfulMapForCurrentContext(InstanceType.JSTypedArrayType,
            JSObject.GetHeaderSize(InstanceType.JSTypedArrayType), ElementsKinds.GetCorrespondingRabGsabElementsKind(elementsKind), 0);
        rabGsabInitialMap.SetConstructor(result);

        _nativeContext.Slots[(int)rabGsabInitialMapIndex] = rabGsabInitialMap;
        Map.SetPrototype(isolate, rabGsabInitialMap, prototype);

        return result;
    }

    /// <summary>
    /// The typed-array related InitializeGlobal_* feature installers of
    /// Genesis::InitializeExperimentalGlobal (bootstrapper.cc): shipped flags
    /// first (js_immutable_arraybuffer is staged), then sharedarraybuffer,
    /// which V8 runs after the feature flags.
    /// </summary>
    void InitializeExperimentalGlobalTypedArrays()
    {
        InitializeGlobal_js_immutable_arraybuffer();
        // FOREACH_EXPERIMENTAL_FEATURE_FLAG (Genesis.ShadowRealm.cs).
        InitializeGlobal_harmony_shadow_realm();
        InitializeGlobal_sharedarraybuffer();
    }

    /// <summary>Genesis::InitializeGlobal_js_immutable_arraybuffer.</summary>
    void InitializeGlobal_js_immutable_arraybuffer()
    {
        if (!_isolate.Flags.js_immutable_arraybuffer) return;
        JSFunction arrayBufferFun = _nativeContext.ArrayBufferFun;
        var prototype = (JSObject)arrayBufferFun.InstancePrototype;
        Bootstrapper.SimpleInstallGetter(_isolate, prototype, ReadOnlyRoots.immutable_string,
            Builtin.ArrayBufferPrototypeGetImmutable, true);
        Bootstrapper.SimpleInstallFunction(_isolate, prototype, "transferToImmutable",
            Builtin.ArrayBufferPrototypeTransferToImmutable, 0, false);
        Bootstrapper.SimpleInstallFunction(_isolate, prototype, "sliceToImmutable",
            Builtin.ArrayBufferPrototypeSliceToImmutable, 2, true);
    }

    /// <summary>Genesis::InitializeGlobal_sharedarraybuffer.</summary>
    void InitializeGlobal_sharedarraybuffer()
    {
        if (_isolate.Flags.enable_sharedarraybuffer_per_context) return;
        JSGlobalObject global = _nativeContext.GlobalObject;
        JSObject.AddProperty(_isolate, global, ReadOnlyRoots.SharedArrayBuffer_string, _nativeContext.SharedArrayBufferFun,
            PropertyAttributes.DONT_ENUM);
    }
}
